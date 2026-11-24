using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DdcMonitorTool.Core
{
    /// <summary>
    /// GPU Gamma Ramp（GDI 一维 LUT）。
    ///
    /// 与 DDC 的区别：这是显卡输出端的查找表，显示器不知道被改了；
    /// 优点是对所有内容生效（含独占全屏游戏），缺点是 HDR 开启时 Windows 会忽略它。
    /// 每块屏各自一份 ramp，必须按 \\.\DISPLAYn 建 DC，不能写主屏。
    /// </summary>
    public static class GammaRamp
    {
        public const int Size = 256;

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, [In] ushort[] ramp);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDCW(string? driver, string device, string? output, IntPtr initData);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool DeleteDC(IntPtr hdc);

        public static ushort[] Identity()
        {
            var ramp = new ushort[Size * 3];
            for (int i = 0; i < Size; i++)
            {
                ushort v = (ushort)Math.Round(i / 255.0 * 65535);
                ramp[i] = ramp[Size + i] = ramp[Size * 2 + i] = v;
            }
            return ramp;
        }

        /// <summary>
        /// 生成 ramp。
        /// gamma &gt; 1 提亮中间调；blackLift 只抬暗部，随亮度快速衰减，白点保持 1.0。
        /// </summary>
        public static ushort[] Build(double gamma, int blackLiftPercent)
        {
            double g = Math.Clamp(gamma, 0.5, 2.5);
            double lift = Math.Clamp(blackLiftPercent, 0, 100) / 100.0 * 0.25;
            var ramp = new ushort[Size * 3];

            for (int i = 0; i < Size; i++)
            {
                double x = i / 255.0;
                double v = Math.Pow(x, 1.0 / g);
                if (lift > 0) v += lift * Math.Pow(1.0 - v, 3.0);
                v = Math.Clamp(v, 0.0, 1.0);

                ushort u = (ushort)Math.Round(v * 65535);
                ramp[i] = ramp[Size + i] = ramp[Size * 2 + i] = u;
            }
            return ramp;
        }

        public static ushort[]? Read(string deviceName)
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = CreateDCW("DISPLAY", deviceName, null, IntPtr.Zero);
                if (hdc == IntPtr.Zero) return null;

                var ramp = new ushort[Size * 3];
                return GetDeviceGammaRamp(hdc, ramp) ? ramp : null;
            }
            catch (Exception ex)
            {
                AppLog.Exception("读取 Gamma Ramp", ex);
                return null;
            }
            finally
            {
                if (hdc != IntPtr.Zero) DeleteDC(hdc);
            }
        }

        public static bool Write(string deviceName, ushort[] ramp)
        {
            if (ramp == null || ramp.Length != Size * 3) return false;

            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = CreateDCW("DISPLAY", deviceName, null, IntPtr.Zero);
                if (hdc == IntPtr.Zero)
                {
                    AppLog.Warn($"无法为 {deviceName} 创建 DC，Gamma 未写入");
                    return false;
                }

                bool ok = SetDeviceGammaRamp(hdc, ramp);
                if (!ok)
                    AppLog.Warn($"SetDeviceGammaRamp({deviceName}) 被驱动拒绝，Win32={Marshal.GetLastWin32Error()}");
                return ok;
            }
            catch (Exception ex)
            {
                AppLog.Exception("写入 Gamma Ramp", ex);
                return false;
            }
            finally
            {
                if (hdc != IntPtr.Zero) DeleteDC(hdc);
            }
        }

        public static bool IsIdentity(ushort[] ramp)
        {
            var id = Identity();
            for (int i = 0; i < id.Length; i++)
                if (Math.Abs(ramp[i] - id[i]) > 8) return false;
            return true;
        }
    }

    /// <summary>
    /// Gamma 基线的持久化。
    ///
    /// 三个必须解决的场景：
    ///   1. 反复应用同一套预设 —— 基线只能取第一次，否则"原始状态"会被自己覆盖。
    ///   2. 正常退出 —— 还原基线并删掉标记。
    ///   3. 进程被杀 / 蓝屏 —— 下次启动时根据标记自动还原，否则屏幕会一直偏色。
    /// </summary>
    public static class GammaState
    {
        private const string ActiveFileName = "active.json";

        public static string Directory { get; } = Path.Combine(AppLog.Directory, "gamma");

        private static string BaselinePath(string deviceName)
            => Path.Combine(Directory, SafeName(deviceName) + ".bin");

        private static string SafeName(string deviceName)
            => deviceName.Replace("\\", string.Empty).Replace(".", string.Empty).Replace("/", string.Empty);

        /// <summary>首次写入前保存真实基线；已存在则不覆盖。</summary>
        public static void SaveBaselineIfMissing(string deviceName, ushort[] ramp)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                string path = BaselinePath(deviceName);
                if (File.Exists(path)) return;

                var bytes = new byte[ramp.Length * 2];
                Buffer.BlockCopy(ramp, 0, bytes, 0, bytes.Length);
                File.WriteAllBytes(path, bytes);
                AppLog.Info($"已保存 {deviceName} 的 Gamma 基线");
            }
            catch (Exception ex)
            {
                AppLog.Exception("保存 Gamma 基线", ex);
            }
        }

        public static bool HasBaseline(string deviceName) => File.Exists(BaselinePath(deviceName));

        public static ushort[]? LoadBaseline(string deviceName)
        {
            try
            {
                string path = BaselinePath(deviceName);
                if (!File.Exists(path)) return null;

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length != GammaRamp.Size * 3 * 2) return null;

                var ramp = new ushort[GammaRamp.Size * 3];
                Buffer.BlockCopy(bytes, 0, ramp, 0, bytes.Length);
                return ramp;
            }
            catch (Exception ex)
            {
                AppLog.Exception("读取 Gamma 基线", ex);
                return null;
            }
        }

        public static void MarkActive(string deviceName, bool active)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var set = new HashSet<string>(ActiveDevices(), StringComparer.OrdinalIgnoreCase);
                if (active) set.Add(deviceName); else set.Remove(deviceName);

                string path = Path.Combine(Directory, ActiveFileName);
                if (set.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                File.WriteAllLines(path, set);
            }
            catch (Exception ex)
            {
                AppLog.Exception("记录 Gamma 状态", ex);
            }
        }

        public static IEnumerable<string> ActiveDevices()
        {
            try
            {
                string path = Path.Combine(Directory, ActiveFileName);
                if (!File.Exists(path)) return Array.Empty<string>();
                return File.ReadAllLines(path);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>还原到基线并清理。</summary>
        public static bool Restore(string deviceName)
        {
            var baseline = LoadBaseline(deviceName);
            bool ok = baseline != null && GammaRamp.Write(deviceName, baseline);
            if (!ok)
            {
                // 基线读不到（驱动不支持读）时退回线性，至少不留偏色
                ok = GammaRamp.Write(deviceName, GammaRamp.Identity());
            }

            try
            {
                string path = BaselinePath(deviceName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }

            MarkActive(deviceName, false);
            AppLog.Info($"已还原 {deviceName} 的 Gamma（{(ok ? "OK" : "失败")}）");
            return ok;
        }

        /// <summary>启动时调用：上次没正常退出的话，把屏幕救回来。</summary>
        public static void RecoverAbandoned()
        {
            foreach (string device in ActiveDevices())
            {
                AppLog.Warn($"检测到上次未正常还原 Gamma（{device}），正在恢复");
                Restore(device);
            }
        }
    }
}
