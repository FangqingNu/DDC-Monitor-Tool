using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DdcMonitorTool.Core;
using DdcMonitorTool.UI;

namespace DdcMonitorTool
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // 有参数 = CLI 模式。WinExe 默认没有控制台，需要借一个。
            if (args.Length > 0)
            {
                AppLog.AttachConsole();
                try
                {
                    return Cli.Run(args);
                }
                catch (Exception ex)
                {
                    AppLog.Exception("CLI", ex);
                    Console.WriteLine("失败：" + ex.Message);
                    return 1;
                }
            }

            return RunGui();
        }

        private static int RunGui()
        {
            // 清单里已声明 PerMonitorV2；这里再设一次用于无清单启动（如 dotnet run）。
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9f));

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ReportFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

            AppLog.Info("===== DDC 显示器工具启动 =====");
            Application.Run(new MainForm());
            AppLog.Info("===== 退出 =====");
            return 0;
        }

        private static void ReportFatal(Exception? ex)
        {
            if (ex == null) return;
            AppLog.Exception("未处理异常", ex);
            try
            {
                MessageBox.Show(
                    "程序遇到未处理的错误，已记录到日志：\n" + AppLog.FilePath + "\n\n" + ex.Message,
                    "出错了",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // 连弹窗都失败就算了
            }
        }
    }
}
