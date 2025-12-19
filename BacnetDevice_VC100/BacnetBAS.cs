using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO.BACnet;
using CShapeDeviceAgent;
using BacnetDevice_VC100.Util;
using BacnetDevice_VC100.Protocol;
using BacnetDevice_VC100.Model;
using BacnetDevice_VC100.Service;

// ⭐ [수정] Config.xml의 ClassName="BacnetDevice_VC100.BacnetBAS"와 일치시킴
// 기존: namespace BacnetDevice_VC100.Device (X)
// 수정: namespace BacnetDevice_VC100        (O)
namespace BacnetDevice_VC100
{
    /// <summary>
    /// BACnet 장비 드라이버
    /// 
    /// [데이터 흐름]
    /// 1. 읽기: BACnet(23.5) → "AO-1,23.5,0;" → Server
    /// 2. 쓰기: Server "AO-1,50.0;" → HandleWrite → BACnet
    /// </summary>
    public class BacnetBAS : CShapeDeviceBase
    {
        #region Constants
        private const int READ_BATCH_SIZE = 20;
        private const int POLLING_INTERVAL_DEFAULT = 10;
        #endregion

        #region Fields
        private BacnetLogger _logger;
        private IBacnetClient _client;
        private DeviceConfigService _configService;
        private List<BacnetPoint> _points;
        private List<List<BacnetPoint>> _pointBatches;

        private int _deviceSeq;
        private bool _useAutoPolling;
        private int _pollingIntervalSeconds;

        private Thread _pollingThread;
        private volatile bool _isRunning = false;
        private bool _isInitialized = false;
        private CancellationTokenSource _cts;
        #endregion

        #region 1. Init (초기화)
        public override bool Init(int deviceSeq)
        {
            _deviceSeq = deviceSeq;
            try
            {
                // 로거 생성 (실패해도 무시하고 진행)
                _logger = new BacnetLogger(_deviceSeq, LogLevel.ERROR);
                _logger.Info($"[Init] DeviceSeq: {_deviceSeq} 초기화");
            }
            catch { }

            // ⭐ 무조건 true를 리턴해야 MainForm 리스트에 추가됨
            return true;
        }
        #endregion

        #region 2. Connect (비동기 연결)
        public override bool Connect(string sIP, int iPort)
        {
            // UI 멈춤 방지를 위해 비동기로 연결 작업 수행
            Task.Run(() => ConnectAsync(sIP, iPort));
            return true;
        }

        private void ConnectAsync(string sIP, int iPort)
        {
            try
            {
                _logger.Info($"[ConnectAsync] 연결 시작 IP:{sIP}");

                // DB 로드
                _configService = new DeviceConfigService(_deviceSeq, _logger);
                _points = _configService.LoadPoints();

                if (_points == null || _points.Count == 0)
                {
                    _logger.Error("[ConnectAsync] 포인트 없음");
                    _points = new List<BacnetPoint>(); // 빈 리스트 할당 (Null 방지)
                    return;
                }

                // 배치 생성
                CreatePointBatches();

                // BACnet 연결
                _client = new BacnetClientWrapper(_logger);
                if (_client.Connect(sIP, iPort))
                {
                    _logger.Info("[ConnectAsync] 연결 성공");
                    ToConnectState(_deviceSeq, true);

                    // 폴링 시작 확인
                    _useAutoPolling = !base.DeviceIF.bRecvTimeCheck;
                    _pollingIntervalSeconds = base.DeviceIF.iTimeinterval > 0
                        ? base.DeviceIF.iTimeinterval / 1000
                        : POLLING_INTERVAL_DEFAULT;

                    if (_useAutoPolling)
                    {
                        StartPollingThread();
                    }
                }
                else
                {
                    _logger.Error("[ConnectAsync] 연결 실패");
                    ToConnectState(_deviceSeq, false);
                }
            }
            catch (Exception ex)
            {
                _logger.Error("[ConnectAsync] 오류", ex);
                ToConnectState(_deviceSeq, false);
            }
        }

