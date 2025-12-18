using System.IO.BACnet;  // ← 이 줄 추가!

namespace BacnetDevice_VC100.Model
{
    /// <summary>
    /// BACnet 포인트 정의
    /// - DB: P_OBJECT 테이블에서 로딩
    /// - 각 포인트의 BACnet 속성 + 폴링 설정
    /// </summary>
    public class BacnetPoint
    {
        // === 식별 정보 ===
        public int DeviceSeq { get; set; }              // Device ID
        public string SystemPtId { get; set; }          // SI 포인트 ID (예: "AV-101")

        // === BACnet 속성 ===
        public uint DeviceInstance { get; set; }        // BACnet Device Instance
        public BacnetObjectTypes ObjectType { get; set; } // AI/AO/AV/BI/BO/BV
        public uint ObjectInstance { get; set; }        // Object Instance Number

        // === 설정 ===
        public bool IsWritable { get; set; }            // 제어 가능 여부
        public bool EnablePolling { get; set; }         // 폴링 활성화
        public int PollingInterval { get; set; }        // 폴링 주기 (초)

        // === 품질 관리 ===
        public float FailValue { get; set; }            // 통신 실패 시 기본값
        public int TimeoutMs { get; set; }              // 타임아웃 (ms)

        public BacnetPoint()
        {
            TimeoutMs = 5000;
            FailValue = 0.0f;
            EnablePolling = true;
            PollingInterval = 30;
        }
    }
}
