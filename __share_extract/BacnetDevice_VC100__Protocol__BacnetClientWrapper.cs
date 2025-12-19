using System;
using System.Collections.Generic;
using System.IO.BACnet;
using BacnetDevice_VC100.Model;
using BacnetDevice_VC100.Util;

namespace BacnetDevice_VC100.Protocol
{
    /// <summary>
    /// BACnet 클라이언트 래퍼
    /// 
    /// [데이터 흐름]
    /// BacnetBAS.Init()
    ///   ↓ new BacnetClientWrapper(_logger)
    ///   ↓ Connect("172.16.130.98", 47808)
    ///     ↓ new BacnetClient(47808)
    ///     ↓ _client.Start() → UDP 소켓 열기
    ///     ↓ _deviceAddress = "172.16.130.98:47808"
    ///     ↓ IsConnected = true
    ///   ↓
    /// BacnetBAS.PollingLoop()
    ///   ↓ ReadPresentValue(deviceInstance, objectType, objectInstance)
    ///     ↓ BACnet Read Request → 장비
    ///     ↓ 장비 응답 (Present Value)
    ///     ↓ return 23.5f
    ///   ↓
    /// Agent로 전송
    /// </summary>
    public class BacnetClientWrapper : IBacnetClient
    {
        private readonly BacnetLogger _logger;
        private BacnetClient _client;
        private BacnetAddress _deviceAddress;
        private bool _isConnected;

