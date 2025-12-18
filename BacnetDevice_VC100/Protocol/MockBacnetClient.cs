using System;
using System.Collections.Generic;
using System.IO.BACnet;  // ← 이 줄 추가!
using BacnetDevice_VC100.Model;

namespace BacnetDevice_VC100.Protocol
{
    /// <summary>
    /// Mock BACnet Client (테스트용)
    /// - 실제 장비 없이 로직 테스트
    /// - 개발 PC에서 실행 가능
    /// - 랜덤 값 반환
    /// </summary>
    public class MockBacnetClient : IBacnetClient
    {
        private bool _isConnected;
        private Random _random = new Random();

        public bool IsConnected => _isConnected;

        /// <summary>
        /// 가짜 연결 (항상 성공)
        /// </summary>
        public bool Connect(string deviceIp, int port)
        {
            Console.WriteLine($"[Mock] Connecting to {deviceIp}:{port}...");
            System.Threading.Thread.Sleep(500); // 연결 시뮬레이션
            _isConnected = true;
            Console.WriteLine("[Mock] Connected!");
            return true;
        }

        /// <summary>
        /// 가짜 Read (랜덤 값 반환)
        /// </summary>
        public float? ReadPresentValue(uint deviceInstance, BacnetObjectTypes objectType, uint objectInstance)
        {
            if (!_isConnected)
                return null;

            // 랜덤 값 생성 (20.0 ~ 30.0)
            float randomValue = 20.0f + (float)(_random.NextDouble() * 10.0);

            Console.WriteLine($"[Mock] Read {objectType}-{objectInstance}: {randomValue:F2}");
            System.Threading.Thread.Sleep(100); // Read 지연 시뮬레이션

            return randomValue;
        }

        /// <summary>
        /// 가짜 Write (항상 성공)
        /// </summary>
        public bool WritePresentValue(uint deviceInstance, BacnetObjectTypes objectType, uint objectInstance, float value, byte priority = 8)
        {
            if (!_isConnected)
                return false;

            Console.WriteLine($"[Mock] Write {objectType}-{objectInstance} = {value} (Priority: {priority})");
            System.Threading.Thread.Sleep(200); // Write 지연 시뮬레이션

            return true;
        }

        /// <summary>
        /// 가짜 멀티 Read
        /// </summary>
        public Dictionary<string, float?> ReadMultiple(List<BacnetPoint> points)
        {
            var results = new Dictionary<string, float?>();

            foreach (var point in points)
            {
                var value = ReadPresentValue(point.DeviceInstance, point.ObjectType, point.ObjectInstance);
                results[point.SystemPtId] = value;
            }

            return results;
        }

        public void Dispose()
        {
            Console.WriteLine("[Mock] Disconnecting...");
            _isConnected = false;
        }
    }
}
