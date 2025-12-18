using System;
using System.Collections.Generic;
using System.IO.BACnet;  // ← 이 줄 추가!
using BacnetDevice_VC100.Model;

namespace BacnetDevice_VC100.Protocol
{
    /// <summary>
    /// BACnet 통신 인터페이스
    /// - 테스트 가능하도록 인터페이스 분리
    /// - Mock/Stub 구현 가능
    /// </summary>
    public interface IBacnetClient : IDisposable
    {
        /// <summary>
        /// BACnet 연결 초기화
        /// </summary>
        bool Connect(string deviceIp, int port);

        /// <summary>
        /// 연결 상태 확인
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Present Value 읽기 (단일 포인트)
        /// </summary>
        /// <returns>성공 시 값, 실패 시 null</returns>
        float? ReadPresentValue(uint deviceInstance, BacnetObjectTypes objectType, uint objectInstance);

        /// <summary>
        /// Present Value 쓰기 (제어)
        /// </summary>
        bool WritePresentValue(uint deviceInstance, BacnetObjectTypes objectType, uint objectInstance, float value, byte priority = 8);

        /// <summary>
        /// 멀티 포인트 읽기 (ReadPropertyMultiple)
        /// </summary>
        Dictionary<string, float?> ReadMultiple(List<BacnetPoint> points);
    }
}
