// DataAccess/RealtimeRepository.cs
using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using BacnetDevice_VC100;
using BacnetDevice_VC100.Models;
using BacnetDevice_VC100.Util;

namespace BacnetDevice_VC100.DataAccess
{
    /// <summary>
    /// TB_BACNET_REALTIME (가칭)에 실시간 값을 upsert 한다.
    /// - 절대 NULL VALUE 를 넣지 않는다.
    /// - 실패 시 FailValue(-9999) + Quality="BAD" 로 저장한다.
    /// </summary>
    public class RealtimeRepository
    {
        private readonly string _connectionString;

        public RealtimeRepository()
        {
            try
            {
                _connectionString = DbConnectionFactory.GetConnectionString();
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("RealtimeRepository 생성 중 DB 연결 문자열 초기화 실패.", ex);
                throw;
            }
        }

        /// <summary>
        /// 실시간 값 Upsert.
        /// PRIMARY KEY(DEVICE_SEQ, SYSTEM_PT_ID) 같은 구조를 가정한다.
        /// </summary>
        public void UpsertRealtime(
    int deviceSeq,
    string systemPtId,
    double value,
    string quality,
    DateTime timestamp,          // 현재는 사용 안 함 (UPDATED_AT = GETDATE())
    string lastErrorMessage)
        {
            const string sql = @"
MERGE dbo.TB_BACNET_REALTIME AS T
USING (SELECT @device AS DEVICE_SEQ, @pt AS SYSTEM_PT_ID) AS S
    ON T.DEVICE_SEQ = S.DEVICE_SEQ AND T.SYSTEM_PT_ID = S.SYSTEM_PT_ID
WHEN MATCHED THEN
    UPDATE SET 
        VALUE      = @value,
        QUALITY    = @quality,
        LAST_ERROR = @error,
        UPDATED_AT = GETDATE()
WHEN NOT MATCHED THEN
    INSERT (DEVICE_SEQ, SYSTEM_PT_ID, VALUE, QUALITY, LAST_ERROR, UPDATED_AT)
    VALUES (@device, @pt, @value, @quality, @error, GETDATE());
";

            try
            {
                // double → 문자열 (DB는 NVARCHAR(50))
                string valueStr = value.ToString(CultureInfo.InvariantCulture);

                // QUALITY(TINYINT) 매핑
                // quality가 "GOOD"/"BAD" 같은 문자열이면 여기서 코드로 바꿔줌
                byte qualityCode;
                if (!byte.TryParse(quality, out qualityCode))
                {
                    // 네가 RealtimeConstants.QualityGood / Bad 를 "GOOD"/"BAD"로 썼다고 가정
                    if (string.Equals(quality, "GOOD", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(quality, RealtimeConstants.QualityGood, StringComparison.OrdinalIgnoreCase))
                    {
                        qualityCode = 1;
                    }
                    else
                    {
                        qualityCode = 0; // 나머지는 전부 BAD 처리
                    }
                }

                using (var conn = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    // ❗ SQL 파라미터 이름과 정확히 맞춰준다
                    cmd.Parameters.Add("@device", SqlDbType.Int).Value = deviceSeq;
                    cmd.Parameters.Add("@pt", SqlDbType.NVarChar, 50).Value =
                        (object)systemPtId ?? DBNull.Value;
                    cmd.Parameters.Add("@value", SqlDbType.NVarChar, 50).Value = valueStr;
                    cmd.Parameters.Add("@quality", SqlDbType.TinyInt).Value = qualityCode;
                    cmd.Parameters.Add("@error", SqlDbType.NVarChar, 200).Value =
                        string.IsNullOrEmpty(lastErrorMessage) ? (object)DBNull.Value : lastErrorMessage;

                    conn.Open();
                    int affected = cmd.ExecuteNonQuery();
                    Console.WriteLine(
                        "[RT][INFO] UpsertRealtime: device_seq={0}, pt={1}, value={2}, quality={3}, rows={4}",
                        deviceSeq, systemPtId, valueStr, qualityCode, affected);
                }
            }
            catch (SqlException ex)
            {
                Console.WriteLine(
                    "[RT][ERROR] UpsertRealtime SQL 예외: device_seq={0}, pt={1}, msg={2}",
                    deviceSeq, systemPtId, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[RT][ERROR] UpsertRealtime 실패: device_seq={0}, pt={1}, msg={2}",
                    deviceSeq, systemPtId, ex.Message);
                throw;
            }
        }
    }
}
