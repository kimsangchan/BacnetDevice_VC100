// Models\SiDeviceInfo.cs
using System;

namespace BacnetInventoryScanner.Models
{
    public class SiDeviceInfo
    {
        public string FixCodeNo { get; set; }      // FIX_CODENO
        public int DeviceId { get; set; }           // DEVICE_ID
        public string CodeName { get; set; }        // CODE_NAME
        public string DeviceIp { get; set; }        // DEVICE_IP
        public int DevicePort { get; set; }         // DEVICE_PORT
        public string MultiParentId { get; set; }   // 4311744512
        public bool IsOnline { get; set; }          // 스캔 결과 (현장에서 응답 오는지)
    }
}