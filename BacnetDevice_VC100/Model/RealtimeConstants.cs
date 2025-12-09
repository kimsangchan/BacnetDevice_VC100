// Models/RealtimeConstants.cs
namespace BacnetDevice_VC100.Models
{
    /// <summary>
    /// 실시간 값 저장 시 공통으로 사용하는 상수/코드 정의.
    /// </summary>
    public static class RealtimeConstants
    {
        /// <summary>
        /// BACnet 읽기 실패 등, 유효하지 않은 값을 표현할 때 사용하는 상수 값.
        /// 실제 설비 값과 절대 겹치지 않는 값으로 잡는다.
        /// </summary>
        public const double FailValue = -9999.0;

        /// <summary>
        /// 값의 품질(정상/실패)을 표현하는 문자열 코드.
        /// DB에 별도 컬럼으로 넣거나, 필요 없으면 무시해도 된다.
        /// </summary>
        public const string QualityGood = "GOOD";
        public const string QualityBad = "BAD";
    }
}
