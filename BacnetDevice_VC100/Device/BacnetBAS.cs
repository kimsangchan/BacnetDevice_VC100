using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BacnetDevice_VC100.Bacnet;
using BacnetDevice_VC100.DataAccess;
using BacnetDevice_VC100.Models;
using BacnetDevice_VC100.Service;
using BacnetDevice_VC100.Util;
using CShapeDeviceAgent;              // CShapeDeviceBase

namespace BacnetDevice_VC100
{
    /// <summary>
    /// SmartDeviceAgent에서 사용하는 BACnet 디바이스 DLL 진입점.
    /// - 하나의 DeviceAgent 프로세스에서 여러 BACnet 장비를 동시에 관리하는 구조를 고려.
    /// - BacnetLogger.SetCurrentDevice(_deviceSeq)로 쓰레드별 디바이스 컨텍스트를 세팅하여
    ///   Log/Device_{deviceSeq}/yyyy-MM-dd.log 에 로그를 분리 저장한다.
    /// </summary>
    public class BacnetBAS : CShapeDeviceBase
    {
        private StationConfig _station;
        private BacnetClientWrapper _client;
        private ObjectRepository _objectRepo;
        private RealtimeRepository _realtimeRepo;
        private PollingService _pollingService;

        // SYSTEM_PT_ID -> BacnetPointInfo 캐시
        private Dictionary<string, BacnetPointInfo> _pointMap;

        private bool _connected;
        private int _deviceSeq;
        private DateTime _lastPollUtc;
        // === DRY-RUN 모드 플래그 ===
        // - true  : 실제 장비로 BACnet Write 보내지 않음 (테스트 전용)
        // - false : 실제 장비에 제어 패킷 전송
        //
        //   값은 프로세스 환경변수 "BACNET_DRY_RUN" 으로 제어:
        //     - "1", "true", "yes" (대소문자 무시) 중 하나면 DRY-RUN 활성화
        //     - 설정 없거나 다른 값이면 정상 모드
        private readonly bool _dryRun = GetDryRunFlag();

        private static bool GetDryRunFlag()
        {
            try
            {
                string value = Environment.GetEnvironmentVariable("BACNET_DRY_RUN");

                if (string.IsNullOrEmpty(value))
                    return false;

                value = value.Trim();

                if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
                    return true;

                return false;
            }
            catch (Exception ex)
            {
                // DRY-RUN 플래그 읽는다고 DLL이 죽으면 안 되므로, 실패 시 그냥 정상 모드로 간다.
                BacnetLogger.Error("DRY-RUN 플래그 확인 중 예외 발생. 정상 모드로 동작합니다.", ex);
                return false;
            }
        }
        /// <summary>
        /// DLL 로드 시 1회 호출되는 초기화 포인트.
        /// 여기서는 가벼운 수준의 작업만 수행하고,
        /// 실제 디바이스별 초기화는 Init/Connect 에서 처리.
        /// </summary>
        public override bool DeviceLoad()
        {
            try
            {
                // CShapeDeviceBase 쪽에서 DeviceID를 어느 시점에 세팅하는지 확실치 않으므로
                // 여기서는 "있으면 쓰고 없으면 넘긴다" 수준으로 처리한다.
                _deviceSeq = DeviceID;

                if (_deviceSeq > 0)
                {
                    BacnetLogger.SetCurrentDevice(_deviceSeq);
                    BacnetLogger.Info("DeviceLoad 호출됨. DeviceID=" + DeviceID);
                }
                else
                {
                    // 디바이스 ID 정보가 아직 없을 수도 있으므로 컨텍스트 없이 콘솔만 찍힐 수 있다.
                    BacnetLogger.Info("DeviceLoad 호출됨. 아직 DeviceID가 설정되지 않았을 수 있습니다.");
                }

                return true;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("DeviceLoad 예외 발생.", ex);
                return false;
            }
        }

