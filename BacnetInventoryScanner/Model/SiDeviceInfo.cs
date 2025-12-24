// Models\SiDeviceInfo.cs
using System;

namespace BacnetInventoryScanner.Models
{
    public class SiDeviceInfo
    {
        public string FixCodeNo { get; set; }      // FIX_CODENO
        public int DeviceId { get; set; }           // DEVICE_ID
        // ⭐ [구분] DB에 저장된 관리 명칭
        public string CodeName { get; set; }

        // ⭐ [추가] 실제 현장 장비에서 스캔된 명칭 (BACnet Object Name)
        public string RealDeviceName { get; set; }
        public string DeviceIp { get; set; }        // DEVICE_IP
        public int DevicePort { get; set; }         // DEVICE_PORT
        public string MultiParentId { get; set; }   // 4311744512
        public bool IsOnline { get; set; }          // 스캔 결과 (현장에서 응답 오는지)
    }
}