// Service/PollingService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BacnetDevice_VC100.Bacnet;
using BacnetDevice_VC100.DataAccess;
using BacnetDevice_VC100.Models;
using BacnetDevice_VC100.Util;

namespace BacnetDevice_VC100.Service
{
    /// <summary>
    /// 하나의 BACnet 장비(device_seq)에 대해 포인트들을 폴링해서 Realtime(DB)에 반영하는 서비스.
    ///
    /// [왜 이렇게 설계했나]
    /// - 폴링은 "주기적으로 PV만 읽는다"에 집중한다. (메타/스냅샷/리빌드는 별도 기능)
    /// - 포인트 단위 예외는 삼켜서 전체 폴링이 멈추지 않게 한다.
    /// - DB에는 NULL을 절대 쓰지 않는다. 실패는 FailValue + Quality=BAD로 저장한다.
    /// </summary>
    public class PollingService
    {
        private readonly ObjectRepository _objectRepo;
        private readonly RealtimeRepository _realtimeRepo;
        private readonly BacnetClientWrapper _client;

        private Timer _timer;
        private volatile bool _isRunning;
        private StationConfig _station;
        private int _deviceSeq;
        private int _intervalMs;

        public PollingService(ObjectRepository objectRepo, RealtimeRepository realtimeRepo, BacnetClientWrapper client)
        {
            _objectRepo = objectRepo;
            _realtimeRepo = realtimeRepo;
            _client = client;
        }
        public void Start(StationConfig station, int deviceSeq, int intervalMs)
        {
            if (station == null) throw new ArgumentNullException("station");
            if (intervalMs < 500) intervalMs = 500; // 너무 빡세게 돌리면 DB/네트워크 터짐 방지

            _station = station;
            _deviceSeq = deviceSeq;
            _intervalMs = intervalMs;

            BacnetLogger.SetCurrentDevice(deviceSeq);
            BacnetLogger.Info(string.Format("[POLL] Start() called. device_seq={0}, intervalMs={1}", deviceSeq, intervalMs));

            // 중복 시작 방지
            Stop();

            _timer = new Timer(Tick, null, dueTime: 0, period: intervalMs);
        }

        public void Stop()
        {
            try
            {
                var t = _timer;
                _timer = null;
                if (t != null)
                {
                    t.Dispose();
                    BacnetLogger.SetCurrentDevice(_deviceSeq);
                    BacnetLogger.Info(string.Format("[POLL] Stop() called. device_seq={0}", _deviceSeq));
                }
            }
            catch (Exception ex)
            {
                BacnetLogger.SetCurrentDevice(_deviceSeq);
                BacnetLogger.Error("[POLL][ERROR] Stop failed", ex);
            }
        }

        private void Tick(object state)
        {
            // Timer는 겹쳐 실행될 수 있어서 가드
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                PollOnce(_station, _deviceSeq);
            }
            catch (Exception ex)
            {
                BacnetLogger.SetCurrentDevice(_deviceSeq);
                BacnetLogger.Error("[POLL][ERROR] Tick unexpected", ex);
            }
            finally
            {
                _isRunning = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }
        /// <summary>
        /// 특정 device_seq 에 대해 한 번 폴링 수행.
        /// - PV(ReadPresentValue)만 수행한다.
        /// - Snapshot/메타 데이터 호출은 절대 하지 않는다.
        /// </summary>
        public void PollOnce(StationConfig station, int deviceSeq)
        {
            // 이 폴링 스레드 로그가 디바이스 폴더로 떨어지도록 컨텍스트 설정
            BacnetLogger.SetCurrentDevice(deviceSeq);

            IList<BacnetPointInfo> points = _objectRepo.GetPointsByDeviceSeq(deviceSeq);
            int total = points != null ? points.Count : 0;

            var sw = Stopwatch.StartNew();

            int okCount = 0;
            int readFail = 0;
            int convertFail = 0;
            int unexpected = 0;
            int upsertFail = 0;

            BacnetLogger.Info(string.Format(
                "[POLL] START device_seq={0}, station={1}, pointCount={2}",
                deviceSeq, station != null ? station.Id : "(null)", total));

            if (points == null || points.Count == 0)
            {
                BacnetLogger.Warn(string.Format("[POLL] points empty. device_seq={0}", deviceSeq));
                return;
            }

            foreach (var pt in points)
            {
                string lastError = null;
                double valueToSave = RealtimeConstants.FailValue;
                string quality = RealtimeConstants.QualityBad;

                try
                {
                    // =========================================================
                    // 1) BACnet PV 읽기
                    //    - Wrapper 시그니처: out object
                    // =========================================================
                    object raw = null;

                    bool ok = _client.TryReadPresentValue(
                        station,
                        pt.BacnetType,
                        pt.Instance,
                        out raw);

                    if (!ok)
                    {
                        // 라이브러리 레벨에서 false 반환 (Timeout/NAK 등)
                        lastError = "READ_FAILED";
                        readFail++;
                    }
                    else
                    {
                        // =========================================================
                        // 2) PV 값을 double로 변환
                        //    - 서버/업로더/파서가 결국 숫자 기반이므로 double로 통일
                        // =========================================================
                        double numeric;
                        if (!TryConvertToDouble(raw, out numeric))
                        {
                            lastError = "CONVERT_FAILED";
                            convertFail++;

                            // 변환 실패는 디버깅 가치가 커서 샘플링 없이 WARN
                            BacnetLogger.Warn(string.Format(
                                "[POLL][WARN] Convert failed: device_seq={0}, pt={1}, rawType={2}, raw={3}",
                                deviceSeq,
                                pt.SystemPtId,
                                raw != null ? raw.GetType().Name : "null",
                                raw != null ? raw.ToString() : "null"));
                        }
                        else
                        {
                            valueToSave = numeric;
                            quality = RealtimeConstants.QualityGood;
                            okCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 포인트 단위로만 죽이고 전체 루프는 계속 감
                    lastError = "UNEXPECTED: " + ex.Message;
                    unexpected++;

                    BacnetLogger.Error(
                        string.Format("[POLL][ERROR] Unexpected exception: device_seq={0}, pt={1}",
                                      deviceSeq, pt != null ? pt.SystemPtId : "(null)"),
                        ex);
                }

                // =========================================================
                // 3) Realtime(DB) 반영
                //    - 여기서 NULL 쓰면 서버 파서/클라이언트가 더 크게 망가짐
                // =========================================================
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
                    upsertFail++;
                    BacnetLogger.Error(
                        string.Format("[POLL][ERROR] UpsertRealtime failed: device_seq={0}, pt={1}",
                                      deviceSeq, pt != null ? pt.SystemPtId : "(null)"),
                        ex);
                }
            }

            sw.Stop();

            BacnetLogger.Info(string.Format(
                "[POLL] END device_seq={0}, total={1}, ok={2}, readFail={3}, convertFail={4}, unexpected={5}, upsertFail={6}, elapsedMs={7}",
                deviceSeq, total, okCount, readFail, convertFail, unexpected, upsertFail, sw.ElapsedMilliseconds));
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
