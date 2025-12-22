using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BacnetInventoryApp.App;
using BacnetInventoryApp.Common;
using BacnetInventoryApp.Model;

namespace BacnetInventoryApp.Driver
{
    public sealed class BacnetRawScanner
    {
        private readonly BacnetIpDiscovery _discovery = new BacnetIpDiscovery();

        public async Task<List<RawDeviceInfo>> DiscoverDevicesAsync(
            Guid sessionId,
            ScanOptions opt,
            IProgress<ScanProgress> progress,
            CancellationToken ct)
        {
            if (opt == null) throw new ArgumentNullException("opt");
            if (string.IsNullOrWhiteSpace(opt.Subnet))
                throw new ArgumentException("Subnet is required. ex) 172.16.130", "opt.Subnet");

            progress?.Report(new ScanProgress(sessionId, ScanStage.Discovery, "Who-Is 시작 subnet=" + opt.Subnet));

            // 데이터 흐름(실데이터):
            // 입력: subnet=172.16.130 timeoutMs=2000
            // 중간: I-Am deviceId=20059 ip=172.16.130.98 vendorId=15
            // 출력: RawDeviceInfo{DeviceId=20059, DeviceIp=..., VendorId=15} 생성
            List<DiscoveredDevice> found = await _discovery.DiscoverAsync(opt.Subnet, opt.TimeoutMs, ct).ConfigureAwait(false);

            var list = new List<RawDeviceInfo>(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                var d = found[i];

                var raw = new RawDeviceInfo
                {
                    // =======================
                    // [RAW_DEVICE 생성 - Discovery 결과]
                    // =======================
                    // 실데이터 예시(로그/현장 기준):
                    // - subnet      : "172.16.130"
                    // - I-Am 수신IP : "172.16.130.98"
                    // - deviceId    : 20059
                    // - vendorId    : 15
                    // - maxApdu     : 1476
                    // - seg         : 0
                    //
                    // 이 단계에서는 "장비가 살아있는지"만 확정한다.
                    // (Description/ObjectName/ObjectList 같은 메타/포인트는 다음 단계에서 수집)
                    SessionId = sessionId,
                    Subnet = opt.Subnet,

                    DeviceId = d.DeviceId,
                    DeviceIp = d.Ip,
                    VendorId = d.VendorId,

                    MaxApdu = d.MaxApdu,
                    Segmentation = d.Segmentation
                };


                list.Add(raw);

                progress?.Report(new ScanProgress(sessionId, ScanStage.Discovery,
                    "I-Am 수신 deviceId=" + raw.DeviceId +
                        " ip=" + raw.DeviceIp +
                        " vendorId=" + raw.VendorId +
                        " maxApdu=" + raw.MaxApdu +
                        " seg=" + raw.Segmentation
)
                { DeviceId = raw.DeviceId, DeviceIp = raw.DeviceIp });
            }

            Logger.Info("[DISCOVERY] RAW_DEVICE built count=" + list.Count);
            return list;
        }

        // 다음 기능에서 구현. 지금은 컴파일만 되게 빈 리스트 반환.
        public Task<List<RawPointInfo>> BuildRawPointTableAsync(
            Guid sessionId,
            List<RawDeviceInfo> devices,
            ScanOptions opt,
            IProgress<ScanProgress> progress,
            CancellationToken ct)
        {
            return Task.FromResult(new List<RawPointInfo>());
        }
    }
}
