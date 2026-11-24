using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DdcMonitorTool.Core
{
    /// <summary>
    /// 前台窗口变化监听。
    ///
    /// 用 SetWinEventHook 而不是轮询：游戏获得焦点的那一刻就能切换，空闲时 CPU 为 0。
    /// 另加一个 3 秒兜底轮询 —— 少数情况下（UAC 提权窗口、某些反作弊）钩子会漏事件。
    /// </summary>
    public sealed class ForegroundWatcher : IDisposable
    {
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private delegate void WinEventDelegate(
            IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback,
            uint processId, uint threadId, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private readonly WinEventDelegate _callback;   // 必须持有引用，否则被 GC 回收后回调会崩
        private readonly ConcurrentDictionary<uint, string> _processCache = new();
        private readonly System.Threading.Timer _fallback;

        private IntPtr _hook = IntPtr.Zero;
        private string _lastReported = string.Empty;
        private bool _disposed;

        public ForegroundWatcher()
        {
            _callback = OnWinEvent;
            _fallback = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>参数是小写 exe 名；无法解析时为空字符串。可能在任意线程触发。</summary>
        public event Action<string>? Changed;

        public bool Running => _hook != IntPtr.Zero;

        public void Start()
        {
            if (_hook != IntPtr.Zero) return;

            _hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);

            if (_hook == IntPtr.Zero)
                AppLog.Warn("SetWinEventHook 失败，自动切换将依赖兜底轮询");

            _lastReported = CurrentExe();
            _fallback.Change(3000, 3000);
            AppLog.Info($"前台监听已启动（hook={( _hook != IntPtr.Zero ? "OK" : "失败")}）");
        }

        public void Stop()
        {
            _fallback.Change(Timeout.Infinite, Timeout.Infinite);
            if (_hook == IntPtr.Zero) return;
            try { UnhookWinEvent(_hook); } catch { }
            _hook = IntPtr.Zero;
        }

        private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0) return;   // 只关心窗口本身，忽略菜单/光标等子对象
            Poll();
        }

        private void Poll()
        {
            string exe = CurrentExe();
            if (string.Equals(exe, _lastReported, StringComparison.OrdinalIgnoreCase)) return;
            _lastReported = exe;
            try { Changed?.Invoke(exe); } catch (Exception e) { AppLog.Exception("前台切换处理", e); }
        }

        /// <summary>当前前台窗口所属进程（小写，带 .exe）。</summary>
        public static string CurrentExe()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return string.Empty;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return string.Empty;

                using var process = Process.GetProcessById((int)pid);
                return (process.ProcessName + ".exe").ToLowerInvariant();
            }
            catch
            {
                // 提权进程 / 已退出进程会拒绝访问，忽略即可
                return string.Empty;
            }
        }

        /// <summary>指定的游戏是否还在运行（用于区分"切到桌面"和"真的退出"）。</summary>
        public static bool IsProcessRunning(string exe)
        {
            try
            {
                string name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? exe[..^4]
                    : exe;
                var processes = Process.GetProcessesByName(name);
                try
                {
                    return processes.Length > 0;
                }
                finally
                {
                    foreach (var p in processes) p.Dispose();
                }
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            _fallback.Dispose();
            _disposed = true;
        }
    }
}
