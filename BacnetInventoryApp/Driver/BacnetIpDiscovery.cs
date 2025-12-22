using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BacnetInventoryApp.Common;

namespace BacnetInventoryApp.Driver
{
    /// <summary>
    /// [기능] BACnet/IP Discovery (Who-Is -> I-Am)
    ///
    /// 실데이터 흐름 예시:
    /// - 입력: subnet="172.16.130", timeoutMs=2000
    /// - 송신: 172.16.130.255:47808 로 Who-Is 브로드캐스트
    /// - 수신: I-Am from 172.16.130.98 -> deviceId=20059 vendorId=15
    /// - 출력: DiscoveredDevice 1건 생성
    /// </summary>
    public sealed class BacnetIpDiscovery
    {
        private const int BacnetIpPort = 47808; // 0xBAC0

        private const byte BVLC_TYPE_BACNET_IP = 0x81;
        private const byte BVLC_FUNC_ORIGINAL_BROADCAST_NPDU = 0x0B;

        private const byte PDU_UNCONFIRMED_SERVICE_REQUEST = 0x10;
        private const byte SERVICE_WHO_IS = 0x08;
        private const byte SERVICE_I_AM = 0x00;
        private const byte BVLC_FUNC_ORIGINAL_UNICAST_NPDU = 0x0A;
        public async Task<List<DiscoveredDevice>> DiscoverAsync(string subnet, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(subnet))
                throw new ArgumentException("subnet is required. ex) 172.16.130", "subnet");
            if (timeoutMs < 300)
                throw new ArgumentOutOfRangeException("timeoutMs", "timeoutMs must be >= 300");

            string broadcastIp = ComputeBroadcast16(subnet);
            var broadcastEndPoint = new IPEndPoint(IPAddress.Parse(broadcastIp), BacnetIpPort);

            // DeviceId 기준 중복 제거 (IP가 바뀌어도 deviceId가 더 안정적일 때가 많음)
            var map = new Dictionary<int, DiscoveredDevice>();
            // ★ 여기서 172.16.130.254 로컬 NIC에 UDP를 묶는다
            var bindIp = IPAddress.Parse("172.16.130.254");

