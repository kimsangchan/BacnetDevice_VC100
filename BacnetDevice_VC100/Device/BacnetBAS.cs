using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BacnetDevice_VC100.Bacnet;
using BacnetDevice_VC100.DataAccess;
using BacnetDevice_VC100.Models;
using BacnetDevice_VC100.Service;
using BacnetDevice_VC100.Util;
using CShapeDeviceAgent;

namespace BacnetDevice_VC100
{
    /// <summary>
    /// BACnet Device DLL Entry
    /// - Agent 기준 제어 / 읽기 프로토콜 완전 호환
    /// - Polling → DB → Snapshot → ToReceive 흐름 보장
    /// </summary>
    public class BacnetBAS : CShapeDeviceBase
    {
        private StationConfig _station;
        private BacnetClientWrapper _client;
        private ObjectRepository _objectRepo;
        private RealtimeRepository _realtimeRepo;
        private PollingService _pollingService;

        private Dictionary<string, BacnetPointInfo> _pointMap;
        private int _deviceSeq;
        private bool _connected;

        #region Device Lifecycle

        public override bool DeviceLoad()
        {
            BacnetLogger.Info("DeviceLoad 호출됨.");
            return true;
        }

        public override bool Init(int iDeviceID)
        {
            BacnetLogger.SetCurrentDevice(iDeviceID);

            _deviceSeq = iDeviceID;
            BacnetLogger.Info($"Init 완료. device_seq={_deviceSeq}");
            BacnetLogger.Info("Init: 실제 제어 모드(정상 모드)로 동작합니다.");
            return true;
        }

        public override bool Connect(string sIP, int iPort)
        {
            BacnetLogger.SetCurrentDevice(_deviceSeq);

            BacnetLogger.Info($"Connect 시작. IP={sIP}, Port={iPort}, DeviceID={_deviceSeq}");

            _station = new StationConfig
            {
                Id = $"BACnet_{sIP.Replace('.', '_')}_{_deviceSeq}",
                Ip = sIP,
                Port = iPort,
                DeviceId = (uint)_deviceSeq
            };

            _objectRepo = new ObjectRepository();
            _realtimeRepo = new RealtimeRepository();

            BuildPointCache();

            _client = new BacnetClientWrapper();
            _client.Start();

            _pollingService = new PollingService(_objectRepo, _realtimeRepo, _client);
            // ⭐ 여기 추가: 폴링 시작 (예: 5000ms)
            _pollingService.Start(_station, _deviceSeq, 5000);
            _connected = true;
            ToConnectState(_deviceSeq, true);

            BacnetLogger.Info($"Connect 완료. StationId={_station.Id}");
            return true;
        }

        public override bool DisConnect()
        {
            BacnetLogger.Info("DisConnect 호출됨.");

            _connected = false;
            ToConnectState(_deviceSeq, false);

            // ⭐ 여기 추가: 폴링 정지
            try
            {
                if (_pollingService != null) _pollingService.Stop();
            }
            catch (Exception ex)
            {
                BacnetLogger.Error("[POLL][ERROR] Stop on disconnect failed", ex);
            }
            _client?.Dispose();
            _client = null;

            return true;
        }

        #endregion

        #region Agent → DLL 진입점

        /// <summary>
        /// DeviceAgent → DLL 데이터 전달 진입점
        /// - 세미콜론 포함 → 제어
        /// - 세미콜론 없음 → 읽기
        /// </summary>
        public override int SendData(int sendType, string cData, int dataSize)
        {
            BacnetLogger.SetCurrentDevice(_deviceSeq);

            if (string.IsNullOrEmpty(cData))
            {
                BacnetLogger.Warn("SendData: 수신 데이터 비어있음");
                return 0;
            }

            bool isControl = cData.IndexOf(';') >= 0;

            return isControl
                ? HandleControl(cData)
                : HandleRead(sendType, cData);
        }

        #endregion

        #region Read / Control

        /// <summary>
        /// 제어 처리
        /// 포맷: "AV-1,23.5;BV-3,1;"
        /// </summary>
        private int HandleControl(string cData)
        {
            int success = 0;
            string[] items = cData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string item in items)
            {
                string[] parts = item.Split(',');
                if (parts.Length < 2) continue;

                string ptId = parts[0].Trim();
                if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
                    continue;

                if (ptId.StartsWith("BV-", StringComparison.OrdinalIgnoreCase))
                    value = value >= 1 ? 1 : 0;

                if (TryControlPoint(ptId, value))
                    success++;
            }

            BacnetLogger.Info($"SendData(Control): 성공={success}");
            return success;
        }

        /// <summary>
        /// 읽기 처리
        /// 요청: "BI-1,AO-2,"
        /// 응답: "BI-1,1,0;AO-2,23.5,0;"
        /// </summary>
        private int HandleRead(int sendType, string cData)
        {
            var snapshot = _realtimeRepo.GetSnapshotByDevice(_deviceSeq);
            var sb = new StringBuilder();

            string[] tokens = cData.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            int count = 0;

            foreach (string pt in tokens)
            {
                double value = snapshot.TryGetValue(pt, out double v)
                    ? v
                    : RealtimeConstants.FailValue;

                sb.AppendFormat(
                    CultureInfo.InvariantCulture,
                    "{0},{1},0;",
                    pt,
                    value
                );
                count++;
            }

            string response = sb.ToString();

            BacnetLogger.Info($"[READ][ToReceive] count={count}, data={response}");

            ToReceive(
                _deviceSeq,
                sendType,
                response,
                count
            );

            return count;
        }

        #endregion

        #region Helpers

        private void BuildPointCache()
        {
            _pointMap = new Dictionary<string, BacnetPointInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _objectRepo.GetPointsByDeviceSeq(_deviceSeq))
            {
                if (!string.IsNullOrEmpty(p.SystemPtId))
                    _pointMap[p.SystemPtId] = p;
            }

            BacnetLogger.Info($"BuildPointCache 완료. count={_pointMap.Count}");
        }

        private bool TryControlPoint(string systemPtId, double value)
        {
            if (!_pointMap.TryGetValue(systemPtId, out var pt))
                return false;

            bool ok = _client.TryWritePresentValue(
                _station,
                pt.BacnetType,
                pt.Instance,
                value,
                out string error);

            if (!ok)
                BacnetLogger.Warn($"제어 실패: {systemPtId}, error={error}");
            else
                _realtimeRepo.UpsertRealtime(
                    _deviceSeq,
                    systemPtId,
                    value,
                    RealtimeConstants.QualityGood,
                    DateTime.Now,
                    null);

            return ok;
        }

        #endregion
    }
}
