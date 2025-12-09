using System;
using BacnetDevice_VC100.Bacnet;
using BacnetDevice_VC100.DataAccess;
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

        public override int SendData(int sendType, string cData, int DataSize)
        {
            try
            {
                BacnetLogger.Info(
                    string.Format("SendData 호출. sendType={0}, DataSize={1}, Data={2}",
                        sendType, DataSize, cData));

                // TODO: 나중에 BACnet WriteProperty 구현 예정
                return 0;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("SendData 예외 발생.", ex);
                return -1;
            }
        }
    }
}
