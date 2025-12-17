public sealed class ControlCommand
{
    public int DeviceSeq { get; set; }
    public string SystemPtId { get; set; }   // 너 시스템이 string이면 string 유지
    public double NewValue { get; set; }
    public string ControlUser { get; set; }  // 없으면 "SYSTEM"
    public bool IsDryRun { get; set; }
    public string Source { get; set; }       // "SI" / "API" / "Manual" 등
}
