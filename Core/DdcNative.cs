using System;
using System.Runtime.InteropServices;

namespace DdcMonitorTool.Core
{
    /// <summary>
    /// 所有 Win32 P/Invoke 声明集中在此处。
    ///
    /// 三组 API：
    ///   1. user32 + dxva2 —— DDC/CI 控制（VESA MCCS over I2C）
    ///   2. DisplayConfig —— 显示拓扑，用于把 \\.\DISPLAYn 精确映射到 EDID / HDR / 连接方式
    ///   3. kernel32 —— 仅错误码描述
    /// </summary>
    internal static class DdcNative
    {
        // ==================== 常量 ====================

        public const uint MONITORINFOF_PRIMARY = 0x00000001;
        public const uint ENUM_CURRENT_SETTINGS = 0xFFFFFFFF;

        // MC_CAPS 位（dxva2.h）
        public const uint MC_CAPS_BRIGHTNESS = 0x00000001;
        public const uint MC_CAPS_CONTRAST = 0x00000002;
        public const uint MC_CAPS_COLOR_TEMPERATURE = 0x00000004;
        public const uint MC_CAPS_RED_GREEN_BLUE_GAIN = 0x00000008;
        public const uint MC_CAPS_RED_GREEN_BLUE_DRIVE = 0x00000010;
        public const uint MC_CAPS_DEGAUSS = 0x00000020;
        public const uint MC_CAPS_DISPLAY_AREA_POSITION = 0x00000040;
        public const uint MC_CAPS_DISPLAY_AREA_SIZE = 0x00000080;
        public const uint MC_CAPS_RESTORE_FACTORY_DEFAULTS = 0x00000400;
        public const uint MC_CAPS_REDUCE_BLUELIGHT = 0x00000800;

        // QueryDisplayConfig 标志
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        // DISPLAYCONFIG_DEVICE_INFO_TYPE
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;

        // ==================== 结构体：显示器枚举 ====================

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEXW
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        /// <summary>
        /// PHYSICAL_MONITOR 的字节步长：HANDLE + WCHAR[128]。
        ///
        /// 这里刻意不用结构体数组做 P/Invoke：实测 [Out] PHYSICAL_MONITOR[] 编组时，
        /// 字符串字段能正常回写，但 hPhysicalMonitor 永远是 0（句柄被吞掉），
        /// 导致所有 DDC 写入都失败。改为手动分配非托管缓冲后一切正常。
        /// </summary>
        public static readonly int PhysicalMonitorSize = IntPtr.Size + 128 * sizeof(char);

        // ==================== 结构体：DisplayConfig ====================

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PATH_INFO
        {
            public PATH_SOURCE_INFO sourceInfo;
            public PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        /// <summary>DISPLAYCONFIG_MODE_INFO 固定 64 字节（16 字节头 + 48 字节联合体）。
        /// 这里不声明结构体，直接用非托管缓冲承载 —— 含 fixed 缓冲区的结构体在
        /// P/Invoke 编组时会被当成单个元素回写，导致后续字节全部丢失。</summary>
        public const int MODE_INFO_SIZE = 64;

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        /// <summary>注意：WCHAR 数组必须用 ByValTStr 声明。
        /// 用 fixed char[] 时编组器只会回写 1 个字符，字符串被截断成 1 个字节。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SOURCE_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TARGET_DEVICE_NAME
        {
            public DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ADVANCED_COLOR_INFO
        {
            public DEVICE_INFO_HEADER header;
            /// <summary>bit0 支持, bit1 已启用, bit2 强制广色域, bit3 被强制禁用。</summary>
            public uint value;
            public uint colorEncoding;      // 0 = RGB, 1 = YCbCr444, 2 = YCbCr422, 3 = YCbCr420, 4 = 强度
            public uint bitsPerColorChannel;

            public bool Supported => (value & 0x1) != 0;
            public bool Enabled => (value & 0x2) != 0;
        }

        // ==================== user32 ====================

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumDisplayMonitors(
            IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettingsExW(
            string deviceName, uint modeNum, ref DEVMODEW devMode, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetDisplayConfigBufferSizes(
            uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPaths, [Out] PATH_INFO[] paths,
            ref uint numModes, IntPtr modes,
            IntPtr currentTopologyId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME deviceName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME deviceName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO colorInfo);

        // ==================== dxva2 ====================

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, uint count, IntPtr buffer);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool DestroyPhysicalMonitor(IntPtr hPhysicalMonitor);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetMonitorCapabilities(
            IntPtr hPhysicalMonitor, ref uint caps, ref uint colorTemperatures);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetMonitorBrightness(
            IntPtr hPhysicalMonitor, ref uint minimum, ref uint current, ref uint maximum);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetMonitorBrightness(IntPtr hPhysicalMonitor, uint newBrightness);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(
            IntPtr hPhysicalMonitor, byte vcpCode, IntPtr vcpType,
            ref uint currentValue, ref uint maximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetVCPFeature(
            IntPtr hPhysicalMonitor, byte vcpCode, uint newValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetCapabilitiesStringLength(
            IntPtr hPhysicalMonitor, ref uint length);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool CapabilitiesRequestAndCapabilitiesReply(
            IntPtr hPhysicalMonitor,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] buffer,
            uint length);

        // ==================== 错误码 ====================

        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_GEN_FAILURE = 31;      // 设备忙 / I2C 无应答，值得重试
        public const int ERROR_NOT_READY = 21;
        public const int ERROR_NOT_SUPPORTED = 50;
        public const int ERROR_INVALID_PARAMETER = 87;
        public const int ERROR_NOT_FOUND = 1168;      // 显示器刚唤醒/切模式时枚举不到

        public static string DescribeError(int code) => code switch
        {
            0 => "成功",
            5 => "拒绝访问（5）— 尝试以管理员身份运行",
            21 => "设备未就绪（21）— 显示器可能正在休眠或切换信号",
            31 => "设备无响应（31）— I2C 通道忙，或该 VCP 不被支持",
            50 => "请求不受支持（50）— 显示器未实现该功能",
            87 => "参数无效（87）",
            1168 => "找不到设备（1168）— 显示器刚唤醒或正在切换分辨率",
            _ => $"Win32 错误 {code}"
        };

        /// <summary>瞬时错误：值得等一会儿重试。其余（如 50 不支持）重试只是白等。</summary>
        public static bool IsTransient(int code)
            => code == ERROR_GEN_FAILURE || code == ERROR_NOT_READY || code == ERROR_NOT_FOUND;
    }
}
