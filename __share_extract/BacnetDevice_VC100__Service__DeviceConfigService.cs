using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.IO.BACnet;
using System.Xml.Linq;
using BacnetDevice_VC100.Model;
using BacnetDevice_VC100.Util;

namespace BacnetDevice_VC100.Data
{
    /// <summary>
    /// 장비 설정 서비스
    /// 
    /// [역할]
    /// - DB에서 포인트 설정 로딩
    /// - Config.XML에서 DB 연결 정보 읽기
    /// </summary>
    public class DeviceConfigService
    {
        private readonly int _deviceSeq;
        private readonly BacnetLogger _logger;
        private readonly string _connectionString;

        /// <summary>
        /// 생성자
        /// 
        /// [처리 순서]
        /// 1. Config.XML에서 DB 연결 정보 읽기
        /// 2. ConnectionString 생성
        /// </summary>
        public DeviceConfigService(int deviceSeq, BacnetLogger logger)
        {
            _deviceSeq = deviceSeq;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Config.XML에서 DB 연결 정보 읽기
            _connectionString = LoadConnectionStringFromConfig();
        }

        /// <summary>
        /// Config.XML에서 DB 연결 문자열 읽기
        /// 
        /// [Config.XML 경로]
        /// 실행폴더\xml\Config.xml
        /// 
        /// [데이터 흐름]
        /// AppDomain.CurrentDomain.BaseDirectory
        ///   ↓ "C:\Surable\SmartDeviceAgent(bIoT)-1\"
        ///   ↓ Path.Combine(exePath, "xml", "Config.xml")
        ///   ↓ "C:\Surable\SmartDeviceAgent(bIoT)-1\xml\Config.xml"
        ///   ↓ XDocument.Load()
        ///   ↓ <Common> 노드 읽기
        ///   ↓ ServerIP, DBMainDataBaseName, DBUserID, DBUserPass
        ///   ↓ PasswordDecryptor.Decrypt(encryptedPassword)
        ///   ↓ ConnectionString 생성
        /// </summary>
        private string LoadConnectionStringFromConfig()
        {
            try
            {
                string exePath = AppDomain.CurrentDomain.BaseDirectory;
                string configPath = System.IO.Path.Combine(exePath, "xml", "Config.xml");

                _logger.Info($"=== Config.XML 로딩 시작 ===");
                _logger.Info($"경로: {configPath}");
                _logger.Info($"존재: {System.IO.File.Exists(configPath)}");

                if (!System.IO.File.Exists(configPath))
                {
                    _logger.Error($"Config.XML 파일 없음");
                    return GetFallbackConnectionString();
                }

                XDocument doc = XDocument.Load(configPath);
                var commonNode = doc.Root?.Element("Common");

                _logger.Info($"Common 노드: {(commonNode == null ? "없음" : "있음")}");

                if (commonNode == null)
                {
                    _logger.Error("Common 노드 없음");
                    return GetFallbackConnectionString();
                }

                string server = commonNode.Element("ServerIP")?.Attribute("value")?.Value;
                string database = commonNode.Element("DB_MainDataBaseName")?.Attribute("value")?.Value;
                string userId = commonNode.Element("DB_UserID")?.Attribute("value")?.Value;

                _logger.Info($"Server: [{server ?? "NULL"}]");
                _logger.Info($"Database: [{database ?? "NULL"}]");
                _logger.Info($"UserID: [{userId ?? "NULL"}]");

                if (string.IsNullOrEmpty(server) ||
                    string.IsNullOrEmpty(database) ||
                    string.IsNullOrEmpty(userId))
                {
                    _logger.Error("필수 정보 없음");
                    return GetFallbackConnectionString();
                }

                // ===== 강제로 평문 사용 =====
                _logger.Warning("🔥 강제: 평문 비밀번호 사용 (admin123!@#)");
                string password = "admin123!@#";

                string connectionString = $"Server={server};Database={database};User Id={userId};Password={password};";

                _logger.Info("DB 연결 문자열 생성 완료");
                _logger.Info($"ConnectionString: {connectionString}");

                return connectionString;
            }
            catch (Exception ex)
            {
                _logger.Error("Config.XML 로딩 실패", ex);
                return GetFallbackConnectionString();
            }
        }

        private string GetFallbackConnectionString()
        {
            _logger.Warning("Fallback 사용");
            return "Server=192.168.131.127;Database=IBSInfo;User Id=sa;Password=admin123!@#;";
        }





        

