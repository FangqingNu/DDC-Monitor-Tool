using System;
using System.Runtime.InteropServices;

namespace DdcMonitorTool.Core
{
    /// <summary>
    /// 跨通道色彩矩阵（Windows 放大镜 API）。
    ///
    /// 为什么需要它：Gamma Ramp 是"一维逐通道 LUT"，每个通道的输出只取决于自己的输入，
    /// 数学上做不出真正的饱和度（饱和度需要 R/G/B 互相参与）。色彩矩阵可以。
    ///
    /// 代价：走 DWM 合成，**只在窗口化 / 无边框窗口下生效，独占全屏无效**；
    /// 而且是全屏统一生效，不分显示器。所以它和 DDC 是互补关系，不是替代。
    /// </summary>
    public static class ColorMatrix
    {
        [DllImport("Magnification.dll", SetLastError = true)]
        private static extern bool MagInitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        private static extern bool MagUninitialize();

        [DllImport("Magnification.dll", SetLastError = true)]
        private static extern bool MagSetFullscreenColorEffect(IntPtr effect);

        private static bool _initialized;
        private static bool _applyFailed;

        /// <summary>50 = 中性。0 接近灰阶，100 约为 1.8 倍饱和度。</summary>
        public static double BoostToScale(int boostPercent)
        {
            double b = Math.Clamp(boostPercent, 0, 100);
            return b <= 50
                ? 0.4 + b / 50.0 * 0.6
                : 1.0 + (b - 50) / 50.0 * 0.8;
        }

        public static float[] BuildSaturationMatrix(double scale)
        {
            const double lr = 0.299, lg = 0.587, lb = 0.114;
            double s = Math.Clamp(scale, 0.0, 3.0);

            // 行主序 5x5，输出 = M × 输入
            return new float[]
            {
                (float)(lr * (1 - s) + s), (float)(lg * (1 - s)),     (float)(lb * (1 - s)),     0, 0,
                (float)(lr * (1 - s)),     (float)(lg * (1 - s) + s), (float)(lb * (1 - s)),     0, 0,
                (float)(lr * (1 - s)),     (float)(lg * (1 - s)),     (float)(lb * (1 - s) + s), 0, 0,
                0, 0, 0, 1, 0,
                0, 0, 0, 0, 1,
            };
        }

        public static bool Apply(int boostPercent)
        {
            if (boostPercent == 50) return Reset();

            try
            {
                if (!_initialized)
                {
                    _initialized = MagInitialize();
                    if (!_initialized)
                    {
                        AppLog.Warn("MagInitialize 失败，色彩矩阵不可用");
                        return false;
                    }
                }

                float[] matrix = BuildSaturationMatrix(BoostToScale(boostPercent));
                IntPtr buffer = Marshal.AllocHGlobal(matrix.Length * sizeof(float));
                try
                {
                    Marshal.Copy(matrix, 0, buffer, matrix.Length);
                    bool ok = MagSetFullscreenColorEffect(buffer);
                    if (!ok)
                    {
                        _applyFailed = true;
                        AppLog.Warn($"MagSetFullscreenColorEffect 失败，Win32={Marshal.GetLastWin32Error()}");
                    }
                    return ok;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                AppLog.Exception("应用色彩矩阵", ex);
                return false;
            }
        }

        public static bool Reset()
        {
            try
            {
                // 即使本进程没应用过，也要尝试初始化再写单位矩阵：
                // 上一次进程被强杀时，DWM 里的效果可能还留着。
                if (!_initialized)
                {
                    _initialized = MagInitialize();
                    if (!_initialized) return true;
                }

                var identity = BuildSaturationMatrix(1.0);
                IntPtr buffer = Marshal.AllocHGlobal(identity.Length * sizeof(float));
                try
                {
                    Marshal.Copy(identity, 0, buffer, identity.Length);
                    return MagSetFullscreenColorEffect(buffer);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Shutdown()
        {
            try
            {
                if (!_initialized) return;
                Reset();
                MagUninitialize();
                _initialized = false;
            }
            catch
            {
                // 退出路径上不再抛异常
            }
        }
    }
}
