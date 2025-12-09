// Models/BacnetPointInfo.cs
using System.IO.BACnet;

namespace BacnetDevice_VC100.Models
{
    /// <summary>
    /// P_OBJECT 한 행을 BACnet 폴링에 사용하기 좋게 변환한 모델.
    /// SYSTEM_PT_ID("AV-2" 등)와 OBJ_TYPE, OBJ_INSTANCE 정보를 담는다.
    /// </summary>
    public class BacnetPointInfo
    {
        /// <summary>
        /// 시스템 포인트 ID (예: "AV-2", "BI-32")
        /// </summary>
        public string SystemPtId { get; set; }

        /// <summary>
        /// 사람이 보는 포인트 이름 (OBJ_NAME)
        /// </summary>
        public string ObjName { get; set; }

        /// <summary>
        /// P_OBJECT.OBJ_TYPE (0~8)
        /// 0=AI, 1=AO, 2=AV, 3=BI, 4=BO, 5=BV, 6=MSI, 7=MSO, 8=MSV
        /// </summary>
        public int ObjTypeCode { get; set; }

        /// <summary>
        /// BACnet 라이브러리에서 사용하는 Object 타입
        /// (OBJECT_ANALOG_INPUT 등)
        /// </summary>
        public BacnetObjectTypes BacnetType { get; set; }

        /// <summary>
        /// BACnet Object 인스턴스 번호 (예: "AV-2" → 2)
        /// </summary>
        public uint Instance { get; set; }

        /// <summary>
        /// P_OBJECT.DEVICE_SEQ (어떤 디바이스에 속한 포인트인지)
        /// </summary>
        public int DeviceSeq { get; set; }
    }
}
