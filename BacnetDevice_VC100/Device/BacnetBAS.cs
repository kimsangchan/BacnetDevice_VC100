using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO.BACnet;
using CShapeDeviceAgent;
using BacnetDevice_VC100.Util;
using BacnetDevice_VC100.Protocol;
using BacnetDevice_VC100.Model;
using BacnetDevice_VC100.Data;

namespace BacnetDevice_VC100
{
    /// <summary>
    /// BACnet 장비 통신 DLL
    /// 
    /// [데이터 흐름]
    /// 1. Init() → DB에서 포인트 로딩, BACnet 연결, 폴링 스레드 시작
    /// 2. PollingLoop() → 독립 스레드에서 주기적 폴링 (RecvTimeCheck 무관)
    /// 3. ReadMultiple() → BACnet 장비에서 데이터 읽기
    /// 4. ToReceive() → Agent로 데이터 전송
    /// 5. SendData() → Agent에서 제어 명령 수신
    /// 6. WriteValue() → BACnet 장비로 제어 전송
    /// 
    /// [폴링 방식]
    /// - 자체 스레드 방식 (기존 BAS.cs 방식과 동일)
    /// - RecvTimeCheck=false 권장 (MainForm 타이머 사용 안 함)
    /// - TimeInterval 주기로 자동 폴링
    /// </summary>
    public class BacnetBAS : CShapeDeviceBase
    {
        #region 필드

        private BacnetLogger _logger;
        private IBacnetClient _client;
        private int _deviceSeq;
        private int _pollingInterval;  // 폴링 주기 (초) - Config.XML에서 로드
        private List<BacnetPoint> _points = new List<BacnetPoint>();
        private bool _isInitialized = false;

        // 자체 폴링 스레드
        private Thread _pollingThread;
        private volatile bool _isRunning = false;

        #endregion

        #region 생성자

        /// <summary>
        /// 생성자
        /// 
        /// [주의]
        /// - deviceSeq를 모르므로 Logger 생성 불가
        /// - Init()에서 Logger 생성
        /// </summary>
        public BacnetBAS()
        {
            // 아무것도 안 함 (Init에서 초기화)
        }

        #endregion

        #region 초기화

        /// <summary>
        /// DLL 초기화
        /// 
        /// [호출 경로]
        /// MainForm.XMLLoad() → DeviceAgent.Init() → BacnetBAS.Init()
        /// 
        /// [처리 순서]
        /// 1. Logger 생성 (deviceSeq 필요)
        /// 2. Config.XML에서 폴링 주기 읽기
        /// 3. BACnet 연결 (IP:Port)
        /// 4. DB에서 포인트 목록 로딩 (P_OBJECT 테이블)
        /// 5. 자체 폴링 스레드 시작 (RecvTimeCheck 무관)
        /// </summary>
        public override bool Init(int deviceSeq)
        {
            _deviceSeq = deviceSeq;

            // ===== 1. Logger 생성 (여기서 처음 생성) =====
            _logger = new BacnetLogger(_deviceSeq, LogLevel.ERROR);

            try
            {
                _logger.Info($"=== BacnetBAS 초기화 시작 ===");                
                _logger.Info($"DeviceSeq: {_deviceSeq}");

                // ===== 2. Config.XML에서 폴링 주기 읽기 =====
                if (base.DeviceIF.iTimeinterval > 0)
                {
                    // TimeInterval은 밀리초 단위 (예: 30000ms → 30초)
                    int intervalMs = base.DeviceIF.iTimeinterval;
                    _pollingInterval = intervalMs / 1000;  // 초 단위로 변환

                    _logger.Info($"폴링 주기: {intervalMs}ms ({_pollingInterval}초)");
                }
                else
                {
                    // Config에 없으면 기본값 30초 사용
                    _pollingInterval = 30;
                    _logger.Warning($"TimeInterval 설정 없음, 기본값 사용: {_pollingInterval}초");
                }

                // ===== 3. BACnet 연결 =====
                // DeviceIF에서 IP:Port 정보 가져오기
                string ip = base.DeviceIF.sSystemIP;
                int port = base.DeviceIF.iSystemPort;

                if (string.IsNullOrEmpty(ip))
                {
                    _logger.Error("IP 주소가 없습니다");
                    return false;
                }

                if (port <= 0)
                {
                    port = 47808;  // BACnet 기본 포트
                    _logger.Warning($"포트 설정 없음, 기본값 사용: {port}");
                }

                _logger.Info($"연결 정보: {ip}:{port}");

                // BACnet 클라이언트 생성
                _client = new BacnetClientWrapper(_logger);

                // 연결 시도
                bool connected = _client.Connect(ip, port);
                if (!connected)
                {
                    _logger.Error($"BACnet 연결 실패: {ip}:{port}");
                    return false;
                }

                _logger.Info($"BACnet 연결 성공: {ip}:{port}");

                // ===== 4. DB에서 포인트 로딩 =====
                LoadPointsFromDatabase();

                // ===== 5. 자체 폴링 스레드 시작 =====
                // RecvTimeCheck 설정과 무관하게 독립적으로 동작
                _isRunning = true;
                _pollingThread = new Thread(PollingLoop);
                _pollingThread.IsBackground = true;  // 메인 종료 시 자동 종료
                _pollingThread.Name = $"BACnet_Poll_{_deviceSeq}";  // 디버깅용 이름
                _pollingThread.Start();

                _logger.Info($"폴링 스레드 시작: {_pollingInterval}초 주기");

                _isInitialized = true;
                _logger.Info("=== BacnetBAS 초기화 완료 ===");
                // ===== 이 줄만 추가! =====
                ToConnectState(_deviceSeq, true);  // MainForm에 연결 상태 알림
                return true;
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.Error("초기화 실패", ex);
                }
                return false;
            }
        }


