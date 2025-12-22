using System;

namespace BacnetInventoryApp.Model
{
    /// <summary>
    /// [RAW 포인트] "장비에서 실제로 읽은" 포인트 스냅샷(메모리용)
    ///
    /// 실데이터 예:
    /// - DeviceSeq(SI) = 5
    /// - DeviceIp      = 172.16.130.100
    /// - BacnetDeviceInstance = 20059  (I-Am에서 확보)
    /// - ObjectId      = "AI-1"
    /// - ObjectName    = "AHU-01 Supply Temp"
    /// - PresentValue  = "23.4"
    ///
    /// ⚠️ 포인트 CRUD/DB 반영은 다음 단계에서 함.
    /// </summary>
    public sealed class RawPointInfo
    {
        // SI 장비 식별자 (P_OBJ_CODE.DEVICE_ID)
        public int DeviceSeq { get; set; }

        public string DeviceIp { get; set; }
        public int DevicePort { get; set; }

        // BACnet 디바이스 인스턴스 (I-Am의 Device Object Instance)
        public uint BacnetDeviceInstance { get; set; }
        public string Description { get; set; }

        // "AI-1" 같은 표시용
        public string ObjectId { get; set; }

        public string ObjectName { get; set; }   // Object_Name
        public string PresentValue { get; set; } // Present_Value (string으로 유지: MSI/MSV 대응)
    }
}
