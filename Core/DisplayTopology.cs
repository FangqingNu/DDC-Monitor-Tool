using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DdcMonitorTool.Core
{
    /// <summary>一块活动显示目标的拓扑信息（由 QueryDisplayConfig 读出）。</summary>
    public sealed class DisplayTarget
    {
        public string GdiName { get; set; } = string.Empty;         // \\.\DISPLAY1
        public string FriendlyName { get; set; } = string.Empty;    // 例如 "BenQ XL2566K"
        public string MonitorDevicePath { get; set; } = string.Empty;
        public uint OutputTechnology { get; set; }
        public int RefreshHz { get; set; }
        public bool HdrSupported { get; set; }
        public bool HdrEnabled { get; set; }
        public int BitsPerColor { get; set; }
    }

    /// <summary>
    /// 显示拓扑查询。旧版靠 EnumDisplayDevices 猜 EDID，多显示器时永远对不上，
    /// 界面只能显示"未知显示器"。这里用 DisplayConfig 拿到每个 GDI 设备名对应的
    /// monitorDevicePath，再据此精确读注册表里的 EDID，同时顺带拿到 HDR 状态。
    /// </summary>
    public static class DisplayTopology
    {
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        public static Dictionary<string, DisplayTarget> Query()
        {
            var result = new Dictionary<string, DisplayTarget>(StringComparer.OrdinalIgnoreCase);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (TryQuery(result) || result.Count > 0)
                        return result;
                }
                catch (Exception ex)
                {
                    AppLog.Exception("DisplayTopology.Query", ex);
                    return result;
                }
            }
            return result;
        }

        private static bool TryQuery(Dictionary<string, DisplayTarget> result)
        {
            int status = DdcNative.GetDisplayConfigBufferSizes(
                DdcNative.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
            if (status != 0)
            {
                AppLog.Warn($"GetDisplayConfigBufferSizes 失败：{DdcNative.DescribeError(status)}");
                return false;
            }
            if (pathCount == 0) return false;

            var paths = new DdcNative.PATH_INFO[pathCount];
            IntPtr modes = Marshal.AllocHGlobal((int)modeCount * DdcNative.MODE_INFO_SIZE);

            try
            {
                status = DdcNative.QueryDisplayConfig(
                    DdcNative.QDC_ONLY_ACTIVE_PATHS,
                    ref pathCount, paths,
                    ref modeCount, modes,
                    IntPtr.Zero);

                if (status == ERROR_INSUFFICIENT_BUFFER) return false; // 拓扑在两次调用之间变了，重来
                if (status != 0)
                {
                    AppLog.Warn($"QueryDisplayConfig 失败：{DdcNative.DescribeError(status)}");
                    return false;
                }

                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];
                    if (path.targetInfo.targetAvailable == 0) continue;

                    string gdi = ReadSourceName(path.sourceInfo);
                    if (string.IsNullOrEmpty(gdi)) continue;

                    var target = new DisplayTarget
                    {
                        GdiName = gdi,
                        OutputTechnology = path.targetInfo.outputTechnology,
                        RefreshHz = ToHz(path.targetInfo.refreshRate),
                    };

                    var name = new DdcNative.TARGET_DEVICE_NAME
                    {
                        header = new DdcNative.DEVICE_INFO_HEADER
                        {
                            type = DdcNative.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                            size = (uint)Marshal.SizeOf<DdcNative.TARGET_DEVICE_NAME>(),
                            adapterId = path.targetInfo.adapterId,
                            id = path.targetInfo.id,
                        },
                        monitorFriendlyDeviceName = string.Empty,
                        monitorDevicePath = string.Empty,
                    };

                    if (DdcNative.DisplayConfigGetDeviceInfo(ref name) == 0)
                    {
                        target.FriendlyName = (name.monitorFriendlyDeviceName ?? string.Empty).Trim();
                        target.MonitorDevicePath = (name.monitorDevicePath ?? string.Empty).Trim();
                    }

                    var color = new DdcNative.ADVANCED_COLOR_INFO
                    {
                        header = new DdcNative.DEVICE_INFO_HEADER
                        {
                            type = DdcNative.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                            size = (uint)Marshal.SizeOf<DdcNative.ADVANCED_COLOR_INFO>(),
                            adapterId = path.targetInfo.adapterId,
                            id = path.targetInfo.id,
                        },
                    };

                    if (DdcNative.DisplayConfigGetDeviceInfo(ref color) == 0)
                    {
                        target.HdrSupported = color.Supported;
                        target.HdrEnabled = color.Enabled;
                        target.BitsPerColor = (int)color.bitsPerColorChannel;
                    }

                    result[gdi] = target;
                }

                return result.Count > 0;
            }
            finally
            {
                Marshal.FreeHGlobal(modes);
            }
        }

        private static string ReadSourceName(DdcNative.PATH_SOURCE_INFO source)
        {
            var src = new DdcNative.SOURCE_DEVICE_NAME
            {
                header = new DdcNative.DEVICE_INFO_HEADER
                {
                    type = DdcNative.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<DdcNative.SOURCE_DEVICE_NAME>(),
                    adapterId = source.adapterId,
                    id = source.id,
                },
                viewGdiDeviceName = string.Empty,
            };

            return DdcNative.DisplayConfigGetDeviceInfo(ref src) == 0
                ? (src.viewGdiDeviceName ?? string.Empty).Trim()
                : string.Empty;
        }

        private static int ToHz(DdcNative.RATIONAL r)
            => r.Denominator == 0 ? 0 : (int)Math.Round((double)r.Numerator / r.Denominator);

        public static string OutputTechnologyName(uint tech) => tech switch
        {
            0 => "VGA (HD15)",
            1 => "S-Video",
            2 => "复合视频",
            3 => "分量视频",
            4 => "DVI",
            5 => "HDMI",
            6 => "LVDS",
            8 => "D-Jpn",
            9 => "SDI",
            10 => "DisplayPort（外接）",
            11 => "DisplayPort（内嵌）",
            12 => "UDI 外接",
            13 => "UDI 内嵌",
            14 => "SDTV 加密狗",
            15 => "Miracast",
            0x80000000 => "内屏（内置面板）",
            _ => $"未知接口 (0x{tech:X})",
        };
    }
}