        /// <summary>
        /// 디바이스별 초기 설정. DeviceID가 여기서 확정된다.
        /// </summary>
        public override bool Init(int iDeviceID)
        {
            try
            {
                DeviceID = iDeviceID;
                _deviceSeq = iDeviceID;
                _lastPollUtc = DateTime.MinValue;

                BacnetLogger.SetCurrentDevice(_deviceSeq);
                BacnetLogger.Debug(string.Format("Init 시작. DeviceID={0}", iDeviceID));

                BacnetLogger.Info(string.Format("Init 완료. device_seq={0}", _deviceSeq));

                // DRY-RUN 모드 안내 로그 (운영 로그에 남겨야 확인 가능)
                if (_dryRun)
                    BacnetLogger.Info("Init: DRY-RUN 모드 활성화됨. 실제 장비로는 제어 패킷을 전송하지 않습니다.");
                else
                    BacnetLogger.Info("Init: 실제 제어 모드(정상 모드)로 동작합니다.");

                return true;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("Init 예외 발생.", ex);
                return false;
            }
        }

        /// <summary>
        /// BACnet 장비와의 실제 통신 연결을 수립한다.
        /// - StationConfig 구성
        /// - ObjectRepository/RealtimeRepository 생성
        /// - 포인트 캐시(BuildPointCache)
        /// - BacnetClientWrapper 시작
        /// - PollingService 준비
        /// </summary>
        public override bool Connect(string sIP, int iPort)
        {
            // 디바이스 컨텍스트 설정
            BacnetLogger.SetCurrentDevice(_deviceSeq);
            BacnetLogger.Info(
                string.Format("Connect 시작. IP={0}, Port={1}, DeviceID={2}", sIP, iPort, DeviceID));

            try
            {
                _station = new StationConfig
                {
                    Id = "BACnet_" + sIP.Replace('.', '_') + "_" + DeviceID,
                    Ip = sIP,
                    Port = iPort,
                    DeviceId = (uint)DeviceID
                };

                _objectRepo = new ObjectRepository();
                _realtimeRepo = new RealtimeRepository();

                // 포인트 캐시 구축 (P_OBJECT → SYSTEM_PT_ID 매핑)
                BuildPointCache();

                _client = new BacnetClientWrapper();
                _client.Start();

                _pollingService = new PollingService(_objectRepo, _realtimeRepo, _client);

                _connected = true;
                ToConnectState(DeviceID, true);

                BacnetLogger.Info("Connect 완료. StationId=" + _station.Id);
                return true;
            }
            catch (Exception ex)
            {
                _connected = false;
                ToConnectState(DeviceID, false);

                BacnetLogger.Error("Connect 예외 발생.", ex);
                return false;
            }
        }

        /// <summary>
        /// 장비 연결 해제 및 리소스 정리.
        /// </summary>
        public override bool DisConnect()
        {
            BacnetLogger.SetCurrentDevice(_deviceSeq);
            BacnetLogger.Info("DisConnect 호출됨. DeviceID=" + DeviceID);

            try
            {
                _connected = false;
                ToConnectState(DeviceID, false);

                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

                _pollingService = null;
                _objectRepo = null;
                _realtimeRepo = null;
                _station = null;
                _pointMap = null;

                BacnetLogger.Info("DisConnect 완료.");
                return true;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("DisConnect 예외 발생.", ex);
                return false;
            }
        }

