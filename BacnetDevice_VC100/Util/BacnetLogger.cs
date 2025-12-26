using System;
using System.IO;
using System.Text;

namespace BacnetDevice_VC100.Util
{
    /// <summary>
    /// BACnet 장비 로거
    /// 
    /// [데이터 흐름]
    /// BacnetBAS.Init()
    ///   ↓ new BacnetLogger(deviceSeq)
    ///   ↓ GetLogFilePath() → "Logs\device_20059\device_20059_20251218.txt"
    ///   ↓
    /// _logger.Info("메시지")
    ///   ↓ Log(LogLevel.INFO, "메시지")
    ///   ↓ lock(_lockObject)
    ///   ↓ "[2025-12-18 17:04:35] [INFO] 메시지"
    ///   ↓ File.AppendAllText(_logFilePath, logEntry)
    ///   ↓
    /// 로그 파일에 기록됨
    /// 
    /// [로그 경로]
    /// Logs\device_20059\device_20059_20251218.txt
    /// </summary>
    public class BacnetLogger
    {
        private readonly int _deviceSeq;
        private readonly LogLevel _minLevel;
        private readonly object _lockObject = new object();
        private string _logFilePath;

        /// <summary>
        /// 생성자
        /// 
        /// [데이터 흐름]
        /// deviceSeq (20059)
        ///   ↓ _deviceSeq = deviceSeq
        ///   ↓ _minLevel = minLevel (INFO)
        ///   ↓ GetLogFilePath()
        ///   ↓ _logFilePath = "Logs\device_20059\device_20059_20251218.txt"
        /// </summary>
        public BacnetLogger(int deviceSeq, LogLevel minLevel = LogLevel.INFO)
        {
            _deviceSeq = deviceSeq;
            _minLevel = minLevel;
            _logFilePath = GetLogFilePath();
        }

        /// <summary>
        /// 로그 파일 경로 생성
        /// 
        /// [데이터 흐름]
        /// AppDomain.CurrentDomain.BaseDirectory
        ///   ↓ "C:\SmartDeviceAgent\"
        ///   ↓ Path.Combine(exePath, "Logs")
        ///   ↓ "C:\SmartDeviceAgent\Logs"
        ///   ↓ Path.Combine(logsFolder, "device_20059")
        ///   ↓ "C:\SmartDeviceAgent\Logs\device_20059"
        ///   ↓ Directory.CreateDirectory(deviceFolder) (폴더 없으면 생성)
        ///   ↓ "device_20059_20251218.txt"
        ///   ↓ Path.Combine(deviceFolder, fileName)
        ///   ↓ "C:\SmartDeviceAgent\Logs\device_20059\device_20059_20251218.txt"
        /// 
        /// [구조]
        /// Logs\
        ///   device_20059\
        ///     device_20059_20251218.txt
        ///     device_20059_20251219.txt
        ///   device_10068\
        ///     device_10068_20251218.txt
        /// </summary>
        // [BacnetDevice_VC100\Util\BacnetLogger.cs]

        private string GetLogFilePath()
        {
            try
            {
                // 1. 기본 Logs 폴더
                string exePath = AppDomain.CurrentDomain.BaseDirectory;
                string logsFolder = Path.Combine(exePath, "Logs");

                // 2. [수정] '날짜' 폴더 생성 (예: Logs\2025-12-26)
                // 장비별 폴더가 아니라, 날짜별로 모으는 것이 관리하기 훨씬 편합니다.
                string todayDate = DateTime.Now.ToString("yyyy-MM-dd");
                string dateFolder = Path.Combine(logsFolder, todayDate);

                if (!Directory.Exists(dateFolder))
                {
                    Directory.CreateDirectory(dateFolder);
                }

                // 3. [수정] 파일명 생성 (예: Device_1010.txt)
                // 이미 폴더가 날짜이므로 파일명에는 날짜를 뺄 수도 있지만, 
                // 파일을 밖으로 복사해 나갈 때를 대비해 파일명에도 날짜를 넣는 것이 좋습니다.
                string fileName = $"Device_{_deviceSeq}_{DateTime.Now:yyyyMMdd}.txt";

                // 만약 '메인 시스템 로그(99999)'라면 이름을 구분해줍니다.
                if (_deviceSeq == 99999)
                {
                    fileName = $"System_Scan_{DateTime.Now:yyyyMMdd}.txt";
                }

                return Path.Combine(dateFolder, fileName);
            }
            catch
            {
                // 실패 시 임시 폴더 사용
                return Path.Combine(Path.GetTempPath(), $"Error_{DateTime.Now:yyyyMMdd}.txt");
            }
        }

        /// <summary>
        /// INFO 레벨 로그
        /// 
        /// [데이터 흐름]
        /// "초기화 시작"
        ///   ↓ Log(LogLevel.INFO, "초기화 시작")
        ///   ↓ "[2025-12-18 17:04:35] [INFO] 초기화 시작"
        ///   ↓ File.AppendAllText(파일경로, 로그문자열)
        /// </summary>
        public void Info(string message)
        {
            Log(LogLevel.INFO, message);
        }

