using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.IO.BACnet;
using BacnetInventoryApp.Common;
using BacnetInventoryApp.Model;
using BacnetInventoryApp.DataAccess;

namespace BacnetInventoryApp.Service
{
    /// <summary>
    /// [서비스] 선택 장비들 포인트 스캔
    ///
    /// ✅ 왜 UDP Who-Is/I-Am을 직접 쓰나?
    /// - System.IO.BACnet의 WhoIs / OnIam 이벤트는 버전별 시그니처가 다르고,
    ///   너 프로젝트에서 이미 컴파일 에러가 났음.
    /// - 그래서 "device instance 확보"는 우리가 직접 만든 UDP 방식으로 확정하고,
    ///   "읽기(ReadPropertyRequest)"는 System.IO.BACnet을 사용한다.
    ///
    /// 실데이터 흐름 예:
    /// - 입력: FacilityDevice { DeviceId="5", DeviceIp="172.16.130.100", DevicePort=47808 }
    /// - 1) UDP Unicast Who-Is → I-Am 수신 → BacnetDeviceInstance=20059
    /// - 2) ReadPropertyRequest(DEVICE:20059, OBJECT_LIST) → 312개
    /// - 3) 각 Object에 대해 Object_Name / Present_Value 읽기
    /// - 출력: RawPointInfo 리스트 (UI gridPoints에 표시)
    /// </summary>
    public sealed class BacnetPointScanService : IDisposable
    {
        private readonly BacnetClient _client;

        // BACnet/IP 표준 포트
        private const int BacnetIpPort = 47808; // 0xBAC0

        public BacnetPointScanService()
        {
            // 로컬 포트는 47808로 열어도 되고, 충돌 피하려면 0(랜덤)도 가능.
            // 여기서는 현장 호환을 위해 47808 유지.
            _client = new BacnetClient(new BacnetIpUdpProtocolTransport(BacnetIpPort, false));
            _client.Start();
        }

        public void Dispose()
        {
            try { _client.Dispose(); } catch { /* no-op */ }
        }

        public async Task<List<RawPointInfo>> ScanSelectedDevicesAsync(
            List<FacilityDevice> selected,
            int maxParallel,
            IProgress<string> progress,
            CancellationToken ct)
        {
            if (selected == null) throw new ArgumentNullException("selected");
            if (selected.Count == 0) return new List<RawPointInfo>();
            if (maxParallel < 1) maxParallel = 1;

            // 현업 기본: 장비/스위치 부하 때문에 "과한 병렬" 금지
            // 74대면 2~4 정도가 안전 (너는 나중에 옵션화하면 됨)
            var sem = new SemaphoreSlim(maxParallel, maxParallel);
            var tasks = new List<Task<List<RawPointInfo>>>();

            foreach (var dev in selected)
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        return await ScanOneDeviceAsync(dev, progress, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("[SCAN] failed ip=" + dev.DeviceIp + " msg=" + ex.Message);
                        return new List<RawPointInfo>();
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct));
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.SelectMany(x => x).ToList();
        }

