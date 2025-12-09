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
                    string.Format("SendData 호출. sendType={0}, DataSize={1}, Data={2}", sendType, dataSize, cData));

                if (string.IsNullOrEmpty(cData))
                {
                    BacnetLogger.Warn("SendData: 수신 데이터가 비어 있습니다.");
                    return 0;
                }

                // 1) 서버에서 요청한 포인트 리스트 파싱 (예: "BI-41,BI-40,AI-0,...")
                string[] tokens = cData.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var requested = new List<string>();

                foreach (string raw in tokens)
                {
                    string pt = raw.Trim();
                    if (pt.Length == 0)
                        continue;

                    // 형식 검사는 느슨하게, BI-41 / AI-0 / AO-3 ... 모두 허용
                    requested.Add(pt);
                }

                if (requested.Count == 0)
                {
                    BacnetLogger.Warn("SendData: 파싱된 포인트가 없습니다. cData=" + cData);
                    return 0;
                }

                // 2) DB에서 현 시점 스냅샷 가져오기
                var rtRepo = new RealtimeRepository();
                var snapshot = rtRepo.GetSnapshotByDevice(_deviceSeq); // _deviceSeq는 Init/Connect 때 저장해둔 값

                // 3) 응답 문자열 구성: "ID,VALUE,0;" 형태
                //    기존 BAS DLL 포맷과 동일 (예: "AI-0,23.4,0;BI-1,0,0;...")
                var sb = new StringBuilder();
                int count = 0;

                foreach (string pt in requested)
                {
                    double value;

                    if (!snapshot.TryGetValue(pt, out value))
                    {
                        // 실시간 테이블에 값이 없으면 FailValue로 리턴
                        value = RealtimeConstants.FailValue;
                    }

                    sb.AppendFormat(
                        CultureInfo.InvariantCulture,
                        "{0},{1},0;",
                        pt,
                        value
                    );

                    count++;
                }

                string response = sb.ToString();

                BacnetLogger.Info(
                    string.Format("SendData: ToReceive 호출. deviceSeq={0}, pointCount={1}, Response={2}",
                                  _deviceSeq, count, response));

                // 4) AGENT(메인 프로그램) 쪽으로 응답 푸시
                //  - iRecvType은 일단 sendType 그대로 전달 (MainForm/ServerPacketAgent에서는 타입은 안 씀)
                //  - iObjCount에는 포인트 개수
                ToReceive(_deviceSeq, sendType, response, count);

                return count;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("SendData 처리 중 예외 발생.", ex);
                return -1;
            }
        }


    }
}
