using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace BacnetInventoryApp.Common
{
    /// <summary>
    /// [SI 현업용 Logger]
    /// - 날짜별 파일 생성: Logs\yyyy-MM-dd.log
    /// - 레벨 필터: MinimumLevel 이상만 기록
    /// - 파일 + Visual Studio Output(Debug) 동시 출력 옵션
    ///
    /// [로그 예시]
    /// 2025-12-19 11:02:01.123 [INFO] [APP] Startup
    /// 2025-12-19 11:02:05.045 [WARN] [DISCOVERY] timeout subnet=172.16.130 timeoutMs=2000
    /// 2025-12-19 11:02:10.332 [ERROR] [DB] upsert failed deviceId=20059 ex=...
    ///
    /// [데이터 흐름 주석 예시]
    /// - 입력: subnet="172.16.130"
    /// - 중간: deviceId=20059 발견
    /// - 출력: RAW_DEVICE 1건 기록 + 로그 남김
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static LoggerOptions _opt;
        private static StreamWriter _writer;
        private static string _currentLogPath;

        public static void Configure(LoggerOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (string.IsNullOrWhiteSpace(options.LogDirectory))
                throw new ArgumentException("LogDirectory is required", "options.LogDirectory");

            lock (_lock)
            {
                _opt = options;
                Directory.CreateDirectory(_opt.LogDirectory);
                RotateIfNeeded(DateTime.Now);
            }
        }

        public static void Flush()
        {
            lock (_lock)
            {
                if (_writer != null) _writer.Flush();
            }
        }

        public static void Trace(string msg) { Write(LogLevel.Trace, msg, null); }
        public static void Debug(string msg) { Write(LogLevel.Debug, msg, null); }
        public static void Info(string msg) { Write(LogLevel.Info, msg, null); }
        public static void Warn(string msg) { Write(LogLevel.Warn, msg, null); }
        public static void Error(string msg, Exception ex) { Write(LogLevel.Error, msg, ex); }
        public static void Fatal(string msg, Exception ex) { Write(LogLevel.Fatal, msg, ex); }

        public static void Write(LogLevel level, string msg, Exception ex)
        {
            if (_opt == null)
                throw new InvalidOperationException("Logger.Configure()가 먼저 호출되어야 합니다.");

            if (msg == null) msg = string.Empty;
            if (level < _opt.MinimumLevel) return;

            lock (_lock)
            {
                RotateIfNeeded(DateTime.Now);

                string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string line = ts + " [" + level.ToString().ToUpperInvariant() + "] " + msg;

                if (ex != null)
                    line += Environment.NewLine + ex.ToString();

                // 파일 기록
                _writer.WriteLine(line);
                _writer.Flush(); // 현장 추적 우선: 즉시 flush

                // VS Output(Debug)에도 출력(옵션)
                if (_opt.AlsoWriteDebugOutput)
                {
                    try { System.Diagnostics.Debug.WriteLine(line); } catch { }
                }
            }
        }

        private static void RotateIfNeeded(DateTime now)
        {
            string fileName = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";
            string path = Path.Combine(_opt.LogDirectory, fileName);

            if (_writer != null && string.Equals(_currentLogPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            // 기존 writer 닫고 새로 열기
            try
            {
                if (_writer != null)
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
            }
            catch
            {
                // 닫기 실패해도 앱을 죽이지 않음(운영 안정성 우선)
            }

            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(false));
            _currentLogPath = path;
        }
    }

    public sealed class LoggerOptions
    {
        public string LogDirectory { get; set; }
        public LogLevel MinimumLevel { get; set; }
        public bool AlsoWriteDebugOutput { get; set; }
    }

    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warn = 3,
        Error = 4,
        Fatal = 5
    }
}