        /// <summary>
        /// DB에서 포인트 설정 로딩
        /// 
        /// [데이터 흐름]
        /// P_OBJECT 테이블 (DEVICE_SEQ, SYSTEM_PT_ID, OBJ_TYPE, OBJ_INST)
        ///   ↓
        /// DeviceConfigService.LoadPoints()
        ///   ↓
        /// List<BacnetPoint> (SystemPtId, ObjectType, ObjectInstance)
        ///   ↓
        /// _points 필드에 저장 (폴링 시 사용)
        /// </summary>
        private void LoadPointsFromDatabase()
        {
            try
            {
                _logger.Info("포인트 설정 로딩 중...");

                var configService = new DeviceConfigService(_deviceSeq, _logger);
                _points = configService.LoadPoints();

                if (_points.Count > 0)
                {
                    _logger.Info($"포인트 로딩 완료: {_points.Count}개");

                    // 처음 3개 샘플 출력
                    for (int i = 0; i < Math.Min(3, _points.Count); i++)
                    {
                        var p = _points[i];
                        _logger.Info($"  [{i + 1}] {p.SystemPtId} ({p.ObjectType}-{p.ObjectInstance})");
                    }
                }
                else
                {
                    _logger.Warning("포인트가 없습니다");
                }
            }
            catch (Exception ex)
            {
                _logger.Error("포인트 로딩 에러", ex);
                _points = new List<BacnetPoint>();
            }
        }

        #endregion

        #region 폴링 (자체 스레드)

        /// <summary>
        /// 자체 폴링 스레드 루프
        /// 
        /// [동작 방식]
        /// - RecvTimeCheck 설정과 무관하게 독립적으로 동작
        /// - TimeInterval 주기로 자동 폴링
        /// - MainForm 타이머 사용 안 함 (TimeRecv 호출 안 됨)
        /// 
        /// [데이터 흐름]
        /// while(_isRunning)
        ///   ↓ Sleep(폴링 주기)
        ///   ↓ BACnet ReadMultiple (_points 전체)
        ///   ↓ Dictionary<PointId, Value?> 결과
        ///   ↓ BuildResponseData() → "AV-101,25.50,0;..."
        ///   ↓ ToReceive() → Agent로 전송
        /// </summary>
        private void PollingLoop()
        {
            _logger.Info("폴링 루프 진입 (독립 스레드)");

            while (_isRunning)
            {
                try
                {
                    if (_points == null || _points.Count == 0)
                    {
                        _logger.Warning("폴링 대기 (포인트 없음)");
                        Thread.Sleep(_pollingInterval);
                        continue;
                    }

                    // ===== 1. 데이터 읽기 =====
                    var results = _client.ReadMultiple(_points);

                    if (results != null && results.Count > 0)
                    {
                        int successCount = results.Values.Count(v => v.HasValue);

                        _logger.Info($"다중 읽기 완료: {successCount}/{_points.Count} 성공");

                        // ===== 2. 데이터 포맷 =====
                        string data = FormatDataForAgent(results);

                        _logger.Info($"Agent 전송 데이터 (앞 100자): {data.Substring(0, Math.Min(100, data.Length))}...");

                        // ===== 3. Agent로 전송 =====
                        ToReceive(_deviceSeq, 1, data, results.Count);

                        _logger.Info($"✅ Agent 전송 완료: DeviceSeq={_deviceSeq}, Count={results.Count}");
                    }
                    else
                    {
                        _logger.Warning("폴링 결과 없음");
                    }

                    Thread.Sleep(_pollingInterval);
                }
                catch (Exception ex)
                {
                    _logger.Error("폴링 에러", ex);
                    Thread.Sleep(_pollingInterval);
                }
            }

            _logger.Info("폴링 스레드 종료");
        }