        /// <summary>
        /// DeviceAgent에서 주기적으로 호출하는 함수.
        /// - DeviceIF.iTimeinterval 기반으로 폴링 주기를 제어.
        /// - PollingService.PollOnce 호출.
        /// </summary>
        public override bool TimeRecv()
        {
            // 디바이스 컨텍스트 설정
            BacnetLogger.SetCurrentDevice(_deviceSeq);

            try
            {
                if (!_connected || _pollingService == null || _station == null)
                    return false;

                int intervalMs = 0;
                try
                {
                    intervalMs = DeviceIF.iTimeinterval;
                }
                catch
                {
                    intervalMs = 0;
                }

                var nowUtc = DateTime.UtcNow;

                if (intervalMs > 0)
                {
                    var elapsedMs = (nowUtc - _lastPollUtc).TotalMilliseconds;
                    if (elapsedMs < intervalMs)
                        return true;
                }

                BacnetLogger.Debug(
                    string.Format("TimeRecv → PollOnce 호출 준비. device_seq={0}", _deviceSeq));

                _pollingService.PollOnce(_station, _deviceSeq);

                _lastPollUtc = nowUtc;
                return true;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("TimeRecv 예외 발생.", ex);
                return false;
            }
        }

        /// <summary>
        /// 메인 소켓 연결 여부 반환 (기존 BAS.dll 호환 유지)
        /// </summary>
        public override bool MainSocketConnect()
        {
            return _connected;
        }

