using System;

namespace BacnetDevice_VC100.Model
{
    /// <summary>
    /// Read 결과 (Realtime 저장용)
    /// </summary>
    public class ReadResult
    {
        public int DeviceSeq { get; set; }
        public string SystemPtId { get; set; }
        public float Value { get; set; }
        public byte Quality { get; set; }               // 0=GOOD, 1=BAD, 2=UNCERTAIN
        public string LastError { get; set; }
        public DateTime UpdatedAt { get; set; }

        public ReadResult()
        {
            UpdatedAt = DateTime.Now;
            Quality = 0; // GOOD
        }
    }
}
