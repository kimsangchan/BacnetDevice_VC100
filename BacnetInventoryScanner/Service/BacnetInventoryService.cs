using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.IO.BACnet;
using System.Linq;
using BacnetDevice_VC100.Util; // 기존 로깅 유틸리티를 사용합니다.

namespace BacnetInventoryScanner.Service
{
    /// <summary>
    /// [최종 병기] 장비 ID를 몰라도 IP 주소만으로 진짜 장비 ID를 낚아채서 수집하는 서비스입니다.
    /// </summary>
    public class BacnetInventoryService
    {
        private readonly BacnetLogger _logger;

        // SI(통합) 시스템의 데이터 규격에 맞춘 타입 매핑 테이블입니다.
        private static readonly Dictionary<BacnetObjectTypes, (string Prefix, int TypeId)> SiMapping =
            new Dictionary<BacnetObjectTypes, (string, int)> {
                { BacnetObjectTypes.OBJECT_ANALOG_INPUT, ("AI", 0) },
                { BacnetObjectTypes.OBJECT_ANALOG_OUTPUT, ("AO", 1) },
                { BacnetObjectTypes.OBJECT_ANALOG_VALUE, ("AV", 2) },
                { BacnetObjectTypes.OBJECT_BINARY_INPUT, ("BI", 3) },
                { BacnetObjectTypes.OBJECT_BINARY_OUTPUT, ("BO", 4) },
                { BacnetObjectTypes.OBJECT_BINARY_VALUE, ("BV", 5) },
                { BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT, ("MSI", 6) },
                { BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT, ("MSO", 7) },
                { BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE, ("MSV", 8) }
            };