        /// <summary>
        /// Agent로 전송할 데이터 포맷 생성
        /// 
        /// [데이터 흐름]
        /// results = { "AO-1": 0, "AO-2": 0, "BI-0": 1, ... }
        ///   ↓
        /// foreach (point in _points)
        ///   ↓ results에서 값 찾기
        ///   ↓ AO-1 → 0 (HasValue)
        ///     ↓ "AO-1,0,0|"
        ///   ↓ AO-2 → 0 (HasValue)
        ///     ↓ "AO-1,0,0|AO-2,0,0|"
        ///   ↓ BI-0 → null (없음)
        ///     ↓ "AO-1,0,0|AO-2,0,0|BI-0,0.0,1|"
        ///   ↓
        /// return "AO-1,0,0|AO-2,0,0|BI-0,0.0,1|..."
        /// 
        /// [포맷]
        /// SystemPtId,Value,ErrorFlag|SystemPtId,Value,ErrorFlag|...
        /// - ErrorFlag: 0 = 성공, 1 = 실패
        /// </summary>
        private string FormatDataForAgent(Dictionary<string, float?> results)
        {
            var sb = new StringBuilder();

            foreach (var point in _points)
            {
                if (results.TryGetValue(point.SystemPtId, out float? value))
                {
                    if (value.HasValue)
                    {
                        // 성공: 실제 값, 에러플래그 0
                        sb.Append($"{point.SystemPtId},{value.Value},0|");
                    }
                    else
                    {
                        // 실패: FailValue, 에러플래그 1
                        sb.Append($"{point.SystemPtId},{point.FailValue},1|");
                    }
                }
                else
                {
                    // 결과 없음: FailValue, 에러플래그 1
                    sb.Append($"{point.SystemPtId},{point.FailValue},1|");
                }
            }

            // 마지막 "|" 제거
            string result = sb.ToString().TrimEnd('|');

            return result;
        }

