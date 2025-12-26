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

        // 장비 정체 파악을 위한 제조사 및 모델명 조회
        // [출처: image_137195.png의 Vendor/Model 정보를 가져오기 위한 핵심 메서드]
        // 장비 정체(Vendor) 확인 메서드
        // 장비의 벤더, 모델, 객체이름(Station Name)을 수집합니다.
        public async Task<Dictionary<string, string>> GetDeviceInfo(string ip, uint deviceId)
        {
            var info = new Dictionary<string, string> {
        { "Vendor", "Unknown" },
        { "Model", "Unknown" },
        { "DeviceName", "Unknown" }
    };

            using (var client = new BacnetClient(new BacnetIpUdpProtocolTransport(0, false)))
            {
                try
                {
                    client.Start();
                    var addr = new BacnetAddress(BacnetAddressTypes.IP, ip + ":47808");
                    var oid = new BacnetObjectId(BacnetObjectTypes.OBJECT_DEVICE, deviceId);

                    // 1. Vendor_Name (121)
                    info["Vendor"] = ReadProp(client, addr, oid, BacnetPropertyIds.PROP_VENDOR_NAME);
                    // 2. Model_Name (70)
                    info["Model"] = ReadProp(client, addr, oid, BacnetPropertyIds.PROP_MODEL_NAME);
                    // 3. Object_Name (77) - 현장의 Station 명칭
                    info["DeviceName"] = ReadProp(client, addr, oid, BacnetPropertyIds.PROP_OBJECT_NAME);
                }
                catch { }
            }
            return info;
        }

        /// <summary>
        /// [추가] 특정 IP로 Who-Is 패킷을 보내 장비의 존재 여부와 ID를 확인합니다.
        /// </summary>
        // [BacnetInventoryService.cs]

        public async Task<(string Ip, uint DeviceId)> DirectScanDevice(string ip)
        {
            uint foundId = 0xFFFFFFFF; // 기본값: 찾지 못함 (UInt32.MaxValue)

            // 포트 0: OS가 할당하는 임의의 남는 포트 사용 (포트 충돌 방지)
            using (var client = new BacnetClient(new BacnetIpUdpProtocolTransport(0, false)))
            {
                try
                {
                    client.Start();
                    var addr = new BacnetAddress(BacnetAddressTypes.IP, ip + ":47808");

                    // I-Am 응답 핸들러
                    client.OnIam += (c, adr, device_id, max_apdu, segmentation, vendor_id) => {
                        // 응답 온 IP가 내가 찌른 IP인지 확인
                        if (adr.ToString().Contains(ip)) foundId = device_id;
                    };

                    // 유니캐스트 Who-Is 전송
                    client.WhoIs(0, -1, addr);

                    // ⭐ [수정] 대기 시간 최적화 (총 300ms 대기)
                    // 기존: 100ms * 8회 = 800ms (너무 김)
                    // 변경: 50ms * 6회 = 300ms (0.3초)
                    // 이유: 없는 장비는 0.3초 만에 빠르게 포기하고 다음으로 넘어가야 함
                    int wait = 0;
                    while (foundId == 0xFFFFFFFF && wait < 6)
                    {
                        await Task.Delay(50); // 50ms 단위로 짧게 체크
                        wait++;
                    }
                }
                catch
                {
                    // 통신 에러 발생 시(방화벽, 네트워크 단절 등) 즉시 무시하고 리턴
                }
            }

            // 찾았으면 ID 반환, 못 찾았으면 0xFFFFFFFF 반환
            return (ip, foundId);
        }


        /// <summary>
        /// [델타 추출] DB 설정값(Server, System, DeviceId)을 그대로 유지하며 신규 포인트 생성
        /// </summary>
        // [BacnetInventoryService.cs]

        public void ExportDeltaOnly(
            string masterFilePath,
            List<Dictionary<string, object>> scannedPoints,
            int serverId,
            int systemCode,
            int dbDeviceId,
            string fixCodeNo,
            int bacnetId
            )
        {
            try
            {
                // 사용할 설정값 변수 (기본값: DB에서 가져온 값)
                int targetServerId = serverId;
                int targetSystemId = systemCode;
                int targetDeviceId = dbDeviceId;

                HashSet<string> existingIds = new HashSet<string>();

                // 1. 마스터 파일이 있으면 -> 설정값 읽어오기 (상속)
                if (File.Exists(masterFilePath))
                {
                    var lines = File.ReadAllLines(masterFilePath, GetEncoding(masterFilePath));

                    // 데이터가 있는 경우(헤더 제외 최소 1줄 이상)
                    if (lines.Length > 1)
                    {
                        var firstRow = lines[1].Split(','); // 첫 번째 데이터 행 파싱
                        if (firstRow.Length >= 3)
                        {
                            // ⭐ [핵심] 마스터 파일의 설정값을 우선 적용 (기존 데이터와 통일)
                            int.TryParse(firstRow[0], out targetServerId); // SERVER_ID
                            int.TryParse(firstRow[1], out targetSystemId); // SYSTEM_ID
                            int.TryParse(firstRow[2], out targetDeviceId); // DEVICE_ID
                        }
                    }

                    // 중복 체크용 ID 수집
                    foreach (var line in lines.Skip(1))
                    {
                        var cols = line.Split(',');
                        if (cols.Length > 5) existingIds.Add(cols[5].Trim().Replace("\"", ""));
                    }
                }

                // 2. 신규 포인트(Delta)만 필터링
                var delta = scannedPoints.Where(p => !existingIds.Contains(p["SYSTEM_PT_ID"].ToString())).ToList();
                if (delta.Count == 0) return;

                string outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "SI_DELTA_ONLY");
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

                string outPath = Path.Combine(outDir, $"Add-S-{fixCodeNo}-{bacnetId}.csv");

                // 3. CSV 쓰기
                using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
                {
                    sw.WriteLine("SERVER_ID,SYSTEM_ID,DEVICE_ID,OBJ_TYPE,DEVICE_SEQ,SYSTEM_PT_ID,OBJ_NAME,OBJ_UNIT_NUM,OBJ_DECIMAL,OBJ_NUMBER,ROUND_YN,OBJ_DESC,OBJ_STATUS1,OBJ_STATUS2,OBJ_STATUS3,OBJ_STATUS4,OBJ_STATUS5,OBJ_STATUS6,OBJ_STATUS7,OBJ_STATUS8,OBJ_STATUS9,OBJ_STATUS10,ALARM_LV,DEADBAND,OBJ_ABOVE,OBJ_BELOW,OBJ_IMPORTANCE,OBJ_TREND_CYCLE,OBJ_ALARM_PAGE,OBJ_ALARM_MSG,OBJ_FMS,FMS_NAME,FMS_START_DATA,FMS_UPDATA_CYCLE,OBJ_SECURITY,OBJ_PDA,OBJ_NOTE");

                    foreach (var pt in delta)
                    {
                        int type = Convert.ToInt32(pt["OBJ_TYPE"]);

                        // ⭐ targetSystemId 등을 사용하여 기존 파일과 ID 통일
                        var row = new List<string> {
                    targetServerId.ToString(),  // SERVER_ID (파일 값)
                    targetSystemId.ToString(),  // SYSTEM_ID (파일 값)
                    targetDeviceId.ToString(),  // DEVICE_ID (파일 값)
                    type.ToString(),
                    fixCodeNo,

                    Clean(pt["SYSTEM_PT_ID"]),
                    Clean(pt["OBJ_NAME"]),
                    pt.ContainsKey("OBJ_UNIT") ? Clean(pt["OBJ_UNIT"]) : "",
                    pt["OBJ_DECIMAL"].ToString(),
                    "0", "False",
                    Clean(pt["OBJ_DESC"])
                };

                        for (int s = 1; s <= 10; s++) row.Add("");
                        row.AddRange(new[] { "0", "0", (type <= 2 ? "100000" : "1"), (type <= 2 ? "-100000" : "-1"), "False", "", "/0", "", "False", "", "", "", "254", "False", "" });

                        sw.WriteLine(string.Join(",", row));
                    }
                }
                _logger.Info($"[델타생성] {Path.GetFileName(outPath)} (+{delta.Count}건, SystemID:{targetSystemId})");
            }
            catch (Exception ex)
            {
                _logger.Error("CSV 생성 실패", ex);
            }
        }

        // ⭐ [해결] 데이터 내의 콤마(,)를 제거하고 따옴표를 정제하는 함수
        private string Clean(object val)
        {
            if (val == null) return "";
            string s = val.ToString();
            // 데이터 내 콤마는 공백으로 치환 (컬럼 밀림 방지)
            s = s.Replace(",", " ");
            // 줄바꿈 제거 및 따옴표 정제
            s = s.Replace("\r", "").Replace("\n", "").Replace("\"", "");
            return s;
        }

        // [BacnetInventoryService.cs]

        /// <summary>
        /// [변경 감지 및 SQL 생성]
        /// - Master 파일과 현재 스캔 데이터를 비교하여 Update/Delete 내역 추출
        /// - FIX_CODENO를 기준으로 SQL 생성 (안전성 강화)
        /// - 변경 전 값을 주석(-- Previous)으로 남겨 쿼리 실행 시 검증 가능
        /// </summary>
        /// <param name="masterFilePath">비교할 마스터 CSV 파일 경로</param>
        /// <param name="scannedPoints">현장에서 스캔된 최신 포인트 리스트</param>
        /// <param name="bacnetId">실제 통신용 BACnet Instance ID</param>
        /// <param name="fixCodeNo">SI 관리용 고유 코드 (DB Key)</param>
        public void DetectChangesAndGenerateSql(string masterFilePath, List<Dictionary<string, object>> scannedPoints, int bacnetId, string fixCodeNo)
        {
            try
            {
                if (!File.Exists(masterFilePath)) return;

                // 1. 인코딩 자동 감지 및 파일 로드 (한글 깨짐 방지)
                Encoding detectedEncoding = GetEncoding(masterFilePath);
                var lines = File.ReadAllLines(masterFilePath, detectedEncoding);

                if (lines.Length < 1) return;

                var headers = lines[0].Split(',');

                // 컬럼 인덱스 매핑 (마스터 파일 구조에 맞게 동적 탐색)
                int idxId = Array.IndexOf(headers, "SYSTEM_PT_ID");
                int idxName = Array.IndexOf(headers, "OBJ_NAME");
                int idxUnit = Array.IndexOf(headers, "OBJ_UNIT_NUM");
                int idxDesc = Array.IndexOf(headers, "OBJ_DESC");
                int idxDec = Array.IndexOf(headers, "OBJ_DECIMAL");
                int idxType = Array.IndexOf(headers, "OBJ_TYPE");

                var masterMap = new Dictionary<string, string[]>();

                // 마스터 데이터 메모리 적재
                foreach (var line in lines.Skip(1))
                {
                    var cols = line.Split(',');
                    if (cols.Length > idxId && idxId >= 0)
                    {
                        // 따옴표 제거 후 Key 저장
                        string ptId = cols[idxId].Trim().Replace("\"", "");
                        masterMap[ptId] = cols;
                    }
                }

                // 2. 결과 저장 경로 설정
                string histDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "HISTORY");
                if (!Directory.Exists(histDir)) Directory.CreateDirectory(histDir);

                // 파일명 생성: History_S-{FixCodeNo}-{BacnetId}_{Date}.csv
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
                string histPath = Path.Combine(histDir, $"History_S-{fixCodeNo}-{bacnetId}_{timestamp}.csv");
                string sqlPath = Path.Combine(histDir, $"Query_S-{fixCodeNo}-{bacnetId}_{timestamp}.sql");

                StringBuilder sqlBuilder = new StringBuilder();
                StringBuilder histBuilder = new StringBuilder();

                // 히스토리 CSV 헤더 (식별용 OBJ_NAME 포함)
                histBuilder.AppendLine("TIMESTAMP,DEVICE_SEQ,SYSTEM_PT_ID,OBJ_NAME,ACTION,COLUMN,OLD_VALUE,NEW_VALUE");

                bool hasChanges = false;

                // 3. [Update 감지] 스캔된 포인트를 순회하며 마스터와 비교
                foreach (var scanPt in scannedPoints)
                {
                    string ptId = scanPt["SYSTEM_PT_ID"].ToString();

                    if (masterMap.ContainsKey(ptId))
                    {
                        var masterCols = masterMap[ptId];
                        // 식별용 현재 이름 (로그 남길 때 어떤 포인트인지 사람이 알아보기 위함)
                        string currentObjName = scanPt.ContainsKey("OBJ_NAME") ? Clean(scanPt["OBJ_NAME"]) : "";

                        // 비교 대상 컬럼 정의
                        var targets = new List<(int Idx, string Key, string ColName)>
                {
                    (idxName, "OBJ_NAME", "OBJ_NAME"),
                    (idxDesc, "OBJ_DESC", "OBJ_DESC"),
                    (idxUnit, "OBJ_UNIT", "OBJ_UNIT_NUM"),
                    (idxDec, "OBJ_DECIMAL", "OBJ_DECIMAL"),
                    (idxType, "OBJ_TYPE", "OBJ_TYPE")
                };

                        foreach (var t in targets)
                        {
                            if (t.Idx < 0) continue; // 해당 컬럼이 마스터 파일에 없으면 패스

                            // Clean() 함수로 공백/따옴표/특수문자 정제 후 비교
                            string oldVal = Clean(masterCols[t.Idx]);
                            string newVal = scanPt.ContainsKey(t.Key) ? Clean(scanPt[t.Key]) : "";

                            if (oldVal != newVal)
                            {
                                hasChanges = true;

                                // [CSV 기록] 변경 이력 저장
                                histBuilder.AppendLine($"{DateTime.Now},{fixCodeNo},{ptId},\"{currentObjName}\",UPDATE,{t.ColName},\"{oldVal}\",\"{newVal}\"");

                                // [SQL 생성] 안전한 쿼리 생성 (이스케이프 처리)
                                string safeVal = newVal.Replace("'", "''");

                                // ⭐ [핵심] WHERE 조건에 FIX_CODENO 사용 + 주석으로 이전 값 표시
                                sqlBuilder.AppendLine($"UPDATE [dbo].[P_OBJECT] SET [{t.ColName}] = '{safeVal}' WHERE [DEVICE_SEQ] = '{fixCodeNo}' AND [SYSTEM_PT_ID] = '{ptId}'; -- Previous: '{oldVal}'");
                            }
                        }
                        // 비교가 끝난 포인트는 맵에서 제거 (나중에 남은 건 Delete 처리)
                        masterMap.Remove(ptId);
                    }
                }

                // 4. [Delete 감지] 마스터에는 있는데 스캔에서 사라진 포인트
                foreach (var kvp in masterMap)
                {
                    string deletedId = kvp.Key;
                    string[] deletedCols = kvp.Value;

                    string deletedObjName = (idxName >= 0 && deletedCols.Length > idxName) ? Clean(deletedCols[idxName]) : "Unknown";

                    hasChanges = true;
                    // [CSV] 삭제 이력
                    histBuilder.AppendLine($"{DateTime.Now},{fixCodeNo},{deletedId},\"{deletedObjName}\",DELETE,ALL,Exist,Removed");

                    // [SQL] 삭제 쿼리 (위험하므로 주석 경고 추가)
                    sqlBuilder.AppendLine($"-- [WARNING] Point Removed: {deletedObjName} ({deletedId})");
                    sqlBuilder.AppendLine($"DELETE FROM [dbo].[P_OBJECT] WHERE [DEVICE_SEQ] = '{fixCodeNo}' AND [SYSTEM_PT_ID] = '{deletedId}';");
                }

                // 5. 파일 저장 (변경사항이 있을 때만)
                if (hasChanges)
                {
                    // UTF-8 BOM으로 저장 (엑셀 한글 깨짐 방지)
                    File.WriteAllText(histPath, histBuilder.ToString(), new UTF8Encoding(true));
                    File.WriteAllText(sqlPath, sqlBuilder.ToString(), new UTF8Encoding(true));
                    _logger.Info($"[변경감지] {Path.GetFileName(histPath)} 생성 완료.");
                }
                else
                {
                    _logger.Info($"[변경없음] {fixCodeNo} (ID:{bacnetId})는 최신 상태입니다.");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"히스토리 분석 실패 (Key:{fixCodeNo})", ex);
            }
        }

        // =========================================================
        // [추가] 인코딩 자동 판별 헬퍼 메서드
        // =========================================================
        private Encoding GetEncoding(string filename)
        {
            // 1. BOM(Byte Order Mark) 검사
            var bom = new byte[4];
            using (var file = new FileStream(filename, FileMode.Open, FileAccess.Read))
            {
                file.Read(bom, 0, 4);
            }

            // UTF-8 BOM (EF BB BF)
            if (bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) return Encoding.UTF8;

            // 그 외에는 한국어 윈도우 기본(CP949)로 간주
            // (.NET Core/5+ 환경에서는 System.Text.Encoding.CodePages 패키지 필요할 수 있음)
            // 여기서는 기본적으로 949 코드페이지를 시도합니다.
            try
            {
                return Encoding.GetEncoding(949);
            }
            catch
            {
                // 만약 949를 지원하지 않는 환경이라면 Default 사용
                return Encoding.Default;
            }
        }
        // [BacnetInventoryService.cs]

        /// <summary>
        /// [통합] 오늘 생성된 개별 히스토리 파일들을 하나로 병합합니다. (Total_History_YYYYMMDD.csv)
        /// </summary>
        public string MergeTodayHistoryFiles()
        {
            try
            {
                string histDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "HISTORY");
                if (!Directory.Exists(histDir)) return null;

                // 1. 오늘 날짜로 생성된 파일만 검색 (예: History_S-*_20251226_*.csv)
                string today = DateTime.Now.ToString("yyyyMMdd");
                string[] files = Directory.GetFiles(histDir, $"History_*_{today}_*.csv");

                if (files.Length == 0) return null;

                // 2. 통합 파일명 생성
                string summaryPath = Path.Combine(histDir, $"Total_History_{today}.csv");
                StringBuilder sb = new StringBuilder();

                // 3. 헤더 추가 (한 번만)
                sb.AppendLine("TIMESTAMP,DEVICE_SEQ,SYSTEM_PT_ID,OBJ_NAME,ACTION,COLUMN,OLD_VALUE,NEW_VALUE");

                int mergedCount = 0;

                // 4. 파일 순회하며 내용 병합
                foreach (string file in files)
                {
                    // 방금 만든 통합 파일(Total_History...)이 검색되면 건너뜀 (무한루프 방지)
                    if (Path.GetFileName(file).StartsWith("Total_")) continue;

                    // 내용 읽기 (UTF-8)
                    var lines = File.ReadAllLines(file, Encoding.UTF8);

                    // 헤더(첫 줄) 제외하고 나머지 줄 복사
                    if (lines.Length > 1)
                    {
                        for (int i = 1; i < lines.Length; i++)
                        {
                            sb.AppendLine(lines[i]);
                            mergedCount++;
                        }
                    }
                }

                if (mergedCount == 0) return null;

                // 5. 통합 파일 저장 (UTF-8 BOM) - 엑셀 한글 깨짐 방지
                File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(true));

                _logger.Info($"[통합완료] {Path.GetFileName(summaryPath)} 생성됨 ({mergedCount}건)");
                return summaryPath;
            }
            catch (Exception ex)
            {
                _logger.Error("히스토리 병합 실패", ex);
                return null;
            }
        }
        // [BacnetInventoryService.cs] 맨 아래에 추가

        /// <summary>
        /// [SQL 통합] 오늘 생성된 개별 SQL 파일들을 하나의 실행 스크립트로 병합합니다.
        /// </summary>
        public string MergeTodaySqlFiles()
        {
            try
            {
                string histDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Exports", "HISTORY");
                if (!Directory.Exists(histDir)) return null;

                string today = DateTime.Now.ToString("yyyyMMdd");
                // 오늘 날짜의 Query_*.sql 파일 검색
                string[] files = Directory.GetFiles(histDir, $"Query_*_{today}_*.sql");

                if (files.Length == 0) return null;

                string summaryPath = Path.Combine(histDir, $"Total_Query_{today}.sql");
                StringBuilder sb = new StringBuilder();

                // 1. SQL 헤더 작성 (DB 컨텍스트 지정 등)
                sb.AppendLine($"-- [Total SQL Script] Generated at {DateTime.Now}");
                sb.AppendLine($"-- 실행 전 DB 백업을 권장합니다.");
                sb.AppendLine("USE [IBSInfo];"); // DB명 지정 (필요 시 수정)
                sb.AppendLine("GO");
                sb.AppendLine("");

                int mergedCount = 0;

                foreach (string file in files)
                {
                    // 통합 파일 본인은 제외
                    if (Path.GetFileName(file).StartsWith("Total_")) continue;

                    // 내용 읽기 (한글 깨짐 방지 UTF-8)
                    string content = File.ReadAllText(file, Encoding.UTF8);

                    // 빈 파일이 아니면 병합
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        sb.AppendLine($"-- ===============================================");
                        sb.AppendLine($"-- Source: {Path.GetFileName(file)}");
                        sb.AppendLine($"-- ===============================================");
                        sb.AppendLine(content);
                        sb.AppendLine("GO"); // 배치 분리기 (SSMS 실행용)
                        sb.AppendLine("");
                        mergedCount++;
                    }
                }

                if (mergedCount == 0) return null;

                // 2. 통합 파일 저장 (UTF-8 BOM 필수 - SSMS에서 한글 주석 깨짐 방지)
                File.WriteAllText(summaryPath, sb.ToString(), new UTF8Encoding(true));

                _logger.Info($"[SQL통합완료] {Path.GetFileName(summaryPath)} ({mergedCount}건 병합)");
                return summaryPath;
            }
            catch (Exception ex)
            {
                _logger.Error("SQL 병합 실패", ex);
                return null;
            }
        }
    }
}