        /// <summary>
        /// 생성자: 모든 통신 과정의 발자취를 남기기 위해 로거를 주입받습니다.
        /// </summary>
        public BacnetInventoryService(BacnetLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// [핵심 로직] IP 주소로 진짜 Device Instance를 찾아내고 즉시 포인트 리스트를 추출합니다.
        /// [데이터 흐름]: IP 입력 -> Who-Is(유니캐스트) -> I-Am 수신(진짜ID 획득) -> 즉시 수집 시작
        /// </summary>
        public async Task<List<Dictionary<string, object>>> HarvestPoints(string ip)
        {
            var resultList = new List<Dictionary<string, object>>();
            uint realId = 0xFFFFFFFF; // 장비의 진짜 Instance ID를 담을 변수입니다.

            // UDP 47808 포트를 열어 통신을 준비합니다.
            using (var client = new BacnetClient(new BacnetIpUdpProtocolTransport(0, false)))
            {
                try
                {
                    client.Start();
                    var addr = new BacnetAddress(BacnetAddressTypes.IP, ip + ":47808");

                    // 2단계: 그러면 Niagara Station이 내부 장비들의 번호를 알려줍니다. (I-Am 응답)
                    // 이때 낚아챈 번호(예: 34, 158025 등)가 바로 '리얼 ID'입니다.
                    client.OnIam += (c, adr, device_id, max_apdu, segmentation, vendor_id) => {
                        if (adr.ToString().Contains(ip))
                        {
                            realId = device_id; // 드디어 진짜 ID를 찾았습니다!
                        }
                    };
                    // 1단계: IP 주소 하나에 대고 "여기 누구누구 있습니까?"(Who-Is)라고 방송을 보냅니다.
                    client.WhoIs(0, -1, addr);

                    // 응답을 기다립니다. (현장 속도를 고려해 최대 1초만 기다립니다.)
                    int waitCount = 0;
                    while (realId == 0xFFFFFFFF && waitCount < 10)
                    {
                        await Task.Delay(100);
                        waitCount++;
                    }

                    if (realId == 0xFFFFFFFF)
                    {
                        _logger.Warning($"[Service] {ip} 장비가 Who-Is 응답을 주지 않아 수집을 포기합니다.");
                        return resultList;
                    }

                    _logger.Info($"[Service] {ip} 진짜 ID 확인됨: {realId}. 포인트 리스트를 조회합니다.");

                    // 3단계: 이제 "IP(아파트 주소)"와 "리얼 ID(호수)"를 조합해서 정확히 찌릅니다.
                    // 이 리얼 ID가 정확해야만 장비가 "아, 내 데이터를 달라는 거구나" 하고 Object_List를 내놓습니다.
                    var deviceOid = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, realId);
                    IList<BacnetValue> listCount;

                    // 0번 인덱스로 전체 포인트가 몇 개인지 묻습니다.
                    if (!client.ReadPropertyRequest(addr, deviceOid, BacnetPropertyIds.PROP_OBJECT_LIST, out listCount, 0, 0))
                    {
                        _logger.Error($"[Service] {ip}(ID:{realId}) 포인트 개수 조회 실패.");
                        return resultList;
                    }

                    int count = Convert.ToInt32(listCount[0].Value);
                    _logger.Info($"[Service] {ip} 장비에서 {count}개의 객체를 발견했습니다.");

                    // 3. [상세 수집] 루프를 돌며 개별 포인트의 이름, 설명, 타입을 가져옵니다.
                    for (int i = 1; i <= count; i++)
                    {
                        IList<BacnetValue> objIdVal;
                        if (client.ReadPropertyRequest(addr, deviceOid, BacnetPropertyIds.PROP_OBJECT_LIST, out objIdVal, 0, (uint)i))
                        {
                            var oid = (BacnetObjectId)objIdVal[0].Value;

                            // 우리가 관리하는 대상(AI~MSV)만 딕셔너리에 담습니다.
                            if (SiMapping.ContainsKey(oid.Type))
                            {
                                var ptData = new Dictionary<string, object>();
                                var map = SiMapping[oid.Type];

                                ptData["SYSTEM_PT_ID"] = $"{map.Prefix}-{oid.Instance}";
                                ptData["OBJ_NAME"] = ReadProp(client, addr, oid, BacnetPropertyIds.PROP_OBJECT_NAME);
                                ptData["OBJ_DESC"] = ReadProp(client, addr, oid, BacnetPropertyIds.PROP_DESCRIPTION);
                                ptData["OBJ_TYPE"] = map.TypeId;
                                ptData["OBJ_DECIMAL"] = (map.TypeId <= 2) ? 1 : 0;

                                // MSI, MSO, MSV인 경우 '가동/정지' 같은 상태 이름을 10개까지 가져옵니다.
                                if (map.TypeId >= 6 && map.TypeId <= 8)
                                {
                                    IList<BacnetValue> states;
                                    if (client.ReadPropertyRequest(addr, oid, BacnetPropertyIds.PROP_STATE_TEXT, out states))
                                    {
                                        for (int s = 0; s < 10; s++)
                                        {
                                            ptData[$"OBJ_STATUS{s + 1}"] = (s < states.Count) ? states[s].Value.ToString() : "";
                                        }
                                    }
                                }
                                resultList.Add(ptData);
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.Error($"[Service] {ip} 처리 중 오류", ex); }
            }
            return resultList;
        }

        // 특정 속성값을 안전하게 읽어오기 위한 헬퍼 함수입니다.
        private string ReadProp(BacnetClient c, BacnetAddress a, BacnetObjectId o, BacnetPropertyIds p)
        {
            try
            {
                IList<BacnetValue> v;
                if (c.ReadPropertyRequest(a, o, p, out v) && v.Count > 0) return v[0].Value.ToString();
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 수집된 데이터를 SI 클라이언트 업로드용 표준 22개 컬럼 CSV 형식으로 저장합니다.
        /// </summary>
        public void ExportToSiCsv(string filePath, List<Dictionary<string, object>> points, int deviceSeq, int deviceId)
        {
            var sb = new StringBuilder();
            var headers = new[] { "SYSTEM_PT_ID", "OBJ_NAME", "OBJ_DESC", "OBJ_TYPE", "DEVICE_SEQ", "DEVICE_ID", "OBJ_DECIMAL", "OBJ_STATUS1", "OBJ_STATUS2", "OBJ_STATUS3", "OBJ_STATUS4", "OBJ_STATUS5", "OBJ_STATUS6", "OBJ_STATUS7", "OBJ_STATUS8", "OBJ_STATUS9", "OBJ_STATUS10", "DATETIME" };
            sb.AppendLine(string.Join(",", headers));

            foreach (var pt in points)
            {
                var row = new List<string>();
                row.Add(pt["SYSTEM_PT_ID"].ToString());
                row.Add($"\"{pt["OBJ_NAME"]}\"");
                row.Add($"\"{pt["OBJ_DESC"]}\"");
                row.Add(pt["OBJ_TYPE"].ToString());
                row.Add(deviceSeq.ToString());
                row.Add(deviceId.ToString());
                row.Add(pt["OBJ_DECIMAL"].ToString());
                for (int s = 1; s <= 10; s++)
                {
                    string key = $"OBJ_STATUS{s}";
                    row.Add(pt.ContainsKey(key) ? $"\"{pt[key]}\"" : "\"\"");
                }
                row.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(string.Join(",", row));
            }
            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }
    }
}