        /// <summary>
        /// BACnet Read 결과를 ToReceive 포맷으로 변환
        /// 
        /// [입력]
        /// Dictionary<string, float?> results
        ///   - Key: SystemPtId (예: "AV-101")
        ///   - Value: 읽은 값 (예: 25.5) 또는 null (실패)
        /// 
        /// [출력]
        /// "PointId,Value,Quality;"
        ///   - Quality=0: GOOD (정상)
        ///   - Quality=1: BAD (실패, FailValue 사용)
        /// 
        /// [예시]
        /// "AV-101,25.50,0;AV-102,30.00,0;BI-39,1.00,0;"
        /// </summary>
        private string BuildResponseData(Dictionary<string, float?> results)
        {
            var sb = new StringBuilder();

            foreach (var kvp in results)
            {
                string pointId = kvp.Key;
                float? value = kvp.Value;

                if (value.HasValue)
                {
                    // 정상 값
                    sb.AppendFormat("{0},{1:F2},0;", pointId, value.Value);
                }
                else
                {
                    // 읽기 실패 → FailValue 사용
                    var point = _points.FirstOrDefault(p => p.SystemPtId == pointId);
                    float failValue = point?.FailValue ?? 0.0f;

                    sb.AppendFormat("{0},{1:F2},1;", pointId, failValue);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// MainForm에서 호출하는 타이머 (사용 안 함)
        /// 
        /// [동작]
        /// - RecvTimeCheck=false 면 호출 안 됨
        /// - RecvTimeCheck=true 여도 자체 스레드가 폴링 수행
        /// - 중복 방지를 위해 비활성
        /// </summary>
        public override bool TimeRecv()
        {
            // 자체 폴링 스레드 방식에서는 사용 안 함
            return true;
        }

        #endregion

        #region 제어 (Write)

        /// <summary>
        /// Agent로부터 제어 명령 수신
        /// 
        /// [호출 경로]
        /// Server → Agent → MainForm → DeviceAgent.SendData() → BacnetBAS.SendData()
        /// 
        /// [입력 데이터]
        /// cData: "AV-101,25.5;AV-102,30.0;"
        ///   - sendType: 0 (제어 명령)
        ///   - DataSize: 데이터 길이
        /// 
        /// [데이터 흐름]
        /// 1. 제어 데이터 파싱 ("AV-101,25.5" → PointId, Value)
        /// 2. _points에서 해당 포인트 검색
        /// 3. BACnet WritePresentValue 실행
        /// 4. 성공 여부 카운트
        /// </summary>
        public override int SendData(int sendType, string cData, int DataSize)
        {
            if (!_isInitialized || _client == null)
            {
                _logger.Warning("초기화되지 않음, 제어 무시");
                return -1;
            }

            try
            {
                _logger.Info($"제어 명령 수신: {cData}");

                // ===== 1. 제어 데이터 파싱 =====
                // "AV-101,25.5;AV-102,30.0;" → List<(PointId, Value)>
                var controlRequests = ParseControlData(cData);

                if (controlRequests.Count == 0)
                {
                    _logger.Warning("제어 데이터 파싱 실패");
                    return 0;
                }

                _logger.Info($"제어 요청: {controlRequests.Count}건");

                // ===== 2. 각 포인트별로 BACnet Write =====
                int successCount = 0;

                foreach (var req in controlRequests)
                {
                    // _points에서 해당 포인트 찾기
                    var point = _points.FirstOrDefault(p => p.SystemPtId == req.PointId);

                    if (point == null)
                    {
                        _logger.Warning($"포인트 없음: {req.PointId}");
                        continue;
                    }

                    // BACnet Write 실행
                    bool result = _client.WritePresentValue(
                        point.DeviceInstance,
                        point.ObjectType,
                        point.ObjectInstance,
                        req.Value
                    );

                    if (result)
                    {
                        successCount++;
                        _logger.Debug($"제어 성공: {req.PointId} = {req.Value:F2}");
                    }
                    else
                    {
                        _logger.Warning($"제어 실패: {req.PointId}");
                    }
                }

                _logger.Info($"제어 완료: {successCount}/{controlRequests.Count}건 성공");

                return successCount;
            }
            catch (Exception ex)
            {
                _logger.Error("제어 처리 에러", ex);
                return -1;
            }
        }

        /// <summary>
        /// 제어 데이터 파싱
        /// 
        /// [입력]
        /// "AV-101,25.5;AV-102,30.0;BI-39,1;"
        /// 
        /// [출력]
        /// List<(PointId, Value)>
        ///   - ("AV-101", 25.5)
        ///   - ("AV-102", 30.0)
        ///   - ("BI-39", 1.0)
        /// 
        /// [처리]
        /// - 세미콜론(;)으로 분리
        /// - 각 항목을 콤마(,)로 분리
        /// - float 변환 실패 시 제외
        /// </summary>
        private List<(string PointId, float Value)> ParseControlData(string cData)
        {
            var result = new List<(string, float)>();

            if (string.IsNullOrEmpty(cData))
                return result;

            try
            {
                // "AV-101,25.5;AV-102,30.0;" → ["AV-101,25.5", "AV-102,30.0"]
                var items = cData.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var item in items)
                {
                    var trimmed = item.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                        continue;

                    // "AV-101,25.5" → ["AV-101", "25.5"]
                    int commaIndex = trimmed.IndexOf(',');
                    if (commaIndex > 0 && commaIndex < trimmed.Length - 1)
                    {
                        string pointId = trimmed.Substring(0, commaIndex).Trim();
                        string valueStr = trimmed.Substring(commaIndex + 1).Trim();

                        if (float.TryParse(valueStr, out float value))
                        {
                            result.Add((pointId, value));
                        }
                        else
                        {
                            _logger.Warning($"값 파싱 실패: {trimmed}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error("제어 데이터 파싱 에러", ex);
            }

            return result;
        }

        #endregion

        #region 종료

        /// <summary>
        /// DLL 종료 (리소스 해제)
        /// 
        /// [호출 경로]
        /// MainForm.Close() → DeviceAgent.DisConnect() → BacnetBAS.DisConnect()
        /// 
        /// [처리 순서]
        /// 1. 폴링 스레드 정지
        /// 2. BACnet 연결 종료
        /// 3. 초기화 플래그 리셋
        /// </summary>
        public override bool DisConnect()
        {
            try
            {
                if (_logger != null)
                {
                    _logger.Info("BacnetBAS 종료 중...");
                }

                // ===== 1. 폴링 스레드 정지 =====
                _isRunning = false;

                if (_pollingThread != null && _pollingThread.IsAlive)
                {
                    // 5초 대기 후 강제 종료
                    if (!_pollingThread.Join(5000))
                    {
                        if (_logger != null)
                        {
                            _logger.Warning("폴링 스레드 강제 종료");
                        }
                        _pollingThread.Abort();
                    }
                }

                // ===== 2. BACnet 연결 종료 =====
                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }

                // ===== 3. 초기화 플래그 리셋 =====
                _isInitialized = false;

                if (_logger != null)
                {
                    _logger.Info("BacnetBAS 종료 완료");
                }

                return true;
            }
            catch (Exception ex)
            {
                if (_logger != null)
                {
                    _logger.Error("종료 에러", ex);
                }
                return false;
            }
        }

        #endregion
    }
}
