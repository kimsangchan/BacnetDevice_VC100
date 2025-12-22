using System.Data.SqlClient;

namespace BacnetInventoryApp.DataAccess
{
    /// <summary>
    /// [DB 연결 팩토리]
    /// - BacnetInventoryApp 전용
    /// - 운영 모듈(BacnetDevice_VC100)과 완전 분리
    ///
    /// 실사용 예:
    /// using (var conn = SqlConnectionFactory.Open())
    /// </summary>
    public static class SqlConnectionFactory
    {
        // TODO: App.config에서 읽도록 다음 단계에서 분리
        private static readonly string _connStr =
            "Server=192.168.131.127;Database=IBSInfo;User Id=sa;Password=admin123!@#;";

        public static SqlConnection Open()
        {
            var conn = new SqlConnection(_connStr);
            conn.Open();
            return conn;
        }
    }
}
