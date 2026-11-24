using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DdcMonitorTool.Core;
using DdcMonitorTool.UI;

namespace DdcMonitorTool
{
    /// <summary>
    /// 无界面模式。给"验证 / 排障 / 脚本调用"用：
    ///   DdcMonitorTool.exe --list        列出显示器与 VCP 能力
    ///   DdcMonitorTool.exe --diag        只看显示拓扑（EDID / 接口 / HDR / 刷新率）
    ///   DdcMonitorTool.exe --selftest    自检，成功返回 0
    ///   DdcMonitorTool.exe --set 10 60   把亮度(0x10)设为 60
    /// </summary>
    internal static class Cli
    {
        public static int Run(string[] args)
        {
            TrySetUtf8();

            if (Flag(args, "--list", "-l", "list")) return List();
            if (Flag(args, "--diag", "diag")) return Diag();
            if (Flag(args, "--selftest", "selftest")) return SelfTest();
            if (Flag(args, "--set", "set")) return Set(args);
            if (Flag(args, "--presets")) return Presets();
            if (Flag(args, "--apply")) return ApplyPreset(args);
            if (Flag(args, "--reset")) return Reset(args);
            if (Flag(args, "--foreground")) return Foreground();

            Usage();
            return 2;
        }

        private static void Usage()
        {
            Console.WriteLine("DDC 显示器工具");
            Console.WriteLine("  DdcMonitorTool.exe                 启动图形界面");
            Console.WriteLine("  DdcMonitorTool.exe --list          列出显示器与 VCP 能力");
            Console.WriteLine("  DdcMonitorTool.exe --diag          查看显示拓扑（EDID / 接口 / HDR）");
            Console.WriteLine("  DdcMonitorTool.exe --selftest      自检");
            Console.WriteLine("  DdcMonitorTool.exe --set 10 60     设置亮度(0x10)=60");
            Console.WriteLine("  DdcMonitorTool.exe --presets       列出游戏调色预设");
            Console.WriteLine("  DdcMonitorTool.exe --apply 暗部增强  应用某个预设");
            Console.WriteLine("  DdcMonitorTool.exe --reset         还原 Gamma + DDC 快照");
            Console.WriteLine("  DdcMonitorTool.exe --foreground    打印当前前台进程名（配绑定用）");
            Console.WriteLine("       可选 --monitor N               指定第 N 台（默认主显示器）");
        }

        // ==================== list ====================

