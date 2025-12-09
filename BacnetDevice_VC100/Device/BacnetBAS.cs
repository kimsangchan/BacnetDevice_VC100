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
    /// </summary>
    public class BacnetBAS : CShapeDeviceBase
    {
        private StationConfig _station;
        private BacnetClientWrapper _client;
        private ObjectRepository _objectRepo;
        private RealtimeRepository _realtimeRepo;
        private PollingService _pollingService;
        // BacnetBAS 클래스 내부 상단에 추가
        private Dictionary<string, BacnetPointInfo> _pointMap;

        private bool _connected;
        private int _deviceSeq;
        private DateTime _lastPollUtc;

        public override bool DeviceLoad()
        {
            BacnetLogger.Info("DeviceLoad 호출됨.");
            return true;
        }

        public override bool Init(int iDeviceID)
        {
            try
            {
                BacnetLogger.Info(string.Format("Init 시작. DeviceID={0}", iDeviceID));

                DeviceID = iDeviceID;
                _deviceSeq = iDeviceID;
                _lastPollUtc = DateTime.MinValue;

                BacnetLogger.Info(string.Format("Init 완료. device_seq={0}", _deviceSeq));
                return true;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("Init 예외 발생.", ex);
                return false;
            }
        }

        public override bool Connect(string sIP, int iPort)
        {
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

                // ★ 여기 추가: 포인트 캐시 빌드
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

        public override bool DisConnect()
        {
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

                BacnetLogger.Info("DisConnect 완료.");
                return true;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("DisConnect 예외 발생.", ex);
                return false;
            }
        }

        public override bool TimeRecv()
        {
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

                BacnetLogger.Info(
                    string.Format("TimeRecv → PollOnce 호출. device_seq={0}", _deviceSeq));

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

        public override bool MainSocketConnect()
        {
            return _connected;
        }

        public override int SendData(int sendType, string cData, int dataSize)
        {
            try
            {
                BacnetLogger.Info(
                    string.Format("SendData 호출. sendType={0}, DataSize={1}, Data={2}",
                                  sendType, dataSize, cData));

                if (string.IsNullOrEmpty(cData))
                {
                    BacnetLogger.Warn("SendData: 수신 데이터가 비어 있습니다.");
                    return 0;
                }

                // ✔ BAS.dll 과 동일하게: 세미콜론이 있으면 제어, 아니면 읽기
                bool isControl = cData.IndexOf(';') >= 0;

                if (isControl)
                {
                    // ============================
                    // 제어 처리 (BV/AV/MSV 등)
                    // ============================
                    int controlCount = 0;

                    string[] items = cData.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (string itemRaw in items)
                    {
                        string item = itemRaw.Trim();
                        if (item.Length == 0)
                            continue;

                        string[] parts = item.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2)
                        {
                            BacnetLogger.Warn("SendData(Control): 잘못된 형식의 항목: " + item);
                            continue;
                        }

                        string ptId = parts[0].Trim();   // "AV-1", "BV-3", "MSV-2" ...
                        string valStr = parts[1].Trim();   // "23.5", "1", "3" ...

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

                        if (ptId.StartsWith("BV-", StringComparison.OrdinalIgnoreCase))
                        {
                            value = (value >= 1.0) ? 1.0 : 0.0;
                        }

                        bool ok = TryControlPoint(ptId, value);
                        if (ok)
                            controlCount++;
                    }

                    BacnetLogger.Info(
                        string.Format("SendData(Control): 처리 완료. 성공 개수 = {0}", controlCount));

                    return controlCount;
                }
                else
                {
                    // ============================
                    // 읽기 요청 처리 (스냅샷 응답)
                    // ============================
                    // cData 예: "BI-41,BI-40,...,AO-1,"
                    string[] tokens = cData.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var requested = new List<string>();

                    foreach (string raw in tokens)
                    {
                        string pt = raw.Trim();
                        if (pt.Length == 0)
                            continue;

                        requested.Add(pt);
                    }

                    if (requested.Count == 0)
                    {
                        BacnetLogger.Warn("SendData(Read): 파싱된 포인트가 없습니다. cData=" + cData);
                        return 0;
                    }

                    if (_realtimeRepo == null)
                    {
                        BacnetLogger.Warn("SendData(Read): RealtimeRepository 가 초기화되지 않았습니다.");
                        return 0;
                    }

                    var snapshot = _realtimeRepo.GetSnapshotByDevice(_deviceSeq);

                    var sb = new StringBuilder();
                    int count = 0;

                    foreach (string pt in requested)
                    {
                        double value;

                        if (!snapshot.TryGetValue(pt, out value))
                            value = RealtimeConstants.FailValue;

                        sb.AppendFormat(
                            CultureInfo.InvariantCulture,
                            "{0},{1},0;",
                            pt,
                            value);

                        count++;
                    }

                    string response = sb.ToString();

                    BacnetLogger.Info(
                        string.Format("SendData(Read): ToReceive 호출. deviceSeq={0}, pointCount={1}, Response={2}",
                                      _deviceSeq, count, response));

                    ToReceive(_deviceSeq, sendType, response, count);

                    return count;
                }
            }
            catch (Exception ex)
            {
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
        /// </summary>
        private bool TryControlPoint(string systemPtId, double value)
        {
            if (_station == null || _client == null)
            {
                BacnetLogger.Warn("TryControlPoint: Station/Client 가 초기화되지 않았습니다.");
                return false;
            }

            try
            {
                BacnetPointInfo ptInfo;
                if (!TryGetPointInfo(systemPtId, out ptInfo))
                {
                    BacnetLogger.Warn(
                        string.Format("TryControlPoint: SYSTEM_PT_ID={0} 에 해당하는 포인트를 찾지 못했습니다.", systemPtId));
                    return false;
                }

                string error;
                bool ok = _client.TryWritePresentValue(
                    _station,
                    ptInfo.BacnetType,
                    ptInfo.Instance,
                    value,
                    out error);

                if (!ok)
                {
                    BacnetLogger.Warn(
                        string.Format("TryControlPoint: 제어 실패. pt={0}, value={1}, error={2}",
                                      systemPtId,
                                      value.ToString(CultureInfo.InvariantCulture),
                                      error ?? "(null)"));
                }
                else
                {
                    BacnetLogger.Info(
                        string.Format("TryControlPoint: 제어 성공. pt={0}, value={1}",
                                      systemPtId,
                                      value.ToString(CultureInfo.InvariantCulture)));

                    // 제어 후 실시간 테이블 동기화 (실패해도 제어 자체는 성공)
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
                        BacnetLogger.Error("TryControlPoint: Realtime 업데이트 중 예외.", ex2);
                    }

                }

                return ok;
            }
            catch (Exception ex)
            {
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
