using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.BACnet;
using System.Text;
using BacnetDevice_VC100.Bacnet;
using BacnetDevice_VC100.DataAccess;
using BacnetDevice_VC100.Models;
using BacnetDevice_VC100.Service;
using BacnetDevice_VC100.Util;
using CShapeDeviceAgent;

namespace BacnetDevice_VC100
{
    /// <summary>
    /// BACnet Device DLL Entry
    /// - Agent 기준 제어 / 읽기 프로토콜 완전 호환
    /// - Polling → DB → Snapshot → ToReceive 흐름 보장
    /// </summary>
    public class BacnetBAS : CShapeDeviceBase
    {
        private StationConfig _station;
        private BacnetClientWrapper _client;
        private ObjectRepository _objectRepo;
        private RealtimeRepository _realtimeRepo;
        private PollingService _pollingService;
        private ControlHistoryRepository _controlHistoryRepo;

        private Dictionary<string, BacnetPointInfo> _pointMap;
        private int _deviceSeq;
        private bool _connected;

        #region Device Lifecycle

        public override bool DeviceLoad()
        {
            BacnetLogger.Info("DeviceLoad 호출됨.");
            return true;
        }

        public override bool Init(int iDeviceID)
        {
            BacnetLogger.SetCurrentDevice(iDeviceID);

            _deviceSeq = iDeviceID;
            BacnetLogger.Info($"Init 완료. device_seq={_deviceSeq}");
            BacnetLogger.Info("Init: 실제 제어 모드(정상 모드)로 동작합니다.");
            return true;
        }

        public override bool Connect(string sIP, int iPort)
        {
            BacnetLogger.SetCurrentDevice(_deviceSeq);

            BacnetLogger.Info($"Connect 시작. IP={sIP}, Port={iPort}, DeviceID={_deviceSeq}");

            _station = new StationConfig
            {
                Id = $"BACnet_{sIP.Replace('.', '_')}_{_deviceSeq}",
                Ip = sIP,
                Port = iPort,
                DeviceId = (uint)_deviceSeq
            };

            _objectRepo = new ObjectRepository();
            _realtimeRepo = new RealtimeRepository();
            _controlHistoryRepo = new ControlHistoryRepository();

            BuildPointCache();

            _client = new BacnetClientWrapper();
            _client.Start();

            _pollingService = new PollingService(_objectRepo, _realtimeRepo, _client);
            // ⭐ 여기 추가: 폴링 시작 (예: 5000ms)
            _pollingService.Start(_station, _deviceSeq, 5000);
            _connected = true;
            ToConnectState(_deviceSeq, true);

            BacnetLogger.Info($"Connect 완료. StationId={_station.Id}");
            return true;
        }

        public override bool DisConnect()
        {
            BacnetLogger.Info("DisConnect 호출됨.");

            _connected = false;
            ToConnectState(_deviceSeq, false);

            // ⭐ 여기 추가: 폴링 정지
            try
            {
                if (_pollingService != null) _pollingService.Stop();
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("[POLL][ERROR] Stop on disconnect failed", ex);
            }
            _client?.Dispose();
            _client = null;

            return true;
        }

        #endregion

        #region Agent → DLL 진입점

        /// <summary>
        /// DeviceAgent → DeviceDLL 진입점.
        ///
        /// ✅ 핵심: Agent가 DLL로 던지는 문자열(cData)의 형태로 "Read/Control"을 구분한다.
        ///
        /// 1) Read(값 수집/폴링) 계열:
        ///    - cData에 ';'가 없다.
        ///    - 예) "READ" / "POLL" 같은 단일 토큰이 오거나,
        ///         또는 sendType으로 구분되는 구조일 수 있다(프로젝트마다 다름).
        ///    - 이 케이스는 HandleRead(sendType, cData)로 전달된다.
        ///
        /// 2) Control(제어) 계열:
        ///    - cData에 ';'가 포함된다. (멀티포인트 제어를 ';'로 붙여서 한 번에 보냄)
        ///    - 예) "AV-1,42;BV-3,1;"  ← 포인트 2개 제어
        ///         - ';' 기준으로 명령 단위 분해
        ///         - 각 명령은 "SYSTEM_PT_ID,NewValue(,옵션...)" 형태인 경우가 많음
        ///
        /// 🔁 우리가 구현할 제어 히스토리:
        /// - 제어 명령 1개(포인트 1개) 처리할 때마다
        ///   TB_BACNET_CONTROL_HISTORY에 1행 INSERT.
        /// - PrevValue는 BACnet 재조회 없이,
        ///   TB_BACNET_REALTIME에서 조회(GetCurrentValue)로 가져온다.
        ///
        /// ⚠ 주의:
        /// - Server/Agent는 수정 불가. DLL 안에서만 "깨지지 않게" 처리해야 함.
        /// - 그래서 파싱 실패/쓰기 실패도 반드시 히스토리에 남겨서 추적 가능하게 한다.
        /// </summary>
        public override int SendData(int sendType, string cData, int dataSize)
        {
            BacnetLogger.SetCurrentDevice(_deviceSeq);

            if (string.IsNullOrEmpty(cData))
            {
                BacnetLogger.Warn("SendData: 수신 데이터 비어있음");
                return 0;
            }

            bool isControl = cData.IndexOf(';') >= 0;

            return isControl
                ? HandleControl(cData)
                : HandleRead(sendType, cData);
        }

