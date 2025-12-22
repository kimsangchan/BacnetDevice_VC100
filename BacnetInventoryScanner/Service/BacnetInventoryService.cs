using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.IO.BACnet;
using System.Linq;
using System.Text.RegularExpressions;
using BacnetDevice_VC100.Util; // 기존 로그 시스템 활용

namespace BacnetInventoryScanner.Service
{
    /// <summary>
    /// [최종 완성형 서비스]
    /// 1. Niagara Station 리얼 ID 자동 추적 및 직접 통신
    /// 2. 깨진 데이터(\uFFFD) 포함 시 "무결성 보장"을 위해 자동 삭제 처리
    /// 3. 현장 특화 단위(CMH, mmAq, ℃ 등) 완벽 기호 변환
    /// 4. 모든 동작 과정에 촘촘한 주석 완비
    /// </summary>
    public class BacnetInventoryService
    {
        private readonly BacnetLogger _logger;

        // BACnet 오브젝트 타입을 SI 시스템의 P_OBJECT 테이블용 숫자 코드로 매핑합니다.
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
        /// 생성자: 통신 과정의 모든 기록을 남기기 위해 MainForm에서 사용하는 로거를 주입받습니다.
        /// </summary>
        public BacnetInventoryService(BacnetLogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// [메인 엔진] IP 주소로 진짜 장비 ID를 낚아채서, 정제된 포인트 데이터를 수집합니다.
        /// </summary>
        /// <param name="ip">Niagara Station의 IP 주소</param>
        public async Task<List<Dictionary<string, object>>> HarvestPoints(string ip)
        {
            var resultList = new List<Dictionary<string, object>>();
            uint realId = 0xFFFFFFFF; // 네트워크 응답으로 확정될 진짜 장비 인스턴스 ID

            // 표준 UDP 47808 포트를 사용하여 통신 소켓을 엽니다.
            using (var client = new BacnetClient(new BacnetIpUdpProtocolTransport(0, false)))
            {
                try
                {
                    client.Start();
                    var addr = new BacnetAddress(BacnetAddressTypes.IP, ip + ":47808");

                    // [1단계] Who-Is를 특정 IP에 유니캐스트로 보내 진짜 ID(Station ID)를 낚아챕니다.
                    client.OnIam += (c, adr, device_id, max_apdu, segmentation, vendor_id) => {
                        if (adr.ToString().Contains(ip)) realId = device_id;
                    };
                    client.WhoIs(0, -1, addr);

                    // 응답 대기 (현장 네트워크 상황을 고려하여 최대 1.5초 대기)
                    int wait = 0;
                    while (realId == 0xFFFFFFFF && wait < 15) { await Task.Delay(100); wait++; }

                    if (realId == 0xFFFFFFFF)
                    {
                        _logger.Warning($"[Service] {ip} 장비의 진짜 ID를 찾지 못했습니다. 수집을 건너뜁니다.");
                        return resultList;
                    }

                    _logger.Info($"[Service] {ip} 리얼 ID({realId}) 확인. 포인트 리스트를 조회합니다.");

                    // [2단계] 진짜 ID를 사용하여 장비 내부의 모든 객체 목록(Object_List)을 조회합니다.
                    var deviceOid = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, realId);
                    IList<BacnetValue> listCount;
                    if (!client.ReadPropertyRequest(addr, deviceOid, BacnetPropertyIds.PROP_OBJECT_LIST, out listCount, 0, 0))
                    {
                        _logger.Error($"[Service] {ip}(ID:{realId}) 포인트 개수 조회 실패.");
                        return resultList;
                    }

                    int count = Convert.ToInt32(listCount[0].Value);
                    _logger.Info($"[Service] {ip}에서 총 {count}개의 객체 감지. 상세 수집을 시작합니다.");

                    // [3단계] 감지된 모든 객체를 루프 돌며 상세 속성을 수집 및 정제합니다.
                    for (int i = 1; i <= count; i++)
                    {
                        IList<BacnetValue> objIdVal;
                        if (client.ReadPropertyRequest(addr, deviceOid, BacnetPropertyIds.PROP_OBJECT_LIST, out objIdVal, 0, (uint)i))
                        {
                            var oid = (BacnetObjectId)objIdVal[0].Value;

                            // SI 수집 대상 타입(AI~MSV)만 처리합니다.
                            if (SiMapping.ContainsKey(oid.Type))
                            {
                                var ptData = new Dictionary<string, object>();
                                var map = SiMapping[oid.Type];

                                // [데이터 수집] 깨진 글자 감지 로직(SanitizeData)을 즉각 적용합니다.
                                ptData["SYSTEM_PT_ID"] = $"{map.Prefix}-{oid.Instance}";
                                ptData["OBJ_NAME"] = SanitizeData(ReadProp(client, addr, oid, BacnetPropertyIds.PROP_OBJECT_NAME));
                                ptData["OBJ_DESC"] = SanitizeData(ReadProp(client, addr, oid, BacnetPropertyIds.PROP_DESCRIPTION));
                                ptData["OBJ_TYPE"] = map.TypeId;
                                ptData["OBJ_DECIMAL"] = (map.TypeId <= 2) ? 1 : 0;

                                // [단위 수집] 95 무시 및 CMH, mmAq 기호 변환 적용
                                string unitRaw = ReadProp(client, addr, oid, BacnetPropertyIds.PROP_UNITS);
                                ptData["OBJ_UNIT"] = MapUnitSymbol(unitRaw);

                                // [상태값 수집] MSO 등 멀티스테이트 포인트의 상태 리스트 1~10 수집
                                if (map.TypeId >= 6 && map.TypeId <= 8)
                                {
                                    IList<BacnetValue> states;
                                    if (client.ReadPropertyRequest(addr, oid, BacnetPropertyIds.PROP_STATE_TEXT, out states))
                                    {
                                        for (int s = 0; s < 10; s++)
                                        {
                                            string statusText = (s < states.Count) ? states[s].Value.ToString() : "";
                                            // 상태값도 깨졌다면 빈칸으로 처리하여 데이터 품질을 유지합니다.
                                            ptData[$"OBJ_STATUS{s + 1}"] = SanitizeData(statusText);
                                        }
                                    }
                                }
                                resultList.Add(ptData);
                            }
                        }
                    }
                }
                catch (Exception ex) { _logger.Error($"[Service] {ip} Harvest 도중 치명적 오류 발생", ex); }
            }

            _logger.Info($"[Service] {ip} 수집 완료. 유효 포인트: {resultList.Count}건");
            return resultList;
        }

