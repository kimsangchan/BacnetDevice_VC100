// DataAccess/ObjectRepository.cs
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO.BACnet;
using BacnetDevice_VC100.Models;
using BacnetDevice_VC100.Util;

namespace BacnetDevice_VC100.DataAccess
{
    /// <summary>
    /// P_OBJECT 테이블에서 BACnet 포인트 목록을 조회하는 Repository.
    /// - OBJ_TYPE (0~8) : 타입 코드
    /// - SYSTEM_PT_ID (AI-0, AO-3, BV-11 ...) : 타입 + 인스턴스 번호
    /// </summary>
    public class ObjectRepository
    {
        private readonly string _connectionString;

        public ObjectRepository()
        {
            try
            {
                _connectionString = DbConnectionFactory.GetConnectionString();
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("ObjectRepository 생성 중 DB 연결 문자열 초기화 실패.", ex);
                throw;
            }
        }


        /// <summary>
        /// 특정 device_seq의 포인트리스트를 P_OBJECT에서 읽어 BacnetPointInfo 목록으로 반환.
        /// </summary>
        public IList<BacnetPointInfo> GetPointsByDeviceSeq(int deviceSeq)
        {
            const string sql = @"
SELECT 
    SYSTEM_PT_ID,
    OBJ_NAME,
    OBJ_TYPE,
    DEVICE_SEQ,
    OBJ_COUNT
FROM dbo.P_OBJECT
WHERE DEVICE_SEQ = @DEVICE_SEQ
ORDER BY OBJ_COUNT ASC;
";

            var list = new List<BacnetPointInfo>();

            try
            {
                using (var conn = new SqlConnection(_connectionString))
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@DEVICE_SEQ", SqlDbType.Int).Value = deviceSeq;

                    conn.Open();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string systemPtId = reader["SYSTEM_PT_ID"] as string;
                            string objName = reader["OBJ_NAME"] as string;
                            int objTypeCode = Convert.ToInt32(reader["OBJ_TYPE"]);
                            int devSeq = Convert.ToInt32(reader["DEVICE_SEQ"]);

                            // SYSTEM_PT_ID에서 인스턴스 번호 파싱 (예: "AI-12" → 12)
                            uint instance = 0;
                            string typePrefix = null;

                            if (!string.IsNullOrEmpty(systemPtId))
                            {
                                var parts = systemPtId.Split('-');
                                if (parts.Length >= 2)
                                {
                                    typePrefix = parts[0]; // "AI", "AO", "BV" ...
                                    uint parsed;
                                    if (uint.TryParse(parts[1], out parsed))
                                    {
                                        instance = parsed;
                                    }
                                    else
                                    {
                                        Console.WriteLine("[OBJ][WARN] SYSTEM_PT_ID 인스턴스 파싱 실패: {0}", systemPtId);
                                    }
                                }
                                else
                                {
                                    typePrefix = systemPtId;
                                    Console.WriteLine("[OBJ][WARN] SYSTEM_PT_ID 형식이 예상과 다릅니다: {0}", systemPtId);
                                }
                            }

                            var info = new BacnetPointInfo
                            {
                                SystemPtId = systemPtId,
                                ObjName = objName,
                                ObjTypeCode = objTypeCode,
                                Instance = instance,
                                DeviceSeq = devSeq,
                                BacnetType = ConvertObjType(objTypeCode)
                            };

                            list.Add(info);
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                Console.WriteLine("[OBJ][ERROR] SQL 예외 발생: " + ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[OBJ][ERROR] 포인트 목록 조회 실패: " + ex.Message);
                throw;
            }

            Console.WriteLine("[OBJ] device_seq={0}, 포인트 {1}개 로드됨", deviceSeq, list.Count);
            return list;
        }

        /// <summary>
        /// P_OBJECT.OBJ_TYPE 코드(0~8)를 BACnetObjectTypes enum으로 변환.
        /// CODE_TO_NUM 매핑 기준:
        /// 0=AI, 1=AO, 2=AV, 3=BI, 4=BO, 5=BV, 6=MSI, 7=MSO, 8=MSV
        /// </summary>
        private BacnetObjectTypes ConvertObjType(int objTypeCode)
        {
            switch (objTypeCode)
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
                    Console.WriteLine("[OBJ][WARN] 알 수 없는 OBJ_TYPE={0}, AI로 처리함", objTypeCode);
                    return BacnetObjectTypes.OBJECT_ANALOG_INPUT;
            }
        }
    }
}
