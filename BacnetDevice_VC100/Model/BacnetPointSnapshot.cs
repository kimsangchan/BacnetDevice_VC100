// Bacnet/Models/BacnetPointSnapshot.cs
using System.Collections.Generic;

namespace BacnetDevice_VC100.Bacnet.Models
{
    public sealed class BacnetPointSnapshot
    {
        public object PresentValue { get; set; }
        public string ObjectName { get; set; }
        public string Description { get; set; }

        // StatusFlags는 라이브러리 표현이 제각각이라 일단 raw로 들고,
        // 필요하면 다음 단계에서 bool 4개로 파싱 로직 추가하자.
        public object StatusFlagsRaw { get; set; }

        // MSO/MSI/MSV의 상태명(옵션)
        public IList<string> StateText { get; set; }
        // ✅ 추가
        public object UnitsRaw { get; set; }      // 보통 int/uint 형태
        public string UnitsText { get; set; }     // 사람이 읽을 텍스트(가능하면)
    }
}