        #endregion

        #region Read / Control

        /// <summary>
        /// [제어 데이터 흐름]
        /// SI Client → Server → DeviceAgent → (DLL) SendData → HandleControl
        ///
        /// [cData 예시]
        /// "AV-14,80;BV-1,1;AO-4,12.55;"
        ///  - ';' 기준: 명령 N개
        ///  - ',' 기준: (포인트키, 목표값)
        ///
        /// [정책]
        /// - 명령 1개 = TB_BACNET_CONTROL_HISTORY 1 row
        /// - PrevValue는 Realtime DB에서 조회 (BACnet 재조회 X)
        /// - 실제 Write(Enumerated/Real 변환 포함)는 BacnetClientWrapper에서만 수행 (중복 제거)
        /// </summary>
        private int HandleControl(string cData)
        {
            // =========================================================
            // cData 예시(멀티포인트):
            //   "AV-14,100;BV-3,1;"
            //
            // 규칙:
            //  - ';' 단위로 명령 분해
            //  - 각 명령은 "SYSTEM_PT_ID,NEW_VALUE" 형태라고 가정
            //    (옵션 값이 더 붙는 프로젝트도 있는데, 그건 뒤 토큰은 무시하는 쪽이 안전)
            // =========================================================

            int total = 0;
            int ok = 0;

            string[] commands = cData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            BacnetLogger.Info(string.Format("[CTRL] START device_seq={0}, raw={1}", _deviceSeq, cData));

            foreach (var cmd in commands)
            {
                total++;

                string systemPtId = null;
                string newValueRaw = null;

                string prevValueStr = null;
                string result = "FAIL";
                string err = null;

                try
                {
                    // 1) "AV-14,100" → [0]=AV-14, [1]=100
                    var parts = cmd.Split(new[] { ',' }, StringSplitOptions.None);
                    if (parts.Length < 2)
                    {
                        err = "PARSE_FAILED: expected 'SYSTEM_PT_ID,NEW_VALUE'";
                        continue;
                    }

                    systemPtId = (parts[0] ?? "").Trim();
                    newValueRaw = (parts[1] ?? "").Trim();

                    if (string.IsNullOrEmpty(systemPtId))
                    {
                        err = "PARSE_FAILED: empty SYSTEM_PT_ID";
                        continue;
                    }

                    // 2) PrevValue는 BACnet 재조회 금지(부하 + 타임아웃 리스크)
                    //    → TB_BACNET_REALTIME에서 마지막 값을 읽는다.
                    double? prev = _realtimeRepo.GetCurrentValue(_deviceSeq, systemPtId);
                    prevValueStr = prev.HasValue ? prev.Value.ToString(CultureInfo.InvariantCulture) : null;

                    // 3) SYSTEM_PT_ID → 실제 BACnetObjectTypes/Instance 매핑
                    //    - P_OBJECT에 없으면 "미등록 포인트 제어"라서 실패로 남김
                    var pt = _objectRepo.GetPointBySystemPtId(_deviceSeq, systemPtId);
                    if (pt == null)
                    {
                        err = "POINT_NOT_FOUND_IN_P_OBJECT";
                        continue;
                    }

                    // 4) 쓰기 값 타입 결정
                    //    - BV/BO/MSV/MSO는 enum(정수)
                    //    - 그 외는 실수(double)
                    object writeValue;
                    if (pt.BacnetType == BacnetObjectTypes.OBJECT_BINARY_VALUE ||
                        pt.BacnetType == BacnetObjectTypes.OBJECT_BINARY_OUTPUT ||
                        pt.BacnetType == BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE ||
                        pt.BacnetType == BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT)
                    {
                        uint enumVal;
                        if (!uint.TryParse(newValueRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out enumVal))
                        {
                            err = "VALUE_CONVERT_FAILED(enum)";
                            continue;
                        }
                        writeValue = enumVal;
                    }
                    else
                    {
                        double d;
                        if (!double.TryParse(newValueRaw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out d))
                        {
                            err = "VALUE_CONVERT_FAILED(real)";
                            continue;
                        }
                        writeValue = d;
                    }

                    // 5) BACnet Write
                    string writeErr;
                    bool writeOk = _client.TryWritePresentValue(_station, pt.BacnetType, pt.Instance, writeValue, out writeErr);

                    if (!writeOk)
                    {
                        err = "WRITE_FAILED: " + (writeErr ?? "unknown");
                        continue;
                    }

                    result = "OK";
                    ok++;
                }
                catch (Exception ex)
                {
                    // 포인트 1개 실패로 전체 제어가 죽으면 운영에서 지옥 열림
                    err = "UNEXPECTED: " + ex.Message;
                    BacnetLogger.Error(
                        string.Format("[CTRL][ERROR] Unexpected. device_seq={0}, cmd={1}", _deviceSeq, cmd),
                        ex);
                }
                finally
                {
                    // =========================================================
                    // 6) 제어 히스토리 기록(포인트당 1행)
                    //    - 성공/실패 모두 남긴다.
                    //    - 실패 원인은 ErrorMessage에 문자열로 박는다.
                    // =========================================================
                    try
                    {
                        _controlHistoryRepo.Insert(
                            _deviceSeq,
                            systemPtId,
                            prevValueStr,
                            newValueRaw,
                            result,
                            err,
                            controlUser: null,   // 지금 Agent가 유저를 안주면 null 유지
                            isDryRun: false,
                            source: "SI");
                    }
                    catch (Exception ex)
                    {
                        // 히스토리 기록 실패는 제어 자체와 별개로 로깅만 하고 넘김(제어 성공까지 롤백하면 더 위험)
                        BacnetLogger.Error(
                            string.Format("[CTRL][ERROR] History insert failed. device_seq={0}, pt={1}", _deviceSeq, systemPtId),
                            ex);
                    }
                }
            }

            BacnetLogger.Info(string.Format("[CTRL] END device_seq={0}, totalCmd={1}, ok={2}", _deviceSeq, total, ok));
            return ok;
        }