            using (var udp = new UdpClient(new IPEndPoint(bindIp, 0)))
            {
                // =======================
// [DISCOVERY][RX 안정화] UDP ICMP Port Unreachable 무시
// =======================
// 실데이터 흐름 예시:
// - Unicast sweep로 172.16.130.1~254에 찌르면, 대부분 IP는 "포트 없음" ICMP를 돌려줌
// - Windows는 이 ICMP를 받으면 UdpClient.ReceiveAsync()가 SocketException으로 터질 수 있음
// - 그래서 아래 설정으로 "UDPConnReset"을 꺼서 스캐너가 죽지 않게 한다.
try
{
    const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
    udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
    Logger.Info("[DISCOVERY][RX] UDPConnReset disabled (ignore ICMP unreachable)");
}
catch (Exception ex)
{
    Logger.Warn("[DISCOVERY][RX] UDPConnReset disable failed: " + ex.Message);
}

                var lep = (IPEndPoint)udp.Client.LocalEndPoint;
                Logger.Info("[DISCOVERY] UDP bound local=" + lep.Address + ":" + lep.Port);

                udp.EnableBroadcast = true;

                byte[] whoisBroadcast = BuildWhoIsPacket(BVLC_FUNC_ORIGINAL_BROADCAST_NPDU);
                byte[] whoisUnicast = BuildWhoIsPacket(BVLC_FUNC_ORIGINAL_UNICAST_NPDU);


                // =======================
                // [DISCOVERY][TX] Who-Is 송신 단계
                // =======================
                // 실데이터 흐름 예시(현장):
                // - 서버 NIC: 172.16.130.254 (mask 255.255.0.0)
                // - 대상 장비: 172.16.130.98 (DeviceId=20059)
                // - 현장에 따라 /24처럼 운용되거나(/255), NIC 설정이 /16인 경우(/255.255)도 있어서
                //   브로드캐스트는 3종(/24, /16, limited) + 브로드캐스트가 막힌 망 대비 유니캐스트 스윕까지 같이 쏜다.
                //
                // 기대 결과(정상):
                // - I-Am from 172.16.130.98 -> deviceId=20059 vendorId=...
                try
                {
                    // (1) Broadcast targets: /24 + /16 + limited
                    var ep24 = new IPEndPoint(IPAddress.Parse("172.16.130.255"), BacnetIpPort);
                    var ep16 = new IPEndPoint(IPAddress.Parse("172.16.255.255"), BacnetIpPort);
                    var epLimited = new IPEndPoint(IPAddress.Broadcast, BacnetIpPort);

                    await udp.SendAsync(whoisBroadcast, whoisBroadcast.Length, ep24).ConfigureAwait(false);
                    await udp.SendAsync(whoisBroadcast, whoisBroadcast.Length, ep16).ConfigureAwait(false);
                    await udp.SendAsync(whoisBroadcast, whoisBroadcast.Length, epLimited).ConfigureAwait(false);

                    Logger.Info("[DISCOVERY][TX] Who-Is broadcast targets=" +
                                ep24.Address + "," + ep16.Address + "," + epLimited.Address +
                                " len=" + whoisBroadcast.Length);

                    // (2) Unicast sweep: broadcast 차단/VLAN/정책망 대비
                    // subnet="172.16.130" -> 172.16.130.1~254 로 유니캐스트 Who-Is 전송
                    string[] parts = subnet.Trim().Split('.');
                    if (parts.Length == 3)
                    {
                        int sent = 0;
                        var txSw = Stopwatch.StartNew();

                        for (int host = 1; host <= 254; host++)
                        {
                            ct.ThrowIfCancellationRequested();

                            string ip = parts[0] + "." + parts[1] + "." + parts[2] + "." + host;
                            var ep = new IPEndPoint(IPAddress.Parse(ip), BacnetIpPort);

                            await udp.SendAsync(whoisUnicast, whoisUnicast.Length, ep).ConfigureAwait(false);
                            sent++;

                            // 너무 공격적으로 쏘면 스위치/장비에서 드랍 가능 -> 배치 딜레이(현업 안전장치)
                            if (sent % 25 == 0)
                                await Task.Delay(5, ct).ConfigureAwait(false);
                        }

                        txSw.Stop();
                        Logger.Info("[DISCOVERY][TX] Unicast Who-Is sweep sent=" + sent +
                                    " range=" + parts[0] + "." + parts[1] + "." + parts[2] + ".1-254" +
                                    " elapsedMs=" + txSw.ElapsedMilliseconds);
                    }
                    else
                    {
                        Logger.Warn("[DISCOVERY][TX] Unicast sweep skipped. subnet format invalid: " + subnet);
                    }
                }
                catch (SocketException ex)
                {
                    Logger.Error("[DISCOVERY][TX] UDP send failed (Who-Is)", ex);
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Error("[DISCOVERY][TX] Unexpected send error (Who-Is)", ex);
                    throw;
                }



                var sw = Stopwatch.StartNew();

                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();

                    int remain = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remain <= 0) break;

                    ReceiveAttempt attempt = await ReceiveWithTimeoutAsync(udp, remain, ct).ConfigureAwait(false);
                    if (!attempt.Got) break;

