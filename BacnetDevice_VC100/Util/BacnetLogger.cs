using System;
using System.IO;

namespace BacnetDevice_VC100.Util
{
    /// <summary>
    /// BacnetDevice_VC100 전용 파일 로거.
    ///
    /// - DeviceAgent에서 한 프로세스 안에 여러 디바이스가 동시에 동작하는 것을 고려.
    /// - 각 쓰레드별로 "현재 디바이스 번호" 컨텍스트를 기억하고,
    ///   해당 디바이스 전용 폴더에 일자별 로그 파일을 만든다.
    /// 
    ///   예) .\Log\Device_20059\2025-12-10.log
    ///
    /// - SetCurrentDevice(deviceSeq)를 호출한 이후에 발생하는 Info/Warn/Error 로그는
    ///   전부 해당 디바이스 폴더로 간다.
    ///
    /// - 디바이스 번호 컨텍스트가 설정되지 않은 상태에서는
    ///   파일에는 쓰지 않고 콘솔에만 출력한다.
    ///
    /// - Info/Warn/Error : 파일 + 콘솔 (컨텍스트 있을 때만 파일)
    /// - Debug           : 콘솔만 (파일 기록 안 함)
    /// </summary>
    internal static class BacnetLogger
    {
        // 쓰레드별 현재 디바이스 컨텍스트
        [ThreadStatic]
        private static int _currentDeviceSeq;

        [ThreadStatic]
        private static bool _hasCurrentDevice;

        private static readonly object _fileLock = new object();
        private static readonly string _rootLogDir;

        static BacnetLogger()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;

                // ./Log
                _rootLogDir = Path.Combine(baseDir, "Log");
                Directory.CreateDirectory(_rootLogDir);
            }
            catch (Exception ex)
            {
                _rootLogDir = null;
                Console.Error.WriteLine("[LOG-ERR] BacnetLogger 초기화 실패: " + ex.Message);
            }
        }

        /// <summary>
        /// 현재 쓰레드에서 사용하는 디바이스 번호를 설정한다.
        /// DeviceAgent가 디바이스별 스레드에서 이 함수를 한 번씩 호출해주면 된다.
        /// BacnetBAS에서도 주요 진입 함수에서 호출하도록 처리한다.
        /// </summary>
        public static void SetCurrentDevice(int deviceSeq)
        {
            if (deviceSeq <= 0)
            {
                // 잘못된 값은 그냥 컨텍스트 해제
                _hasCurrentDevice = false;
                _currentDeviceSeq = 0;
                return;
            }

            _currentDeviceSeq = deviceSeq;
            _hasCurrentDevice = true;
        }

        /// <summary>
        /// 현재 쓰레드의 디바이스 컨텍스트를 제거한다.
        /// 꼭 호출할 필요는 없지만, 필요 시 명시적으로 정리할 때 사용.
        /// </summary>
        public static void ClearCurrentDevice()
        {
            _hasCurrentDevice = false;
            _currentDeviceSeq = 0;
        }

        /// <summary>
        /// 디버그용 로그: 콘솔에만 출력, 파일에는 기록하지 않음.
        /// </summary>
        public static void Debug(string message)
        {
            WriteToConsole("DEBUG", message, null);
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
            // 1) 콘솔 출력은 항상
            WriteToConsole(level, message, ex);

            // 2) 파일 경로 사용 불가면 종료
            if (string.IsNullOrEmpty(_rootLogDir))
                return;

            // 3) 현재 쓰레드에 디바이스 컨텍스트가 없으면 파일 기록 안 함
            if (!_hasCurrentDevice || _currentDeviceSeq <= 0)
                return;

            try
            {
                string deviceFolderName = "Device_" + _currentDeviceSeq.ToString();

                // .\Log\Device_XXXX\yyyy-MM-dd.log
                string deviceLogDir = Path.Combine(_rootLogDir, deviceFolderName);
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                string fileName = today + ".log";

                string filePath = Path.Combine(deviceLogDir, fileName);
                string line = FormatLine(level, message, ex);

                lock (_fileLock)
                {
                    Directory.CreateDirectory(deviceLogDir);
                    File.AppendAllText(filePath, line);
                }
            }
            catch (Exception ioEx)
            {
                // 로깅 때문에 본 기능이 죽으면 안 되므로, 예외는 다시 던지지 않는다.
                Console.Error.WriteLine("[LOG-ERR] 파일 로그 쓰기 실패: " + ioEx.Message);
            }
        }

        private static string FormatLine(string level, string message, Exception ex)
        {
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

            if (ex == null)
            {
                return string.Format(
                    "{0} [{1}] {2}{3}",
                    now,
                    level,
                    message ?? string.Empty,
                    Environment.NewLine
                );
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

        private static void WriteToConsole(string level, string message, Exception ex)
        {
            string now = DateTime.Now.ToString("HH:mm:ss.fff");

            if (ex == null)
            {
                Console.WriteLine(
                    "{0} [{1}] {2}",
                    now,
                    level,
                    message ?? string.Empty
                );
            }
            else
            {
                Console.WriteLine(
                    "{0} [{1}] {2} | EX: {3}",
                    now,
                    level,
                    message ?? string.Empty,
                    ex.Message
                );
            }
        }
    }
}