        /// <summary>
        /// 읽기 처리
        /// 요청: "BI-1,AO-2,"
        /// 응답: "BI-1,1,0;AO-2,23.5,0;"
        /// </summary>
        private int HandleRead(int sendType, string cData)
        {
            var snapshot = _realtimeRepo.GetSnapshotByDevice(_deviceSeq);
            var sb = new StringBuilder();

            string[] tokens = cData.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            int count = 0;

            foreach (string pt in tokens)
            {
                double value = snapshot.TryGetValue(pt, out double v)
                    ? v
                    : RealtimeConstants.FailValue;

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0},{1},0;",
                    pt,
                    value
                );
                count++;
            }

            string response = sb.ToString();

            BacnetLogger.Info($"[READ][ToReceive] count={count}, data={response}");

            ToReceive(
                _deviceSeq,
                sendType,
                response,
                count
            );

            return count;
        }

        #endregion

        #region Helpers

        private void BuildPointCache()
        {
            _pointMap = new Dictionary<string, BacnetPointInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _objectRepo.GetPointsByDeviceSeq(_deviceSeq))
            {
                if (!string.IsNullOrEmpty(p.SystemPtId))
                    _pointMap[p.SystemPtId] = p;
            }

            BacnetLogger.Info($"BuildPointCache 완료. count={_pointMap.Count}");
        }

        private bool TryControlPoint(string systemPtId, double value)
        {
            if (!_pointMap.TryGetValue(systemPtId, out var pt))
                return false;

            bool ok = _client.TryWritePresentValue(
                _station,
                pt.BacnetType,
                pt.Instance,
                value,
                out string error);

            if (!ok)
                BacnetLogger.Warn($"제어 실패: {systemPtId}, error={error}");
            else
                _realtimeRepo.UpsertRealtime(
                    _deviceSeq,
                    systemPtId,
                    value,
                    RealtimeConstants.QualityGood,
                    DateTime.Now,
                    null);

            return ok;
        }

        #endregion
    }
}