        private void CreatePointBatches()
        {
            _pointBatches = new List<List<BacnetPoint>>();
            for (int i = 0; i < _points.Count; i += READ_BATCH_SIZE)
            {
                var batch = _points.GetRange(i, Math.Min(READ_BATCH_SIZE, _points.Count - i));
                _pointBatches.Add(batch);
            }
        }
        #endregion

        #region 3. Polling (데이터 읽기)
        private void StartPollingThread()
        {
            if (_pollingThread != null && _pollingThread.IsAlive) return;

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _pollingThread = new Thread(PollingLoop);
            _pollingThread.IsBackground = true;
            _pollingThread.Start();

            _logger.Info($"[Polling] 스레드 시작 (주기: {_pollingIntervalSeconds}초)");
        }

        private void PollingLoop()
        {
            while (_isRunning)
            {
                try
                {
                    // 전체 읽기 및 전송
                    // 동기 메서드 안에서 비동기 호출 시 Wait() 사용 (스레드 내부이므로 안전)
                    ReadAllPointsAndSendToAgent().Wait(_cts.Token);

                    Thread.Sleep(_pollingIntervalSeconds * 1000);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Error("[Polling] 루프 에러", ex);
                    Thread.Sleep(5000);
                }
            }
        }

        private async Task ReadAllPointsAndSendToAgent()
        {
            if (_pointBatches == null) return;

            var allResults = new Dictionary<string, float?>();

            foreach (var batch in _pointBatches)
            {
                var batchResults = _client.ReadMultiple(batch);
                foreach (var kvp in batchResults) allResults[kvp.Key] = kvp.Value;
            }

            string data = FormatData(allResults);

            try
            {
                if (_isRunning) ToReceive(_deviceSeq, 1, data, allResults.Count);
            }
            catch { } // UI 전송 실패 시 무시

            _logger.Info($"[Read] {allResults.Count}개 포인트 처리 완료");
        }

        public override bool TimeRecv()
        {
            Task.Run(async () => { try { await ReadAllPointsAndSendToAgent(); } catch { } });
            return true;
        }
        #endregion

        // BacnetBAS.cs

        #region 4. SendData
        public override int SendData(int sendType, string cData, int DataSize)
        {
            // [Input 예시]
            // 조회: "AV-1;AI-2;BI-1;"   (콤마 없음)
            // 제어: "AV-1,100;BO-2,1;" (콤마 있음)

            if (!_isInitialized || _client == null || string.IsNullOrEmpty(cData)) return 0;

            try
            {
                if (cData.IndexOf(',') >= 0)
                    return ProcessWriteCommand(cData); // 제어 로직
                else
                    return ProcessReadCommand(cData);  // 조회 로직
            }
            catch (Exception ex)
            {
                _logger.Error("[SendData] Error", ex);
                return 0;
            }
        }

        private int ProcessReadCommand(string cData)
        {
            // [Input] "AV-1;AI-2;BI-1;"
            var requestIds = cData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            // [Log Fix] 요청받은 개수가 0이면 즉시 종료
            if (requestIds.Length == 0) return 0;

            Task.Run(() =>
            {
                try
                {
                    // [Search] 요청된 포인트 ID 매칭
                    var searchSet = new HashSet<string>(requestIds);
                    var targets = _points.Where(p => searchSet.Contains(p.SystemPtId)).ToList();

                    // [Action] BACnet 장비로부터 값 읽기
                    var results = _client.ReadMultiple(targets);

                    // [Formatting] "ID,Value,Status;" 형식 변환
                    // 결과 예시: "AV-1,25.5,0;AI-2,0,1;" (0:정상, 1:통신실패)
                    string response = FormatData(results);

                    // [Feedback] Server로 데이터 전송 (Device -> Server)
                    if (!string.IsNullOrEmpty(response))
                    {
                        ToReceive(_deviceSeq, 1, response, requestIds.Length);
                    }
                }
                catch (Exception ex) { _logger.Error("[ReadCmd] Error", ex); }
            });

            // [Return] ★중요: "찾은 개수"가 아니라 "요청받은 개수"를 리턴해야
            // Server -> Device 로그창에 "전송됨" 로그가 뜸.
            return requestIds.Length;
        }

