using System;
using System.Data.SqlClient;

namespace BacnetDevice_VC100.DataAccess
{
    /// <summary>
    /// SQL Server 연결 관리 (Connection Pool 자동 관리)
    /// - Config.XML 기반 연결 문자열 생성
    /// - 암호화된 비밀번호 복호화 지원 (나중에 추가)
    /// - Thread-Safe Connection 제공
    /// </summary>
    public class SqlConnectionFactory
    {
        private readonly string _connectionString;

        /// <summary>
        /// 생성자 (Config.XML 정보로 연결 문자열 생성)
        /// </summary>
        /// <param name="serverIp">DB 서버 IP (Config.XML의 ServerIP)</param>
        /// <param name="databaseName">DB 이름 (Config.XML의 DB_MainDataBaseName)</param>
        /// <param name="userId">DB 사용자 (Config.XML의 DB_UserID)</param>
        /// <param name="password">DB 비밀번호 (Config.XML의 DB_UserPass - 암호화된 상태)</param>
        public SqlConnectionFactory(string serverIp, string databaseName, string userId, string password)
        {
            if (string.IsNullOrEmpty(serverIp))
                throw new ArgumentException("Server IP cannot be empty", nameof(serverIp));
            if (string.IsNullOrEmpty(databaseName))
                throw new ArgumentException("Database name cannot be empty", nameof(databaseName));
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be empty", nameof(password));

            // TODO: 암호화된 비밀번호 복호화 (현재는 평문으로 가정)
            string decryptedPassword = DecryptPassword(password);

            // Connection String 생성 (Pool 자동 관리)
            _connectionString = $"Data Source={serverIp};Initial Catalog={databaseName};" +
                               $"User ID={userId};Password={decryptedPassword};" +
                               $"Connection Timeout=10;Min Pool Size=2;Max Pool Size=10;";
        }

        /// <summary>
        /// 새 DB Connection 생성 (사용 후 반드시 Dispose 필요)
        /// </summary>
        /// <returns>열린 SqlConnection (using 블록 사용 권장)</returns>
        public SqlConnection CreateConnection()
        {
            try
            {
                var connection = new SqlConnection(_connectionString);
                connection.Open();
                return connection;
            }
            catch (SqlException ex)
            {
                throw new Exception($"DB 연결 실패: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 연결 테스트 (초기화 시 DB 접근 가능 여부 확인)
        /// </summary>
        /// <returns>연결 성공 여부</returns>
        public bool TestConnection()
        {
            try
            {
                using (var conn = CreateConnection())
                {
                    return conn.State == System.Data.ConnectionState.Open;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 암호화된 비밀번호 복호화
        /// TODO: DeviceAgent의 암호화 방식과 동일하게 구현
        /// </summary>
        private string DecryptPassword(string encryptedPassword)
        {
            // 임시: 평문 반환 (실제 운영에서는 복호화 로직 추가)
            // Config.XML의 "OXGdXW6Vuj6Hny7mwhmvgdieuhEhlJW6" 같은 값 처리

            // 1단계: 일단 평문으로 가정
            return encryptedPassword;

            // 2단계: DeviceAgent의 복호화 방식 확인 후 구현
            // return YourDecryptionMethod(encryptedPassword);
        }

        /// <summary>
        /// 연결 문자열 조회 (디버깅용 - 비밀번호는 마스킹)
        /// </summary>
        public string GetMaskedConnectionString()
        {
            return _connectionString.Replace("Password=", "Password=***");
        }
    }
}
