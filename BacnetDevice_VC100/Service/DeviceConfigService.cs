using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Xml.Linq;
using BacnetDevice_VC100.Model;
using BacnetDevice_VC100.Util;

// ⭐ 네임스페이스를 BacnetBAS.cs에서 using한 것과 일치시킴
namespace BacnetDevice_VC100.Service
{
    public class DeviceConfigService
    {
        private readonly int _deviceSeq;
        private readonly BacnetLogger _logger;
        private string _connectionString;

        public DeviceConfigService(int deviceSeq, BacnetLogger logger)
        {
            _deviceSeq = deviceSeq;
            _logger = logger;
            // ⭐ 생성자에서 예외 발생 가능성 있음! 안전하게 처리 필요
            try
            {
                _connectionString = LoadConnectionString();
            }
            catch (Exception ex)
            {
                _logger.Error("ConnectionString 로드 실패", ex);
                _connectionString = ""; // 빈 문자열로 초기화해서 NullReference 방지
            }
        }

        private string LoadConnectionString()
        {
            try
            {
                // Config.xml 경로 (실행 파일 기준)
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "XML", "Config.xml");

                // 파일이 없으면 에러 로그 찍고 기본값 리턴 (또는 null)
                if (!File.Exists(path))
                {
                    _logger.Error($"Config.xml 파일 없음: {path}");
                    return GetDefaultConnectionString();
                }

                var doc = XDocument.Load(path);
                var common = doc.Root?.Element("Common");

                string server = common?.Element("ServerIP")?.Attribute("value")?.Value ?? "127.0.0.1";
                string db = common?.Element("DB_MainDataBaseName")?.Attribute("value")?.Value ?? "IBSInfo";
                string id = common?.Element("DB_UserID")?.Attribute("value")?.Value ?? "sa";

                // ⭐ 암호화된 비밀번호 읽기 (예: "OXGdXW6Vuj6Hny7mwhmvgdieuhEhlJW6...")
                string encryptedPw = common?.Element("DB_UserPass")?.Attribute("value")?.Value;
                string pw = "admin123!@#"; // 기본값 (복호화 실패 시 대비)

                if (!string.IsNullOrEmpty(encryptedPw))
                {
                    try
                    {
                        // ⭐ 여기서 복호화 필수!
                        string decrypted = PasswordDecryptor.Decrypt(encryptedPw);
                        // ⭐ 이 로그를 꼭 확인하세요! (비밀번호 노출 주의)
                        _logger.Info($"[Debug] Encrypted: {encryptedPw}, Decrypted: {decrypted}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("비밀번호 복호화 실패, 기본값 사용", ex);
                    }
                }

                return $"Server={server};Database={db};User Id={id};Password={pw};";
            }
            catch (Exception ex)
            {
                _logger.Error("Config 로드 실패", ex);
                return GetDefaultConnectionString();
            }
        }

        private string GetDefaultConnectionString()
        {
            return "Server=127.0.0.1;Database=IBSInfo;User Id=sa;Password=admin123!@#;";
        }

        public List<BacnetPoint> LoadPoints()
        {
            var points = new List<BacnetPoint>();
            try
            {
                using (var conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    // P_OBJECT 테이블에서 해당 장비의 포인트 조회
                    string query = @"SELECT SYSTEM_PT_ID, OBJ_TYPE, OBJ_COUNT 
                                     FROM P_OBJECT 
                                     WHERE DEVICE_SEQ = @DeviceSeq 
                                     ORDER BY OBJ_COUNT";

                    using (var cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@DeviceSeq", _deviceSeq);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string ptId = reader["SYSTEM_PT_ID"].ToString();
                                int typeCode = Convert.ToInt32(reader["OBJ_TYPE"]);

                                // BacnetPoint 파싱 로직 (이전 코드 참조)
                                try
                                {
                                    var (objType, instance) = BacnetPoint.ParseSystemPtId(ptId);
                                    points.Add(new BacnetPoint
                                    {
                                        SystemPtId = ptId,
                                        ObjectType = objType,
                                        ObjectInstance = instance,
                                        DeviceInstance = (uint)_deviceSeq
                                    });
                                }
                                catch
                                {
                                    _logger.Warning($"잘못된 포인트 ID: {ptId}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("DB 포인트 로딩 실패", ex);
            }
            return points;
        }
    }
}
