using System;

namespace BacnetInventoryApp.Model
{
    /// <summary>
    /// [모델] 설비 디바이스 (SI 기준 장비 마스터)
    ///
    /// 실데이터 예시 (P_OBJ_CODE 한 행):
    /// - MULTI_PARENT_ID = 4311744512   (설비 그룹)
    /// - DEVICE_ID       = 20059        (SI 장비 식별자, BACnet DeviceId 아님)
    /// - DEVICE_IP       = 172.16.130.98
    /// - DEVICE_PORT     = 47808
    /// - DEVICE_CINFO    = "AHU-01"
    ///
    /// ⚠️ 주의
    /// - DEVICE_ID != BACnet Device Object Instance
    /// - 이 모델은 "설비로 등록된 장비"를 의미함
    /// </summary>
    public sealed class FacilityDevice
    {
        public string DeviceId { get; set; }     // SI 장비 ID (문자열로 유지)
        public string DeviceIp { get; set; }     // 시설망 IP
        public int DevicePort { get; set; }      // 기본 47808
        public string DeviceInfo { get; set; }   // DEVICE_CINFO (장비 설명)
    }
}