        /// <summary>
        /// 데이터 품질 정화: 유니코드 대체 문자(\uFFFD)나 제어 문자가 포함되면 아예 비웁니다.
        /// </summary>
        private string SanitizeData(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";

            // 1. 유니코드 대체 문자()가 포함되어 있다면 해석 실패 데이터이므로 무조건 비웁니다.
            if (input.Contains("\uFFFD")) return "";

            // 2. 아스키 제어 문자(0~31번)를 제거하고 공백을 다듬습니다.
            string clean = Regex.Replace(input, @"[\x00-\x1F]", "").Trim();

            return clean;
        }

        /// <summary>
        /// BACnet 표준 단위 인덱스를 현장용 단위 기호로 번역합니다.
        /// </summary>
        private string MapUnitSymbol(string code)
        {
            if (string.IsNullOrEmpty(code) || code == "95") return ""; // 95: 단위없음(No Units)

            switch (code)
            {
                case "62": return "℃";      // Degrees Celsius
                case "98": return "%";       // Percent
                case "135": return "CMH";    // Cubic Meters per Hour (m³/h)
                case "206": return "mmAq";   // Millimeters of Water (mmH2O)
                case "27": return "Hz";      // Hertz
                case "48": return "kW";      // Kilowatts
                case "19": return "kWh";     // Kilowatt-hours
                case "53": return "Pa";      // Pascals
                case "54": return "kPa";     // Kilopascals
                case "111": return "rpm";    // Revolutions per minute
                default: return code;        // 그 외 코드는 분석용으로 숫자 유지
            }
        }

        /// <summary>
        /// 속성을 안전하게 읽어오는 헬퍼 함수입니다.
        /// </summary>
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
        /// 수집된 데이터를 SI 표준 CSV로 저장합니다. 한국어 엑셀 호환을 위해 CP949를 사용합니다.
        /// </summary>
        public void ExportToSiCsv(string filePath, List<Dictionary<string, object>> points, int deviceSeq, int deviceId)
        {
            var sb = new StringBuilder();
            // SI 시스템의 P_OBJECT 테이블 컬럼 스키마와 완벽히 일치시킵니다.
            var headers = new[] { "SYSTEM_PT_ID", "OBJ_NAME", "OBJ_DESC", "OBJ_TYPE", "DEVICE_SEQ", "DEVICE_ID", "OBJ_DECIMAL", "OBJ_NUMBER", "ROUND_YN", "OBJ_IMPORTANCE", "OBJ_SECURITY", "OBJ_UNIT", "OBJ_STATUS1", "OBJ_STATUS2", "OBJ_STATUS3", "OBJ_STATUS4", "OBJ_STATUS5", "OBJ_STATUS6", "OBJ_STATUS7", "OBJ_STATUS8", "OBJ_STATUS9", "OBJ_STATUS10", "DATETIME" };
            sb.AppendLine(string.Join(",", headers));

            foreach (var pt in points)
            {
                var row = new List<string>();
                row.Add(pt["SYSTEM_PT_ID"].ToString());
                // 데이터 내부에 콤마(,)가 포함되어 문서가 깨지는 것을 방지하기 위해 모든 텍스트를 따옴표로 감쌉니다.
                row.Add($"\"{pt["OBJ_NAME"]}\"");
                row.Add($"\"{pt["OBJ_DESC"]}\"");
                row.Add(pt["OBJ_TYPE"].ToString());
                row.Add(deviceSeq.ToString());
                row.Add(deviceId.ToString());
                row.Add(pt["OBJ_DECIMAL"].ToString());

                // SI 시스템 기본값들
                row.Add("0"); row.Add("false"); row.Add("false"); row.Add("254");

                // 단위 및 상태값 10개 배치
                row.Add(pt.ContainsKey("OBJ_UNIT") ? $"\"{pt["OBJ_UNIT"]}\"" : "\"\"");
                for (int s = 1; s <= 10; s++)
                {
                    string key = $"OBJ_STATUS{s}";
                    row.Add(pt.ContainsKey(key) ? $"\"{pt[key]}\"" : "\"\"");
                }

                row.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine(string.Join(",", row));
            }

            // ⭐ 엑셀에서 한글이 절대 깨지지 않도록 CP949(한국어 ANSI) 인코딩으로 저장합니다.
            File.WriteAllText(filePath, sb.ToString(), Encoding.GetEncoding(949));
        }
    }
}