        private int ProcessWriteCommand(string cData)
        {
            // [Input] "AV-1,100;BO-2,1;"
            var items = cData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (items.Length == 0) return 0;

            // [Response Builder] 제어 결과 담을 버퍼
            var sb = new StringBuilder(items.Length * 30);

            foreach (var item in items)
            {
                // item: "AV-1,100"
                int commaIdx = item.IndexOf(',');
                if (commaIdx <= 0) continue;

                // [Parsing] ID="AV-1", Value=100
                string id = item.Substring(0, commaIdx).Trim();
                string valStr = item.Substring(commaIdx + 1).Trim();

                if (float.TryParse(valStr, out float val))
                {
                    var point = _points.FirstOrDefault(p => p.SystemPtId == id);
                    bool isWritten = false;

                    // [Action] BACnet 장비에 값 쓰기
                    if (point != null)
                    {
                        isWritten = _client.WritePresentValue(point.DeviceInstance, point.ObjectType, point.ObjectInstance, val);
                    }

                    // [Result Append] 성공: "AV-1,100,0;" / 실패: "AV-1,100,1;"
                    sb.Append($"{id},{val},{(isWritten ? "0" : "1")};");
                }
            }

            // [Feedback] 제어 결과 리스트 전송 (Device -> Server)
            // 예시: "AV-1,100,0;BO-2,1,0;"
            if (sb.Length > 0)
            {
                ToReceive(_deviceSeq, 1, sb.ToString(), items.Length);
            }

            // [Return] ★중요: 처리 시도한 개수를 리턴해야 로그가 뜸
            return items.Length;
        }
        #endregion



        // BacnetBAS 클래스 내부 어디든 (보통 맨 아래쪽) 넣어주세요.

        private List<(string PointId, float Value)> ParseControlData(string cData)
        {
            var result = new List<(string, float)>();
            if (string.IsNullOrEmpty(cData)) return result;

            try
            {
                // 1. 세미콜론(;)으로 분리 -> ["AV-1,100", "AI-2,50"]
                var items = cData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var item in items)
                {
                    var trimmed = item.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    // 2. 콤마(,)로 분리 -> ["AV-1", "100"]
                    int commaIndex = trimmed.IndexOf(',');
                    if (commaIndex > 0 && commaIndex < trimmed.Length - 1)
                    {
                        string pointId = trimmed.Substring(0, commaIndex).Trim();
                        string valueStr = trimmed.Substring(commaIndex + 1).Trim();

                        // 3. 값 변환
                        if (float.TryParse(valueStr, out float value))
                        {
                            result.Add((pointId, value));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_logger != null) _logger.Error("ParseControlData Error", ex);
            }

            return result;
        }





        #region Helper
        private string FormatData(Dictionary<string, float?> results)
        {
            var sb = new StringBuilder(results.Count * 20);
            foreach (var pt in _points)
            {
                if (results.TryGetValue(pt.SystemPtId, out float? val))
                    sb.Append(val.HasValue ? $"{pt.SystemPtId},{val:F2},0;" : $"{pt.SystemPtId},{pt.FailValue},1;");
                else
                    sb.Append($"{pt.SystemPtId},{pt.FailValue},1;");
            }
            return sb.ToString().TrimEnd(';');
        }

        public override bool DisConnect()
        {
            _isRunning = false;
            if (_cts != null) _cts.Cancel();
            if (_client != null) _client.Dispose();
            ToConnectState(_deviceSeq, false);
            return true;
        }
        #endregion
    }
}
