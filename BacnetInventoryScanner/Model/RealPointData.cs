using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BacnetInventoryScanner.Model
{
    // 1. 포인트 데이터 모델 확장 (상태값 저장을 위한 배열 추가)
    public class RealPointData
    {
        public string SystemPtId { get; set; }
        public string Name { get; set; }
        public string Desc { get; set; }
        public int TypeId { get; set; }
        // MSI/MSO/MSV를 위한 상태 문자열 배열 (최대 10개)
        public string[] StatusTexts { get; set; } = new string[10];
    }
}
