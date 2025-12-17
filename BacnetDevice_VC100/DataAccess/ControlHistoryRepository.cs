using System;
using System.Data;
using System.Data.SqlClient;
using BacnetDevice_VC100.Util;

namespace BacnetDevice_VC100.DataAccess
{
    /// <summary>
    /// TB_BACNET_CONTROL_HISTORY 기록 전용 Repository.
    ///
    /// ✅ 이 테이블의 목적:
    /// - "누가/언제/무슨 포인트를/어떤 값에서 어떤 값으로/성공했는지"를
    ///   서버/에이전트 수정 없이 DLL 단에서 증거로 남긴다.
    ///
    /// ✅ 실제 INSERT 되는 데이터 예시:
    /// - DeviceSeq   = 20060
    /// - SystemPtId  = "AV-14"
    /// - PrevValue   = "87.45"         (TB_BACNET_REALTIME.VALUE에서 읽음)
    /// - NewValue    = "100"           (Agent가 보낸 제어 값)
    /// - Result      = "OK" / "FAIL"
    /// - ErrorMessage= "WRITE_FAILED: timeout" 같은 원인
    /// - ControlTime = GETDATE()
    /// - ControlUser = (Agent가 준 값이 없으면 null 또는 "SYSTEM")
    /// - IsDryRun    = false (지금은 실제 제어만 하므로 false)
    /// - Source      = "SI" 또는 "AGENT" 같은 구분값 (추적용)
    /// </summary>
    public class ControlHistoryRepository
    {
        private readonly string _connectionString;

        public ControlHistoryRepository()
        {
            try
            {
                _connectionString = DbConnectionFactory.GetConnectionString();
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("ControlHistoryRepository 생성 실패(커넥션스트링).", ex);
                throw;
            }
        }

        public void Insert(
            int deviceSeq,
            string systemPtId,
            string prevValue,
            string newValue,
            string result,
            string errorMessage,
            string controlUser,
            bool isDryRun,
            string source)
        {
            const string sql = @"
INSERT INTO dbo.TB_BACNET_CONTROL_HISTORY
(
  DeviceSeq, SystemPtId, PrevValue, NewValue,
  Result, ErrorMessage, ControlTime, ControlUser,
  IsDryRun, Source
)
VALUES
(
  @DeviceSeq, @SystemPtId, @PrevValue, @NewValue,
  @Result, @ErrorMessage, GETDATE(), @ControlUser,
  @IsDryRun, @Source
);
";

            try
            {
                using (var conn = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@DeviceSeq", SqlDbType.Int).Value = deviceSeq;
                    cmd.Parameters.Add("@SystemPtId", SqlDbType.NVarChar, 50).Value = (object)systemPtId ?? DBNull.Value;
                    cmd.Parameters.Add("@PrevValue", SqlDbType.NVarChar, 50).Value = (object)prevValue ?? DBNull.Value;
                    cmd.Parameters.Add("@NewValue", SqlDbType.NVarChar, 50).Value = (object)newValue ?? DBNull.Value;
                    cmd.Parameters.Add("@Result", SqlDbType.NVarChar, 20).Value = (object)result ?? DBNull.Value;
                    cmd.Parameters.Add("@ErrorMessage", SqlDbType.NVarChar, 200).Value =
                        string.IsNullOrEmpty(errorMessage) ? (object)DBNull.Value : errorMessage;
                    cmd.Parameters.Add("@ControlUser", SqlDbType.NVarChar, 50).Value =
                        string.IsNullOrEmpty(controlUser) ? (object)DBNull.Value : controlUser;
                    cmd.Parameters.Add("@IsDryRun", SqlDbType.Bit).Value = isDryRun;
                    cmd.Parameters.Add("@Source", SqlDbType.NVarChar, 30).Value =
                        string.IsNullOrEmpty(source) ? (object)DBNull.Value : source;

                    conn.Open();
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                BacnetLogger.Error(
                    string.Format("ControlHistory INSERT 실패. device_seq={0}, pt={1}", deviceSeq, systemPtId),
                    ex);
                throw;
            }
        }
    }
}
