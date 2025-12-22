using System;
using System.Windows.Forms;
using BacnetInventoryApp.Common;

namespace BacnetInventoryApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ✅ Logger 초기화 (날짜별 파일 생성)
            Logger.Configure(new LoggerOptions
            {
                MinimumLevel = LogLevel.Info,
                LogDirectory = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"),
                AlsoWriteDebugOutput = true
            });

            Logger.Info("[APP] Startup");

            try
            {
                Application.Run(new MainForm()); // 너 프로젝트 시작 폼 이름으로 유지
            }
            catch (Exception ex)
            {
                Logger.Fatal("[APP] Unhandled exception", ex);
                MessageBox.Show("치명적 오류: " + ex.Message);
            }
            finally
            {
                Logger.Info("[APP] Shutdown");
                Logger.Flush();
            }
        }
    }
}
