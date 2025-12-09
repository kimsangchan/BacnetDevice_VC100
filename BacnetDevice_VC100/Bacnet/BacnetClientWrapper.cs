// Bacnet/BacnetClientWrapper.cs
using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// <summary>
        /// 특정 오브젝트의 PresentValue를 쓰기 (BV/AV/MSV 등 공통).
        /// - AV/AI/AO 계열: Real 실수로 기록
        /// - BV/BO/MSV/MSO 계열: Enumerated(정수)로 기록
        /// </summary>
        public bool TryWritePresentValue(
            StationConfig station,
            BacnetObjectTypes objectType,
            uint instance,
            object rawValue,
            out string errorMessage,
            int timeoutMs = 3000)
        {
            errorMessage = null;

            if (!_started)
            {
                errorMessage = "CLIENT_NOT_STARTED";
                Console.WriteLine("[BACNET][WARN] Client not started. Call Start() first.");
                return false;
            }

            // JACE 주소 구성 (IP:Port)
            var addr = new BacnetAddress(
                BacnetAddressTypes.IP,
                station.Ip + ":" + station.Port);

            var objId = new BacnetObjectId(objectType, instance);

            try
            {
                BacnetValue bacnetValue;

                // BV/BO/MSV/MSO 계열은 Enumerated 로 처리
                bool isEnumType =
                    objectType == BacnetObjectTypes.OBJECT_BINARY_VALUE ||
                    objectType == BacnetObjectTypes.OBJECT_BINARY_OUTPUT ||
                    objectType == BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE ||
                    objectType == BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT;

                if (isEnumType)
                {
                    uint enumVal = Convert.ToUInt32(rawValue, CultureInfo.InvariantCulture);
                    bacnetValue = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, enumVal);

                }
                else
                {
                    // 나머지는 실수 Real 로
                    double d = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
                    bacnetValue = new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, d);
                }

                IList<BacnetValue> values = new BacnetValue[] { bacnetValue };

                _client.WritePropertyRequest(
                    addr,
                    objId,
                    BacnetPropertyIds.PROP_PRESENT_VALUE,
                    values);

                Console.WriteLine(
                    "[BACNET] Write success. ObjType={0}, Inst={1}, Value={2}",
                    objectType, instance, rawValue);

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                Console.WriteLine("[BACNET][ERROR] Exception while writing: " + ex.Message);
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
