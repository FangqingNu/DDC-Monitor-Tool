using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DdcMonitorTool.Core
{
    /// <summary>单次 DDC 写入的结果记录。</summary>
    public sealed class ChangeResult
    {
        public byte Code { get; init; }
        public string Item { get; init; } = string.Empty;
        public int Requested { get; init; }
        public int? Before { get; init; }
        public int? After { get; init; }
        public bool Success { get; init; }
        public string? Error { get; init; }

        public string Describe()
        {
            if (!Success) return $"{Item}：失败（{Error}）";
            if (Before.HasValue && After.HasValue)
                return $"{Item}：{Before} → {After}（请求 {Requested}）";
            return $"{Item}：已写入 {Requested}";
        }
    }

    /// <summary>
    /// 对单块显示器做安全的 DDC/CI 写入。
    ///
    /// 安全约束（旧版没有的）：
    ///   - 句柄打开后一直持有，不再每次点按钮都重新枚举 + 重读 caps（旧版每次点击约 1 秒）。
    ///   - 全局写入间隔 ≥120ms：显示器 EEPROM 控制器扛不住突发写入。
    ///   - 写入后读回校验，不一致如实上报，不谎报成功。
    ///   - 只对"设备忙"类错误重试；"不支持"重试只是白等。
    /// </summary>
    public sealed class MonitorController : IDisposable
    {
        private static readonly object WriteGate = new();
        private static DateTime _lastWriteUtc = DateTime.MinValue;
        private static readonly TimeSpan MinWriteInterval = TimeSpan.FromMilliseconds(120);

        public MonitorInfo Monitor { get; }

        private IntPtr _hPhysical = IntPtr.Zero;
        private bool _disposed;

        public MonitorController(MonitorInfo monitor) => Monitor = monitor;

        public bool IsOpen => _hPhysical != IntPtr.Zero;

        public IntPtr Handle => _hPhysical != IntPtr.Zero
            ? _hPhysical
            : throw new InvalidOperationException("控制器尚未打开");

        // ==================== 生命周期 ====================

        public void Open()
        {
            if (IsOpen) return;

            var hmonitors = MonitorDetector.EnumHMonitors();
            IntPtr matched = IntPtr.Zero;
            foreach (var hmon in hmonitors)
            {
                try
                {
                    var info = MonitorDetector.GetMonitorInfo(hmon);
                    if (string.Equals(info.DeviceName, Monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = hmon;
                        break;
                    }
                }
                catch
                {
                    // 单个显示器枚举失败不影响其它
                }
            }

            if (matched == IntPtr.Zero)
                throw new InvalidOperationException($"找不到显示器 {Monitor.DeviceName}（可能已断开或切换了输入源）");

            var physicals = MonitorDetector.OpenPhysical(matched);
            if (physicals.Count == 0)
                throw new InvalidOperationException($"{Monitor.DeviceName} 没有可用的 DDC/CI 通道");

            _hPhysical = physicals[0].Handle;
            // 多余句柄立即释放，避免泄漏
            foreach (var extra in physicals.Skip(1))
                MonitorDetector.SafeDestroy(extra.Handle);

            AppLog.Info($"已连接 {Monitor.DisplayLabel}（{Monitor.DeviceName}）");
        }

        public void Close()
        {
            if (_hPhysical == IntPtr.Zero) return;
            MonitorDetector.SafeDestroy(_hPhysical);
            _hPhysical = IntPtr.Zero;
            AppLog.Info($"已断开 {Monitor.DisplayLabel}");
        }

        public void Dispose()
        {
            if (_disposed) return;
            Close();
            _disposed = true;
        }

        // ==================== 能力 ====================

        public bool Supports(byte code) => Monitor.Supports(code);

        public int? Read(byte code)
        {
            if (!IsOpen) return null;
            return MonitorDetector.TryReadVcp(_hPhysical, code, out int cur, out _, out _) ? cur : null;
        }

        // ==================== 写入 ====================

        public ChangeResult SetBrightness(int value) => SetAndVerify(VcpCodes.Brightness, value);
        public ChangeResult SetContrast(int value) => SetAndVerify(VcpCodes.Contrast, value);
        public ChangeResult SetRedGain(int value) => SetAndVerify(VcpCodes.RedGain, value);
        public ChangeResult SetGreenGain(int value) => SetAndVerify(VcpCodes.GreenGain, value);
        public ChangeResult SetBlueGain(int value) => SetAndVerify(VcpCodes.BlueGain, value);
        public ChangeResult SetVolume(int value) => SetAndVerify(VcpCodes.AudioVolume, value);
        public ChangeResult SetSharpness(int value) => SetAndVerify(VcpCodes.Sharpness, value);

        public ChangeResult SetColorTemperature(int value)
            => SetAndVerify(VcpCodes.ColorTemperature, value);

        /// <summary>写入后读回校验。用于连续型 VCP（亮度/对比度/增益/音量）。</summary>
        public ChangeResult SetAndVerify(byte code, int value)
        {
            string name = VcpCodes.GetName(code);
            if (!IsOpen) return Fail(code, name, value, "控制器未打开");
            if (!Supports(code)) return Fail(code, name, value, "显示器未声明支持该项");

            lock (WriteGate)
            {
                try
                {
                    Throttle();

                    int? before = null;
                    if (MonitorDetector.TryReadVcp(_hPhysical, code, out int b, out _, out _))
                        before = b;

                    if (!WriteRaw(code, (uint)value, out string? writeError))
                        return Fail(code, name, value, writeError, before);

                    // 面板需要一点时间反映新值
                    Thread.Sleep(60);

                    int? after = null;
                    if (MonitorDetector.TryReadVcp(_hPhysical, code, out int a, out _, out _))
                        after = a;

                    bool ok = after.HasValue && after.Value == value;
                    var result = new ChangeResult
                    {
                        Code = code,
                        Item = name,
                        Requested = value,
                        Before = before,
                        After = after,
                        Success = ok,
                        Error = ok ? null : (after.HasValue ? "写入后读回值不一致" : "写入后无法读回"),
                    };

                    AppLog.Write(ok ? "INFO" : "WARN", result.Describe());
                    return result;
                }
                catch (Exception ex)
                {
                    AppLog.Exception($"写入 {name}", ex);
                    return Fail(code, name, value, ex.Message);
                }
            }
        }

        /// <summary>不校验的写入。用于输入源切换、恢复出厂这类"一写就断线"的操作。</summary>
        public ChangeResult SetRaw(byte code, uint value)
        {
            string name = VcpCodes.GetName(code);
            if (!IsOpen) return Fail(code, name, (int)value, "控制器未打开");

            lock (WriteGate)
            {
                Throttle();
                if (!WriteRaw(code, value, out string? error))
                    return Fail(code, name, (int)value, error);

                AppLog.Info($"{name}：已写入 {value}");
                return new ChangeResult
                {
                    Code = code,
                    Item = name,
                    Requested = (int)value,
                    Success = true,
                };
            }
        }

        /// <summary>
        /// 恢复出厂。MCCS 标准码是 0x04（Restore Factory Defaults），
        /// 0x0C 是部分厂商（BenQ 等）的私有实现，标准码不可用时才退回。
        /// </summary>
        public ChangeResult FactoryReset()
        {
            byte code = Supports(VcpCodes.RestoreFactoryDefaults)
                ? VcpCodes.RestoreFactoryDefaults
                : VcpCodes.FactoryReset;
            return SetRaw(code, 1);
        }

        /// <summary>批量还原（快照恢复）。返回逐项结果。</summary>
        public List<ChangeResult> Restore(IReadOnlyDictionary<byte, int> values)
        {
            var results = new List<ChangeResult>();
            foreach (var kv in values)
            {
                if (!Supports(kv.Key)) continue;
                results.Add(SetAndVerify(kv.Key, kv.Value));
            }
            return results;
        }

        // ==================== 内部 ====================

        private bool WriteRaw(byte code, uint value, out string? error)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (DdcNative.SetVCPFeature(_hPhysical, code, value))
                {
                    error = null;
                    return true;
                }

                int err = Marshal.GetLastWin32Error();
                if (!DdcNative.IsTransient(err) || attempt == 2)
                {
                    // 内屏与部分面板不实现 VCP 0x10，只认专用接口
                    if (code == VcpCodes.Brightness && DdcNative.SetMonitorBrightness(_hPhysical, value))
                    {
                        error = null;
                        return true;
                    }

                    error = DdcNative.DescribeError(err);
                    return false;
                }
                Thread.Sleep(80 << attempt);
            }

            error = "写入失败";
            return false;
        }

        private static void Throttle()
        {
            var wait = MinWriteInterval - (DateTime.UtcNow - _lastWriteUtc);
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            _lastWriteUtc = DateTime.UtcNow;
        }

        private static ChangeResult Fail(byte code, string name, int value, string? error, int? before = null)
        {
            var result = new ChangeResult
            {
                Code = code,
                Item = name,
                Requested = value,
                Before = before,
                Success = false,
                Error = error ?? "未知错误",
            };
            AppLog.Warn(result.Describe());
            return result;
        }
    }
}
