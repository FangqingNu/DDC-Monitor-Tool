using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DdcMonitorTool.Core
{
    /// <summary>
    /// 进程级日志。三路输出：内存环形缓冲（界面日志页）、磁盘文件（排障）、控制台（CLI 模式）。
    ///
    /// 旧版直接用 Console.WriteLine，而 WinExe 没有控制台，所以检测过程的所有细节
    /// 在界面上完全看不到 —— 这也是"日志页总是空的"的根因。
    /// </summary>
    public static class AppLog
    {
        private const int RingCapacity = 3000;
        private const long MaxFileBytes = 2 * 1024 * 1024;

        private static readonly object Gate = new();
        private static readonly Queue<string> Ring = new(RingCapacity);
        private static StreamWriter? _writer;
        private static bool _consoleReady;

        public static string Directory { get; }
        public static string FilePath { get; }

        static AppLog()
        {
            Directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DdcMonitorTool");
            FilePath = Path.Combine(Directory, "app.log");
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                RotateIfNeeded();
                _writer = new StreamWriter(
                    new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false))
                { AutoFlush = true };
            }
            catch
            {
                _writer = null; // 只读目录等场景下降级为仅内存
            }
        }

        /// <summary>新行追加时触发。可能来自任意线程，订阅方需自行切回 UI 线程。</summary>
        public static event Action<string>? Appended;

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERR ", message);

        public static void Exception(string context, Exception ex)
            => Write("ERR ", $"{context}: {ex.GetType().Name}: {ex.Message}");

        public static void Write(string level, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {level} {message}";
            lock (Gate)
            {
                Ring.Enqueue(line);
                while (Ring.Count > RingCapacity) Ring.Dequeue();
                try { _writer?.WriteLine(line); } catch { /* 磁盘满/占用，忽略 */ }
            }

            if (_consoleReady)
            {
                try { Console.WriteLine(line); } catch { }
            }

            try { Appended?.Invoke(line); } catch { /* 订阅方异常不得影响记录 */ }
        }

        public static string[] Snapshot()
        {
            lock (Gate) return Ring.ToArray();
        }

        public static void Clear()
        {
            lock (Gate) Ring.Clear();
        }

        /// <summary>WinExe 启动时没有控制台；CLI 模式下借用父进程控制台或新建一个。</summary>
        public static void AttachConsole()
        {
            if (_consoleReady) return;
            bool ok = NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);
            if (!ok) ok = NativeMethods.AllocConsole();
            if (!ok) return;

            try
            {
                var stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
                _consoleReady = true;
            }
            catch
            {
                _consoleReady = false;
            }
        }

        private static void RotateIfNeeded()
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (!fi.Exists || fi.Length < MaxFileBytes) return;
                string old = FilePath + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(FilePath, old);
            }
            catch
            {
                // 轮转失败不影响继续追加
            }
        }
    }

    internal static class NativeMethods
    {
        public const int AttachParentProcess = -1;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AttachConsole(int dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AllocConsole();
    }
}