        private static int List()
        {
            var monitors = MonitorDetector.DetectAll();
            if (monitors.Count == 0)
            {
                Console.WriteLine("未检测到任何显示器。");
                return 1;
            }

            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                Console.WriteLine();
                Console.WriteLine($"[{i + 1}] {m.DisplayLabel}{(m.IsPrimary ? "（主显示器）" : string.Empty)}   {m.DeviceName}");
                Console.WriteLine($"    分辨率   {m.ResolutionLabel} @ {m.RefreshLabel}");
                Console.WriteLine($"    接口     {m.ConnectionLabel}");
                Console.WriteLine($"    EDID     {m.ManufacturerLine}");
                Console.WriteLine($"    HDR      {m.HdrLabel}");
                Console.WriteLine($"    DDC/CI   {(m.DdcSupported ? "可用" : "不可用")}  caps=0x{m.McCapsRaw:X4}");
                if (!string.IsNullOrEmpty(m.CapabilityModel))
                    Console.WriteLine($"    自报型号 {m.CapabilityModel}");
                if (!string.IsNullOrEmpty(m.DdcError))
                    Console.WriteLine($"    错误     {m.DdcError}");

                foreach (var f in m.VcpFeatures.Values.Where(f => f.Supported).OrderBy(f => f.Code))
                {
                    string allowed = f.AllowedValues.Count > 0
                        ? "  可选 " + string.Join(" ", f.AllowedValues.Select(v => v.ToString("X2")))
                        : string.Empty;
                    Console.WriteLine($"      {f.CodeHex}  {f.Name,-12} = {f.Current}  (0–{f.Maximum}){allowed}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"共 {monitors.Count} 台。");
            return 0;
        }

        // ==================== diag ====================

        private static int Diag()
        {
            var topology = DisplayTopology.Query();
            Console.WriteLine($"活动输出：{topology.Count} 个");

            foreach (var t in topology.Values)
            {
                Console.WriteLine();
                Console.WriteLine($"  {t.GdiName}  {t.FriendlyName}");
                Console.WriteLine($"    接口     {DisplayTopology.OutputTechnologyName(t.OutputTechnology)}");
                Console.WriteLine($"    刷新率   {t.RefreshHz} Hz");
                Console.WriteLine($"    色深     {t.BitsPerColor} bit");
                Console.WriteLine($"    HDR      {(t.HdrSupported ? (t.HdrEnabled ? "已开启" : "支持但已关闭") : "不支持")}");
                Console.WriteLine($"    设备路径 {t.MonitorDevicePath}");

                var edid = EdidReader.FromDevicePath(t.MonitorDevicePath);
                Console.WriteLine($"    EDID     {(edid == null ? "未读到" : edid.Summary)}");

                var ramp = GammaRamp.Read(t.GdiName);
                Console.WriteLine($"    Gamma    {(ramp == null ? "不可读" : GammaRamp.IsIdentity(ramp) ? "线性（未被修改）" : "已被修改")}");
            }
            return 0;
        }

        // ==================== set ====================

        private static int Set(string[] args)
        {
            var positional = args.SkipWhile(a => !Is(a, "--set", "set")).Skip(1)
                .Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

            if (positional.Count < 2)
            {
                Console.WriteLine("用法：--set <VCP 十六进制码> <值>  例如 --set 10 60");
                return 2;
            }

            if (!byte.TryParse(positional[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte code))
            {
                Console.WriteLine($"无法解析 VCP 代码：{positional[0]}");
                return 2;
            }

            if (!int.TryParse(positional[1], out int value))
            {
                Console.WriteLine($"无法解析数值：{positional[1]}");
                return 2;
            }

            var monitors = MonitorDetector.DetectAll();
            if (monitors.Count == 0)
            {
                Console.WriteLine("未检测到显示器。");
                return 1;
            }

            var monitor = Pick(args, monitors);
            using var controller = new MonitorController(monitor);
            controller.Open();
            var result = controller.SetAndVerify(code, value);
            Console.WriteLine(result.Describe());
            return result.Success ? 0 : 1;
        }

        private static int Foreground()
        {
            string exe = ForegroundWatcher.CurrentExe();
            Console.WriteLine(string.IsNullOrEmpty(exe) ? "（无法解析前台进程）" : exe);
            if (!string.IsNullOrEmpty(exe))
                Console.WriteLine($"  识别为：{GameCatalog.DisplayName(exe)}");
            return 0;
        }

        // ==================== 游戏调色 ====================

        private static int Presets()
        {
            foreach (var p in GameConfigStore.AllProfiles())
            {
                Console.WriteLine();
                Console.WriteLine($"[{(p.BuiltIn ? "内置" : "自定义")}] {p.Name}");
                if (!string.IsNullOrEmpty(p.Description)) Console.WriteLine($"        {p.Description}");
                Console.WriteLine($"        DDC  亮度={Show(p.Brightness)} 对比度={Show(p.Contrast)} 暗部={Show(p.BlackLevel)} "
                                + $"锐度={Show(p.Sharpness)} 色温={Show(p.ColorTemperature)}   （除色温外均为 0–100%）");
                Console.WriteLine($"        GPU  Gamma={p.Gamma:0.00} 暗部提升={p.BlackLift} 色彩增强={p.ColorBoost}");
            }

            var config = GameConfigStore.Load();
            if (config.Bindings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("游戏绑定：");
                foreach (var kv in config.Bindings)
                    Console.WriteLine($"  {GameCatalog.DisplayName(kv.Key)} ({kv.Key}) → {kv.Value}");
            }
            return 0;

            static string Show(int? value) => value is int v ? $"{v}%" : "不动";
        }

        private static int ApplyPreset(string[] args)
        {
            string? name = args.SkipWhile(a => !Is(a, "--apply")).Skip(1)
                .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("用法：--apply <预设名>，可用 --presets 查看");
                return 2;
            }

            var profile = GameConfigStore.Find(name);
            if (profile == null)
            {
                Console.WriteLine($"找不到预设「{name}」。可用预设："
                                  + string.Join("、", GameConfigStore.AllProfiles().Select(p => p.Name)));
                return 2;
            }

            var monitors = MonitorDetector.DetectAll();
            if (monitors.Count == 0)
            {
                Console.WriteLine("未检测到显示器。");
                return 1;
            }

            var monitor = Pick(args, monitors);
            using var controller = new MonitorController(monitor);
            controller.Open();

            var report = GameApplier.Apply(controller, monitor, profile);
            foreach (var result in report.Ddc) Console.WriteLine("  " + result.Describe());
            foreach (string note in report.Notes) Console.WriteLine("  ! " + note);
            Console.WriteLine($"  => {report.Summary()}");
            return report.AllOk ? 0 : 1;
        }

        private static int Reset(string[] args)
        {
            var monitors = MonitorDetector.DetectAll();
            if (monitors.Count == 0) return 1;
            var monitor = Pick(args, monitors);

            if (GammaState.HasBaseline(monitor.DeviceName))
            {
                GammaState.Restore(monitor.DeviceName);
                Console.WriteLine("  Gamma 已还原");
            }
            else
            {
                Console.WriteLine("  Gamma 没有基线，跳过");
            }

            ColorMatrix.Reset();
            ColorMatrix.Shutdown();
            Console.WriteLine("  色彩矩阵已重置");

            var snapshot = SnapshotStore.Get(monitor.SnapshotKey);
            if (snapshot == null)
            {
                Console.WriteLine("  没有 DDC 快照，跳过");
                return 0;
            }

            using var controller = new MonitorController(monitor);
            controller.Open();
            var results = controller.Restore(snapshot.ToCodes());
            Console.WriteLine($"  DDC 快照还原 {results.Count(r => r.Success)}/{results.Count} 项");
            return 0;
        }

        // ==================== selftest ====================

        private static int SelfTest()
        {
            var failures = new List<string>();

            Console.WriteLine("[1/5] 日志与数据目录");
            Console.WriteLine($"      日志 {AppLog.FilePath}");
            Console.WriteLine($"      快照 {SnapshotStore.FilePath}");

            Console.WriteLine("[2/5] 显示拓扑");
            var topology = DisplayTopology.Query();
            Console.WriteLine($"      活动输出 {topology.Count} 个");
            if (topology.Count == 0) failures.Add("显示拓扑为空");

            Console.WriteLine("[3/5] 显示器检测");
            var monitors = MonitorDetector.DetectAll();
            Console.WriteLine($"      检测到 {monitors.Count} 台");
            foreach (var m in monitors)
            {
                int supported = m.VcpFeatures.Values.Count(f => f.Supported);
                Console.WriteLine($"      · {m.DisplayLabel} | {m.ResolutionLabel}@{m.RefreshLabel} | DDC={m.DdcSupported} | {supported} 项 VCP | EDID={m.ManufacturerLine}");
                if (m.Width is null or 0) failures.Add($"{m.DeviceName} 分辨率读取失败");
            }
            if (monitors.Count == 0) failures.Add("未检测到显示器");

            Console.WriteLine("[4/5] DDC 写入回路（写入当前值，不改变画面）");
            var target = monitors.FirstOrDefault(m => m.DdcSupported);
            if (target == null)
            {
                Console.WriteLine("      跳过：没有可用的 DDC 通道");
            }
            else
            {
                try
                {
                    using var controller = new MonitorController(target);
                    controller.Open();
                    Console.WriteLine($"      已连接 {target.DisplayLabel}");

                    var brightness = target.Value(VcpCodes.Brightness);
                    if (brightness is int cur)
                    {
                        var r = controller.SetAndVerify(VcpCodes.Brightness, cur);
                        Console.WriteLine($"      亮度回写 {cur}：{(r.Success ? "OK" : "失败 - " + r.Error)}");
                        if (!r.Success) failures.Add("亮度写入校验失败");
                    }
                    else
                    {
                        Console.WriteLine("      跳过：该显示器不报告亮度");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("      失败：" + ex.Message);
                    failures.Add("控制器打开失败：" + ex.Message);
                }
            }

            Console.WriteLine("[5/5] 界面构建");
            try
            {
                using var form = new MainForm();
                form.CreateControl();
                form.PrimeWith(monitors);
                Console.WriteLine($"      窗口 {form.Width}×{form.Height}，控件 {CountControls(form)} 个");
            }
            catch (Exception ex)
            {
                Console.WriteLine("      失败：" + ex.Message);
                failures.Add("界面构建失败：" + ex.Message);
            }

            Console.WriteLine("[6/6] 量程换算（模拟 0–255 对比度 / 0–5 锐度的显示器）");
            {
                var fake = new MonitorInfo { DeviceName = @"\\.\DISPLAY9" };
                fake.VcpFeatures[VcpCodes.Contrast] = new VcpFeature(VcpCodes.Contrast)
                { Supported = true, Maximum = 255, Current = 199 };
                fake.VcpFeatures[VcpCodes.Sharpness] = new VcpFeature(VcpCodes.Sharpness)
                { Supported = true, Maximum = 5, Current = 3 };

                int c = GameApplier.ToAbsolute(fake, VcpCodes.Contrast, 78);
                int s5 = GameApplier.ToAbsolute(fake, VcpCodes.Sharpness, 100);
                int back = GameApplier.ToPercent(fake, VcpCodes.Contrast, 199);

                Console.WriteLine($"      78% → {c}（期望 199）");
                Console.WriteLine($"      100% → {s5}（期望 5）");
                Console.WriteLine($"      199 → {back}%（期望 78）");

                if (c != 199) failures.Add("对比度百分比换算错误");
                if (s5 != 5) failures.Add("锐度百分比换算错误");
                if (back != 78) failures.Add("反向百分比换算错误");
            }

            Console.WriteLine();
            if (failures.Count == 0)
            {
                Console.WriteLine("RESULT=OK");
                return 0;
            }

            Console.WriteLine("RESULT=FAIL");
            foreach (string f in failures) Console.WriteLine("  - " + f);
            return 1;
        }

        private static int CountControls(System.Windows.Forms.Control root)
        {
            int count = 1;
            foreach (System.Windows.Forms.Control child in root.Controls)
                count += CountControls(child);
            return count;
        }

        // ==================== 辅助 ====================

        private static MonitorInfo Pick(string[] args, List<MonitorInfo> monitors)
        {
            int index = args.ToList().IndexOf("--monitor");
            if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out int n))
            {
                if (n >= 1 && n <= monitors.Count) return monitors[n - 1];
            }
            return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        }

        private static bool Flag(string[] args, params string[] names)
            => args.Any(a => names.Any(n => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)));

        private static bool Is(string value, params string[] names)
            => names.Any(n => string.Equals(value, n, StringComparison.OrdinalIgnoreCase));

        private static void TrySetUtf8()
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        }
    }
}
