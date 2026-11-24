using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace DdcMonitorTool.Core
{
    /// <summary>从 EDID 解析出的显示器身份。读不到就是 Unknown，绝不猜。</summary>
    public sealed class EdidRecord
    {
        public string InstanceName { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = "Unknown";
        public string Model { get; set; } = "Unknown";
        public string ProductCode { get; set; } = string.Empty;
        public string Serial { get; set; } = string.Empty;
        public int? Year { get; set; }
        public int? Week { get; set; }
        public string Source { get; set; } = string.Empty;

        /// <summary>稳定标识：换接口、改分辨率都不会变，用来索引快照。</summary>
        public string StableKey
        {
            get
            {
                if (Manufacturer != "Unknown" || !string.IsNullOrEmpty(ProductCode))
                    return $"{Manufacturer}-{ProductCode}";
                return InstanceName;
            }
        }

        public string Summary
        {
            get
            {
                var parts = new List<string>();
                if (Manufacturer != "Unknown") parts.Add(Manufacturer);
                if (Model != "Unknown") parts.Add(Model);
                if (!string.IsNullOrEmpty(Serial)) parts.Add("SN " + Serial);
                if (Year.HasValue) parts.Add($"{Year} 年{(Week.HasValue ? $" 第 {Week} 周" : "")}");
                return parts.Count > 0 ? string.Join(" · ", parts) : "未知";
            }
        }
    }

    /// <summary>
    /// EDID 读取。
    ///
    /// 主路径：DisplayConfig 给出的 monitorDevicePath
    ///   \\?\DISPLAY#DEL4098#5&2f1a1b2c&0&UID4352#{e6f07b5f-...}
    ///   -> HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\DEL4098\5&2f1a1b2c&0&UID4352\Device Parameters\EDID
    ///
    /// 这条路径是"每个显示器各自唯一"的，多屏也不会错位；旧版只在"1 台显示器 + 1 条 EDID"时才敢附加，
    /// 结果双屏用户永远看到"未知显示器"。
    /// </summary>
    public static class EdidReader
    {
        private static readonly char[] PnpChars =
            "\0ABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

        public static EdidRecord? FromDevicePath(string monitorDevicePath)
        {
            if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;

            string path = monitorDevicePath.Trim();
            if (path.StartsWith(@"\\?\")) path = path.Substring(4);

            var parts = path.Split('#');
            if (parts.Length < 3) return null;

            string enumerator = parts[0];  // DISPLAY
            string hardwareId = parts[1];  // DEL4098
            string instance = parts[2];    // 5&2f1a1b2c&0&UID4352

            string keyPath = $@"SYSTEM\CurrentControlSet\Enum\{enumerator}\{hardwareId}\{instance}\Device Parameters";
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key?.GetValue("EDID") is byte[] bytes)
                    return Parse(bytes, instance, "registry");
            }
            catch (Exception ex)
            {
                AppLog.Exception($"读取 EDID 失败（{instance}）", ex);
            }
            return null;
        }

        /// <summary>没有拓扑信息时的兜底：把注册表里所有 EDID 都读出来。</summary>
        public static List<EdidRecord> ReadAllFromRegistry()
        {
            var records = new List<EdidRecord>();
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
                if (root == null) return records;

                foreach (string hardwareId in root.GetSubKeyNames())
                {
                    using var hwKey = root.OpenSubKey(hardwareId);
                    if (hwKey == null) continue;

                    foreach (string instance in hwKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var p = hwKey.OpenSubKey(instance + @"\Device Parameters");
                            if (p?.GetValue("EDID") is not byte[] bytes) continue;
                            var rec = Parse(bytes, instance, "registry");
                            if (rec != null) records.Add(rec);
                        }
                        catch
                        {
                            // 单个实例读失败不影响其它显示器
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Exception("枚举注册表 EDID", ex);
            }
            return records;
        }

        public static EdidRecord? Parse(byte[]? data, string instanceName, string source)
        {
            if (data == null || data.Length < 18) return null;

            // EDID 头：00 FF FF FF FF FF FF 00
            if (data[0] != 0x00 || data[1] != 0xFF || data[2] != 0xFF || data[3] != 0xFF ||
                data[4] != 0xFF || data[5] != 0xFF || data[6] != 0xFF || data[7] != 0x00)
                return null;

            var rec = new EdidRecord { InstanceName = instanceName, Source = source };

            ushort mfrRaw = (ushort)((data[8] << 8) | data[9]);
            rec.Manufacturer = DecodeManufacturer(mfrRaw);
            rec.ProductCode = ((data[10] << 8) | data[11]).ToString("X4");
            rec.Serial = ((uint)(data[12] | (data[13] << 8) | (data[14] << 16) | (data[15] << 24))).ToString("X8");

            if (data[16] >= 1 && data[16] <= 53) rec.Week = data[16];
            int year = 1990 + data[17];
            if (year is >= 1990 and <= 2100) rec.Year = year;

            // 四个 18 字节描述符，从偏移 54 开始
            for (int off = 54; off + 18 <= data.Length && off + 18 <= 126; off += 18)
            {
                if (data[off] != 0x00 || data[off + 1] != 0x00) continue;

                byte tag = data[off + 3];
                if (tag == 0xFC || tag == 0xFF)
                {
                    var sb = new StringBuilder();
                    for (int i = 5; i < 18; i++)
                    {
                        byte b = data[off + i];
                        if (b == 0x0A || b == 0x00) break;
                        sb.Append((char)b);
                    }
                    string text = sb.ToString().Trim();
                    if (text.Length == 0) continue;

                    if (tag == 0xFC) rec.Model = text;
                    else if (rec.Serial.All(c => c == '0')) rec.Serial = text;
                }
            }

            return rec;
        }

        private static string DecodeManufacturer(ushort raw)
        {
            int c1 = (raw >> 10) & 0x1F;
            int c2 = (raw >> 5) & 0x1F;
            int c3 = raw & 0x1F;
            if (c1 <= 0 || c1 >= PnpChars.Length || c2 <= 0 || c2 >= PnpChars.Length ||
                c3 <= 0 || c3 >= PnpChars.Length)
                return "Unknown";
            return $"{PnpChars[c1]}{PnpChars[c2]}{PnpChars[c3]}";
        }
    }
}
