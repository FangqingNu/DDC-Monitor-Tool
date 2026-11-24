using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DdcMonitorTool.Core
{
    /// <summary>
    /// 一套游戏调色参数。
    ///
    /// DDC 项的亮度/对比度/暗部/锐度都存 **0–100 百分比**，应用时按目标显示器的真实量程换算
    /// （有的屏对比度是 0–255、锐度是 0–5）。这样同一套预设在任何屏上都成立。
    /// 色温是 MCCS 枚举值，按原值存。
    /// int? 表示"这一项不动"，不会覆盖用户其它设置。
    /// GPU 项（Gamma / 暗部提升 / 色彩矩阵）始终参与，1.0 / 0 / 50 即中性。
    /// </summary>
    public sealed class GameProfile
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool BuiltIn { get; set; }

        // ---- DDC / 硬件 ----
        public int? Brightness { get; set; }
        public int? Contrast { get; set; }
        /// <summary>同时写入 0x6C / 0x6E / 0x70，值越大暗部越亮。</summary>
        public int? BlackLevel { get; set; }
        public int? Sharpness { get; set; }
        public int? ColorTemperature { get; set; }

        // ---- GPU / 软件 ----
        public double Gamma { get; set; } = 1.0;
        public int BlackLift { get; set; }
        public int ColorBoost { get; set; } = 50;

        public bool UsesGpu => Math.Abs(Gamma - 1.0) > 0.001 || BlackLift > 0 || ColorBoost != 50;

        public GameProfile Clone() => new()
        {
            Name = Name,
            Description = Description,
            BuiltIn = false,
            Brightness = Brightness,
            Contrast = Contrast,
            BlackLevel = BlackLevel,
            Sharpness = Sharpness,
            ColorTemperature = ColorTemperature,
            Gamma = Gamma,
            BlackLift = BlackLift,
            ColorBoost = ColorBoost,
        };
    }

    /// <summary>常见 FPS / 竞技游戏的进程名。用户也可以手动添加任意 exe。</summary>
    public static class GameCatalog
    {
        public static readonly (string Exe, string Name)[] Known =
        {
            ("cs2.exe", "CS2"),
            ("csgo.exe", "CS:GO"),
            ("valorant.exe", "无畏契约"),
            ("valorant-win64-shipping.exe", "无畏契约"),
            ("r5apex.exe", "Apex 英雄"),
            ("r5apex_dx12.exe", "Apex 英雄 (DX12)"),
            ("rainbowsix.exe", "彩虹六号"),
            ("rainbowsix_dx11.exe", "彩虹六号 (DX11)"),
            ("tslgame.exe", "PUBG"),
            ("overwatch.exe", "守望先锋"),
            ("cod.exe", "使命召唤"),
            ("modernwarfare.exe", "现代战争"),
            ("bf2042.exe", "战地 2042"),
            ("bfv.exe", "战地 V"),
            ("deltaforce.exe", "三角洲行动"),
            ("deltaforceclient-win64-shipping.exe", "三角洲行动"),
            ("arena_breakout.exe", "三角洲：竞技场"),
            ("escapefromtarkov.exe", "逃离塔科夫"),
            ("aimlab.exe", "Aim Lab"),
            ("kovaak's shipping.exe", "KovaaK's"),
            ("gta5.exe", "GTA V"),
            ("hll-win64-shipping.exe", "人间地狱"),
        };

        private static readonly Dictionary<string, string> Map =
            Known.GroupBy(k => k.Exe, StringComparer.OrdinalIgnoreCase)
                 .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        public static bool IsKnown(string exe) => Map.ContainsKey(exe);

        public static string DisplayName(string exe)
            => Map.TryGetValue(exe, out var name) ? name : exe;
    }

    /// <summary>用户配置：自定义预设 + 游戏绑定 + 自动切换开关。</summary>
    public sealed class GameConfig
    {
        public List<GameProfile> Profiles { get; set; } = new();
        public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AutoSwitch { get; set; } = true;
    }

    public static class GameConfigStore
    {
        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public static string FilePath { get; } = Path.Combine(AppLog.Directory, "game.json");

        /// <summary>内置预设。数值都落在本机 VA2756 的真实量程内（0–100 / 0–11 色温）。</summary>
        public static List<GameProfile> BuiltIns() => new()
        {
            // 注：内置预设不碰锐度 —— 各厂锐度的 0 和 100 含义差别太大，
            // 在未知面板上拉满容易出白边，交给用户自己调更稳。
            new GameProfile
            {
                Name = "竞技",
                Description = "高对比 + 轻提暗部 + 略增饱和，画面锐利但不发灰",
                BuiltIn = true,
                Contrast = 78,
                BlackLevel = 58,
                Gamma = 1.05,
                BlackLift = 10,
                ColorBoost = 60,
            },
            new GameProfile
            {
                Name = "暗部增强",
                Description = "大幅抬暗部，阴影里的人更早出现；代价是黑位略发灰",
                BuiltIn = true,
                Contrast = 72,
                BlackLevel = 70,
                Gamma = 1.10,
                BlackLift = 22,
                ColorBoost = 55,
            },
            new GameProfile
            {
                Name = "影院",
                Description = "降低锐度、提高饱和度，偏沉浸而不是竞技",
                BuiltIn = true,
                Contrast = 70,
                BlackLevel = 50,
                ColorTemperature = 3,   // 6500K
                Gamma = 1.0,
                BlackLift = 0,
                ColorBoost = 62,
            },
            new GameProfile
            {
                Name = "夜间护眼",
                Description = "压低亮度、低饱和；显示器支持暖色温时一并切过去",
                BuiltIn = true,
                Brightness = 45,
                Contrast = 60,
                ColorTemperature = 1,   // 4000K
                Gamma = 1.0,
                BlackLift = 0,
                ColorBoost = 45,
            },
        };

        /// <summary>内置预设 + 用户预设，同名时用户预设优先。</summary>
        public static List<GameProfile> AllProfiles()
        {
            var config = Load();
            var list = BuiltIns();
            foreach (var p in config.Profiles)
            {
                int index = list.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) list[index] = p;
                else list.Add(p);
            }
            return list;
        }

        public static GameProfile? Find(string name)
            => AllProfiles().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public static GameConfig Load()
        {
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(FilePath)) return new GameConfig();
                    var config = JsonSerializer.Deserialize<GameConfig>(File.ReadAllText(FilePath));
                    if (config == null) return new GameConfig();
                    config.Bindings = new Dictionary<string, string>(config.Bindings, StringComparer.OrdinalIgnoreCase);
                    return config;
                }
                catch (Exception ex)
                {
                    AppLog.Exception("读取游戏配置", ex);
                    return new GameConfig();
                }
            }
        }

        public static void Save(GameConfig config)
        {
            lock (Gate)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(AppLog.Directory);
                    // 内置预设不落盘，避免以后改内置值被旧文件盖住
                    var copy = new GameConfig
                    {
                        Profiles = config.Profiles.Where(p => !p.BuiltIn).ToList(),
                        Bindings = new Dictionary<string, string>(config.Bindings, StringComparer.OrdinalIgnoreCase),
                        AutoSwitch = config.AutoSwitch,
                    };
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(copy, JsonOptions));
                }
                catch (Exception ex)
                {
                    AppLog.Exception("保存游戏配置", ex);
                }
            }
        }

        public static void SaveProfile(GameProfile profile)
        {
            var config = Load();
            config.Profiles.RemoveAll(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            config.Profiles.Add(profile);
            Save(config);
            AppLog.Info($"已保存游戏预设：{profile.Name}");
        }

        public static void DeleteProfile(string name)
        {
            var config = Load();
            config.Profiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            Save(config);
        }

        public static void SetBinding(string exe, string profileName)
        {
            var config = Load();
            config.Bindings[exe] = profileName;
            Save(config);
            AppLog.Info($"已绑定 {exe} → {profileName}");
        }

        public static void RemoveBinding(string exe)
        {
            var config = Load();
            if (config.Bindings.Remove(exe)) Save(config);
        }
    }

    /// <summary>一次应用的结果汇总。</summary>
    public sealed class GameApplyReport
    {
        public List<ChangeResult> Ddc { get; } = new();
        public bool GammaApplied { get; set; }
        public bool MatrixApplied { get; set; }
        public List<string> Notes { get; } = new();

        public bool AllOk => Ddc.All(r => r.Success) && !Notes.Any(n => n.Contains("失败"));

        public string Summary()
        {
            int ok = Ddc.Count(r => r.Success);
            var parts = new List<string>();
            if (Ddc.Count > 0) parts.Add($"DDC {ok}/{Ddc.Count}");
            if (GammaApplied) parts.Add("Gamma");
            if (MatrixApplied) parts.Add("色彩矩阵");
            return parts.Count == 0 ? "无改动" : string.Join(" · ", parts);
        }
    }

    /// <summary>把一套预设落到硬件与 GPU 上。DDC 部分复用调用方持有的控制器。</summary>
    public static class GameApplier
    {
        /// <summary>该项在目标显示器上的最大值；读不到时按 0–100 处理。</summary>
        public static int RangeMax(MonitorInfo? monitor, byte code)
        {
            if (monitor != null && monitor.VcpFeatures.TryGetValue(code, out var feature)
                && feature.Supported && feature.Maximum is int max && max > 0)
                return max;
            return 100;
        }

        /// <summary>百分比 → 显示器真实值。</summary>
        public static int ToAbsolute(MonitorInfo? monitor, byte code, int percent)
        {
            int max = RangeMax(monitor, code);
            return (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * max);
        }

        /// <summary>显示器真实值 → 百分比。</summary>
        public static int ToPercent(MonitorInfo? monitor, byte code, int absolute)
        {
            int max = RangeMax(monitor, code);
            if (max <= 0) return absolute;
            return (int)Math.Round(Math.Clamp(absolute, 0, max) * 100.0 / max);
        }

        /// <summary>
        /// 把预设里写的色温映射到这块屏真实支持的档位。
        /// 只接受相差 1 档以内的（MCCS 枚举大致按色温递增），差太多宁可不改。
        /// </summary>
        public static int? ResolveColorTemperature(MonitorInfo monitor, int requested, out string? note)
        {
            note = null;
            if (!monitor.VcpFeatures.TryGetValue(VcpCodes.ColorTemperature, out var feature)
                || feature.AllowedValues.Count == 0)
                return requested;   // 没能力表，按原值写

            if (feature.AllowedValues.Contains(requested)) return requested;

            int best = feature.AllowedValues.OrderBy(v => Math.Abs(v - requested)).First();
            if (Math.Abs(best - requested) <= 1)
            {
                note = $"色温 {VcpCodes.ColorTemperatureName(requested)} 不可用，已改用 {VcpCodes.ColorTemperatureName(best)}";
                return best;
            }

            string available = string.Join(" / ", feature.AllowedValues.Select(VcpCodes.ColorTemperatureName));
            note = $"色温 {VcpCodes.ColorTemperatureName(requested)} 不可用（该屏只支持 {available}），已跳过";
            return null;
        }

        public static GameApplyReport Apply(MonitorController controller, MonitorInfo monitor, GameProfile profile)
        {
            var report = new GameApplyReport();

            void WritePercent(byte code, int? percent)
            {
                if (percent is not int p) return;
                if (!monitor.Supports(code))
                {
                    report.Notes.Add($"{VcpCodes.GetName(code)}：显示器不支持，已跳过");
                    return;
                }
                report.Ddc.Add(controller.SetAndVerify(code, ToAbsolute(monitor, code, p)));
            }

            void WriteValue(byte code, int? value)
            {
                if (value is not int v) return;
                if (!monitor.Supports(code))
                {
                    report.Notes.Add($"{VcpCodes.GetName(code)}：显示器不支持，已跳过");
                    return;
                }

                // 枚举型 VCP（如色温）：显示器没列这个档位就别写，免得它自己乱切
                if (monitor.VcpFeatures.TryGetValue(code, out var feature)
                    && feature.AllowedValues.Count > 0
                    && !feature.AllowedValues.Contains(v))
                {
                    report.Notes.Add($"{VcpCodes.GetName(code)}：该显示器没有这个档位，已跳过");
                    return;
                }

                report.Ddc.Add(controller.SetAndVerify(code, v));
            }

            WritePercent(VcpCodes.Brightness, profile.Brightness);
            WritePercent(VcpCodes.Contrast, profile.Contrast);
            WritePercent(VcpCodes.Sharpness, profile.Sharpness);

            // 色温档位各厂不同（本机只有 7500/8200/9300/11500/用户1），
            // 差一档以内就近取，差太多就明确跳过，不硬写
            if (profile.ColorTemperature is int wantedTemp)
            {
                int? resolved = ResolveColorTemperature(monitor, wantedTemp, out string? tempNote);
                if (tempNote != null) report.Notes.Add(tempNote);
                if (resolved.HasValue) WriteValue(VcpCodes.ColorTemperature, resolved.Value);
            }

            if (profile.BlackLevel is int black)
            {
                foreach (byte code in new[] { VcpCodes.RedBlackLevel, VcpCodes.GreenBlackLevel, VcpCodes.BlueBlackLevel })
                {
                    if (!monitor.Supports(code)) continue;
                    report.Ddc.Add(controller.SetAndVerify(code, ToAbsolute(monitor, code, black)));
                }
            }

            // ---- GPU 层 ----
            bool wantsGamma = Math.Abs(profile.Gamma - 1.0) > 0.001 || profile.BlackLift > 0;
            if (wantsGamma)
            {
                if (monitor.HdrEnabled)
                {
                    report.Notes.Add("HDR 已开启，Gamma / 暗部提升不会生效（Windows 会忽略 Gamma Ramp）");
                }
                else
                {
                    var current = GammaRamp.Read(monitor.DeviceName);
                    if (current != null)
                        GammaState.SaveBaselineIfMissing(monitor.DeviceName, current);

                    bool ok = GammaRamp.Write(monitor.DeviceName, GammaRamp.Build(profile.Gamma, profile.BlackLift));
                    report.GammaApplied = ok;
                    if (ok) GammaState.MarkActive(monitor.DeviceName, true);
                    else report.Notes.Add("Gamma 写入被驱动拒绝");
                }
            }
            else if (GammaState.HasBaseline(monitor.DeviceName))
            {
                GammaState.Restore(monitor.DeviceName);
            }

            if (profile.ColorBoost != 50)
            {
                report.MatrixApplied = ColorMatrix.Apply(profile.ColorBoost);
                if (!report.MatrixApplied)
                    report.Notes.Add("色彩矩阵不可用（仅窗口化 / 无边框下有效）");
            }
            else
            {
                ColorMatrix.Reset();
            }

            AppLog.Info($"应用游戏预设「{profile.Name}」：{report.Summary()}");
            return report;
        }
    }
}
