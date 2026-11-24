using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DdcMonitorTool.Core
{
    /// <summary>某块显示器在某个时刻的 VCP 值快照。</summary>
    public sealed class MonitorSnapshot
    {
        /// <summary>快照格式版本。新增可调项后递增，老快照会自动补齐缺失项。</summary>
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;
        public string Key { get; set; } = string.Empty;        // EDID 稳定标识
        public string Label { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public DateTime TakenAt { get; set; }
        /// <summary>VCP 代码（十六进制字符串，如 "10"）→ 当时的值。</summary>
        public Dictionary<string, int> Values { get; set; } = new();

        public static MonitorSnapshot From(MonitorInfo monitor)
        {
            var snap = new MonitorSnapshot
            {
                Version = CurrentVersion,
                Key = monitor.SnapshotKey,
                Label = monitor.DisplayLabel,
                DeviceName = monitor.DeviceName,
                TakenAt = DateTime.Now,
            };

            foreach (var feature in monitor.VcpFeatures.Values)
            {
                if (!feature.Supported || !feature.Current.HasValue) continue;
                if (!VcpCodes.PrimaryVcps.Contains(feature.Code)) continue;
                // 输入源不进快照：还原时切换接口会导致黑屏，风险大于收益
                if (feature.Code == VcpCodes.InputSource) continue;
                snap.Values[feature.Code.ToString("X2")] = feature.Current.Value;
            }
            return snap;
        }

        public Dictionary<byte, int> ToCodes()
        {
            var map = new Dictionary<byte, int>();
            foreach (var kv in Values)
            {
                if (byte.TryParse(kv.Key, System.Globalization.NumberStyles.HexNumber, null, out byte code))
                    map[code] = kv.Value;
            }
            return map;
        }

        public string Describe()
            => $"{Label} · {TakenAt:yyyy-MM-dd HH:mm} · {Values.Count} 项";
    }

    /// <summary>
    /// 快照仓库。旧版只有"恢复出厂设置"这一个不可逆的危险按钮，
    /// 却没有"改之前存一份"的能力 —— 一旦调乱了只能靠 OSD 手点回去。
    /// </summary>
    public static class SnapshotStore
    {
        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string FilePath { get; } = Path.Combine(AppLog.Directory, "snapshots.json");

        public static List<MonitorSnapshot> Load()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new List<MonitorSnapshot>();
                    string json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<List<MonitorSnapshot>>(json) ?? new List<MonitorSnapshot>();
                }
                catch (Exception ex)
                {
                    AppLog.Exception("读取快照", ex);
                    return new List<MonitorSnapshot>();
                }
            }
        }

        public static MonitorSnapshot? Get(string key)
            => Load().FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

        public static bool Save(MonitorSnapshot snapshot, bool overwrite = true)
        {
            lock (Gate)
            {
                try
                {
                    var all = Load();
                    int index = all.FindIndex(s => string.Equals(s.Key, snapshot.Key, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        if (!overwrite) return false;
                        all[index] = snapshot;
                    }
                    else
                    {
                        all.Add(snapshot);
                    }

                    Directory.CreateDirectory(AppLog.Directory);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOptions));
                    AppLog.Info($"已保存快照：{snapshot.Describe()}");
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Exception("保存快照", ex);
                    return false;
                }
            }
        }
    }
}
