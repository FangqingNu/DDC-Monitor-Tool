using System;
using System.Collections.Generic;
using System.Globalization;

namespace DdcMonitorTool.Core
{
    /// <summary>
    /// VESA MCCS VCP 代码与枚举值。
    ///
    /// 原则：能力一律来自显示器自报的 capability string（0xF0）或 MC_CAPS 位，
    /// 代码里只提供"名字"，不提供"假设"。
    /// </summary>
    public static class VcpCodes
    {
        public const byte RestoreFactoryDefaults = 0x04;
        public const byte FactoryReset = 0x0C;
        public const byte Brightness = 0x10;
        public const byte Contrast = 0x12;
        public const byte ColorTemperature = 0x14;
        public const byte RedGain = 0x16;
        public const byte GreenGain = 0x18;
        public const byte BlueGain = 0x1A;
        public const byte RedBlackLevel = 0x6C;
        public const byte GreenBlackLevel = 0x6E;
        public const byte BlueBlackLevel = 0x70;
        public const byte Sharpness = 0x87;
        public const byte InputSource = 0x60;
        public const byte AudioVolume = 0x62;
        public const byte AudioMute = 0x8D;
        public const byte PowerMode = 0xD6;
        public const byte Capabilities = 0xF0;

        private static readonly Dictionary<byte, string> Names = new()
        {
            [0x02] = "新控制值",
            [0x04] = "恢复出厂默认",
            [0x05] = "恢复出厂亮度/对比度",
            [0x08] = "恢复色彩默认",
            [0x0A] = "时钟",
            [0x0B] = "相位",
            [0x0C] = "恢复出厂设置",
            [0x0E] = "水平位置",
            [0x12] = "对比度",
            [0x14] = "色温",
            [0x16] = "红色增益",
            [0x17] = "绿色偏移",
            [0x18] = "绿色增益",
            [0x1A] = "蓝色增益",
            [0x52] = "色饱和度",
            [0x59] = "6 轴饱和度（红）",
            [0x5A] = "6 轴饱和度（黄）",
            [0x5B] = "6 轴饱和度（绿）",
            [0x5C] = "6 轴饱和度（青）",
            [0x5D] = "6 轴饱和度（蓝）",
            [0x5E] = "6 轴饱和度（洋红）",
            [0x60] = "输入源",
            [0x62] = "音量",
            [0x6C] = "红色黑电平",
            [0x6E] = "绿色黑电平",
            [0x70] = "蓝色黑电平",
            [0x87] = "锐度",
            [0x8D] = "静音",
            [0xAC] = "水平频率",
            [0xAE] = "垂直频率",
            [0xB6] = "显示器使用时长",
            [0xC6] = "屏幕旋转",
            [0xC8] = "色域预设",
            [0xCC] = "OSD 语言",
            [0xD6] = "电源模式",
            [0xDF] = "VCP 版本",
            [0xE0] = "制造商保留",
            [0xF0] = "能力字符串",
        };

        public static string GetName(byte code)
        {
            if (code == Brightness) return "亮度";
            return Names.TryGetValue(code, out var name) ? name : $"VCP 0x{code:X2}";
        }

        /// <summary>界面上真正给用户调的几个，其余只在诊断页展示。</summary>
        public static readonly byte[] PrimaryVcps =
        {
            Brightness, Contrast, RedGain, GreenGain, BlueGain,
            ColorTemperature, InputSource, AudioVolume, Sharpness,
            RedBlackLevel, GreenBlackLevel, BlueBlackLevel,
        };

        // ==================== 枚举值名称 ====================

        public static string ColorTemperatureName(int value) => value switch
        {
            0x00 => "未知 / 当前",
            0x01 => "4000 K",
            0x02 => "5000 K",
            0x03 => "6500 K",
            0x04 => "7500 K",
            0x05 => "8200 K",
            0x06 => "9300 K",
            0x07 => "10000 K",
            0x08 => "11500 K",
            0x09 => "Native（原生）",
            0x0A => "用户 1",
            0x0B => "用户 2",
            0x0C => "用户 3",
            _ => $"预设 0x{value:X2}",
        };