        /// <summary>
        /// DeviceAgent → DLL 데이터 전달 진입점.
        /// - 제어(세미콜론 포함) / 읽기(세미콜론 없음) 모두 처리.
        /// </summary>
        public override int SendData(int sendType, string cData, int dataSize)
        {
            // 디바이스 컨텍스트 설정 (이 스레드에서 찍히는 로그는 전부 이 디바이스로 귀속)
            BacnetLogger.SetCurrentDevice(_deviceSeq);

            try
            {
                // SendData 진입 로그는 호출 빈도가 높으므로 DEBUG로만 (파일 X, 콘솔 O)
                BacnetLogger.Debug(
                    string.Format("SendData 호출. deviceSeq={0}, sendType={1}, DataSize={2}, Data={3}",
                                  _deviceSeq, sendType, dataSize, cData));

                // 1) 방어 코드: 아무 내용도 없으면 할 일이 없음
                if (string.IsNullOrEmpty(cData))
                {
                    BacnetLogger.Warn("SendData: 수신 데이터가 비어 있습니다.");
                    return 0;
                }

                // 2) 제어 / 읽기 구분
                //    - BAS.dll 규칙과 동일: 세미콜론(;)이 포함되어 있으면 "제어(Control)" 패턴으로 간주
                //    - 세미콜론이 없으면 "읽기(Read)" 요청으로 간주
                //
                //    예시)
                //      제어: "AV-1,23.5;BV-3,1;MSV-2,3;"
                //      읽기: "BI-41,BI-40,AO-1,"
                bool isControl = cData.IndexOf(';') >= 0;

                if (isControl)
                {
                    // =====================================================================
                    // [1] 제어(Control) 데이터 처리 영역
                    //     - 형식 예: "AV-1,23.5;BV-3,1;MSV-2,3;"
                    //     - ';' 로 제어 명령 묶음을 자르고,
                    //     - 각 항목마다 "포인트ID,값" 형식으로 파싱해서 TryControlPoint 호출
                    // =====================================================================

                    int controlCount = 0; // 성공한 제어 개수 카운트

                    // 1-1) 세미콜론(;) 기준으로 항목 분리
                    //      cData 예:  "AV-1,23.5;BV-3,1;MSV-2,3;"
                    //      → items: ["AV-1,23.5", "BV-3,1", "MSV-2,3"]
                    string[] items = cData.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (string itemRaw in items)
                    {
                        // 앞뒤 공백 제거
                        string item = itemRaw.Trim();
                        if (item.Length == 0)
                            continue;

                        // 1-2) 각 항목은 "포인트ID,값" 형식
                        //      예: "AV-1,23.5"
                        //          "BV-3,1"
                        //
                        //      그래서 ',' 기준으로 다시 자른다.
                        string[] parts = item.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                        {
                            // 포인트ID와 값 두 개가 없으면 잘못된 형식
                            BacnetLogger.Warn("SendData(Control): 잘못된 형식의 항목: " + item);
                            continue;
                        }

                        // 1-3) parts[0]  → 포인트 ID (예: "AV-1", "BV-3", "MSV-2")
                        //      parts[1]  → 제어 값   (예: "23.5", "1", "3")
                        string ptId = parts[0].Trim();
                        string valStr = parts[1].Trim();

                        // 1-4) 문자열 값 → double 로 변환
                        //      - BACnet 쪽에서 PresentValue 는 수치형으로 처리하므로 double 사용
                        double value;
                        if (!double.TryParse(
                                valStr,
                                NumberStyles.Any,
                                CultureInfo.InvariantCulture,
                                out value))
                        {
                            BacnetLogger.Warn(
                                string.Format("SendData(Control): 값 파싱 실패. pt={0}, raw={1}", ptId, valStr));
                            continue;
                        }

                        // 1-5) BV(Binary Value) 타입인 경우
                        //      - 0 또는 1의 이진 값만 의미가 있으므로
                        //      - 1 이상이면 1, 그 외는 0 으로 강제 보정
                        if (ptId.StartsWith("BV-", StringComparison.OrdinalIgnoreCase))
                        {
                            value = (value >= 1.0) ? 1.0 : 0.0;
                        }

                        // 1-6) 실제 제어 수행
                        //      - TryControlPoint 내부에서:
                        //          * P_OBJECT 캐시(_pointMap)에서 SYSTEM_PT_ID 조회
                        //          * BacnetClientWrapper.TryWritePresentValue 호출
                        //          * 성공 시 INFO 로그 + Realtime 업데이트
                        bool ok = TryControlPoint(ptId, value);
                        if (ok)
                            controlCount++;
                    }

                    // 1-7) 제어 묶음 처리 결과 요약
                    //      - 개별 제어 성공/실패는 TryControlPoint 로그에서 확인 가능
                    //      - 여기서는 "몇 개 요청 중 몇 개 성공했는지" 정도만 INFO로 남김
                    BacnetLogger.Info(
                        string.Format("SendData(Control): 처리 완료. deviceSeq={0}, 성공 개수={1}",
                                      _deviceSeq, controlCount));

                    return controlCount;
                }
                else
                {
                    // =====================================================================
                    // [2] 읽기(Read) 데이터 처리 영역
                    //     - 형식 예: "BI-41,BI-40,AO-1,"
                    //     - 콤마(,)로 포인트 ID만 나열
                    //     - 각 포인트에 대해 RealtimeRepository 스냅샷에서 값을 꺼내서
                    //       "포인트ID,값,0;" 형식의 응답 문자열을 만든 뒤 ToReceive 로 전달
                    // =====================================================================

                    // 2-1) 콤마(,) 기준으로 포인트 ID 분리
                    //
                    //     cData 예: "BI-41,BI-40,AO-1,"
                    //       → Split 결과: ["BI-41", "BI-40", "AO-1"]
                    //
                    //     ※ 마지막에 콤마로 끝나도 RemoveEmptyEntries 옵션 덕분에 빈 문자열은 제거됨.
                    string[] tokens = cData.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var requested = new List<string>();

                    foreach (string raw in tokens)
                    {
                        // 포인트 ID 양 끝 공백 제거 (혹시 모를 공백 방어)
                        string pt = raw.Trim();
                        if (pt.Length == 0)
                            continue;

                        // 실제로 읽기를 요청한 포인트 목록에 추가
                        // 예: "BI-41", "BI-40", "AO-1"
                        requested.Add(pt);
                    }

                    // 요청 포인트가 하나도 없으면 경고만 남기고 종료
                    if (requested.Count == 0)
                    {
                        BacnetLogger.Warn("SendData(Read): 파싱된 포인트가 없습니다. cData=" + cData);
                        return 0;
                    }

                    // 2-2) 실시간 값 조회에 사용할 RealtimeRepository 가 준비되어 있는지 확인
                    if (_realtimeRepo == null)
                    {
                        BacnetLogger.Warn("SendData(Read): RealtimeRepository 가 초기화되지 않았습니다.");
                        return 0;
                    }

                    // 2-3) 특정 디바이스에 대한 "현재 스냅샷"을 한 번에 가져온다.
                    //
                    //     snapshot 예시 타입:
                    //       Dictionary<string, double>
                    //       key   : "BI-41", "AO-1" 같은 SYSTEM_PT_ID
                    //       value : 그 시점의 현재 값 (실수)
                    var snapshot = _realtimeRepo.GetSnapshotByDevice(_deviceSeq);

                    // 2-4) DeviceAgent 에 다시 내려 줄 응답을 문자열로 조립.
                    //
                    //      응답 포맷(기존 BAS.dll 과 동일 가정):
                    //        "포인트ID,값,0;포인트ID,값,0;..."
                    //
                    //      예:
                    //        요청: "BI-41,BI-40,AO-1,"
                    //        응답: "BI-41,1,0;BI-40,0,0;AO-1,23.5,0;"
                    //
                    //      여기서 "0" 은 상태코드/에러코드용 자리로 사용(정상일 때 0).
                    var sb = new StringBuilder();
                    int count = 0;

                    foreach (string pt in requested)
                    {
                        double value;

                        // 2-5) 스냅샷에 값이 없으면 디폴트 실패값 사용
                        //      - 예:  RealtimeConstants.FailValue = -9999 또는 설정값
                        if (!snapshot.TryGetValue(pt, out value))
                            value = RealtimeConstants.FailValue;

                        // 2-6) 하나의 포인트 응답을 "포인트ID,값,0;" 형태로 붙인다.
                        //
                        //      CultureInfo.InvariantCulture 를 쓰는 이유:
                        //        - 소수점 구분자가 ',' 가 아니라 '.' 으로 고정되도록 하기 위해
                        //        - (서버/OS 로케일에 따라 ',' 로 찍히면 CSV 파싱이 꼬이기 때문)
                        sb.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "{0},{1},0;",
                            pt,
                            value);

                        count++;
                    }

                    // 2-7) 최종 응답 문자열
                    string response = sb.ToString();

                    // 읽기 응답은 호출 빈도가 높으므로 DEBUG로만 (파일 X)
                    // 문제 상황이 생기면 이 DEBUG 로그를 보고
                    // "어떤 요청에 어떤 응답을 내렸는지"를 추적할 수 있다.
                    BacnetLogger.Debug(
                        string.Format("SendData(Read): ToReceive 호출. deviceSeq={0}, pointCount={1}, Response={2}",
                                      _deviceSeq, count, response));

                    // 2-8) DeviceAgent로 응답 전송
                    //
                    //    - _deviceSeq : 어느 디바이스에 대한 응답인지
                    //    - sendType   : Agent 쪽에서 구분에 사용하는 타입 값 (BAS.dll 과 호환)
                    //    - response   : 우리가 만든 "포인트ID,값,0;" 묶음
                    //    - count      : 응답에 포함된 포인트 개수
                    ToReceive(_deviceSeq, sendType, response, count);

                    return count;
                }
            }
            catch (Exception ex)
            {
                // 상위에서 잡히지 않은 예외는 ERROR 로 기록하고, -1 리턴해서 Agent 쪽에 실패를 알린다.
                BacnetLogger.Error("SendData 처리 중 예외 발생.", ex);
                return -1;
            }
        }


