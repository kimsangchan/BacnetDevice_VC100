using System;
using System.Configuration;
using System.IO;
using System.Xml.Linq;

namespace BacnetDevice_VC100.Util
{
    /// <summary>
    /// DB 연결 문자열을 해결하는 헬퍼.
    /// 1) exe.config의 connectionStrings["DbConnectionString"] 우선 사용
    /// 2) 없으면 SmartDeviceAgent의 Config.XML에서 ServerIP/DB명/UserId를 읽어
    ///    admin123!@# 비밀번호로 연결 문자열을 구성한다.
    /// </summary>
    internal static class DbConnectionFactory
    {
        // TODO: 나중에 필요하면 환경변수/별도 설정으로 빼도 됨.
        private const string DefaultDbPassword = "admin123!@#";

        public static string GetConnectionString()
        {
            // 1) exe.config 우선
            var cs = ConfigurationManager.ConnectionStrings["DbConnectionString"];
            if (cs != null && !string.IsNullOrWhiteSpace(cs.ConnectionString))
            {
                return cs.ConnectionString;
            }

            // 2) Config.XML fallback
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // Config.XML이 "XML" 폴더 아래에 있다고 가정
                string configPath = Path.Combine(baseDir, "XML", "Config.XML");


                if (!File.Exists(configPath))
                    throw new InvalidOperationException("Config.XML을 찾을 수 없습니다. 경로=" + configPath);

                var doc = XDocument.Load(configPath);

                var profile = doc.Element("profile");
                if (profile == null)
                    throw new InvalidOperationException("<profile> 루트 노드를 찾을 수 없습니다.");

                var common = profile.Element("Common");
                if (common == null)
                    throw new InvalidOperationException("<Common> 노드를 찾을 수 없습니다.");

                Func<string, string> read = name =>
                {
                    var el = common.Element(name);
                    if (el == null)
                        throw new InvalidOperationException("<" + name + "> 노드를 찾을 수 없습니다.");

                    var attr = el.Attribute("value");
                    if (attr == null || string.IsNullOrWhiteSpace(attr.Value))
                        throw new InvalidOperationException(name + " value가 비어 있습니다.");

                    return attr.Value.Trim();
                };

                string serverIp = read("ServerIP");               // 예: 192.168.131.127
                string dbName = read("DB_MainDataBaseName");    // 예: IBSInfo
                string userId = read("DB_UserID");              // 예: sa

                string conn = string.Format(
                    "Server={0};Database={1};User Id={2};Password={3};",
                    serverIp, dbName, userId, DefaultDbPassword);

                BacnetLogger.Info(
                    string.Format("Config.XML 기반 DbConnectionString 생성. Server={0}, Database={1}, User={2}",
                                  serverIp, dbName, userId));

                return conn;
            }
            catch (Exception ex)
            {
                // 여기서 실패하면 리포지토리 생성 자체가 불가능하므로 예외를 그대로 던진다.
                BacnetLogger.Error("DbConnectionString 생성 실패.", ex);
                throw;
            }
        }
    }
}