        /// <summary>
        /// WARNING 레벨 로그
        /// 
        /// [데이터 흐름]
        /// "경고 메시지"
        ///   ↓ Log(LogLevel.WARNING, "경고 메시지")
        ///   ↓ "[2025-12-18 17:04:35] [WARNING] 경고 메시지"
        ///   ↓ File.AppendAllText(파일경로, 로그문자열)
        /// </summary>
        public void Warning(string message)
        {
            Log(LogLevel.WARNING, message);
        }

        /// <summary>
        /// ERROR 레벨 로그
        /// 
        /// [데이터 흐름]
        /// "DB 연결 실패", Exception
        ///   ↓ message += Exception 정보
        ///   ↓ "DB 연결 실패\nException: SqlException\nMessage: 연결 실패\nStackTrace: ..."
        ///   ↓ Log(LogLevel.ERROR, message)
        ///   ↓ "[2025-12-18 17:04:35] [ERROR] DB 연결 실패\nException: ..."
        ///   ↓ File.AppendAllText(파일경로, 로그문자열)
        /// </summary>
        public void Error(string message, Exception ex = null)
        {
            if (ex != null)
            {
                message = $"{message}\nException: {ex.GetType().Name}\nMessage: {ex.Message}\nStackTrace: {ex.StackTrace}";
            }

            Log(LogLevel.ERROR, message);
        }

        /// <summary>
        /// DEBUG 레벨 로그
        /// 
        /// [데이터 흐름]
        /// "디버그 정보"
        ///   ↓ Log(LogLevel.DEBUG, "디버그 정보")
        ///   ↓ "[2025-12-18 17:04:35] [DEBUG] 디버그 정보"
        ///   ↓ File.AppendAllText(파일경로, 로그문자열)
        /// </summary>
        public void Debug(string message)
        {
            Log(LogLevel.DEBUG, message);
        }

        /// <summary>
        /// 로그 작성
        /// 
        /// [데이터 흐름]
        /// LogLevel.INFO, "초기화 시작"
        ///   ↓ level < _minLevel ? → 종료 (레벨 필터링)
        ///   ↓ lock(_lockObject) (스레드 안전)
        ///   ↓ 날짜 체크 → 자정 넘으면 새 파일 경로 생성
        ///   ↓ DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") → "2025-12-18 17:04:35"
        ///   ↓ $"[{timestamp}] [{level}] {message}"
        ///   ↓ "[2025-12-18 17:04:35] [INFO] 초기화 시작"
        ///   ↓ File.AppendAllText(_logFilePath, logEntry + "\n", UTF8)
        ///   ↓ 파일에 추가됨
        /// 
        /// [포맷]
        /// [2025-12-18 17:04:35] [INFO] 메시지
        /// [타임스탬프] [레벨] 내용
        /// </summary>
        private void Log(LogLevel level, string message)
        {
            // 레벨 필터링: 설정된 최소 레벨보다 낮으면 무시
            if (level < _minLevel)
                return;

            try
            {
                lock (_lockObject)
                {
                    // 날짜가 바뀌면 새 파일 경로 생성 (자정 넘어가면 자동 파일 전환)
                    string currentDate = DateTime.Now.ToString("yyyyMMdd");
                    if (!_logFilePath.Contains(currentDate))
                    {
                        _logFilePath = GetLogFilePath();
                    }

                    // 타임스탬프 생성
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                    // 로그 엔트리 생성: [2025-12-18 17:04:35] [INFO] 메시지
                    string logEntry = $"[{timestamp}] [{level}] {message}";

                    // 파일에 추가 (UTF-8 인코딩)
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 로그 실패 시 무시 (무한 루프 방지)
            }
        }
    }

    /// <summary>
    /// 로그 레벨
    /// 
    /// [데이터 흐름]
    /// new BacnetLogger(deviceSeq, LogLevel.ERROR)
    ///   ↓ _minLevel = ERROR (3)
    ///   ↓ Log(LogLevel.DEBUG, ...) → 무시 (DEBUG=0 < ERROR=3)
    ///   ↓ Log(LogLevel.INFO, ...) → 무시 (INFO=1 < ERROR=3)
    ///   ↓ Log(LogLevel.WARNING, ...) → 무시 (WARNING=2 < ERROR=3)
    ///   ↓ Log(LogLevel.ERROR, ...) → 기록됨 (ERROR=3 >= ERROR=3)
    /// 
    /// [레벨]
    /// DEBUG (0) - 디버깅용 상세 정보
    /// INFO (1) - 일반 정보
    /// WARNING (2) - 경고
    /// ERROR (3) - 에러
    /// </summary>
    public enum LogLevel
    {
        DEBUG = 0,
        INFO = 1,
        WARNING = 2,
        ERROR = 3
    }
}