        /// <summary>
        /// 생성자
        /// 
        /// [데이터 흐름]
        /// new BacnetClientWrapper(logger)
        ///   ↓ _logger = logger
        ///   ↓ _client = null
        ///   ↓ _deviceAddress = null
        ///   ↓ _isConnected = false
        /// </summary>
        public BacnetClientWrapper(BacnetLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 연결 상태 프로퍼티
        /// 
        /// [데이터 흐름]
        /// if (_client.IsConnected)
        ///   ↓ get { return _isConnected; }
        ///   ↓ true/false
        /// </summary>
        public bool IsConnected
        {
            get { return _isConnected; }
        }

        /// <summary>
        /// BACnet 연결
        /// 
        /// [데이터 흐름]
        /// Connect("172.16.130.98", 47808)
        ///   ↓ _logger.Info("연결 시도: 172.16.130.98:47808")
        ///   ↓ new BacnetClient(47808)
        ///     ↓ UDP 소켓 생성 (포트: 47808)
        ///   ↓ _client.Start()
        ///     ↓ UDP 소켓 Bind 및 수신 시작
        ///   ↓ _deviceAddress = new BacnetAddress(IP, "172.16.130.98:47808")
        ///     ↓ Type = BacnetAddressTypes.IP
        ///     ↓ Address = "172.16.130.98:47808"
        ///   ↓ _isConnected = true
        ///   ↓ _logger.Info("연결 성공")
        ///   ↓ return true
        /// </summary>
        public bool Connect(string deviceIp, int port)
        {
            try
            {
                _logger.Info($"BACnet 연결 시도: {deviceIp}:{port}");

                _client = new BacnetClient(port);
                _client.Start();

                _deviceAddress = new BacnetAddress(
                    BacnetAddressTypes.IP,
                    $"{deviceIp}:{port}"
                );

                _isConnected = true;
                _logger.Info($"BACnet 연결 성공: {deviceIp}:{port}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"BACnet 연결 실패: {deviceIp}:{port}", ex);
                return false;
            }
        }

        /// <summary>
        /// BACnet 연결 해제
        /// 
        /// [데이터 흐름]
        /// Disconnect()
        ///   ↓ _client != null?
        ///   ↓ _client.Dispose()
        ///     ↓ UDP 소켓 닫기
        ///     ↓ 리소스 해제
        ///   ↓ _client = null
        ///   ↓ _isConnected = false
        ///   ↓ _logger.Info("연결 해제")
        /// </summary>
        public bool Disconnect()
        {
            try
            {
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

                _isConnected = false;
                _logger.Info("BACnet 연결 해제");

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("BACnet 연결 해제 실패", ex);
                return false;
            }
        }

        /// <summary>
        /// BACnet Present Value 읽기 (float? 반환)
        /// 
        /// [데이터 흐름]
        /// ReadPresentValue(20059, ANALOG_VALUE, 101)
        ///   ↓ deviceInstance = 20059
        ///   ↓ objectType = ANALOG_VALUE
        ///   ↓ objectInstance = 101
        ///   ↓
        /// _isConnected? → false → return null
        ///   ↓ true
        ///   ↓ new BacnetObjectId(ANALOG_VALUE, 101)
        ///   ↓ _client.ReadPropertyRequest()
        ///     ↓ BACnet Read Request 패킷 생성
        ///       ↓ Device: _deviceAddress
        ///       ↓ Object: ANALOG_VALUE:101
        ///       ↓ Property: PRESENT_VALUE
        ///     ↓ UDP 패킷 전송 → 장비
        ///     ↓ 장비 응답 대기
        ///     ↓ 응답 패킷 수신
        ///       ↓ values[0].Value = 23.5
        ///   ↓ Convert.ToSingle(23.5)
        ///   ↓ _logger.Info("읽기 성공: AV:101 = 23.5")
        ///   ↓ return 23.5f
        /// 
        /// [반환]
        /// 성공: float? = 23.5f
        /// 실패: float? = null
        /// </summary>
        public float? ReadPresentValue(uint deviceInstance, BacnetObjectTypes objectType, uint objectInstance)
        {
            if (!_isConnected)
            {
                _logger.Warning($"읽기 실패 (미연결): {objectType}:{objectInstance}");
                return null;
            }

            try
            {
                BacnetObjectId objectId = new BacnetObjectId(objectType, objectInstance);

                IList<BacnetValue> values;
                bool success = _client.ReadPropertyRequest(
                    _deviceAddress,
                    objectId,
                    BacnetPropertyIds.PROP_PRESENT_VALUE,
                    out values
                );

                if (success && values != null && values.Count > 0)
                {
                    // object → float 변환
                    float result = Convert.ToSingle(values[0].Value);
                    _logger.Info($"읽기 성공: {objectType}:{objectInstance} = {result}");
                    return result;
                }

                _logger.Warning($"읽기 실패 (값 없음): {objectType}:{objectInstance}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"읽기 에러: {objectType}:{objectInstance}", ex);
                return null;
            }
        }

        /// <summary>
        /// BACnet Present Value 쓰기
        /// 
        /// [데이터 흐름]
        /// WritePresentValue(20059, ANALOG_VALUE, 101, 25.0f, 8)
        ///   ↓ deviceInstance = 20059
        ///   ↓ objectType = ANALOG_VALUE
        ///   ↓ objectInstance = 101
        ///   ↓ value = 25.0f
        ///   ↓ priority = 8 (기본값)
        ///   ↓
        /// _isConnected? → false → return false
        ///   ↓ true
        ///   ↓ new BacnetObjectId(ANALOG_VALUE, 101)
        ///   ↓ new BacnetValue(REAL, 25.0)
        ///   ↓ _client.WritePropertyRequest()
        ///     ↓ BACnet Write Request 패킷
        ///       ↓ Device: _deviceAddress
        ///       ↓ Object: ANALOG_VALUE:101
        ///       ↓ Property: PRESENT_VALUE
        ///       ↓ Value: 25.0
        ///       ↓ Priority: 8
        ///     ↓ UDP 패킷 전송 → 장비
        ///     ↓ 성공 응답 수신
        ///   ↓ _logger.Info("쓰기 성공: AV:101 = 25.0")
        ///   ↓ return true
        /// </summary>
        public bool WritePresentValue(uint deviceInstance, BacnetObjectTypes objectType, uint objectInstance, float value, byte priority = 8)
        {
            if (!_isConnected)
            {
                _logger.Warning($"쓰기 실패 (미연결): {objectType}:{objectInstance}");
                return false;
            }

            try
            {
                BacnetObjectId objectId = new BacnetObjectId(objectType, objectInstance);

                BacnetValue bacnetValue = new BacnetValue(
                    BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL,
                    value
                );

                bool success = _client.WritePropertyRequest(
                    _deviceAddress,
                    objectId,
                    BacnetPropertyIds.PROP_PRESENT_VALUE,
                    new[] { bacnetValue }
                );

                if (success)
                {
                    _logger.Info($"쓰기 성공: {objectType}:{objectInstance} = {value}");
                    return true;
                }

                _logger.Warning($"쓰기 실패: {objectType}:{objectInstance}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error($"쓰기 에러: {objectType}:{objectInstance}", ex);
                return false;
            }
        }

        /// <summary>
        /// 다중 포인트 읽기 (Dictionary<string, float?> 반환)
        /// 
        /// [데이터 흐름]
        /// ReadMultiple(points)
        ///   ↓ points = [point1(AV-101), point2(AV-102), point3(BI-39), ...]
        ///   ↓ results = new Dictionary<string, float?>()
        ///   ↓
        /// _isConnected? → false → return new Dictionary()
        ///   ↓ true
        ///   ↓
        /// foreach (point in points)
        ///   ↓ ReadPresentValue(point.DeviceInstance, point.ObjectType, point.ObjectInstance)
        ///     ↓ 성공 → 23.5f
        ///     ↓ results["AV-101"] = 23.5f
        ///   ↓ ReadPresentValue(...)
        ///     ↓ 성공 → 24.3f
        ///     ↓ results["AV-102"] = 24.3f
        ///   ↓ ReadPresentValue(...)
        ///     ↓ 실패 → null
        ///     ↓ results["BI-39"] = null
        ///   ↓
        /// _logger.Info("다중 읽기 완료: 2/3 성공")
        /// return results
        /// 
        /// [결과]
        /// Dictionary<string, float?> = {
        ///   "AV-101": 23.5f,
        ///   "AV-102": 24.3f,
        ///   "BI-39": null
        /// }
        /// </summary>
        public Dictionary<string, float?> ReadMultiple(List<BacnetPoint> points)
        {
            var results = new Dictionary<string, float?>();

            if (!_isConnected)
            {
                _logger.Warning("다중 읽기 실패 (미연결)");
                return results;
            }

            try
            {
                int successCount = 0;

                foreach (var point in points)
                {
                    float? value = ReadPresentValue(point.DeviceInstance, point.ObjectType, point.ObjectInstance);
                    results[point.SystemPtId] = value;

                    if (value.HasValue)
                    {
                        successCount++;
                    }
                }

                _logger.Info($"다중 읽기 완료: {successCount}/{points.Count} 성공");
                return results;
            }
            catch (Exception ex)
            {
                _logger.Error("다중 읽기 에러", ex);
                return results;
            }
        }

        /// <summary>
        /// 리소스 해제
        /// 
        /// [데이터 흐름]
        /// Dispose()
        ///   ↓ Disconnect()
        ///     ↓ _client.Dispose()
        ///     ↓ UDP 소켓 닫기
        ///   ↓ 리소스 정리 완료
        /// </summary>
        public void Dispose()
        {
            Disconnect();
        }
    }
}