        private async Task<List<RawPointInfo>> ScanOneDeviceAsync(
    FacilityDevice dev,
    IProgress<string> progress,
    CancellationToken ct)
        {
            // =========================
            // [실데이터 흐름 예시]
            // =========================
            // 입력(dev):
            //   dev.DeviceId="5"              (SI 디바이스 SEQ/ID 문자열)
            //   dev.DeviceIp="172.16.130.100"
            //   dev.DevicePort=47808
            //
            // DB(P_OBJECT):
            //   DEVICE_SEQ=5 인 포인트들:
            //     SYSTEM_PT_ID="AI-1", OBJ_NAME="AHU-01 SAT", OBJ_DESC="Supply Air Temp"
            //     SYSTEM_PT_ID="BV-7", OBJ_NAME="AHU-01 Fan", OBJ_DESC="Fan Run"
            //
            // 처리:
            //   1) DB에서 포인트 목록 로드 (SYSTEM_PT_ID + OBJ_NAME/OBJ_DESC)
            //   2) SYSTEM_PT_ID -> BacnetObjectId 변환 (AI-1 => OBJECT_ANALOG_INPUT instance=1)
            //   3) ReadPropertyRequest(PRESENT_VALUE)로 값 읽기
            //
            // 출력:
            //   RawPointInfo { DeviceSeq=5, ObjectId="AI-1", ObjectName="AHU-01 SAT", Description="Supply Air Temp", PresentValue="23.4" }
            // =========================

            int siDeviceSeq = 0;
            int.TryParse(dev.DeviceId, out siDeviceSeq);

            Logger.Info("[SCAN] start SI=" + dev.DeviceId + " ip=" + dev.DeviceIp + ":" + dev.DevicePort);
            progress?.Report("포인트 목록 로드: SI=" + dev.DeviceId + " ip=" + dev.DeviceIp);

            // 1) DB에서 포인트 목록 가져오기 (DEVICE_SEQ = SI 디바이스 seq)
            // ※ 여기서 “Description 넣어달라” = OBJ_DESC를 RawPointInfo.Description에 넣는 걸 의미
            List<DbPointRow> points = await LoadPointsFromDbAsync(siDeviceSeq, ct).ConfigureAwait(false);

            if (points.Count == 0)
            {
                Logger.Warn("[SCAN] no points in DB. DEVICE_SEQ=" + siDeviceSeq);
                return new List<RawPointInfo>();
            }

            var adr = new BacnetAddress(BacnetAddressTypes.IP, dev.DeviceIp + ":" + dev.DevicePort);

            var result = new List<RawPointInfo>(points.Count);

            // 2) 각 포인트 Present_Value 읽기
            for (int i = 0; i < points.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                DbPointRow row = points[i];

                BacnetObjectId oid;
                if (!TryParseSystemPtId(row.SystemPtId, out oid))
                {
                    Logger.Debug("[SCAN] skip invalid SYSTEM_PT_ID=" + row.SystemPtId + " DEVICE_SEQ=" + siDeviceSeq);
                    continue;
                }

                string pv = null;

                try
                {
                    IList<BacnetValue> v;
                    bool ok = _client.ReadPropertyRequest(adr, oid, BacnetPropertyIds.PROP_PRESENT_VALUE, out v);
                    if (ok && v != null && v.Count > 0)
                        pv = Convert.ToString(v[0].Value);
                }
                catch (Exception ex)
                {
                    // 현업 스타일: 한 포인트 실패가 전체 스캔을 죽이면 안 됨
                    Logger.Debug("[SCAN] PV read fail pt=" + row.SystemPtId + " ex=" + ex.Message);
                }

                result.Add(new RawPointInfo
                {
                    DeviceSeq = siDeviceSeq,
                    DeviceIp = dev.DeviceIp,
                    DevicePort = dev.DevicePort,

                    // 표시는 SI/CSV 친화적으로 SYSTEM_PT_ID 그대로
                    ObjectId = row.SystemPtId,

                    // DB 메타 (없으면 null)
                    ObjectName = row.ObjectName,
                    Description = row.Description,

                    PresentValue = pv
                });

                if (i % 50 == 0)
                    progress?.Report("읽는중: SI=" + dev.DeviceId + " " + (i + 1) + "/" + points.Count);
            }

            Logger.Info("[SCAN] done SI=" + dev.DeviceId + " ip=" + dev.DeviceIp + " points=" + result.Count);
            progress?.Report("스캔 완료: SI=" + dev.DeviceId + " points=" + result.Count);

            return result;
        }

        private bool TryParseSystemPtId(string systemPtId, out BacnetObjectId oid)
        {
            oid = new BacnetObjectId();

            if (string.IsNullOrWhiteSpace(systemPtId))
                return false;

            // 예: "AI-1"
            string[] parts = systemPtId.Split('-');
            if (parts.Length != 2) return false;

            int inst;
            if (!int.TryParse(parts[1], out inst)) return false;

            string prefix = parts[0].Trim().ToUpperInvariant();

            BacnetObjectTypes type;
            switch (prefix)
            {
                case "AI": type = BacnetObjectTypes.OBJECT_ANALOG_INPUT; break;
                case "AO": type = BacnetObjectTypes.OBJECT_ANALOG_OUTPUT; break;
                case "AV": type = BacnetObjectTypes.OBJECT_ANALOG_VALUE; break;
                case "BI": type = BacnetObjectTypes.OBJECT_BINARY_INPUT; break;
                case "BO": type = BacnetObjectTypes.OBJECT_BINARY_OUTPUT; break;
                case "BV": type = BacnetObjectTypes.OBJECT_BINARY_VALUE; break;
                case "MSI": type = BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT; break;
                case "MSO": type = BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT; break;
                case "MSV": type = BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE; break;
                default: return false;
            }

            oid = new BacnetObjectId(type, (uint)inst);
            return true;
        }

