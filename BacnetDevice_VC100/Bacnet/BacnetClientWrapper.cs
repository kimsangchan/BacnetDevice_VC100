// Bacnet/BacnetClientWrapper.cs
using System;
using System.Collections.Generic;
using System.IO.BACnet;

namespace BacnetDevice_VC100.Bacnet
{
    /// <summary>
    /// BACnet/IP 클라이언트 래퍼.
    /// - JACE(172.16.130.98:47808 등)에 붙어서 특정 Object PresentValue 읽기
    /// - 예외/타임아웃/로그 처리 포함
    /// </summary>
    public class BacnetClientWrapper : IDisposable
    {
        private BacnetClient _client;
        private bool _started;

        public BacnetClientWrapper()
        {
            // 0xBAC0 = 기본 BACnet/IP 포트(47808)
            // 여기선 로컬 포트는 기본값 쓰고, BroadcastDisabled=false
            var transport = new BacnetIpUdpProtocolTransport(0xBAC0, false);
            _client = new BacnetClient(transport);
        }

        /// <summary>
        /// BACnet 클라이언트 시작 (소켓 오픈).
        /// 여러 번 호출해도 한 번만 시작되도록 처리.
        /// </summary>
        public void Start()
        {
            if (_started) return;

            try
            {
                Console.WriteLine("[BACNET] Starting client...");
                _client.Start();
                _started = true;
                Console.WriteLine("[BACNET] Client started.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BACNET][ERROR] Failed to start client: " + ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 대상 Station(Device)에 대한 PresentValue 읽기.
        /// objectType / instance는 지금은 직접 넘긴다.
        /// </summary>
        public bool TryReadPresentValue(
    StationConfig station,
    BacnetObjectTypes objectType,
    uint instance,
    out object value,
    int timeoutMs = 3000)
        {
            value = null;

            if (!_started)
            {
                Console.WriteLine("[BACNET][WARN] Client not started. Call Start() first.");
                return false;
            }

            var addr = new BacnetAddress(
                BacnetAddressTypes.IP,
                station.Ip + ":" + station.Port);

            var objId = new BacnetObjectId(objectType, instance);

            try
            {
                Console.WriteLine(
                    "[BACNET] ReadPropertyRequest: Station={0}, DeviceId={1}, Obj={2}-{3}",
                    station.Id, station.DeviceId, objectType, instance);

                IList<BacnetValue> values;
                var ok = _client.ReadPropertyRequest(
                    addr,
                    objId,
                    BacnetPropertyIds.PROP_PRESENT_VALUE,
                    out values);

                if (!ok || values == null || values.Count == 0)
                {
                    Console.WriteLine("[BACNET][WARN] ReadPropertyRequest failed or empty result.");
                    return false;
                }

                var first = values[0];
                value = first.Value;

                Console.WriteLine("[BACNET] Read success. Value = " + value);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BACNET][ERROR] Exception while reading: " + ex.Message);
                return false;
            }
        }


        public void Dispose()
        {
            try
            {
                if (_client != null)
                {
                    Console.WriteLine("[BACNET] Stopping client...");
                    _client.Dispose();
                    _client = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BACNET][ERROR] Dispose failed: " + ex.Message);
            }
        }
    }
}
