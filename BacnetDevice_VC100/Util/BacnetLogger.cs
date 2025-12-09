using System;
using System.IO;

namespace BacnetDevice_VC100.Util
{
    /// <summary>
    /// BacnetDevice_VC100 전용 심플 파일 로거.
    /// SmartDeviceAgent.exe 기준 ./Log/BacnetDevice_VC100.log에 로그를 남긴다.
    /// </summary>
    internal static class BacnetLogger
    {
        private static readonly object _sync = new object();
        private static readonly string _logPath;

        static BacnetLogger()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string logDir = Path.Combine(baseDir, "Log");

                Directory.CreateDirectory(logDir);

                _logPath = Path.Combine(logDir, "BacnetDevice_VC100.log");
            }
            catch
            {
                // 로거 초기화 실패가 DLL 로딩 실패로 이어지면 안 되므로,
                // 여기서는 예외를 밖으로 던지지 않는다.
                _logPath = null;
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static void Write(string level, string message, Exception ex)
        {
            if (string.IsNullOrEmpty(_logPath))
                return;

            try
            {
                string line = FormatLine(level, message, ex);

                lock (_sync)
                {
                    File.AppendAllText(_logPath, line);
                }
            }
            catch
            {
                // 로깅 실패 때문에 본 기능이 죽으면 안 되므로
                // 마지막 방어선인 여기서는 예외를 다시 던지지 않는다.
            }
        }

        private static string FormatLine(string level, string message, Exception ex)
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            if (ex == null)
            {
                return string.Format("{0} [{1}] {2}{3}",
                    now, level, message ?? string.Empty, Environment.NewLine);
            }

            return string.Format(
                "{0} [{1}] {2} | EX: {3}{4}{5}{4}",
                now,
                level,
                message ?? string.Empty,
                ex.Message,
                Environment.NewLine,
                ex.StackTrace
            );
        }
    }
}
