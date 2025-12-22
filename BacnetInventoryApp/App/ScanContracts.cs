using System;

namespace BacnetInventoryApp.App
{
    /// <summary>
    /// [스캔 옵션]
    /// 예) Subnet="172.16.130", TimeoutMs=2000
    /// </summary>
    public sealed class ScanOptions
    {
        public string Subnet { get; set; }     // "172.16.130" (현장 대부분 /24)
        public int TimeoutMs { get; set; } = 2000;
        public int MaxParallel { get; set; } = 6; // 다음 기능(포인트 수집)에서 사용
    }

    public enum ScanStage
    {
        Start,
        Discovery,
        DiscoveryCompleted,
        Harvest,
        HarvestCompleted,
        Completed,
        Error
    }

    /// <summary>
    /// [UI/로그에 뿌릴 진행상태]
    ///
    /// 데이터 흐름 예시:
    /// - Stage=Discovery, Message="Who-Is broadcast subnet=172.16.130"
    /// - Stage=Discovery, Message="I-Am deviceId=20059 ip=172.16.130.98 vendorId=15"
    /// </summary>
    public sealed class ScanProgress
    {
        public ScanProgress(Guid sessionId, ScanStage stage, string message)
        {
            SessionId = sessionId;
            Stage = stage;
            Message = message;
        }

        public Guid SessionId { get; private set; }
        public ScanStage Stage { get; private set; }
        public string Message { get; private set; }

        public int? DeviceId { get; set; }
        public string DeviceIp { get; set; }
    }
}