        /// <summary>
        /// DB 연결 생성
        /// </summary>
        private SqlConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        /// <summary>
        /// DB에서 포인트 설정 로딩
        /// 
        /// [데이터 흐름]
        /// P_OBJECT 테이블 (DEVICE_SEQ, SYSTEM_PT_ID, OBJ_TYPE)
        ///   ↓
        /// ParsePointFromReader()
        ///   ↓
        /// List<BacnetPoint> (SystemPtId, ObjectType, ObjectInstance)
        /// 
        /// [쿼리]
        /// SELECT SYSTEM_PT_ID, OBJ_TYPE
        /// FROM P_OBJECT
        /// WHERE DEVICE_SEQ = @deviceSeq
        /// ORDER BY OBJ_COUNT ASC
        /// </summary>
        public List<BacnetPoint> LoadPoints()
        {
            var points = new List<BacnetPoint>();
            int totalRows = 0;
            int skipCount = 0;

            try
            {
                _logger.Info($"포인트 로딩 시작: DeviceSeq={_deviceSeq}");

                using (var conn = CreateConnection())
                {
                    conn.Open();

                    string query = @"
                        SELECT 
                            SYSTEM_PT_ID,
                            OBJ_TYPE
                        FROM P_OBJECT
                        WHERE DEVICE_SEQ = @deviceSeq
                        ORDER BY OBJ_COUNT ASC";

                    using (var cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@deviceSeq", _deviceSeq);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                totalRows++;
                                var point = ParsePointFromReader(reader);

                                if (point != null)
                                {
                                    points.Add(point);
                                }
                                else
                                {
                                    skipCount++;
                                }
                            }
                        }
                    }
                }

                _logger.Info($"포인트 로딩 완료: {points.Count}개 (전체: {totalRows}, 제외: {skipCount})");

                if (skipCount > 0)
                {
                    _logger.Warning($"제외된 포인트: {skipCount}개");
                }

                return points;
            }
            catch (Exception ex)
            {
                _logger.Error("포인트 로딩 실패", ex);
                return new List<BacnetPoint>();
            }
        }

        /// <summary>
        /// DB Reader에서 BacnetPoint 파싱
        /// 
        /// [입력]
        /// - SYSTEM_PT_ID: "AV-101", "BI-39" 등
        /// - OBJ_TYPE: 0(AI), 1(AO), 2(AV), 3(BI), 4(BO), 5(BV), ...
        /// 
        /// [출력]
        /// BacnetPoint {
        ///   SystemPtId = "AV-101",
        ///   ObjectType = OBJECT_ANALOG_VALUE,
        ///   ObjectInstance = 101
        /// }
        /// </summary>
        private BacnetPoint ParsePointFromReader(IDataReader reader)
        {
            try
            {
                string systemPtId = reader["SYSTEM_PT_ID"].ToString().Trim();

                if (string.IsNullOrEmpty(systemPtId))
                {
                    return null;
                }

                int objType = Convert.ToInt32(reader["OBJ_TYPE"]);
                BacnetObjectTypes bacnetObjectType = ConvertObjectTypeFromDB(objType);

                // SYSTEM_PT_ID에서 Instance 추출 (예: "BI-39" → 39)
                if (!systemPtId.Contains("-"))
                {
                    _logger.Warning($"포인트 제외 (형식 오류): {systemPtId}");
                    return null;
                }

                string[] parts = systemPtId.Split('-');
                if (parts.Length < 2)
                {
                    _logger.Warning($"포인트 제외 (파싱 실패): {systemPtId}");
                    return null;
                }

                string instanceStr = parts[parts.Length - 1].Trim();

                if (!uint.TryParse(instanceStr, out uint objectInstance))
                {
                    _logger.Warning($"포인트 제외 (Instance 오류): {systemPtId}");
                    return null;
                }

                return new BacnetPoint
                {
                    DeviceSeq = _deviceSeq,
                    SystemPtId = systemPtId,
                    DeviceInstance = (uint)_deviceSeq,
                    ObjectType = bacnetObjectType,
                    ObjectInstance = objectInstance,
                    IsWritable = false,
                    EnablePolling = true,
                    PollingInterval = 30,
                    FailValue = 0.0f,
                    TimeoutMs = 5000
                };
            }
            catch (Exception ex)
            {
                _logger.Error("포인트 파싱 에러", ex);
                return null;
            }
        }

        /// <summary>
        /// DB OBJ_TYPE을 BACnet ObjectType으로 변환
        /// 
        /// [매핑]
        /// 0 → AI (Analog Input)
        /// 1 → AO (Analog Output)
        /// 2 → AV (Analog Value)
        /// 3 → BI (Binary Input)
        /// 4 → BO (Binary Output)
        /// 5 → BV (Binary Value)
        /// 6 → MSI (Multi-State Input)
        /// 7 → MSO (Multi-State Output)
        /// 8 → MSV (Multi-State Value)
        /// </summary>
        private BacnetObjectTypes ConvertObjectTypeFromDB(int objType)
        {
            switch (objType)
            {
                case 0: return BacnetObjectTypes.OBJECT_ANALOG_INPUT;
                case 1: return BacnetObjectTypes.OBJECT_ANALOG_OUTPUT;
                case 2: return BacnetObjectTypes.OBJECT_ANALOG_VALUE;
                case 3: return BacnetObjectTypes.OBJECT_BINARY_INPUT;
                case 4: return BacnetObjectTypes.OBJECT_BINARY_OUTPUT;
                case 5: return BacnetObjectTypes.OBJECT_BINARY_VALUE;
                case 6: return BacnetObjectTypes.OBJECT_MULTI_STATE_INPUT;
                case 7: return BacnetObjectTypes.OBJECT_MULTI_STATE_OUTPUT;
                case 8: return BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE;
                default:
                    _logger.Warning($"알 수 없는 OBJ_TYPE: {objType}, 기본값(AV) 사용");
                    return BacnetObjectTypes.OBJECT_ANALOG_VALUE;
            }
        }
    }
}