                    DiscoveredDevice dev;
                    if (TryParseIam(attempt.Result.Buffer, attempt.Result.RemoteEndPoint, out dev))
                    {
                        if (!map.ContainsKey(dev.DeviceId))
                        {
                            map[dev.DeviceId] = dev;

                            // 예) [DISCOVERY] I-Am deviceId=20059 ip=172.16.130.98 vendorId=15 maxApdu=1476 seg=0
                            Logger.Info("[DISCOVERY] I-Am deviceId=" + dev.DeviceId +
                                        " ip=" + dev.Ip +
                                        " vendorId=" + dev.VendorId +
                                        " maxApdu=" + dev.MaxApdu +
                                        " seg=" + dev.Segmentation);
                        }
                    }
                    else
                    {
                        Logger.Debug("[DISCOVERY] Non I-Am packet ignored from " + attempt.Result.RemoteEndPoint.Address);
                    }
                }
            }

            var list = new List<DiscoveredDevice>(map.Values);
            Logger.Info("[DISCOVERY] Discovery completed count=" + list.Count + " subnet=" + subnet + " broadcast=" + broadcastIp + " timeoutMs=" + timeoutMs);
            return list;
        }

        private static string ComputeBroadcast16(string subnet)
        {
            // 예:
            // subnet = "172.16.130"
            // 실제 네트워크 = 172.16.0.0 /16
            // 브로드캐스트 = 172.16.255.255
            string[] parts = subnet.Trim().Split('.');
            if (parts.Length < 2)
                throw new ArgumentException("Subnet must be like '172.16.x'");

            return parts[0] + "." + parts[1] + ".255.255";
        }


        private static byte[] BuildWhoIsPacket(byte bvlcFunc)
        {
            // 최소 Who-Is:
            // BVLC(4) + NPDU(2) + APDU(2) = 8 bytes
            // BVLC: 81 (func) 00 08
            // NPDU: 01 00   (control=0x00 minimal)
            // APDU: 10 08
            byte[] p = new byte[8];
            p[0] = BVLC_TYPE_BACNET_IP;
            p[1] = bvlcFunc;
            p[2] = 0x00;
            p[3] = 0x08;

            p[4] = 0x01; // NPDU version
            p[5] = 0x00; // NPDU control (minimal)

            Logger.Info("[DISCOVERY] Who-Is NPDU control=0x" + p[5].ToString("X2") + " bvlcFunc=0x" + p[1].ToString("X2"));

            p[6] = PDU_UNCONFIRMED_SERVICE_REQUEST;
            p[7] = SERVICE_WHO_IS;
            return p;
        }


        private static async Task<ReceiveAttempt> ReceiveWithTimeoutAsync(UdpClient udp, int timeoutMs, CancellationToken ct)
        {
            try
            {
                Task<UdpReceiveResult> recvTask = udp.ReceiveAsync();
                Task delayTask = Task.Delay(timeoutMs, ct);

                Task finished = await Task.WhenAny(recvTask, delayTask).ConfigureAwait(false);
                if (finished == recvTask)
                {
                    return ReceiveAttempt.GotResult(recvTask.Result);
                }

                return ReceiveAttempt.Timeout();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException ex)
            {
                Logger.Error("[DISCOVERY] UDP receive failed", ex);
                return ReceiveAttempt.Timeout();
            }
            catch (Exception ex)
            {
                Logger.Error("[DISCOVERY] Unexpected receive error", ex);
                return ReceiveAttempt.Timeout();
            }
        }

        private static bool TryParseIam(byte[] buf, IPEndPoint remote, out DiscoveredDevice dev)
        {
            dev = null;

            // 최소 길이 (대충 방어)
            if (buf == null || buf.Length < 15) return false;

            if (buf[0] != BVLC_TYPE_BACNET_IP) return false;

            // func: 0x0A(unicast) 또는 0x0B(broadcast)
            byte func = buf[1];
            if (func != 0x0A && func != 0x0B) return false;

            int idx = 4; // BVLC(4) 이후

            // NPDU version
            if (idx >= buf.Length || buf[idx] != 0x01) return false;
            idx++;

            // NPDU control (주소정보가 있으면 파싱 복잡 -> 이번 단계는 단순망 기준)
            if (idx >= buf.Length) return false;
            byte npduCtrl = buf[idx];
            idx++;

            // APDU header
            if (idx + 2 > buf.Length) return false;
            if (buf[idx] != PDU_UNCONFIRMED_SERVICE_REQUEST) return false;
            idx++;

            if (buf[idx] != SERVICE_I_AM) return false;
            idx++;

            // payload 최소 길이: ObjectId(4)+MaxApdu(2)+Seg(1)+Vendor(2)
            if (idx + 9 > buf.Length) return false;

            uint objectId =
                ((uint)buf[idx] << 24) |
                ((uint)buf[idx + 1] << 16) |
                ((uint)buf[idx + 2] << 8) |
                (uint)buf[idx + 3];
            idx += 4;

            int objectType = (int)((objectId >> 22) & 0x3FF);
            int instance = (int)(objectId & 0x3FFFFF);

            // Device object type = 8
            if (objectType != 8) return false;

            int maxApdu = ((int)buf[idx] << 8) | buf[idx + 1];
            idx += 2;

            byte segmentation = buf[idx];
            idx++;

            int vendorId = ((int)buf[idx] << 8) | buf[idx + 1];

            dev = new DiscoveredDevice
            {
                DeviceId = instance,
                Ip = remote.Address.ToString(),
                VendorId = vendorId,
                MaxApdu = maxApdu,
                Segmentation = segmentation
            };

            return true;
        }
    }

    public sealed class DiscoveredDevice
    {
        public int DeviceId { get; set; }
        public string Ip { get; set; }
        public int VendorId { get; set; }
        public int MaxApdu { get; set; }
        public byte Segmentation { get; set; }
    }

    public sealed class ReceiveAttempt
    {
        public bool Got { get; private set; }
        public UdpReceiveResult Result { get; private set; }

        private ReceiveAttempt() { }

        public static ReceiveAttempt GotResult(UdpReceiveResult r)
        {
            return new ReceiveAttempt { Got = true, Result = r };
        }

        public static ReceiveAttempt Timeout()
        {
            return new ReceiveAttempt { Got = false };
        }
    }
}
