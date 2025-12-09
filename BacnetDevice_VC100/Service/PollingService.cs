// Service/PollingService.cs
using BacnetDevice_VC100.Bacnet;
using BacnetDevice_VC100.DataAccess;
using BacnetDevice_VC100.Models;
using System;
using System.Collections.Generic;
using System.IO.BACnet;

namespace BacnetDevice_VC100.Service
{
    /// <summary>
    /// 하나의 BACnet 장비(device_seq)에 대해 포인트들을 폴링해서 DB에 반영하는 서비스.
    /// - 포인트별로 예외를 개별 처리해서, 일부 포인트 실패해도 전체 폴링은 계속 간다.
    /// - DB에는 절대 NULL 값을 쓰지 않고, 실패는 FailValue(-9999) + Quality="BAD"로 저장한다.
    /// </summary>
    public class PollingService
    {
        private readonly ObjectRepository _objectRepo;
        private readonly RealtimeRepository _realtimeRepo;
        private readonly BacnetClientWrapper _client;

        public PollingService(
            ObjectRepository objectRepo,
            RealtimeRepository realtimeRepo,
            BacnetClientWrapper client)
        {
            _objectRepo = objectRepo;
            _realtimeRepo = realtimeRepo;
            _client = client;
        }

        /// <summary>
        /// 특정 device_seq 에 대해 한 번 폴링 수행.
        /// </summary>
        public void PollOnce(StationConfig station, int deviceSeq)
        {
            Console.WriteLine("=== PollOnce START: device_seq={0}, Station={1} ===",
                deviceSeq, station.Id);

            IList<BacnetPointInfo> points = _objectRepo.GetPointsByDeviceSeq(deviceSeq);

            foreach (var pt in points)
            {
                // objId는 디버깅용으로만 쓸 수 있는데, 지금은 안 쓰고 있어서 제거
                // var objId = new BacnetObjectId(pt.BacnetType, pt.Instance);

                string lastError = null;
                double valueToSave;
                string quality;

                try
                {
                    // ❗ Wrapper 시그니처: out object
                    object raw;

                    bool ok = _client.TryReadPresentValue(
                        station,
                        pt.BacnetType,
                        pt.Instance,
                        out raw);

                    if (!ok)
                    {
                        // 라이브러리 레벨에서 false 반환 (Timeout, NAK 등)
                        lastError = "READ_FAILED";
                        valueToSave = RealtimeConstants.FailValue;
                        quality = RealtimeConstants.QualityBad;

                        Console.WriteLine("[POLL][WARN] Read failed: device_seq={0}, pt={1}, reason={2}",
                            deviceSeq, pt.SystemPtId, lastError);
                    }
                    else
                    {
                        // 읽기 성공 → 실제 값으로 변환
                        double numeric;
                        if (!TryConvertToDouble(raw, out numeric))
                        {
                            lastError = "CONVERT_FAILED";
                            valueToSave = RealtimeConstants.FailValue;
                            quality = RealtimeConstants.QualityBad;

                            Console.WriteLine("[POLL][WARN] Convert failed: device_seq={0}, pt={1}, rawType={2}",
                                deviceSeq, pt.SystemPtId, raw != null ? raw.GetType().Name : "null");
                        }
                        else
                        {
                            valueToSave = numeric;
                            quality = RealtimeConstants.QualityGood;
                            Console.WriteLine("[POLL][OK] {0} = {1}", pt.SystemPtId, numeric);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // BacnetException 같은 구체 타입이 없으니 일단 최상위 Exception으로 처리
                    lastError = "UNEXPECTED: " + ex.Message;
                    valueToSave = RealtimeConstants.FailValue;
                    quality = RealtimeConstants.QualityBad;

                    Console.WriteLine("[POLL][ERROR] Unexpected 예외: device_seq={0}, pt={1}, msg={2}",
                        deviceSeq, pt.SystemPtId, ex);
                    // 포인트 단위로만 죽이고 전체 루프는 계속 감.
                }

                // ★ 여기서 절대 NULL 안 들어감
                try
                {
                    _realtimeRepo.UpsertRealtime(
                        deviceSeq,
                        pt.SystemPtId,
                        valueToSave,
                        quality,
                        DateTime.Now,
                        lastError);
                }
                catch (Exception ex)
                {
                    // DB 에러도 포인트 단위로만 로그 찍고 다음 포인트 진행
                    Console.WriteLine("[POLL][ERROR] UpsertRealtime 예외: device_seq={0}, pt={1}, msg={2}",
                        deviceSeq, pt.SystemPtId, ex.Message);
                }
            }

            Console.WriteLine("=== PollOnce END: device_seq={0}, Station={1} ===",
                deviceSeq, station.Id);
        }

        /// <summary>
        /// object → double 변환 유틸.
        /// BACnet 라이브러리가 어떤 타입을 돌려줘도 최대한 방어적으로 처리.
        /// </summary>
        private bool TryConvertToDouble(object rawValue, out double result)
        {
            result = 0.0;

            if (rawValue == null)
                return false;

            try
            {
                if (rawValue is IConvertible)
                {
                    result = Convert.ToDouble(rawValue);
                    return true;
                }

                double parsed;
                if (double.TryParse(rawValue.ToString(), out parsed))
                {
                    result = parsed;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
