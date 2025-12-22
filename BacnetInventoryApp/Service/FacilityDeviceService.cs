using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using BacnetInventoryApp.Common;
using BacnetInventoryApp.Model;

namespace BacnetInventoryApp.Service
{
    public sealed class FacilityDeviceService
    {
        private readonly string _connStr;

        public FacilityDeviceService(string connStr)
        {
            if (string.IsNullOrWhiteSpace(connStr))
                throw new ArgumentException("connStr is required", "connStr");
            _connStr = connStr;
        }

        /// <summary>
        /// [기능] 설비 디바이스 목록 로드 (P_OBJ_CODE)
        ///
        /// 실데이터 흐름:
        /// - 입력: facilityGroupId="4311744512"
        /// - SQL:  P_OBJ_CODE WHERE MULTI_PARENT_ID=4311744512
        /// - 출력: FacilityDevice{DeviceId=20059, DeviceIp=172.16.130.98, DevicePort=47808, DeviceInfo=AHU-01}
        /// </summary>
        public List<FacilityDevice> Load(string facilityGroupId)
        {
            if (string.IsNullOrWhiteSpace(facilityGroupId))
                throw new ArgumentException("facilityGroupId is required", "facilityGroupId");

            var list = new List<FacilityDevice>();

            const string sql = @"
SELECT
    DEVICE_ID,
    DEVICE_IP,
    ISNULL(DEVICE_PORT, 47808) AS DEVICE_PORT,
    DEVICE_CINFO
FROM P_OBJ_CODE
WHERE MULTI_PARENT_ID = @MULTI_PARENT_ID
  AND DEVICE_IP IS NOT NULL
ORDER BY DEVICE_IP
";

            Logger.Info("[FACILITY] Load start group=" + facilityGroupId);

            try
            {
                using (var conn = new SqlConnection(_connStr))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.CommandTimeout = 10;
                    cmd.Parameters.AddWithValue("@MULTI_PARENT_ID", facilityGroupId);

                    conn.Open();

                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            // 실데이터 예:
                            // DEVICE_ID="20059", DEVICE_IP="172.16.130.98", DEVICE_PORT=47808, DEVICE_CINFO="AHU-01"
                            var dev = new FacilityDevice
                            {
                                DeviceId = Convert.ToString(r["DEVICE_ID"]),
                                DeviceIp = Convert.ToString(r["DEVICE_IP"]),
                                DevicePort = Convert.ToInt32(r["DEVICE_PORT"]),
                                DeviceInfo = r["DEVICE_CINFO"] == DBNull.Value ? null : Convert.ToString(r["DEVICE_CINFO"])
                            };

                            list.Add(dev);
                        }
                    }
                }

                Logger.Info("[FACILITY] Load done count=" + list.Count);
                return list;
            }
            catch (SqlException ex)
            {
                Logger.Error("[FACILITY] SQL error", ex);
                return list;
            }
            catch (Exception ex)
            {
                Logger.Error("[FACILITY] Unexpected error", ex);
                return list;
            }
        }
    }
}
