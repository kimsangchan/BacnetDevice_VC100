// DataAccess/RealtimeRepository.cs
using System;
using System.Collections.Generic;
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
        /// <summary>
        /// TB_BACNET_REALTIME에서 특정 디바이스의 최신 스냅샷을 조회한다.
        /// </summary>
        public Dictionary<string, double> GetSnapshotByDevice(int deviceSeq)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            const string sql = @"
SELECT
    SYSTEM_PT_ID,
    VALUE,
    QUALITY,
    LAST_ERROR
FROM TB_BACNET_REALTIME WITH (NOLOCK)
WHERE DEVICE_SEQ = @deviceSeq;
";

            try
            {
                using (var conn = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@deviceSeq", SqlDbType.Int).Value = deviceSeq;
                    conn.Open();

                    using (var reader = cmd.ExecuteReader())
                    {
                        int ordSysPtId = reader.GetOrdinal("SYSTEM_PT_ID");
                        int ordValue = reader.GetOrdinal("VALUE");
                        // QUALITY / LAST_ERROR 는 지금은 안 써도 됨

                        while (reader.Read())
                        {
                            // 1) 포인트 ID
                            if (reader.IsDBNull(ordSysPtId))
                                continue;

                            string ptId = reader.GetString(ordSysPtId).Trim();
                            if (string.IsNullOrEmpty(ptId))
                                continue;

                            // 2) VALUE → 문자열로 안전하게 읽어서 double.TryParse
                            double value = RealtimeConstants.FailValue;

                            if (!reader.IsDBNull(ordValue))
                            {
                                object raw = reader.GetValue(ordValue);
                                string s = Convert.ToString(raw, CultureInfo.InvariantCulture);

                                double parsed;
                                if (double.TryParse(
                                        s,
                                        NumberStyles.Float | NumberStyles.AllowThousands,
                                        CultureInfo.InvariantCulture,
                                        out parsed))
                                {
                                    value = parsed;
                                }
                            }

                            // 마지막 값으로 덮어쓰기 (한 포인트당 1행 가정)
                            result[ptId] = value;
                        }
                    }
                }

                return result;
            }
            catch (SqlException ex)
            {
                BacnetLogger.Error(
                    string.Format(
                        "GetSnapshotByDevice SQL 예외. device_seq={0}, msg={1}",
                        deviceSeq, ex.Message),
                    ex);
                throw;
            }
            catch (Exception ex)
            {
                BacnetLogger.Error(
                    string.Format(
                        "GetSnapshotByDevice 실패. device_seq={0}, msg={1}",
                        deviceSeq, ex.Message),
                    ex);
                throw;
            }
        }
        public double? GetCurrentValue(int deviceSeq, string systemPtId)
        {
            const string sql = @"
SELECT TOP 1 VALUE
FROM dbo.TB_BACNET_REALTIME WITH (NOLOCK)
WHERE DEVICE_SEQ = @deviceSeq
  AND SYSTEM_PT_ID = @pt;
";

            try
            {
                using (var conn = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@deviceSeq", SqlDbType.Int).Value = deviceSeq;
                    cmd.Parameters.Add("@pt", SqlDbType.NVarChar, 50).Value = (object)systemPtId ?? DBNull.Value;

                    conn.Open();
                    object raw = cmd.ExecuteScalar();
                    if (raw == null || raw == DBNull.Value)
                        return null;

                    double parsed;
                    var s = Convert.ToString(raw, CultureInfo.InvariantCulture);

                    if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
                        return parsed;

                    return null;
                }
            }
            catch (Exception ex)
            {
                BacnetLogger.Error(
                    string.Format("GetCurrentValue 실패. device_seq={0}, pt={1}", deviceSeq, systemPtId),
                    ex);
                throw;
            }
        }

    }
}

