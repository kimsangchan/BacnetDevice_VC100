// [Models\SiDeviceInfo.cs]

namespace BacnetInventoryScanner.Models
{
    public class SiDeviceInfo
    {
        // --- 기존 필드 ---
        public string FixCodeNo { get; set; }       // DB Key (P_OBJ_CODE.FIX_CODENO)
        public uint BacnetId { get; set; }          // 통신 ID (스캔값)

        // --- ⭐ [추가] CSV 생성을 위한 DB 정보 ---
        public int ServerId { get; set; }           // P_OBJ_CODE.SERVER_ID
        public int SystemCode { get; set; }         // P_OBJ_CODE.SYSTEM_CODE (CSV의 SYSTEM_ID 매핑)
        public int DbDeviceId { get; set; }         // P_OBJ_CODE.DEVICE_ID (화면 출력 순서)

        // --- 기타 필드 ---
        public string CodeName { get; set; }
        public string RealDeviceName { get; set; }
        public string DeviceIp { get; set; }
        public bool IsOnline { get; set; }
    }
}