        public static string InputSourceName(int value) => value switch
        {
            0x01 => "模拟视频 (VGA)",
            0x02 => "复合视频 1",
            0x03 => "DVI",
            0x04 => "复合视频 2",
            0x05 => "分量视频 1",
            0x06 => "分量视频 2",
            0x07 => "S-Video 1",
            0x08 => "S-Video 2",
            0x09 => "调谐器 1",
            0x0A => "调谐器 2",
            0x0B => "HDMI-1",
            0x0C => "数字视频 1",
            0x0D => "数字视频 2",
            0x0E => "分量视频 3",
            0x0F => "DisplayPort-1",
            0x10 => "DisplayPort-2",
            0x11 => "DisplayPort",
            0x12 => "HDMI-2",
            0x13 => "USB-C",
            0xF0 => "内置",
            0xF1 => "网络",
            0xF2 => "无线",
            _ => $"输入 0x{value:X2}",
        };

        // ==================== capability string 解析 ====================

        /// <summary>
        /// 解析 0xF0 能力字符串里的 vcp(...) 段。
        /// 返回 code -> 允许值列表（连续型 VCP 的列表为空）。
        /// 例：vcp(10 12 14(01 02 03) 60(01 03 0B 11))
        /// </summary>
        public static Dictionary<byte, List<int>> ParseVcpSection(string? capabilityString)
        {
            var map = new Dictionary<byte, List<int>>();
            if (string.IsNullOrEmpty(capabilityString)) return map;

            string? body = ExtractSection(capabilityString!, "vcp");
            if (body == null) return map;

            int i = 0;
            while (i < body.Length)
            {
                while (i < body.Length && char.IsWhiteSpace(body[i])) i++;
                if (i >= body.Length) break;

                int start = i;
                while (i < body.Length && !char.IsWhiteSpace(body[i]) && body[i] != '(' && body[i] != ')') i++;
                string token = body.Substring(start, i - start);

                if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte code))
                    continue;

                var values = new List<int>();
                if (i < body.Length && body[i] == '(')
                {
                    i++;
                    int valueStart = i;
                    int depth = 1;
                    while (i < body.Length && depth > 0)
                    {
                        if (body[i] == '(') depth++;
                        else if (body[i] == ')') { depth--; if (depth == 0) break; }
                        i++;
                    }
                    string inner = body.Substring(valueStart, i - valueStart);
                    if (i < body.Length) i++;

                    foreach (string v in inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed))
                            values.Add(parsed);
                }

                map[code] = values;
            }
            return map;
        }

        /// <summary>能力字符串里的 model("...") 段，显示器自报的型号名。</summary>
        public static string? ParseModelName(string? capabilityString)
        {
            if (string.IsNullOrEmpty(capabilityString)) return null;
            string body = capabilityString!;
            int idx = body.IndexOf("model", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int open = body.IndexOf('(', idx);
            if (open < 0) return null;
            int close = body.IndexOf(')', open);
            if (close < 0) return null;

            string raw = body.Substring(open + 1, close - open - 1).Trim();
            return raw.Length == 0 ? null : raw;
        }

        private static string? ExtractSection(string text, string name)
        {
            int idx = text.IndexOf(name + "(", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                // 允许中间有空格：vcp (
                int alt = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
                if (alt < 0) return null;
                int open = text.IndexOf('(', alt);
                if (open < 0) return null;
                idx = alt;
            }

            int start = text.IndexOf('(', idx);
            if (start < 0) return null;

            int depth = 1;
            int i = start + 1;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') depth--;
                i++;
            }
            int end = depth == 0 ? i - 1 : text.Length;
            return text.Substring(start + 1, Math.Max(0, end - start - 1));
        }
    }

    /// <summary>单个 VCP 的探测结果。Supported=false 时 Current/Min/Max 必须为 null，不得伪造。</summary>
    public sealed class VcpFeature
    {
        public byte Code { get; }
        public string Name { get; }
        public bool Supported { get; set; }
        public int? Current { get; set; }
        public int? Maximum { get; set; }
        public string? Error { get; set; }
        public List<int> AllowedValues { get; } = new();

        public VcpFeature(byte code, string? name = null)
        {
            Code = code;
            Name = name ?? VcpCodes.GetName(code);
        }

        public string CodeHex => $"0x{Code:X2}";

        public string RangeText
            => Supported
                ? (Maximum.HasValue ? $"{Current} / 0–{Maximum}" : $"{Current}")
                : "不支持";

        public override string ToString() => $"{CodeHex} {Name}: {RangeText}";
    }
}
