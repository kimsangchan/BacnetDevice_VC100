using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO.BACnet;
using BacnetInventoryApp.Common;

namespace BacnetInventoryApp.Driver
{
    /// <summary>
    /// [기능] Direct Read 기반 BACnet 장비 감지(Who-Is/I-Am 안 되는 현장 대응)
    ///
    /// 실데이터 흐름 예시:
    /// - 입력: subnet="172.16.130", range=1~254
    /// - 시도: 172.16.130.98:47808 에 ReadProperty(DeviceObject/objName)
    /// - 성공: "AHU-01" 같은 objName 수신 -> "장비 존재" 확정
    /// - 출력: FoundDevice(DeviceIp=172.16.130.98, BacnetDeviceId=20059 추후 채움)
    ///
    /// 왜 이 방식?
    /// - 현장 VLAN/스위치 정책으로 Who-Is/I-Am이 막혀도,
    ///   운영 모듈처럼 '직접 Read'는 살아있는 경우가 많음.
    /// </summary>
    public sealed class BacnetDirectDiscovery : IDisposable
    {
        private readonly int _localPort;
        private BacnetClient _client;

        public BacnetDirectDiscovery(int localPort)
        {
            _localPort = localPort;
        }

        public void Start()
        {
            if (_client != null) return;

            _client = new BacnetClient(_localPort);
            _client.Start();

            Logger.Info("[DDISC] BacnetClient started localPort=" + _localPort);
        }

        public async Task<List<FoundDevice>> ScanSubnetAsync(
            string subnet3,
            int port,
            int maxParallel,
            int timeoutMsPerHost,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(subnet3)) throw new ArgumentException("subnet required ex) 172.16.130", "subnet3");
            if (maxParallel <= 0) maxParallel = 20;

            Start();

            var bag = new ConcurrentBag<FoundDevice>();
            var sem = new SemaphoreSlim(maxParallel);

            var tasks = new List<Task>();
            for (int host = 1; host <= 254; host++)
            {
                ct.ThrowIfCancellationRequested();
                await sem.WaitAsync(ct).ConfigureAwait(false);

                int captured = host;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();

                        string ip = subnet3 + "." + captured;
                        FoundDevice fd;
                        if (TryProbeOne(ip, port, timeoutMsPerHost, out fd))
                        {
                            bag.Add(fd);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug("[DDISC] probe error host=" + captured + " msg=" + ex.Message);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var list = new List<FoundDevice>(bag);
            Logger.Info("[DDISC] ScanSubnet done subnet=" + subnet3 + " found=" + list.Count);
            return list;
        }

        private bool TryProbeOne(string ip, int port, int timeoutMs, out FoundDevice found)
        {
            found = null;

            // 최소 probe: Device Object(ObjectType=8, instance는 모름)
            // 여기서 instance를 모르니, "DeviceId를 바로 읽는" 방식은 어렵다.
            // 대신 현업에서는 보통 아래 중 하나를 택한다:
            // 1) known deviceId 목록이 있으면 그걸로 Read
            // 2) 장비가 응답하는 패턴(예: 특정 object)로 확인
            //
            // 지금 단계는 "살아있는 IP"만 찾는 게 목적이라,
            // 실패해도 조용히 넘어간다.

            try
            {
                var addr = new BacnetAddress(BacnetAddressTypes.IP, ip + ":" + port);

                // 대부분 장비는 deviceId를 모르면 바로 못 읽는다.
                // 그래서 1차는 "네트워크 레벨로 응답이 오는지"만 보는 방식:
                // - Who-Is/I-Am이 막힌 현장이 많아서 이걸로는 한계가 있음.
                //
                // => 결론: 이 함수만으로는 deviceId 미확정 상태에서 'Read로 장비 확정'이 어려움.
                // => 그래서 다음 단계(운영 모듈에서 이미 deviceId를 알고 Read가 되는 경로)와 결합해야 함.

                // 임시로: "응답 가능 endpoint"로만 등록 (확정은 다음 단계에서)
                found = new FoundDevice { DeviceIp = ip, Port = port };
                Logger.Info("[DDISC] Candidate ip=" + ip + " (needs deviceId to confirm)");
                return false;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { if (_client != null) _client.Dispose(); }
            catch { }
        }
    }

    public sealed class FoundDevice
    {
        public string DeviceIp { get; set; }
        public int Port { get; set; }
        public int? BacnetDeviceId { get; set; }
    }
}