        /// <summary>
        /// P_OBJECT 기반 포인트 캐시 구축 (SYSTEM_PT_ID -> BacnetPointInfo)
        /// Connect 시 1회 호출.
        /// </summary>
        private void BuildPointCache()
        {
            // 디바이스 컨텍스트 설정 (호출 스레드 기준)
            BacnetLogger.SetCurrentDevice(_deviceSeq);

            try
            {
                if (_objectRepo == null)
                {
                    BacnetLogger.Warn("BuildPointCache: ObjectRepository 가 초기화되지 않았습니다.");
                    _pointMap = new Dictionary<string, BacnetPointInfo>(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                IList<BacnetPointInfo> points = _objectRepo.GetPointsByDeviceSeq(_deviceSeq);
                var dict = new Dictionary<string, BacnetPointInfo>(StringComparer.OrdinalIgnoreCase);

                int skipped = 0;

                foreach (var p in points)
                {
                    if (p == null || string.IsNullOrEmpty(p.SystemPtId))
                    {
                        skipped++;
                        continue;
                    }

                    string key = p.SystemPtId.Trim();

                    // 동일 SYSTEM_PT_ID 가 여러 행이면 마지막 것을 사용
                    dict[key] = p;
                }

                _pointMap = dict;

                BacnetLogger.Info(
                    string.Format("BuildPointCache 완료. deviceSeq={0}, count={1}, skipped={2}",
                                  _deviceSeq, dict.Count, skipped));
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("BuildPointCache 예외 발생.", ex);
                _pointMap = new Dictionary<string, BacnetPointInfo>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// SYSTEM_PT_ID -> BacnetPointInfo 캐시 조회.
        /// </summary>
        private bool TryGetPointInfo(string systemPtId, out BacnetPointInfo info)
        {
            info = null;

            if (string.IsNullOrEmpty(systemPtId))
                return false;

            if (_pointMap == null)
                return false;

            string key = systemPtId.Trim();
            return _pointMap.TryGetValue(key, out info);
        }

        /// <summary>
        /// 단일 포인트 제어 (BV/AV/MSV 포함).
        /// systemPtId: "AV-1", "BV-3", "MSV-2" 등
        ///
        /// - 이 함수 하나가 "실제 BACnet 장비에 값을 쓰는 진입점"
        /// - DRY-RUN 모드일 때는 실제 장비로 패킷을 보내지 않고,
        ///   "성공했다고 가정"하고 로그 + Realtime 업데이트까지만 수행한다.
        /// </summary>
        private bool TryControlPoint(string systemPtId, double value)
        {
            // 디바이스 컨텍스트 설정
            BacnetLogger.SetCurrentDevice(_deviceSeq);

            // 0) 기본 환경 체크: Station/Client 가 준비 안 되었으면 제어 불가
            if (_station == null || _client == null)
            {
                BacnetLogger.Warn("TryControlPoint: Station/Client 가 초기화되지 않았습니다.");
                return false;
            }

            try
            {
                // 1) SYSTEM_PT_ID → BacnetPointInfo 매핑 조회
                //
                //    - systemPtId 예: "AV-1", "BV-3", "MSV-2"
                //    - _pointMap 은 BuildPointCache()에서
                //      P_OBJECT 테이블 기반으로 미리 채워놓은 캐시
                //
                //    이 정보를 통해:
                //      - Bacnet 객체 타입 (AnalogValue, BinaryValue, MultiStateValue 등)
                //      - Instance 번호
                //    를 알 수 있다.
                BacnetPointInfo ptInfo;
                if (!TryGetPointInfo(systemPtId, out ptInfo))
                {
                    BacnetLogger.Warn(
                        string.Format("TryControlPoint: SYSTEM_PT_ID={0} 에 해당하는 포인트를 찾지 못했습니다.", systemPtId));
                    return false;
                }

                // 2) 여기서부터 "실제 장비까지 제어할지, DRY-RUN으로만 로그 찍고 끝낼지" 갈림
                //
                //    _dryRun == true:
                //      - 실제 BacnetClientWrapper.TryWritePresentValue(...) 호출하지 않는다.
                //      - 대신 "DRY-RUN: 실제 제어는 생략" 이라는 로그만 남긴다.
                //
                //    _dryRun == false:
                //      - 실제 BACnet Write 실행
                //      - 실패 시 WARN 로그, 성공 시 INFO 로그
                bool ok = false;
                string error = null;

                if (_dryRun)
                {
                    // ============================
                    // [DRY-RUN 모드]
                    // ============================
                    //
                    // - 실제 장비에는 아무것도 보내지 않는다.
                    // - 운영자 입장에서는 "어떤 포인트에 어떤 값이 들어갈 예정인지" 로그로만 확인 가능.
                    // - 테스트/검증 단계에서 실장비를 건드리지 않고 흐름만 검증할 때 사용.
                    BacnetLogger.Info(
                        string.Format(
                            "TryControlPoint[DRY-RUN]: pt={0}, value={1}, type={2}, instance={3}",
                            systemPtId,
                            value.ToString(CultureInfo.InvariantCulture),
                            ptInfo.BacnetType,
                            ptInfo.Instance));

                    // DRY-RUN 에서는 "성공했다고 가정"한다.
                    ok = true;
                }
                else
                {
                    // ============================
                    // [실제 제어 모드]
                    // ============================
                    //
                    // - BacnetClientWrapper 를 통해 BACnet 네트워크로 WritePresentValue 전송.
                    // - 내부적으로는 네이티브 BACnet 스택/라이브러리를 감싼 래퍼일 가능성이 높음.
                    ok = _client.TryWritePresentValue(
                        _station,
                        ptInfo.BacnetType,
                        ptInfo.Instance,
                        value,
                        out error);

                    if (!ok)
                    {
                        // 실패한 경우: WARN 수준으로 남긴다.
                        // - 운영 시 "어느 포인트 제어가 왜 실패했는지"를 추적할 수 있도록,
                        //   포인트ID / 값 / 에러 메시지를 모두 기록.
                        BacnetLogger.Warn(
                            string.Format("TryControlPoint: 제어 실패. pt={0}, value={1}, error={2}",
                                          systemPtId,
                                          value.ToString(CultureInfo.InvariantCulture),
                                          error ?? "(null)"));
                    }
                    else
                    {
                        // 성공한 경우: INFO 로그로 남김.
                        BacnetLogger.Info(
                            string.Format("TryControlPoint: 제어 성공. pt={0}, value={1}",
                                          systemPtId,
                                          value.ToString(CultureInfo.InvariantCulture)));
                    }
                }

                // 3) 제어 성공(or DRY-RUN 가정 성공) 시 RealtimeRepository 업데이트
                //
                //    - DRY-RUN 이든 아니든, "논리적으로는 이 값이 들어갔다"고 보는 게 맞기 때문에
                //      실시간 테이블에는 값을 반영해둔다.
                //    - 이렇게 하면 DRY-RUN 상태에서도
                //      SendData(Read) → GetSnapshotByDevice 에서
                //      "제어 후 값이 반영된 것처럼" 읽어볼 수 있다.
                if (ok)
                {
                    try
                    {
                        if (_realtimeRepo != null)
                        {
                            _realtimeRepo.UpsertRealtime(
                                _deviceSeq,
                                systemPtId,
                                value,
                                RealtimeConstants.QualityGood,
                                DateTime.Now,
                                null);
                        }
                    }
                    catch (Exception ex2)
                    {
                        // Realtime 반영 실패는 제어 자체 실패와는 별개이므로
                        // ERROR 로그만 남기고 흐름은 유지한다.
                        BacnetLogger.Error("TryControlPoint: Realtime 업데이트 중 예외.", ex2);
                    }
                }

                return ok;
            }
            catch (Exception ex)
            {
                // 4) 예외 발생 시: ERROR 로그 남기고 false 반환
                BacnetLogger.Error(
                    string.Format("TryControlPoint: 예외 발생. pt={0}, value={1}",
                                  systemPtId,
                                  value.ToString(CultureInfo.InvariantCulture)),
                    ex);
                return false;
            }
        }

    }
}
