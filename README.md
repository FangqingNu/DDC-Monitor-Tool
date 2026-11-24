# DDC 显示器工具 · DDC Monitor Tool

**把你的平价显示器，也调得跟大牌一样。**
**Make your budget monitor adjustable like a premium one.**

[![build](https://github.com/FangqingNu/DDC-Monitor-Tool/actions/workflows/build.yml/badge.svg)](https://github.com/FangqingNu/DDC-Monitor-Tool/actions/workflows/build.yml)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)
![dotnet](https://img.shields.io/badge/.NET-6.0-512BD4)
![license](https://img.shields.io/badge/license-MIT-green)

[中文](#中文) · [English](#english)

---

## 中文

通过 **DDC/CI（VESA MCCS）** 直接控制外接显示器的亮度、对比度、RGB 增益、色温、黑电平与输入源。
不依赖厂商软件，也不需要 NVIDIA / AMD 控制面板——**任何支持 DDC/CI 的显示器**都能用，
包括那些连官方驱动都没有的平价屏。

内置**游戏调色**：一套参数同时落到显示器硬件和 GPU 输出，游戏一启动自动切换，退出自动还原。

![图标](assets/app_256.png)

### 功能

| 功能 | 说明 |
|---|---|
| **图像调节** | 亮度、对比度、黑电平、锐度，拖动即时生效 |
| **色彩** | 色温预设（只列显示器真实支持的档位）、RGB 三通道增益 |
| **游戏调色** | 4 套预设 + 自定义，硬件层与 GPU 层同时生效 |
| **按游戏自动切换** | 绑定 `exe → 预设`，游戏获得焦点就切，进程退出就还原 |
| **快照** | 首次连接自动记录原始值，随时一键还原 |
| **诊断** | 接口类型、EDID、HDR、色深、MC_CAPS、全部 VCP 的实时值 |
| **命令行** | `--list` / `--diag` / `--selftest` / `--set` / `--apply` / `--reset` |

### 下载

到 [Releases](https://github.com/FangqingNu/DDC-Monitor-Tool/releases) 下载，两个版本：

- `DdcMonitorTool-fx-win-x64.exe` —— 需要先装 [.NET 6 桌面运行时](https://dotnet.microsoft.com/download/dotnet/6.0)，约 400 KB
- `DdcMonitorTool-standalone-win-x64.exe` —— 自带运行时，双击即用，体积大

也可以直接用 Actions 里最新一次构建的 artifact。

### 自己编译

```powershell
git clone https://github.com/FangqingNu/DDC-Monitor-Tool.git
cd ddc-monitor-tool

dotnet publish -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o publish
```

需要 .NET 6 SDK。产物在 `publish\DdcMonitorTool.exe`。

### 界面

左侧是显示器选择与页面导航，右侧是内容区。导航顺序：图像 / 色彩 / **游戏** / 输入源 / 诊断 / 日志。

| 页面 | 内容 |
|---|---|
| **图像** | 亮度、对比度、音量/锐度（显示器声明了才出现）、快照 |
| **色彩** | 色温预设、RGB 增益 |
| **游戏** | 自动切换、预设、调色参数、游戏绑定 |
| **输入源** | 只列出显示器支持的接口，切换前二次确认 |
| **诊断** | 接口、EDID、HDR、色深、MC_CAPS、VCP 能力表、恢复出厂 |
| **日志** | 运行记录，同时写入日志文件 |

拖动滑块后 **250ms** 才真正写入，避免一次拖动给显示器 EEPROM 刷几十条命令。
数值旁的 `✕` 表示写入后读回不一致——工具不会把失败说成成功。

快捷键：`F5` 重新检测，`Ctrl+1`~`Ctrl+6` 切页。

### 游戏调色

分**两层**，因为它们能做的事不重叠：

| 层 | 手段 | 生效范围 | 能做什么 |
|---|---|---|---|
| **硬件（DDC）** | VCP `0x12` 对比度、`0x6C/6E/70` 黑电平、`0x87` 锐度、`0x10` 亮度、`0x14` 色温 | **独占全屏也生效**，显示器自己执行 | 对比、暗部、锐度、白点 |
| **软件（GPU）** | Gamma Ramp（一维 LUT）、放大镜色彩矩阵（5×5 跨通道） | Gamma 全屏有效；色彩矩阵**仅窗口化 / 无边框** | 中间调亮度、暗部提升、真正的饱和度 |

为什么饱和度不走 DDC：Gamma 是"逐通道一维 LUT"，每个通道的输出只取决于自己的输入，
数学上做不出饱和度；而且不少显示器的 `0x52` 读数不可信（开发机上恒为 0），
直接写它有把屏幕变灰的风险。所以饱和度用 GPU 色彩矩阵实现，代价是独占全屏下无效。

内置预设（数值为百分比，应用时按显示器真实量程换算）：

| 预设 | 思路 |
|---|---|
| **竞技** | 对比 78% + 暗部 58% + Gamma 1.05 + 轻提暗部，锐利但不发灰 |
| **暗部增强** | 暗部 70% + Gamma 1.10 + 暗部提升 22，阴影里的人更早出现，代价是黑位略灰 |
| **影院** | 6500K（不支持时就近取 7500K）、高饱和，偏沉浸 |
| **夜间护眼** | 亮度 45%、低饱和；显示器支持暖色温时一并切过去 |

内置预设**不碰锐度**——各厂锐度的 0 和 100 含义差别太大，在未知面板上拉满容易出白边，
留给手动滑块。滑块改完可以「保存为预设」，自定义预设落盘，重启不丢。

#### 自动切换

- 把 `exe → 预设` 绑起来，游戏一获得焦点就应用。
- **Alt-Tab 到桌面不会还原**，只有绑定的游戏进程全部退出才还原（按进程判断，不是按窗口）。
- 退出时先按进程退出前的 DDC 值还原硬件，再把 Gamma 写回基线、色彩矩阵置为单位矩阵。
- 监听用 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`，空闲 0% CPU；另有 3 秒兜底轮询。
- 进程退出后系统会残留一两秒的进程对象，所以还原由 2 秒定时器复查，确认真的没了才动手。

> 只读取前台窗口的进程名，不注入游戏、不读游戏内存、不改游戏文件。

#### 崩溃保护

Gamma 写入前会把**原始 ramp 存到磁盘**并留下标记。进程被强杀 / 蓝屏后，下次启动会检测到标记并自动还原，
不会让桌面一直带着偏色。色彩矩阵在正常退出时也会重置。

### 命令行

```
DdcMonitorTool.exe                    启动图形界面
DdcMonitorTool.exe --list             列出显示器与全部 VCP 能力
DdcMonitorTool.exe --diag             只看显示拓扑（接口 / EDID / HDR / 刷新率 / Gamma 状态）
DdcMonitorTool.exe --selftest         自检，成功返回 0
DdcMonitorTool.exe --set 10 60        亮度(0x10)设为 60
DdcMonitorTool.exe --set 12 50 --monitor 2
DdcMonitorTool.exe --presets          列出游戏调色预设
DdcMonitorTool.exe --apply 暗部增强    应用某个预设（含 Gamma 与色彩矩阵）
DdcMonitorTool.exe --reset            还原 Gamma 基线 + DDC 快照
DdcMonitorTool.exe --foreground       打印当前前台进程名（配绑定用）
```

`--selftest` 会做完整链路验证：显示拓扑 → 显示器检测 → DDC 写入回路（**写回当前值，不改变画面**）
→ 界面构建 → 量程换算。

### 适配范围

这个工具的核心假设只有一条：**显示器实现了 DDC/CI（VESA MCCS）**。
除此之外，凡是"因屏而异"的部分都从运行时探测，不写死：

| 差异 | 处理方式 |
|---|---|
| 量程不同（对比度 0–100 / 0–255，锐度 0–5 / 0–100） | 预设按 **0–100%** 存，应用时按该屏 `GetVCPFeature` 返回的 max 换算 |
| 能力清单不同 | 读 `0xF0` 能力字符串解析 `vcp(...)`，没声明的项直接不出现 |
| 不实现 `0xF0` | 回退到 `MC_CAPS` 位，再回退到逐项试探 |
| 输入源 / 色温档位不同 | 直接用能力字符串里的枚举值；色温差 1 档以内就近映射，差太多明确跳过并说明 |
| 锐度含义差异极大 | 内置预设不碰锐度，只留手动滑块 |
| 内屏只认专用亮度接口 | `GetMonitorBrightness` / `SetMonitorBrightness` 兜底 |
| 慢速 I2C（廉价面板常见） | 重试退避 80 / 160 / 320ms；单次探测总预算 4 秒 |
| 唤醒瞬间句柄为空 | 空句柄也重试，150ms 后再取 |
| 同型号双屏 EDID 完全相同 | 快照键按设备名拆分，互不覆盖 |
| 写入被 OSD 锁定 | 读后校验，值不一致显示 `✕`，不谎报成功 |

**仍然做不到的：**

- OSD 里 DDC/CI 是关闭的 —— 只能检测出来并提示你去开，软件绕不过去。
- 经转接器 / 扩展坞连接 —— 很多 USB-C 转 HDMI 不透传 DDC，会显示"不可用"。
- 读不出但能写的 VCP —— 保守按"不支持"处理，不做盲写。
- `0x52` 饱和度读数不可信的屏 —— 不暴露成滑块，改用 GPU 色彩矩阵。
- 厂商私有 VCP（BenQ 的 DyAc / 暗部平衡、LG 的 Smart Energy 等）—— 不在 MCCS 标准里，没有通用码可写。

判断某台屏能不能用，最快的方式是跑一次 `--list`：能看到多少项 VCP，就能调多少项。

### 技术要点

```
UI 线程  ──await──>  后台线程  ──P/Invoke──>  dxva2.dll  ──I2C/DDC──>  显示器
   ↑                                        ↑
   └────────── 结果回填 ────────────────────┘
```

- 所有 DDC 往返（一次约 30–80ms）都在线程池，UI 不阻塞。
- 写入串行化，两次写入至少间隔 **120ms**：廉价面板的 EEPROM 控制器扛不住突发写。
- 写入后读回校验，不一致就如实上报。

能力不是猜的，按优先级来自三个来源：能力字符串（`0xF0`）→ `MC_CAPS` 位 → 逐个试读。
读不到就显示"不支持"，绝不伪造数值。

#### 三个 P/Invoke 坑（踩过，写在这里省得下次再踩）

1. **`fixed char[]` 不能用于 P/Invoke 结构体**。
   `DISPLAYCONFIG_SOURCE_DEVICE_NAME` 用 `fixed char viewGdiDeviceName[32]` 声明时，
   编组器只回写 1 个字符，设备名变成 `"\"`。必须用
   `[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] string`。

2. **`[Out] PHYSICAL_MONITOR[]` 会吞掉句柄**。
   数组元素的描述字符串能正常回写，但 `hPhysicalMonitor` 永远是 0，导致所有 DDC 写入失败。
   改成 `Marshal.AllocHGlobal` + `Marshal.ReadIntPtr` 手动读，句柄立刻正常。

3. **第一次 `GetPhysicalMonitorsFromHMONITOR` 常返回"成功但空句柄"**。
   DDC 通道还没被唤醒，隔 150ms 重试一次就有值。所以空结果也要重试，不能只看返回值。

### 排障

**列表里显示"DDC/CI 不可用"**

1. 显示器 OSD 里把 **DDC/CI** 打开（大多数外接屏默认关闭）。
2. 用 HDMI / DP / DVI；VGA 与部分转接器不支持 DDC。
3. 笔记本内屏走 eDP，通常不支持。
4. 个别显示器需要管理员权限，右键"以管理员身份运行"。

**调了没反应**

- 诊断页看 **HDR**：HDR 开启时 Windows 接管整条色彩管线，Gamma 类调节会失效。先关 HDR。
- 看 **接口**：USB-C 转 HDMI 的扩展坞经常不透传 DDC。
- 看写入结果里的 `✕`：显示器接受了命令但读回值不同，通常是该档位被 OSD 锁定。

**切换输入源后黑屏**

正常现象，信号要重新握手 1–3 秒。选错接口就用显示器上的物理按钮切回来，不会损坏设备。

### 数据文件

```
%LOCALAPPDATA%\DdcMonitorTool\
├── app.log         运行日志（超过 2MB 自动轮转为 app.log.1）
├── snapshots.json  各显示器的 DDC 设置快照
├── game.json       游戏预设、游戏绑定、自动切换开关
└── gamma\          Gamma 基线（.bin）与"已应用"标记（active.json）
```

诊断页有"打开日志目录"按钮。

### 目录结构

```
DdcMonitorTool.csproj
app.manifest                 DPI 感知（PerMonitorV2）/ 兼容性声明
Program.cs                   入口：CLI 分发 + GUI 启动 + 全局异常
Cli.cs                       --list / --diag / --selftest / --set / --apply / --reset
Core/
├── DdcNative.cs             全部 P/Invoke 与结构体
├── DisplayTopology.cs       QueryDisplayConfig：接口 / HDR / EDID 路径
├── Edid.cs                  EDID 解析 + 按设备路径精确读注册表
├── MonitorInfo.cs           单块显示器的画像
├── VcpCodes.cs              VCP 代码、枚举名、能力字符串解析
├── MonitorDetector.cs       枚举 + 能力探测（后台线程）
├── MonitorController.cs     单屏 DDC 写入（节流 + 校验）
├── Snapshot.cs              快照存取
├── GameProfile.cs           游戏预设 / 绑定 / 应用逻辑
├── GammaRamp.cs             GPU Gamma LUT + 基线持久化与崩溃恢复
├── ColorMatrix.cs           放大镜色彩矩阵（真饱和度）
├── ForegroundWatcher.cs     前台窗口监听（WinEventHook + 兜底轮询）
└── AppLog.cs                三路日志
UI/
├── Theme.cs                 色板 / 圆角 / 深色菜单渲染
├── Controls.cs              Card、ThinSlider、PillButton、NavItem、Switch…
└── MainForm.cs              主窗口
tools/make_icon.py           生成 assets/app.ico
assets/app.ico               9 档尺寸图标
```

### 已知限制

- **Windows 专属**，依赖 `dxva2.dll` 与 DisplayConfig。
- **色彩矩阵只在窗口化 / 无边框窗口下生效**，独占全屏无效——这是 DWM 合成路径决定的，绕不过去。
- **HDR 开启时 Gamma Ramp 会被系统忽略**，游戏页会给出提示；此时只有 DDC 那几项生效。
- 游戏模式在工具**运行时**才有效；关掉工具不会自动还原（但 Gamma 基线会在下次启动时恢复）。
- 色温档位名（4000K / 6500K…）是 MCCS 约定的编号，实际色温由厂商决定，不同型号可能不一致。
- 输入源名称同理，`0x0F` 有的厂商是 DP-1、有的是 DP-2，以显示器实际行为为准。
- 目标框架 .NET 6 已结束支持。升级到 .NET 8 只需改 `TargetFramework` 并安装对应 SDK，代码无需改动。

### 免责声明

本工具直接向显示器发送 MCCS 命令。写入频率已做限制（≥120ms/次），但**恢复出厂设置**是不可逆操作，
执行前请先保存快照。因使用本工具导致的设置丢失或设备异常，作者不承担责任。

### 许可证

[MIT](LICENSE)

---

## English

Control your external monitor's brightness, contrast, RGB gain, color temperature, black level and
input source directly over **DDC/CI (VESA MCCS)** — no vendor software, no NVIDIA/AMD control panel.
Works with **any monitor that implements DDC/CI**, including budget panels that never got a proper driver.

It also ships a **game color mode**: one profile applied to both the monitor hardware and the GPU
output, switched automatically when a game starts and reverted when it exits.

### Features

| Feature | Notes |
|---|---|
| **Image** | Brightness, contrast, black level, sharpness — live while dragging |
| **Color** | Color-temperature presets (only the ones the panel actually reports), RGB gain |
| **Game color** | 4 built-in presets + your own, applied to hardware and GPU together |
| **Per-game auto switch** | Bind `exe → preset`; applies on focus, restores when the process exits |
| **Snapshots** | Original values captured on first connect, one-click restore |
| **Diagnostics** | Connection type, EDID, HDR, bit depth, MC_CAPS, live VCP table |
| **CLI** | `--list` / `--diag` / `--selftest` / `--set` / `--apply` / `--reset` |

### Download

Grab a build from [Releases](https://github.com/FangqingNu/DDC-Monitor-Tool/releases):

- `DdcMonitorTool-fx-win-x64.exe` — requires the [.NET 6 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/6.0), ~400 KB
- `DdcMonitorTool-standalone-win-x64.exe` — self-contained, just run it

### Build from source

```powershell
git clone https://github.com/FangqingNu/DDC-Monitor-Tool.git
cd ddc-monitor-tool

dotnet publish -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o publish
```

Requires the .NET 6 SDK. Output: `publish\DdcMonitorTool.exe`.

### Game color

Two layers, because they cannot do the same things:

| Layer | Mechanism | Where it applies | What it does |
|---|---|---|---|
| **Hardware (DDC)** | VCP `0x12` contrast, `0x6C/6E/70` black level, `0x87` sharpness, `0x10` brightness, `0x14` color temp | **Works in exclusive fullscreen** | Contrast, shadows, sharpness, white point |
| **Software (GPU)** | Gamma ramp (1D LUT), Magnification 5×5 color matrix | Gamma: everywhere. Matrix: **windowed / borderless only** | Midtone lift, shadow lift, real saturation |

Saturation is not exposed over DDC on purpose: a gamma ramp is a per-channel 1D LUT and cannot
produce saturation mathematically, and many panels report a bogus `0x52` value (always `0` on the
development machine). Writing it risks turning the screen grey. So saturation uses the GPU color
matrix instead — with the trade-off that it does not apply in exclusive fullscreen.

Built-in presets (values are percentages, mapped to the panel's real range at apply time):

| Preset | Idea |
|---|---|
| **Competitive** | Contrast 78% + black level 58% + gamma 1.05, crisp without washing out |
| **Shadow boost** | Black level 70% + gamma 1.10 + shadow lift 22 — enemies show up earlier, blacks get slightly grey |
| **Cinema** | 6500K (falls back to 7500K when unsupported), higher saturation |
| **Night** | Brightness 45%, low saturation; warm color temp when the panel supports it |

Built-in presets deliberately **never touch sharpness** — the meaning of 0 and 100 differs wildly
between vendors, and maxing it on an unknown panel produces white halos.

#### Auto switching

- Bind `exe → preset`; the preset is applied the moment the game gets focus.
- **Alt-Tab does not revert.** Settings are restored only when all bound game processes have exited
  (judged by process, not by window).
- On restore: DDC values go back to the pre-game snapshot, gamma goes back to its baseline, the
  color matrix is reset to identity.
- Foreground tracking uses `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` — 0% CPU when idle — plus a
  3-second fallback poll.
- A killed process lingers for a second or two, so a 2-second timer re-checks before restoring.

> Only the foreground process name is read. No injection, no game memory access, no file modification.

#### Crash safety

The original gamma ramp is persisted to disk with an "applied" marker before the first write.
If the process is killed or the machine crashes, the next launch detects the marker and restores
the ramp automatically, so the desktop never stays tinted. The color matrix is reset on exit too.

### CLI

```
DdcMonitorTool.exe                    launch the GUI
DdcMonitorTool.exe --list             list monitors and every VCP they expose
DdcMonitorTool.exe --diag             display topology only (connection / EDID / HDR / gamma state)
DdcMonitorTool.exe --selftest         self-test, exits 0 on success
DdcMonitorTool.exe --set 10 60        set brightness (0x10) to 60
DdcMonitorTool.exe --presets          list game presets
DdcMonitorTool.exe --apply "Shadow boost"
DdcMonitorTool.exe --reset            restore gamma baseline + DDC snapshot
DdcMonitorTool.exe --foreground       print the current foreground process name
```

`--selftest` runs the full chain: topology → detection → DDC write loop (**writes the current value
back, so nothing changes on screen**) → UI construction → range conversion.

### Compatibility

The only assumption is that the monitor implements DDC/CI (VESA MCCS). Everything that varies
between panels is probed at runtime:

| Variation | Handling |
|---|---|
| Different ranges (contrast 0–100 vs 0–255, sharpness 0–5 vs 0–100) | Presets are stored as **0–100%** and mapped to the panel's reported max |
| Different capability list | Parses the `0xF0` capability string; unsupported items never appear |
| No `0xF0` support | Falls back to `MC_CAPS` bits, then to probing each VCP |
| Different input sources / color temp steps | Uses the enumerated values from the capability string; color temp snaps to the nearest step only if it is one step away, otherwise it is skipped with an explanation |
| Built-in panels with a proprietary brightness API | `GetMonitorBrightness` / `SetMonitorBrightness` fallback |
| Slow I2C (common on cheap panels) | Retry backoff 80 / 160 / 320 ms; 4-second budget per detection pass |
| Empty handle right after wake-up | Retried after 150 ms instead of trusting the first call |
| Two identical monitors (same EDID) | Snapshot keys are disambiguated by device name |
| Writes locked by the OSD | Read-back verification; a mismatch shows `✕` instead of a fake success |

**Still not possible:**

- DDC/CI disabled in the monitor's OSD — the tool can only detect it and tell you to enable it.
- Adapters and docks — many USB-C to HDMI adapters do not pass DDC through.
- VCPs that cannot be read but can be written — treated as unsupported rather than blind-written.
- Panels with an unreliable `0x52` saturation value — not exposed; the GPU matrix is used instead.
- Vendor-private VCPs (BenQ DyAc / Black eQualizer, LG Smart Energy, …) — outside MCCS, no generic code exists.

The fastest way to check a monitor is `--list`: however many VCPs it reports is how much you can tune.

### Implementation notes

```
UI thread  ──await──>  worker thread  ──P/Invoke──>  dxva2.dll  ──I2C/DDC──>  monitor
    ↑                                            ↑
    └──────────── results marshalled back ───────┘
```

- Every DDC round-trip (~30–80 ms) runs on the thread pool; the UI never blocks.
- Writes are serialized and throttled to one per **120 ms** — cheap EEPROM controllers cannot keep up.
- Every write is verified by reading the value back; mismatches are reported honestly.

Capabilities come from three sources in priority order: the capability string (`0xF0`), then
`MC_CAPS` bits, then probing. Anything unreadable is reported as unsupported — never faked.

#### Three P/Invoke traps worth knowing

1. **`fixed char[]` does not work in P/Invoke structs.** With
   `fixed char viewGdiDeviceName[32]`, the marshaller copies back exactly one character and the
   device name becomes `"\"`. Use `[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] string`.

2. **`[Out] PHYSICAL_MONITOR[]` swallows the handle.** The description string marshals fine but
   `hPhysicalMonitor` is always `0`, which breaks every DDC write. Allocating with
   `Marshal.AllocHGlobal` and reading with `Marshal.ReadIntPtr` fixes it.

3. **The first `GetPhysicalMonitorsFromHMONITOR` often "succeeds" with a null handle.** The DDC
   channel is not awake yet; retrying after 150 ms returns a valid one. Retry on empty results too.

### Troubleshooting

**"DDC/CI unavailable"**

1. Enable **DDC/CI** in the monitor's OSD (off by default on most external displays).
2. Use HDMI / DP / DVI; VGA and some adapters do not carry DDC.
3. Laptop internal panels use eDP and usually do not support it.
4. A few monitors need elevation — run as administrator.

**Nothing happens when I change a value**

- Check **HDR** on the diagnostics page: while HDR is on, Windows owns the color pipeline and
  gamma-style adjustments are ignored.
- Check the **connection**: USB-C docks frequently do not pass DDC.
- A `✕` in the result means the monitor accepted the command but read back a different value —
  usually that setting is locked in the OSD.

### Data files

```
%LOCALAPPDATA%\DdcMonitorTool\
├── app.log         run log (rotated to app.log.1 at 2 MB)
├── snapshots.json  per-monitor DDC snapshots
├── game.json       game presets, bindings, auto-switch flag
└── gamma\          gamma baseline (.bin) and the "applied" marker (active.json)
```

### Known limitations

- **Windows only** — depends on `dxva2.dll` and DisplayConfig.
- **The color matrix only works windowed / borderless**, not in exclusive fullscreen (DWM compositing).
- **Gamma ramps are ignored while HDR is on**; the game page says so, and only the DDC items apply.
- Game mode only works while the tool is running. Gamma is still recovered on the next launch.
- Color-temperature and input-source names are MCCS conventions; the actual behavior is vendor-defined.
- .NET 6 is out of support. Moving to .NET 8 is a `TargetFramework` change; no code changes needed.

### Disclaimer

This tool sends MCCS commands straight to your monitor. Writes are throttled, but **factory reset**
is irreversible — save a snapshot first. The author is not responsible for lost settings or
damaged hardware.

### License

[MIT](LICENSE)