        private async Task<List<DbPointRow>> LoadPointsFromDbAsync(int deviceSeq, CancellationToken ct)
        {
            var list = new List<DbPointRow>();

            using (var conn = SqlConnectionFactory.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT
    SYSTEM_PT_ID,
    OBJ_NAME,
    OBJ_DESC
FROM P_OBJECT
WHERE DEVICE_SEQ = @DeviceSeq
ORDER BY OBJ_COUNT";

                cmd.Parameters.AddWithValue("@DeviceSeq", deviceSeq);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        ct.ThrowIfCancellationRequested();

                        list.Add(new DbPointRow
                        {
                            SystemPtId = r["SYSTEM_PT_ID"] as string,
                            ObjectName = r["OBJ_NAME"] as string,
                            Description = r["OBJ_DESC"] as string
                        });
                    }
                }
            }

            await Task.Yield();
            return list;
        }


        private sealed class DbPointRow
        {
            public string SystemPtId { get; set; }
            public string ObjectName { get; set; }
            public string Description { get; set; }
        }



        // =========================================================
        // (핵심) UDP Unicast Who-Is → I-Am 파싱
        // =========================================================
        private async Task<uint> ResolveDeviceInstanceByUdpAsync(string ip, int port, int timeoutMs, CancellationToken ct)
        {
            // BVLC/NPDU/APDU 최소 Who-Is (Unicast)
            // BVLC: 81 0A 00 08
            // NPDU: 01 00
            // APDU: 10 08
            byte[] whois = BuildWhoIsUnicast();

            var remote = new IPEndPoint(IPAddress.Parse(ip), port);

            // ⚠️ Unicast sweep를 하면 ICMP unreachable이 섞여 SocketException이 날 수 있음
            // → UDPConnReset 비활성화로 "강제 끊김" 예외를 무시하도록 함
            using (var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                DisableUdpConnReset(udp);

                try
                {
                    await udp.SendAsync(whois, whois.Length, remote).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    Logger.Error("[SCAN][UDP] Who-Is send failed ip=" + ip, ex);
                    throw;
                }

                var start = Environment.TickCount;

                while (Environment.TickCount - start < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();

                    int remain = timeoutMs - (Environment.TickCount - start);
                    if (remain <= 0) break;

                    UdpReceiveResult? rx = await ReceiveWithTimeoutAsync(udp, remain, ct).ConfigureAwait(false);
                    if (rx == null) break;

                    uint instance;
                    if (TryParseIamDeviceInstance(rx.Value.Buffer, out instance))
                    {
                        // 실데이터 예: instance=20059
                        return instance;
                    }
                }
            }

            throw new TimeoutException("I-Am timeout (unicast who-is) ip=" + ip + ":" + port);
        }

        private static async Task<UdpReceiveResult?> ReceiveWithTimeoutAsync(UdpClient udp, int timeoutMs, CancellationToken ct)
        {
            try
            {
                Task<UdpReceiveResult> recvTask = udp.ReceiveAsync();
                Task delayTask = Task.Delay(timeoutMs, ct);

                Task finished = await Task.WhenAny(recvTask, delayTask).ConfigureAwait(false);
                if (finished == recvTask) return recvTask.Result;
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (SocketException ex)
            {
                Logger.Warn("[SCAN][UDP] receive socket error ignored: " + ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warn("[SCAN][UDP] receive error ignored: " + ex.Message);
                return null;
            }
        }

        private static byte[] BuildWhoIsUnicast()
        {
            // BVLC: 81 0A 00 08  (0A = Original-Unicast-NPDU)
            // NPDU: 01 00
            // APDU: 10 08
            return new byte[]
            {
                0x81, 0x0A, 0x00, 0x08,
                0x01, 0x00,
                0x10, 0x08
            };
        }

        private static bool TryParseIamDeviceInstance(byte[] buf, out uint instance)
        {
            instance = 0;

            // 최소 길이 방어
            if (buf == null || buf.Length < 15) return false;

            // BVLC type
            if (buf[0] != 0x81) return false;

            // func: 0x0A(unicast) or 0x0B(broadcast)
            if (buf[1] != 0x0A && buf[1] != 0x0B) return false;

            int idx = 4;

            // NPDU version 0x01
            if (idx >= buf.Length || buf[idx] != 0x01) return false;
            idx++;

            // NPDU control
            if (idx >= buf.Length) return false;
            idx++;

            // APDU: 10 00 (Unconfirmed + I-Am)
            if (idx + 2 > buf.Length) return false;
            if (buf[idx] != 0x10) return false;
            idx++;
            if (buf[idx] != 0x00) return false;
            idx++;

            // payload: ObjectId(4)+MaxApdu(2)+Seg(1)+Vendor(2)
            if (idx + 9 > buf.Length) return false;

            uint objectId =
                ((uint)buf[idx] << 24) |
                ((uint)buf[idx + 1] << 16) |
                ((uint)buf[idx + 2] << 8) |
                (uint)buf[idx + 3];

            // objectType(10bit) + instance(22bit)
            int objectType = (int)((objectId >> 22) & 0x3FF);
            int inst = (int)(objectId & 0x3FFFFF);

            // Device object type = 8
            if (objectType != 8) return false;

            instance = (uint)inst;
            return true;
        }

        private static void DisableUdpConnReset(UdpClient udp)
        {
            // Windows에서 ICMP unreachable이 들어오면 ReceiveAsync가 SocketException을 던질 수 있음
            // 이를 무시하도록 설정 (SIO_UDP_CONNRESET)
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                udp.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
                Logger.Info("[SCAN][UDP] UDPConnReset disabled (ignore ICMP unreachable)");
            }
            catch
            {
                // 지원 안 되는 환경도 있어서 "조용히 무시"가 아니라 로그만 남기고 진행
                Logger.Warn("[SCAN][UDP] UDPConnReset disable not supported (ignored)");
            }
        }

        private string TryReadObjectName(BacnetAddress adr, BacnetObjectId oid)
        {
            try
            {
                IList<BacnetValue> v;
                bool ok = _client.ReadPropertyRequest(adr, oid, BacnetPropertyIds.PROP_OBJECT_NAME, out v);
                if (!ok || v == null || v.Count == 0) return null;
                return Convert.ToString(v[0].Value);
            }
            catch (Exception ex)
            {
                Logger.Debug("[SCAN] ObjectName read fail " + ToShortObjectId(oid) + " ex=" + ex.Message);
                return null;
            }
        }

        private string TryReadPresentValue(BacnetAddress adr, BacnetObjectId oid)
        {
            try
            {
                IList<BacnetValue> v;
                bool ok = _client.ReadPropertyRequest(adr, oid, BacnetPropertyIds.PROP_PRESENT_VALUE, out v);
                if (!ok || v == null || v.Count == 0) return null;
                return Convert.ToString(v[0].Value);
            }
            catch (Exception ex)
            {
                Logger.Debug("[SCAN] PV read fail " + ToShortObjectId(oid) + " ex=" + ex.Message);
                return null;
            }
        }


        // ⚠️ 인스턴스 메서드(_client)에서 static으로 접근 못하니까 작은 래퍼를 둠 (C# 7.3 스타일)
        private bool ReadPropertyInternal(BacnetAddress adr, BacnetObjectId oid, BacnetPropertyIds prop, out IList<BacnetValue> values)
        {
            values = null;
            try { return _client.ReadPropertyRequest(adr, oid, prop, out values); }
            catch { return false; }
        }

        // static helper 호출 시 인스턴스가 필요하므로, 아래처럼 교정:
        private static bool ProgramReadProperty(BacnetAddress adr, BacnetObjectId oid, BacnetPropertyIds prop, out IList<BacnetValue> values)
        {
            // 이 메서드는 위 코드 구조상 호출되면 안 됨.
            // (붙여넣기 실수 방지용) - 아래에 실제로는 인스턴스 메서드로 교체할 거야.
            values = null;
            return false;
        }

        private static string ToShortObjectId(BacnetObjectId oid)
        {
            string prefix;
            switch (oid.Type)
            {
                case BacnetObjectTypes.OBJECT_ANALOG_INPUT: prefix = "AI"; break;
                case BacnetObjectTypes.OBJECT_ANALOG_OUTPUT: prefix = "AO"; break;
                case BacnetObjectTypes.OBJECT_ANALOG_VALUE: prefix = "AV"; break;
                case BacnetObjectTypes.OBJECT_BINARY_INPUT: prefix = "BI"; break;
                case BacnetObjectTypes.OBJECT_BINARY_OUTPUT: prefix = "BO"; break;
                case BacnetObjectTypes.OBJECT_BINARY_VALUE: prefix = "BV"; break;
                case BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT: prefix = "MSI"; break;
                case BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT: prefix = "MSO"; break;
                case BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE: prefix = "MSV"; break;
                default: prefix = oid.Type.ToString(); break;
            }
            return prefix + "-" + oid.Instance;
        }
    }
}
