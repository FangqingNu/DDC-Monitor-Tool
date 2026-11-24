using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DdcMonitorTool.Core
{
    /// <summary>
    /// 显示器检测。只在后台线程调用 —— 一次完整探测包含几十次 DDC 往返，
    /// 在 UI 线程跑会直接卡死窗口（旧版正是如此，还用 Application.DoEvents 硬顶）。
    /// </summary>
    public static class MonitorDetector
    {
        private static readonly object DdcGate = new();

        public static List<MonitorInfo> DetectAll(Action<string>? progress = null)
        {
            void Say(string msg)
            {
                AppLog.Info(msg);
                progress?.Invoke(msg);
            }

            var results = new List<MonitorInfo>();
            Say("开始检测显示器");

            var topology = DisplayTopology.Query();
            Say($"显示拓扑：{topology.Count} 个活动输出");

            IntPtr[] hmonitors;
            try
            {
                hmonitors = EnumHMonitors();
            }
            catch (Exception ex)
            {
                AppLog.Exception("枚举显示器", ex);
                Say($"枚举显示器失败：{ex.Message}");
                return results;
            }

            var fallbackEdid = topology.Count == 0 ? EdidReader.ReadAllFromRegistry() : new List<EdidRecord>();
            Say($"活动显示器：{hmonitors.Length} 台");

            for (int i = 0; i < hmonitors.Length; i++)
            {
                var info = new MonitorInfo();
                try
                {
                    var basic = GetMonitorInfo(hmonitors[i]);
                    info.DeviceName = basic.DeviceName;
                    info.IsPrimary = basic.IsPrimary;
                    info.Width = basic.Width;
                    info.Height = basic.Height;
                }
                catch (Exception ex)
                {
                    AppLog.Exception($"读取显示器 {i + 1} 基本信息", ex);
                    info.DeviceName = $"DISPLAY{i + 1}";
                }

                // 拓扑 + EDID
                if (topology.TryGetValue(info.DeviceName, out var target))
                {
                    info.FriendlyName = target.FriendlyName;
                    info.MonitorDevicePath = target.MonitorDevicePath;
                    info.OutputTechnology = target.OutputTechnology;
                    info.HdrSupported = target.HdrSupported;
                    info.HdrEnabled = target.HdrEnabled;
                    info.BitsPerColor = target.BitsPerColor;
                    if (target.RefreshHz > 0) info.RefreshHz = target.RefreshHz;
                    info.Edid = EdidReader.FromDevicePath(target.MonitorDevicePath);
                }
                else if (fallbackEdid.Count == 1 && hmonitors.Length == 1)
                {
                    info.Edid = fallbackEdid[0];
                }

                if (!info.RefreshHz.HasValue || !info.Width.HasValue)
                {
                    try
                    {
                        var mode = ReadCurrentMode(info.DeviceName);
                        info.Width ??= mode.Width;
                        info.Height ??= mode.Height;
                        info.RefreshHz ??= mode.RefreshHz;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Exception($"读取 {info.DeviceName} 显示模式", ex);
                    }
                }

                // 物理显示器句柄（仅本次探测有效，用完即关）
                var physicals = OpenPhysical(hmonitors[i], info);
                if (physicals.Count > 0)
                {
                    info.PhysicalDescription = physicals[0].Description;
                    try
                    {
                        ProbeDdc(physicals[0].Handle, info);
                    }
                    catch (Exception ex)
                    {
                        info.DdcError = ex.Message;
                        AppLog.Exception("DDC 探测", ex);
                    }
                    finally
                    {
                        foreach (var p in physicals)
                            SafeDestroy(p.Handle);
                    }
                }
                else if (string.IsNullOrEmpty(info.DdcError))
                {
                    info.DdcError = "无法获取物理显示器句柄";
                }

                // 保证主控项一定有条目，UI 不必到处判空
                foreach (byte code in VcpCodes.PrimaryVcps)
                {
                    if (!info.VcpFeatures.ContainsKey(code))
                        info.VcpFeatures[code] = new VcpFeature(code) { Supported = false, Error = "未探测到" };
                }

                results.Add(info);
                Say($"{info.DeviceName} → {info.DisplayLabel} · {info.ResolutionLabel} @ {info.RefreshLabel} · DDC={(info.DdcSupported ? "可用" : "不可用")}");
            }

            // 两块同型号显示器的 EDID 完全一样，快照会互相覆盖，这里按设备名拆开
            foreach (var group in results.GroupBy(r => r.SnapshotKey).Where(g => g.Count() > 1).ToList())
            {
                string baseKey = group.Key;
                foreach (var monitor in group)
                    monitor.SnapshotKeyOverride = $"{baseKey}@{monitor.DeviceName}";
                AppLog.Info($"检测到 {group.Count()} 台相同型号显示器，快照键已按设备名区分");
            }

            Say($"检测完成：{results.Count} 台");
            return results;
        }

        // ==================== 枚举 ====================

        public static IntPtr[] EnumHMonitors()
        {
            var handles = new List<IntPtr>();
            DdcNative.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref DdcNative.RECT rect, IntPtr data) =>
            {
                handles.Add(hMonitor);
                return true;
            };

            if (!DdcNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
                throw new InvalidOperationException(
                    $"EnumDisplayMonitors 失败：{DdcNative.DescribeError(Marshal.GetLastWin32Error())}");

            return handles.ToArray();
        }

        public static (string DeviceName, bool IsPrimary, int? Width, int? Height) GetMonitorInfo(IntPtr hMonitor)
        {
            var mi = new DdcNative.MONITORINFOEXW
            {
                cbSize = (uint)Marshal.SizeOf<DdcNative.MONITORINFOEXW>(),
                szDevice = string.Empty,
            };
            if (!DdcNative.GetMonitorInfoW(hMonitor, ref mi))
                throw new InvalidOperationException(
                    $"GetMonitorInfoW 失败：{DdcNative.DescribeError(Marshal.GetLastWin32Error())}");

            bool primary = (mi.dwFlags & DdcNative.MONITORINFOF_PRIMARY) != 0;
            int? w = mi.rcMonitor.Right - mi.rcMonitor.Left;
            int? h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
            return (mi.szDevice, primary, w > 0 ? w : null, h > 0 ? h : null);
        }

        private static (int? Width, int? Height, int? RefreshHz) ReadCurrentMode(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName) || !deviceName.StartsWith(@"\\.\", StringComparison.Ordinal))
                return (null, null, null);

            var dm = new DdcNative.DEVMODEW
            {
                dmSize = (ushort)Marshal.SizeOf<DdcNative.DEVMODEW>(),
                dmDeviceName = string.Empty,
                dmFormName = string.Empty,
            };

            if (!DdcNative.EnumDisplaySettingsExW(deviceName, DdcNative.ENUM_CURRENT_SETTINGS, ref dm, 0))
                return (null, null, null);

            return (
                dm.dmPelsWidth > 0 ? (int)dm.dmPelsWidth : null,
                dm.dmPelsHeight > 0 ? (int)dm.dmPelsHeight : null,
                dm.dmDisplayFrequency > 1 ? (int)dm.dmDisplayFrequency : null);
        }

        // ==================== 物理句柄 ====================

        public sealed class PhysicalMonitor
        {
            public IntPtr Handle { get; }
            public string Description { get; }
            public PhysicalMonitor(IntPtr handle, string description)
            {
                Handle = handle;
                Description = description;
            }
        }

        public static List<PhysicalMonitor> OpenPhysical(IntPtr hMonitor, MonitorInfo? info = null)
        {
            var list = new List<PhysicalMonitor>();

            // 实测：进程内第一次调用经常"成功但返回空句柄"（DDC 通道还没被唤醒），
            // 隔一百多毫秒再调就有值了。所以空结果也要重试，不能只看返回值。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                list.Clear();

                uint count = 0;
                if (!DdcNative.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (info != null) info.DdcError = "枚举物理显示器失败：" + DdcNative.DescribeError(err);
                    if (attempt < 2 && DdcNative.IsTransient(err))
                    {
                        Thread.Sleep(150);
                        continue;
                    }
                    return list;
                }

                if (count == 0)
                {
                    if (info != null) info.DdcError = "该输出没有可用的物理显示器";
                    return list;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)count * DdcNative.PhysicalMonitorSize);
                try
                {
                    if (!DdcNative.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buffer))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (info != null) info.DdcError = "获取物理显示器句柄失败：" + DdcNative.DescribeError(err);
                        if (attempt < 2 && DdcNative.IsTransient(err))
                        {
                            Thread.Sleep(150);
                            continue;
                        }
                        return list;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        IntPtr entry = buffer + i * DdcNative.PhysicalMonitorSize;
                        IntPtr handle = Marshal.ReadIntPtr(entry);
                        if (handle == IntPtr.Zero) continue;
                        string desc = Marshal.PtrToStringUni(entry + IntPtr.Size) ?? string.Empty;
                        list.Add(new PhysicalMonitor(handle, desc));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                if (list.Count > 0)
                {
                    if (info != null) info.DdcError = null;
                    return list;
                }

                if (attempt < 2) Thread.Sleep(150);
            }

            if (info != null) info.DdcError ??= "DDC/CI 通道无响应（句柄为空）";
            return list;
        }

        public static void SafeDestroy(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try { DdcNative.DestroyPhysicalMonitor(handle); } catch { }
        }

        // ==================== DDC 探测 ====================

        private static void ProbeDdc(IntPtr hPhysical, MonitorInfo info)
        {
            lock (DdcGate)
            {
                // 1) MC_CAPS
                uint caps = 0, colorTemps = 0;
                bool capsOk = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (DdcNative.GetMonitorCapabilities(hPhysical, ref caps, ref colorTemps)) { capsOk = true; break; }
                    int err = Marshal.GetLastWin32Error();
                    if (!DdcNative.IsTransient(err)) break;
                    Thread.Sleep(80 << attempt);
                }
                info.McCapsRaw = capsOk ? caps : 0;
                if (!capsOk)
                    AppLog.Warn($"{info.DeviceName} 读取 MC_CAPS 失败（很多显示器本来就不实现，不影响 VCP）");

                // 2) 能力字符串（真正的能力清单）
                info.CapabilityString = ReadCapabilityString(hPhysical);
                var advertised = VcpCodes.ParseVcpSection(info.CapabilityString);
                info.CapabilityModel = VcpCodes.ParseModelName(info.CapabilityString) ?? string.Empty;

                if (!string.IsNullOrEmpty(info.CapabilityString))
                    AppLog.Info($"{info.DeviceName} 能力字符串：{advertised.Count} 项 VCP");
                else
                    AppLog.Warn($"{info.DeviceName} 未返回能力字符串（0xF0），改为逐个试探主控项");

                // 3) 逐项读值
                IEnumerable<byte> codes;
                if (advertised.Count > 0)
                {
                    codes = advertised.Keys
                        .Where(c => c != VcpCodes.Capabilities && c != VcpCodes.PowerMode)
                        .OrderBy(c => Array.IndexOf(VcpCodes.PrimaryVcps, c) is int idx && idx >= 0 ? idx : 999)
                        .ThenBy(c => c)
                        .Take(40);
                }
                else
                {
                    codes = VcpCodes.PrimaryVcps;
                }

                int okCount = 0;
                var budget = System.Diagnostics.Stopwatch.StartNew();
                int scanned = 0;

                foreach (byte code in codes.ToList())
                {
                    // 慢显示器读 40 项要十几秒，给个总预算，超了就只保留已读到的
                    if (budget.ElapsedMilliseconds > 4000)
                    {
                        AppLog.Warn($"{info.DeviceName} DDC 探测超过 4 秒，剩余 {codes.Count() - scanned} 项跳过");
                        break;
                    }
                    scanned++;

                    var feature = new VcpFeature(code);
                    if (advertised.TryGetValue(code, out var allowed))
                        feature.AllowedValues.AddRange(allowed);

                    if (TryReadVcp(hPhysical, code, out int current, out int maximum, out string? error))
                    {
                        feature.Supported = true;
                        feature.Current = current;
                        feature.Maximum = maximum > 0 ? maximum : null;
                        okCount++;
                    }
                    else
                    {
                        feature.Supported = false;
                        feature.Error = error;
                    }

                    info.VcpFeatures[code] = feature;
                }

                // 4) MC_CAPS 声明的能力即使能力字符串没写，也补一条
                void Ensure(byte code, uint bit)
                {
                    if ((caps & bit) == 0) return;
                    if (info.VcpFeatures.TryGetValue(code, out var f) && f.Supported) return;
                    if (TryReadVcp(hPhysical, code, out int cur, out int max, out string? err))
                    {
                        info.VcpFeatures[code] = new VcpFeature(code) { Supported = true, Current = cur, Maximum = max > 0 ? max : null };
                        okCount++;
                    }
                    else if (!info.VcpFeatures.ContainsKey(code))
                    {
                        info.VcpFeatures[code] = new VcpFeature(code) { Supported = false, Error = err };
                    }
                }

                Ensure(VcpCodes.Brightness, DdcNative.MC_CAPS_BRIGHTNESS);
                Ensure(VcpCodes.Contrast, DdcNative.MC_CAPS_CONTRAST);
                Ensure(VcpCodes.ColorTemperature, DdcNative.MC_CAPS_COLOR_TEMPERATURE);
                Ensure(VcpCodes.RedGain, DdcNative.MC_CAPS_RED_GREEN_BLUE_GAIN | DdcNative.MC_CAPS_RED_GREEN_BLUE_DRIVE);

                // 5) 亮度：通用 VCP 不行就试专用接口（内屏）
                if (!(info.VcpFeatures.TryGetValue(VcpCodes.Brightness, out var bri) && bri.Supported)
                    && TryReadBrightnessDedicated(hPhysical, out int dcur, out int dmax))
                {
                    info.VcpFeatures[VcpCodes.Brightness] = new VcpFeature(VcpCodes.Brightness)
                    {
                        Supported = true,
                        Current = dcur,
                        Maximum = dmax,
                    };
                    okCount++;
                    AppLog.Info($"{info.DeviceName} 亮度走 GetMonitorBrightness（内屏常见）");
                }

                info.DdcSupported = okCount > 0 || caps != 0;
                if (!info.DdcSupported)
                    info.DdcError ??= "显示器未响应任何 DDC/CI 请求";

                AppLog.Info($"{info.DeviceName} DDC 探测完成：{okCount} 项可读写，caps=0x{caps:X4}");
            }
        }

        public static bool TryReadVcp(IntPtr hPhysical, byte code, out int current, out int maximum, out string? error)
        {
            uint cur = 0, max = 0;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (DdcNative.GetVCPFeatureAndVCPFeatureReply(hPhysical, code, IntPtr.Zero, ref cur, ref max))
                {
                    current = (int)cur;
                    maximum = (int)max;
                    error = null;
                    return true;
                }

                int err = Marshal.GetLastWin32Error();
                if (!DdcNative.IsTransient(err) || attempt == 2)
                {
                    // 内屏与部分面板不实现 VCP 0x10，只认专用接口
                    if (code == VcpCodes.Brightness && TryReadBrightnessDedicated(hPhysical, out current, out maximum))
                    {
                        error = null;
                        return true;
                    }

                    current = 0;
                    maximum = 0;
                    error = DdcNative.DescribeError(err);
                    return false;
                }
                // 廉价显示器控制器反应慢，退避重试
                Thread.Sleep(80 << attempt);
            }

            current = 0;
            maximum = 0;
            error = "读取失败";
            return false;
        }

        /// <summary>GetMonitorBrightness：内屏与部分面板唯一的亮度通道。</summary>
        public static bool TryReadBrightnessDedicated(IntPtr hPhysical, out int current, out int maximum)
        {
            uint lo = 0, cur = 0, hi = 0;
            if (DdcNative.GetMonitorBrightness(hPhysical, ref lo, ref cur, ref hi) && hi > 0)
            {
                current = (int)cur;
                maximum = (int)hi;
                return true;
            }

            current = 0;
            maximum = 0;
            return false;
        }

        public static string? ReadCapabilityString(IntPtr hPhysical)
        {
            uint length = 0;
            if (!DdcNative.GetCapabilitiesStringLength(hPhysical, ref length)) return null;
            if (length == 0 || length > 1_000_000) return null;

            var buffer = new byte[length];
            if (!DdcNative.CapabilitiesRequestAndCapabilitiesReply(hPhysical, buffer, length)) return null;

            int end = Array.IndexOf(buffer, (byte)0);
            if (end < 0) end = buffer.Length;
            return System.Text.Encoding.ASCII.GetString(buffer, 0, end).Trim();
        }
    }
}
