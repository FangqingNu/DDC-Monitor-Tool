using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DdcMonitorTool.Core;

namespace DdcMonitorTool.UI
{
    /// <summary>
    /// 主窗口。
    ///
    /// 与旧版的根本区别：所有 DDC 往返（探测 + 写入）都在后台线程，UI 线程只做渲染。
    /// 旧版在 Load 里同步跑完整探测（多台显示器 + 多次重试可达数秒），窗口整段时间是白的，
    /// 还靠 Application.DoEvents() 硬撑消息泵。
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int PageImage = 0;
        private const int PageColor = 1;
        private const int PageGame = 2;
        private const int PageInput = 3;
        private const int PageDiag = 4;
        private const int PageLog = 5;

        private static readonly string[] PageTitles = { "图像", "色彩", "游戏", "输入源", "诊断", "日志" };
        private static readonly string[] PageSubtitles =
        {
            "亮度、对比度与其它图像参数，拖动即时生效",
            "色温预设与 RGB 增益",
            "游戏调色预设与按游戏自动切换",
            "切换显示器的输入信号",
            "显示链路与 VCP 能力的真实探测结果",
            "运行记录，同时写入日志文件",
        };

        // ---- 静态骨架 ----
        private readonly Panel _pageHost = new();
        private readonly Label _titleLabel = new();
        private readonly Label _subtitleLabel = new();
        private readonly DropDownButton _monitorPicker = new();
        private readonly Label _statusLabel = new();
        private readonly StatusDot _statusDot = new();
        private readonly List<NavItem> _navItems = new();
        private readonly List<Panel> _pages = new();
        private int _currentPage = PageImage;

        // ---- 状态 ----
        private List<MonitorInfo> _monitors = new();
        private MonitorInfo? _selected;
        private MonitorController? _controller;
        private readonly SemaphoreSlim _ddcLock = new(1, 1);
        private bool _detecting;
        private bool _rebuilding;

        // ---- 日志 ----
        private TextBox? _logBox;
        private readonly ConcurrentQueue<string> _logQueue = new();
        private readonly StringBuilder _logText = new();
        private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 200 };

        // ---- 每次重建都会换新的控件引用 ----
        private SliderRow? _brightnessRow;
        private SliderRow? _contrastRow;
        private SliderRow? _volumeRow;
        private SliderRow? _sharpnessRow;
        private SliderRow? _redRow;
        private SliderRow? _greenRow;
        private SliderRow? _blueRow;
        private DropDownButton? _tempPicker;
        private DropDownButton? _inputPicker;
        private LabeledValue? _snapshotInfo;
        private PillButton? _restoreSnapshotButton;

        private readonly Dictionary<byte, Debounce> _debounces = new();

        // ---- 游戏调色 ----
        private ForegroundWatcher? _watcher;
        private bool _autoSwitchEnabled = true;
        private string? _activeGameExe;
        private Dictionary<byte, int>? _preGameValues;
        private ToggleRow? _autoSwitchRow;
        private LabeledValue? _gameStatus;
        private FlowLayoutPanel? _presetButtons;
        private TableLayoutPanel? _bindingList;
        private SliderRow? _gameContrast;
        private SliderRow? _gameBlack;
        private SliderRow? _gameSharpness;
        private SliderRow? _gameGamma;
        private SliderRow? _gameLift;
        private SliderRow? _gameBoost;
        private PillButton? _gameApplyButton;
        private GameProfile? _lastProfile;
        private readonly System.Windows.Forms.Timer _gameExitTimer = new() { Interval = 2000 };

        public MainForm()
        {
            Text = "DDC 显示器工具";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ClientSize = new Size(1060, 720);
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            DoubleBuffered = true;
            KeyPreview = true;

            BuildShell();

            AppLog.Appended += OnLogAppended;
            _logTimer.Tick += (_, _) => FlushLog();
            _logTimer.Start();

            // 上次没正常退出的话，先把屏幕从偏色里救回来
            GammaState.RecoverAbandoned();

            _gameExitTimer.Tick += async (_, _) => await CheckGameExitAsync();

            _watcher = new ForegroundWatcher();
            _watcher.Changed += exe =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke(new Action(() => OnForegroundChanged(exe))); } catch { }
            };
            _watcher.Start();

            // 先把空页面建出来，检测在后台跑，用户不会看到一块纯黑的内容区
            RebuildPages();

            Load += async (_, _) => await RefreshAsync();
            FormClosing += OnFormClosing;
            KeyDown += OnKeyDown;
        }

        // ================================================================ 骨架

        private void BuildShell()
        {
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 240, BackColor = Theme.Sidebar };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

            BuildSidebar(sidebar);
            BuildContent(content);

            Controls.Add(content);
            Controls.Add(sidebar);
        }

        private void BuildSidebar(Panel sidebar)
        {
            var brand = new Panel
            {
                Dock = DockStyle.Top,
                Height = 86,
                Padding = new Padding(18, 20, 18, 0),
                BackColor = Theme.Sidebar,
            };

            var icon = new PictureBox
            {
                Size = new Size(26, 26),
                Location = new Point(18, 22),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = LoadAppIcon(),
                BackColor = Color.Transparent,
            };
            brand.Controls.Add(icon);

            brand.Controls.Add(new Label
            {
                Text = "DDC 显示器工具",
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold),
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(52, 20),
                BackColor = Color.Transparent,
            });

            brand.Controls.Add(new Label
            {
                Text = "V" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0"),
                Font = Theme.Small,
                ForeColor = Theme.Text3,
                AutoSize = true,
                Location = new Point(53, 42),
                BackColor = Color.Transparent,
            });

            var nav = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 6),
                BackColor = Theme.Sidebar,
            };
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            for (int i = 0; i < PageTitles.Length; i++)
            {
                int index = i;
                var item = new NavItem(PageTitles[i]);
                item.Click += (_, _) => ShowPage(index);
                nav.RowCount++;
                nav.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
                item.Dock = DockStyle.Fill;
                item.Margin = new Padding(0, 2, 0, 2);
                nav.Controls.Add(item, 0, nav.RowCount - 1);
                _navItems.Add(item);
            }

            // 底部：显示器选择 + 状态
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 108,
                Padding = new Padding(14, 0, 14, 14),
                BackColor = Theme.Sidebar,
            };

            _monitorPicker.Dock = DockStyle.Top;
            _monitorPicker.Height = 32;
            _monitorPicker.Font = Theme.Body;
            _monitorPicker.SelectionChanged += async (_, _) =>
            {
                int index = _monitorPicker.SelectedValue;
                if (index >= 0 && index < _monitors.Count) await SelectMonitorAsync(_monitors[index]);
            };

            _statusDot.Dock = DockStyle.Top;
            _statusDot.Height = 20;

            _statusLabel.Dock = DockStyle.Top;
            _statusLabel.Height = 34;
            _statusLabel.Font = Theme.Small;
            _statusLabel.ForeColor = Theme.Text3;
            _statusLabel.Text = "启动中…";
            _statusLabel.AutoEllipsis = true;

            footer.Controls.Add(_statusLabel);
            footer.Controls.Add(_statusDot);
            footer.Controls.Add(_monitorPicker);

            sidebar.Controls.Add(footer);
            sidebar.Controls.Add(nav);
            sidebar.Controls.Add(brand);
        }

        private void BuildContent(Panel content)
        {
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 96,
                Padding = new Padding(30, 24, 30, 0),
                BackColor = Theme.Bg,
            };

            _titleLabel.Text = PageTitles[0];
            _titleLabel.Font = Theme.H1;
            _titleLabel.ForeColor = Theme.Text;
            _titleLabel.AutoSize = true;
            _titleLabel.Location = new Point(30, 22);
            _titleLabel.BackColor = Color.Transparent;

            _subtitleLabel.Text = PageSubtitles[0];
            _subtitleLabel.Font = Theme.Small;
            _subtitleLabel.ForeColor = Theme.Text3;
            _subtitleLabel.AutoSize = true;
            _subtitleLabel.Location = new Point(32, 56);
            _subtitleLabel.BackColor = Color.Transparent;

            var refresh = new PillButton
            {
                Text = "重新检测",
                Size = new Size(88, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(content.Width - 118, 26),
            };
            refresh.MakeSecondary();
            refresh.Click += async (_, _) => await RefreshAsync();

            header.Controls.Add(_titleLabel);
            header.Controls.Add(_subtitleLabel);
            header.Controls.Add(refresh);
            header.Resize += (_, _) => refresh.Left = header.Width - refresh.Width - 30;

            _pageHost.Dock = DockStyle.Fill;
            _pageHost.BackColor = Theme.Bg;

            content.Controls.Add(_pageHost);
            content.Controls.Add(header);
        }

        private static Image? LoadAppIcon()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    return Icon.ExtractAssociatedIcon(exe)?.ToBitmap();
            }
            catch
            {
                // 图标取不到不影响使用
            }
            return null;
        }

        // ================================================================ 页面

        /// <summary>
        /// 自检用：直接把检测结果塞进界面并构建全部页面，
        /// 这样 --selftest 能验证每个页面的布局代码，而不需要真的显示窗口。
        /// </summary>
        internal void PrimeWith(IReadOnlyList<MonitorInfo> monitors)
        {
            _monitors = monitors.ToList();
            if (_monitors.Count == 0) return;

            _monitorPicker.Enabled = true;
            _monitorPicker.SetChoices(
                _monitors.Select((m, i) => new Choice($"{i + 1}. {m.DisplayLabel}", i)), 0);

            _selected = _monitors[0];
            RebuildPages();

            // 逐页切换一遍，确保每页都能正常布局
            for (int i = 0; i < _pages.Count; i++) ShowPage(i);
            ShowPage(0);
            AppLog.Info($"页面构建完成：{_pages.Count} 页，控件 " + CountAll(this) + " 个");
        }

        internal static int CountAll(Control root)
        {
            int count = 1;
            foreach (Control child in root.Controls) count += CountAll(child);
            return count;
        }

        private void ShowPage(int index)
        {
            _currentPage = index;
            for (int i = 0; i < _navItems.Count; i++)
                _navItems[i].Selected = i == index;
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].Visible = i == index;

            _titleLabel.Text = PageTitles[index];
            _subtitleLabel.Text = PageSubtitles[index];
        }

        private (Panel Page, TableLayoutPanel Stack) NewPage()
        {
            var page = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Theme.Bg,
                Visible = false,
            };

            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Bg,
                Padding = new Padding(28, 4, 28, 28),
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            page.Controls.Add(stack);
            _pageHost.Controls.Add(page);
            _pages.Add(page);
            return (page, stack);
        }

        private static void AddCard(TableLayoutPanel stack, Control card)
        {
            stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Dock = DockStyle.Top;
            card.Margin = new Padding(0, 0, 0, 14);
            stack.Controls.Add(card, 0, stack.RowCount - 1);
        }

        private void RebuildPages()
        {
            if (_rebuilding) return;
            _rebuilding = true;
            try
            {
                foreach (byte key in _debounces.Keys.ToList())
                {
                    _debounces[key].Dispose();
                    _debounces.Remove(key);
                }

                _pageHost.SuspendLayout();
                foreach (var page in _pages) page.Dispose();
                _pages.Clear();
                _pageHost.Controls.Clear();

                _brightnessRow = _contrastRow = _volumeRow = _sharpnessRow = null;
                _redRow = _greenRow = _blueRow = null;
                _tempPicker = null;
                _inputPicker = null;
                _snapshotInfo = null;
                _restoreSnapshotButton = null;
                _logBox = null;

                _autoSwitchRow = null;
                _gameStatus = null;
                _presetButtons = null;
                _bindingList = null;
                _gameContrast = _gameBlack = _gameSharpness = null;
                _gameGamma = _gameLift = _gameBoost = null;
                _gameApplyButton = null;

                BuildImagePage();
                BuildColorPage();
                BuildGamePage();
                BuildInputPage();
                BuildDiagPage();
                BuildLogPage();

                ShowPage(Math.Min(_currentPage, _pages.Count - 1));
            }
            finally
            {
                _pageHost.ResumeLayout();
                _rebuilding = false;
            }
        }

        private void BuildImagePage()
        {
            var (_, stack) = NewPage();
            var monitor = _selected;

            var card = new Card();
            card.AddTitle("图像调节", monitor == null ? "未选择显示器" : "拖动滑块即时写入显示器，松手后 250ms 生效");

            if (monitor != null && monitor.Supports(VcpCodes.Brightness))
            {
                _brightnessRow = MakeSliderRow(card, monitor, VcpCodes.Brightness, "亮度", "");
                _brightnessRow.Slider.ValueChanged += (_, _) => Kick(VcpCodes.Brightness);
            }
            else
            {
                card.AddRow(MakeUnsupported("亮度", monitor));
            }

            if (monitor != null && monitor.Supports(VcpCodes.Contrast))
            {
                _contrastRow = MakeSliderRow(card, monitor, VcpCodes.Contrast, "对比度", "");
                _contrastRow.Slider.ValueChanged += (_, _) => Kick(VcpCodes.Contrast);
            }
            else
            {
                card.AddRow(MakeUnsupported("对比度", monitor));
            }

            AddCard(stack, card);

            // 音量 / 锐度：只有显示器声明了才出现
            var extras = new List<byte>();
            if (monitor != null && monitor.Supports(VcpCodes.AudioVolume)) extras.Add(VcpCodes.AudioVolume);
            if (monitor != null && monitor.Supports(VcpCodes.Sharpness)) extras.Add(VcpCodes.Sharpness);

            if (extras.Count > 0)
            {
                var extraCard = new Card();
                extraCard.AddTitle("其它");
                foreach (byte code in extras)
                {
                    var row = MakeSliderRow(extraCard, monitor!, code, VcpCodes.GetName(code), "");
                    if (code == VcpCodes.AudioVolume) _volumeRow = row;
                    else _sharpnessRow = row;
                    row.Slider.ValueChanged += (_, _) => Kick(code);
                }
                AddCard(stack, extraCard);
            }

            // 快照
            var snapshotCard = new Card();
            snapshotCard.AddTitle("快照", "改动前先存一份，随时可以还原");

            var snap = monitor == null ? null : SnapshotStore.Get(monitor.SnapshotKey);
            _snapshotInfo = new LabeledValue
            {
                Caption = "当前快照",
                Value = snap == null ? "尚未保存" : snap.TakenAt.ToString("yyyy-MM-dd HH:mm"),
                ValueColor = snap == null ? Theme.Text3 : Theme.Text2,
            };
            snapshotCard.AddRow(_snapshotInfo, 10);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 12, 0, 0),
                BackColor = Theme.Card,
            };

            var save = new PillButton { Text = "保存当前为新快照", Size = new Size(150, 32) };
            save.MakeSecondary();
            save.Click += async (_, _) => await SaveSnapshotAsync();
            buttons.Controls.Add(save);

            _restoreSnapshotButton = new PillButton { Text = "还原到快照", Size = new Size(112, 32) };
            _restoreSnapshotButton.MakeSecondary();
            _restoreSnapshotButton.Enabled = snap != null && snap.Values.Count > 0;
            _restoreSnapshotButton.Click += async (_, _) => await RestoreSnapshotAsync();
            buttons.Controls.Add(_restoreSnapshotButton);

            snapshotCard.AddRow(buttons, 0);
            AddCard(stack, snapshotCard);
        }

        private SliderRow MakeSliderRow(Card card, MonitorInfo monitor, byte code, string caption, string unit)
        {
            var feature = monitor.VcpFeatures.TryGetValue(code, out var f) ? f : null;
            int min = 0;
            int max = feature?.Maximum ?? 100;
            if (max <= min) max = min + 100;
            int current = feature?.Current ?? min;

            var row = new SliderRow(caption);
            row.Slider.Minimum = min;
            row.Slider.Maximum = max;
            row.Slider.SetValueSilently(current);
            row.Header.Value = $"{current}{unit}";
            row.Header.ValueColor = Theme.Text;

            card.AddRow(row, 12);
            return row;
        }

        private static Control MakeUnsupported(string caption, MonitorInfo? monitor)
        {
            string reason = monitor == null ? "未选择显示器" : "显示器未声明支持该功能";
            return new LabeledValue { Caption = caption, Value = reason, ValueColor = Theme.Text3 };
        }

        private void BuildColorPage()
        {
            var (_, stack) = NewPage();
            var monitor = _selected;

            var tempCard = new Card();
            tempCard.AddTitle("色温预设", "来自显示器的 0x14 能力表，未声明的档位不会出现");

            if (monitor != null && monitor.Supports(VcpCodes.ColorTemperature))
            {
                var feature = monitor.VcpFeatures[VcpCodes.ColorTemperature];
                var choices = feature.AllowedValues.Count > 0
                    ? feature.AllowedValues.Select(v => new Choice(VcpCodes.ColorTemperatureName(v), v))
                    : Enumerable.Range(1, 9).Select(v => new Choice(VcpCodes.ColorTemperatureName(v), v));

                _tempPicker = new DropDownButton { Dock = DockStyle.Top };
                _tempPicker.SetChoices(choices, feature.Current);
                _tempPicker.SelectionChanged += async (_, _) => await ApplyColorTemperatureAsync();
                tempCard.AddRow(_tempPicker, 12);
            }
            else
            {
                tempCard.AddRow(MakeUnsupported("色温", monitor), 12);
            }

            AddCard(stack, tempCard);

            var gainCard = new Card();
            gainCard.AddTitle("RGB 增益", "逐通道白点，100 为中性；三通道一起改可做色温微调");

            bool hasGain = monitor != null &&
                           (monitor.Supports(VcpCodes.RedGain) || monitor.Supports(VcpCodes.GreenGain) || monitor.Supports(VcpCodes.BlueGain));

            if (hasGain)
            {
                _redRow = MakeSliderRow(gainCard, monitor!, VcpCodes.RedGain, "红色", "");
                _redRow.Slider.ValueChanged += (_, _) => Kick(VcpCodes.RedGain);
                _greenRow = MakeSliderRow(gainCard, monitor!, VcpCodes.GreenGain, "绿色", "");
                _greenRow.Slider.ValueChanged += (_, _) => Kick(VcpCodes.GreenGain);
                _blueRow = MakeSliderRow(gainCard, monitor!, VcpCodes.BlueGain, "蓝色", "");
                _blueRow.Slider.ValueChanged += (_, _) => Kick(VcpCodes.BlueGain);

                var reset = new PillButton { Text = "三通道归位到最大值", Size = new Size(160, 30), Margin = new Padding(0, 14, 0, 0) };
                reset.MakeSecondary();
                reset.Click += async (_, _) => await ResetGainsAsync();
                gainCard.AddRow(ButtonRow(reset), 4);
            }
            else
            {
                gainCard.AddRow(MakeUnsupported("RGB 增益", monitor), 12);
            }

            AddCard(stack, gainCard);
        }

        private void BuildGamePage()
        {
            var (_, stack) = NewPage();
            var monitor = _selected;
            var config = GameConfigStore.Load();
            _autoSwitchEnabled = config.AutoSwitch;
            var profile = _lastProfile;

            // ---------------- 自动切换 ----------------
            var autoCard = new Card();
            autoCard.AddTitle("自动切换", "游戏获得焦点时应用预设，全部退出后恢复原样");

            _autoSwitchRow = new ToggleRow("监听前台游戏");
            _autoSwitchRow.Toggle.Checked = _autoSwitchEnabled;
            _autoSwitchRow.Toggle.CheckedChanged += (_, _) => OnAutoSwitchChanged(_autoSwitchRow!.Toggle.Checked);
            autoCard.AddRow(_autoSwitchRow, 10);

            _gameStatus = new LabeledValue
            {
                Caption = "当前状态",
                Value = DescribeGameStatus(),
                ValueColor = _activeGameExe == null ? Theme.Text3 : Theme.Green,
            };
            autoCard.AddRow(_gameStatus, 8);

            autoCard.AddRow(new Label
            {
                Text = "只读取前台窗口的进程名，不注入游戏、不读内存。Alt-Tab 到桌面不会恢复，"
                     + "只有绑定的游戏进程真的退出才还原。",
                Font = Theme.Small,
                ForeColor = Theme.Text3,
                BackColor = Color.Transparent,
                Dock = DockStyle.Top,
                Height = 46,
                Margin = new Padding(0, 12, 0, 0),
                AutoEllipsis = true,
            }, 0);
            AddCard(stack, autoCard);

            // ---------------- 预设 ----------------
            var presetCard = new Card();
            presetCard.AddTitle("游戏预设", "点一下立即应用；下面的滑块可以微调后另存");

            _presetButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                BackColor = Theme.Card,
                Margin = new Padding(0, 10, 0, 0),
            };
            FillPresetButtons();
            presetCard.AddRow(_presetButtons, 10);
            AddCard(stack, presetCard);

            // ---------------- 参数 ----------------
            var tuneCard = new Card();
            tuneCard.AddTitle("调色参数",
                "对比度 / 暗部 / 锐度走显示器硬件（独占全屏有效）；滑块是百分比，按本机真实量程换算");

            SliderRow AddSlider(string caption, int min, int max, int value, Func<int, string> format)
            {
                var row = new SliderRow(caption);
                row.Slider.Minimum = min;
                row.Slider.Maximum = max;
                row.Slider.SetValueSilently(value);
                row.Header.Value = format(value);
                row.Slider.ValueChanged += (_, _) =>
                {
                    row.Header.Value = format(row.Slider.Value);
                    MarkGameDirty();
                };
                tuneCard.AddRow(row, 12);
                return row;
            }

            // 硬件项一律用 0–100 百分比，应用时按显示器真实量程换算
            int InitialPercent(byte code, int? fromProfile, int fallback)
            {
                if (fromProfile is int p) return Math.Clamp(p, 0, 100);
                int? current = monitor?.Value(code);
                return current is int c ? GameApplier.ToPercent(monitor, code, c) : fallback;
            }

            Func<int, string> PercentFormat(byte code)
            {
                int max = GameApplier.RangeMax(monitor, code);
                return v => max == 100
                    ? v.ToString()
                    : $"{v}% · {GameApplier.ToAbsolute(monitor, code, v)}";
            }

            _gameContrast = AddSlider("对比度（硬件）", 0, 100,
                InitialPercent(VcpCodes.Contrast, profile?.Contrast, 65), PercentFormat(VcpCodes.Contrast));
            _gameBlack = AddSlider("暗部 / 黑电平（硬件）", 0, 100,
                InitialPercent(VcpCodes.RedBlackLevel, profile?.BlackLevel, 50), PercentFormat(VcpCodes.RedBlackLevel));
            _gameSharpness = AddSlider("锐度（硬件）", 0, 100,
                InitialPercent(VcpCodes.Sharpness, profile?.Sharpness, 100), PercentFormat(VcpCodes.Sharpness));
            _gameGamma = AddSlider("Gamma（GPU）", 80, 130,
                (int)Math.Round((profile?.Gamma ?? 1.0) * 100), v => (v / 100.0).ToString("0.00"));
            _gameLift = AddSlider("暗部提升（GPU）", 0, 100,
                profile?.BlackLift ?? 0, v => v == 0 ? "关" : v.ToString());
            _gameBoost = AddSlider("色彩增强（GPU 矩阵）", 0, 100,
                profile?.ColorBoost ?? 50, v => v == 50 ? "中性" : v.ToString());

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Theme.Card,
                Margin = new Padding(0, 14, 0, 0),
            };

            _gameApplyButton = new PillButton { Text = "应用当前参数", Size = new Size(130, 32) };
            _gameApplyButton.Click += async (_, _) => await ApplyCustomGameAsync();
            actions.Controls.Add(_gameApplyButton);

            var savePreset = new PillButton { Text = "保存为预设", Size = new Size(108, 32), Margin = new Padding(8, 0, 0, 0) };
            savePreset.MakeSecondary();
            savePreset.Click += (_, _) => SaveCurrentAsPreset();
            actions.Controls.Add(savePreset);

            var restore = new PillButton { Text = "还原到快照", Size = new Size(108, 32), Margin = new Padding(8, 0, 0, 0) };
            restore.MakeSecondary();
            restore.Click += async (_, _) => await RestoreSnapshotAsync();
            actions.Controls.Add(restore);

            tuneCard.AddRow(actions, 6);
            AddCard(stack, tuneCard);

            // ---------------- 游戏绑定 ----------------
            var bindCard = new Card();
            bindCard.AddTitle("游戏绑定", "绑定后，这些游戏一获得焦点就自动切到对应预设");

            var addRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Theme.Card,
                Margin = new Padding(0, 10, 0, 0),
            };

            var gamePicker = new DropDownButton { Width = 230, Height = 32 };
            var games = GameCatalog.Known
                .Select((g, i) => new Choice($"{g.Name} · {g.Exe}", i))
                .ToList();
            gamePicker.SetChoices(games, 0);
            addRow.Controls.Add(gamePicker);

            var profiles = GameConfigStore.AllProfiles();
            var profilePicker = new DropDownButton { Width = 120, Height = 32, Margin = new Padding(8, 0, 0, 0) };
            profilePicker.SetChoices(profiles.Select((p, i) => new Choice(p.Name, i)), 0);
            addRow.Controls.Add(profilePicker);

            var addButton = new PillButton { Text = "添加", Size = new Size(64, 32), Margin = new Padding(8, 0, 0, 0) };
            addButton.Click += (_, _) =>
            {
                var all = GameConfigStore.AllProfiles();
                if (profilePicker.SelectedValue < 0 || profilePicker.SelectedValue >= all.Count) return;
                string exe = GameCatalog.Known[gamePicker.SelectedValue].Exe;
                GameConfigStore.SetBinding(exe, all[profilePicker.SelectedValue].Name);
                FillBindingList();
                RefreshGameStatusText();
            };
            addRow.Controls.Add(addButton);

            bindCard.AddRow(addRow, 10);

            _bindingList = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Card,
            };
            _bindingList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _bindingList.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));
            _bindingList.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64f));
            FillBindingList();
            bindCard.AddRow(_bindingList, 10);

            AddCard(stack, bindCard);
        }

        private void FillPresetButtons()
        {
            if (_presetButtons == null) return;
            _presetButtons.Controls.Clear();

            foreach (var profile in GameConfigStore.AllProfiles())
            {
                string name = profile.Name;
                var button = new PillButton
                {
                    Text = name,
                    Size = new Size(102, 32),
                    Margin = new Padding(0, 0, 8, 8),
                };
                button.MakeSecondary();
                if (!profile.BuiltIn) button.TextColor = Theme.Accent;
                button.Click += async (_, _) => await ApplyGameProfileAsync(GameConfigStore.Find(name), null);
                _presetButtons.Controls.Add(button);
            }
        }

        private void FillBindingList()
        {
            if (_bindingList == null) return;

            _bindingList.SuspendLayout();
            try
            {
                _bindingList.Controls.Clear();
                _bindingList.RowStyles.Clear();
                _bindingList.RowCount = 0;

                var config = GameConfigStore.Load();
                if (config.Bindings.Count == 0)
                {
                    _bindingList.RowCount = 1;
                    _bindingList.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
                    _bindingList.Controls.Add(new Label
                    {
                        Text = "还没有绑定。选一个游戏和预设，点“添加”。",
                        ForeColor = Theme.Text3,
                        Font = Theme.Small,
                        BackColor = Color.Transparent,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                    }, 0, 0);
                    return;
                }

                foreach (var pair in config.Bindings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string exe = pair.Key;
                    string profileName = pair.Value;

                    _bindingList.RowCount++;
                    _bindingList.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));

                    int row = _bindingList.RowCount - 1;
                    _bindingList.Controls.Add(new Label
                    {
                        Text = $"{GameCatalog.DisplayName(exe)}   {exe}",
                        ForeColor = Theme.Text2,
                        Font = Theme.Body,
                        BackColor = Color.Transparent,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                        AutoEllipsis = true,
                    }, 0, row);

                    _bindingList.Controls.Add(new Label
                    {
                        Text = profileName,
                        ForeColor = Theme.Text,
                        Font = Theme.Body,
                        BackColor = Color.Transparent,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                    }, 1, row);

                    var remove = new PillButton { Text = "解除", Size = new Size(52, 24), Radius = 6 };
                    remove.MakeGhost();
                    remove.Dock = DockStyle.Right;
                    remove.Click += (_, _) =>
                    {
                        GameConfigStore.RemoveBinding(exe);
                        FillBindingList();
                        RefreshGameStatusText();
                    };
                    _bindingList.Controls.Add(remove, 2, row);
                }
            }
            finally
            {
                _bindingList.ResumeLayout();
            }
        }

        private string DescribeGameStatus()
        {
            if (_activeGameExe == null) return "空闲";
            return $"{GameCatalog.DisplayName(_activeGameExe)} · 已应用";
        }

        private void RefreshGameStatusText()
        {
            if (_gameStatus == null || _gameStatus.IsDisposed) return;
            _gameStatus.Value = DescribeGameStatus();
            _gameStatus.ValueColor = _activeGameExe == null ? Theme.Text3 : Theme.Green;
        }

        private void MarkGameDirty()
        {
            if (_gameApplyButton == null || _gameApplyButton.IsDisposed) return;
            _gameApplyButton.Text = "应用改动 ●";
        }

        private void OnAutoSwitchChanged(bool enabled)
        {
            _autoSwitchEnabled = enabled;
            var config = GameConfigStore.Load();
            config.AutoSwitch = enabled;
            GameConfigStore.Save(config);
            _statusLabel.Text = enabled ? "自动切换已开启" : "自动切换已关闭";
        }

        private GameProfile ProfileFromSliders(string name)
        {
            return new GameProfile
            {
                Name = name,
                Contrast = _gameContrast?.Slider.Value,
                BlackLevel = _gameBlack?.Slider.Value,
                Sharpness = _gameSharpness?.Slider.Value,
                Gamma = (_gameGamma?.Slider.Value ?? 100) / 100.0,
                BlackLift = _gameLift?.Slider.Value ?? 0,
                ColorBoost = _gameBoost?.Slider.Value ?? 50,
            };
        }

        private void SyncSlidersFrom(GameProfile profile)
        {
            void Set(SliderRow? row, int? value, Func<int, string> format)
            {
                if (row == null || row.IsDisposed || value is not int v) return;
                row.Slider.SetValueSilently(v);
                row.Header.Value = format(row.Slider.Value);
            }

            Set(_gameContrast, profile.Contrast, v => v.ToString());
            Set(_gameBlack, profile.BlackLevel, v => v.ToString());
            Set(_gameSharpness, profile.Sharpness, v => v.ToString());
            Set(_gameGamma, (int)Math.Round(profile.Gamma * 100), v => (v / 100.0).ToString("0.00"));
            Set(_gameLift, profile.BlackLift, v => v == 0 ? "关" : v.ToString());
            Set(_gameBoost, profile.ColorBoost, v => v == 50 ? "中性" : v.ToString());

            if (_gameApplyButton != null && !_gameApplyButton.IsDisposed)
                _gameApplyButton.Text = "应用当前参数";
        }

        private void SaveCurrentAsPreset()
        {
            string? name = Prompt.Show(this, "保存为预设", "预设名称", "我的预设");
            if (string.IsNullOrWhiteSpace(name)) return;

            var profile = ProfileFromSliders(name.Trim());
            profile.Description = "自定义";
            GameConfigStore.SaveProfile(profile);
            FillPresetButtons();
            _statusLabel.Text = $"已保存预设：{profile.Name}";
        }

        private async Task ApplyCustomGameAsync()
        {
            await ApplyGameProfileAsync(ProfileFromSliders("自定义"), null);
        }

        private async Task ApplyGameProfileAsync(GameProfile? profile, string? fromGame)
        {
            if (profile == null || _selected == null) return;
            var monitor = _selected;

            await _ddcLock.WaitAsync();
            try
            {
                var ctrl = await EnsureControllerAsync();
                if (ctrl == null) return;

                if (fromGame != null && _preGameValues == null)
                    _preGameValues = await Task.Run(() => CaptureCurrent(ctrl));

                var report = await Task.Run(() => GameApplier.Apply(ctrl, monitor, profile));
                if (!ReferenceEquals(_selected, monitor)) return;

                _lastProfile = profile;
                if (fromGame != null)
                {
                    _activeGameExe = fromGame;
                    // 游戏可能在后台被关掉（没有前台切换事件），靠定时器兜底
                    _gameExitTimer.Start();
                }

                SyncSlidersFrom(profile);
                RefreshGameStatusText();

                string summary = report.Summary();
                _statusLabel.Text = $"{profile.Name}：{summary}";
                if (report.Notes.Count > 0)
                    AppLog.Warn($"{profile.Name}：" + string.Join("；", report.Notes));
            }
            catch (Exception ex)
            {
                AppLog.Exception("应用游戏预设", ex);
                _statusLabel.Text = "应用失败：" + ex.Message;
            }
            finally
            {
                _ddcLock.Release();
            }
        }

        private static Dictionary<byte, int> CaptureCurrent(MonitorController controller)
        {
            var values = new Dictionary<byte, int>();
            foreach (byte code in VcpCodes.PrimaryVcps)
            {
                if (!controller.Supports(code)) continue;
                int? current = controller.Read(code);
                if (current.HasValue) values[code] = current.Value;
            }
            return values;
        }

        /// <summary>
        /// 判断绑定游戏是否都退出了。
        ///
        /// 必须可以重复调用：taskkill / 正常退出时进程对象会残留一两秒，
        /// 第一次查往往还是"在运行"，所以由 2 秒定时器持续复查，直到真的没了才还原。
        /// </summary>
        private async Task CheckGameExitAsync()
        {
            if (_activeGameExe == null)
            {
                _gameExitTimer.Stop();
                return;
            }

            var config = GameConfigStore.Load();
            bool anyRunning = config.Bindings.Keys.Any(ForegroundWatcher.IsProcessRunning);
            if (anyRunning) return;

            _gameExitTimer.Stop();
            await RestoreGameAsync();
        }

        private async Task RestoreGameAsync()
        {
            _gameExitTimer.Stop();
            bool restored = false;
            await _ddcLock.WaitAsync();
            try
            {
                var ctrl = await EnsureControllerAsync();
                if (ctrl != null && _preGameValues != null)
                {
                    var values = _preGameValues;
                    await Task.Run(() => ctrl.Restore(values));
                }

                if (_selected != null && GammaState.HasBaseline(_selected.DeviceName))
                    await Task.Run(() => GammaState.Restore(_selected.DeviceName));

                ColorMatrix.Reset();

                _preGameValues = null;
                _activeGameExe = null;
                _lastProfile = null;
                restored = true;
                _statusLabel.Text = "已退出游戏，设置已还原";
                AppLog.Info("游戏退出，已还原调色");
            }
            catch (Exception ex)
            {
                AppLog.Exception("还原游戏设置", ex);
            }
            finally
            {
                _ddcLock.Release();
            }

            // 页面里的滑块要跟着回到显示器的真实值
            if (restored && !IsDisposed) RebuildPages();
        }

        private async void OnForegroundChanged(string exe)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;

                if (!_autoSwitchEnabled || _selected == null) return;

                var config = GameConfigStore.Load();
                bool bound = config.Bindings.ContainsKey(exe);
                if (bound || _activeGameExe != null)
                    AppLog.Info($"[前台] {exe}" + (bound ? $" → {config.Bindings[exe]}" : string.Empty));

                if (config.Bindings.TryGetValue(exe, out string? profileName))
                {
                    if (string.Equals(_activeGameExe, exe, StringComparison.OrdinalIgnoreCase)) return;
                    var profile = GameConfigStore.Find(profileName);
                    if (profile == null)
                    {
                        AppLog.Warn($"绑定 {exe} → {profileName}，但预设不存在");
                        return;
                    }
                    await ApplyGameProfileAsync(profile, exe);
                }
                else if (_activeGameExe != null)
                {
                    // 前台不是绑定的游戏：只有所有绑定游戏都退出才还原，Alt-Tab 不算
                    await CheckGameExitAsync();
                }
            }
            catch (Exception ex)
            {
                AppLog.Exception("前台切换", ex);
            }
        }

        private void BuildInputPage()
        {
            var (_, stack) = NewPage();
            var monitor = _selected;

            var card = new Card();
            card.AddTitle("输入源", "只列出显示器自报支持的接口");

            if (monitor != null && monitor.Supports(VcpCodes.InputSource))
            {
                var feature = monitor.VcpFeatures[VcpCodes.InputSource];
                var values = feature.AllowedValues.Count > 0
                    ? feature.AllowedValues
                    : new List<int> { 0x01, 0x03, 0x0B, 0x11 };

                _inputPicker = new DropDownButton { Dock = DockStyle.Top };
                _inputPicker.SetChoices(values.Select(v => new Choice(VcpCodes.InputSourceName(v), v)), feature.Current);
                card.AddRow(_inputPicker, 12);

                var apply = new PillButton { Text = "切换到该输入源", Size = new Size(150, 32), Margin = new Padding(0, 14, 0, 0) };
                apply.Click += async (_, _) => await SwitchInputAsync();
                card.AddRow(ButtonRow(apply), 4);

                var hint = new Label
                {
                    Text = "切换后当前画面会中断，显示器需要 1–3 秒重新握手。如果选错导致黑屏，"
                         + "用显示器上的物理按钮切回来即可，不会损坏设备。",
                    Font = Theme.Small,
                    ForeColor = Theme.Text3,
                    BackColor = Color.Transparent,
                    Dock = DockStyle.Top,
                    Height = 58,
                    Margin = new Padding(0, 12, 0, 0),
                    AutoEllipsis = true,
                };
                card.AddRow(hint, 0);
            }
            else
            {
                card.AddRow(MakeUnsupported("输入源", monitor), 12);
            }

            AddCard(stack, card);
        }

        private void BuildDiagPage()
        {
            var (_, stack) = NewPage();
            var monitor = _selected;

            var linkCard = new Card();
            linkCard.AddTitle("显示链路");
            var linkStack = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Card,
            };
            linkStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            void Row(string caption, string value, Color? color = null)
            {
                linkStack.RowCount++;
                linkStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
                var row = new LabeledValue { Caption = caption, Value = value, Dock = DockStyle.Fill, Margin = new Padding(0) };
                if (color.HasValue) row.ValueColor = color.Value;
                linkStack.Controls.Add(row, 0, linkStack.RowCount - 1);
            }

            if (monitor == null)
            {
                Row("状态", "未检测到显示器", Theme.Red);
            }
            else
            {
                Row("设备", $"{monitor.DeviceName}{(monitor.IsPrimary ? "（主）" : string.Empty)}");
                Row("型号", monitor.DisplayLabel);
                Row("EDID", monitor.ManufacturerLine);
                Row("接口", monitor.ConnectionLabel);
                Row("分辨率", $"{monitor.ResolutionLabel} @ {monitor.RefreshLabel}");
                Row("HDR", monitor.HdrLabel, monitor.HdrEnabled ? Theme.Amber : monitor.HdrSupported ? Theme.Text : Theme.Text3);
                Row("色深", monitor.BitsPerColor > 0 ? $"{monitor.BitsPerColor} bit" : "未知");
                Row("DDC/CI", monitor.DdcSupported ? "可用" : "不可用",
                    monitor.DdcSupported ? Theme.Green : Theme.Red);
                Row("MC_CAPS", $"0x{monitor.McCapsRaw:X4}");
                if (!string.IsNullOrEmpty(monitor.DdcError))
                    Row("错误", monitor.DdcError, Theme.Red);
            }

            linkCard.AddRow(linkStack, 8);

            if (monitor != null && monitor.HdrEnabled)
            {
                linkCard.AddRow(new Label
                {
                    Text = "HDR 已开启：Windows 会接管整条色彩管线，很多调色手段在这个状态下不生效。"
                         + "如果觉得颜色不对，先在系统设置里关掉 HDR。",
                    Font = Theme.Small,
                    ForeColor = Theme.Amber,
                    BackColor = Color.Transparent,
                    Dock = DockStyle.Top,
                    Height = 58,
                    Margin = new Padding(0, 12, 0, 0),
                    AutoEllipsis = true,
                }, 0);
            }

            AddCard(stack, linkCard);

            // VCP 能力表
            var vcpCard = new Card();
            vcpCard.AddTitle("VCP 能力", monitor == null ? "—" : $"{monitor.VcpFeatures.Values.Count(f => f.Supported)} 项可读写");

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Card,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110f));

            if (monitor != null)
            {
                foreach (var feature in monitor.VcpFeatures.Values
                             .Where(f => f.Supported)
                             .OrderBy(f => Array.IndexOf(VcpCodes.PrimaryVcps, f.Code) is int i && i >= 0 ? i : 999)
                             .ThenBy(f => f.Code))
                {
                    table.RowCount++;
                    table.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));

                    table.Controls.Add(Cell(feature.CodeHex, Theme.Text3, Theme.Mono), 0, table.RowCount - 1);
                    table.Controls.Add(Cell(feature.Name, Theme.Text2, Theme.Body), 1, table.RowCount - 1);
                    table.Controls.Add(Cell(feature.RangeText, Theme.Text, Theme.Body, right: true), 2, table.RowCount - 1);
                }
            }

            vcpCard.AddRow(table, 8);
            AddCard(stack, vcpCard);

            // 危险操作
            var danger = new Card();
            danger.AddTitle("维护", "这两项会直接改动显示器，操作前建议先保存快照");

            var openLog = new PillButton { Text = "打开日志目录", Size = new Size(120, 30), Margin = new Padding(0, 10, 0, 0) };
            openLog.MakeSecondary();
            openLog.Click += (_, _) => OpenLogFolder();
            danger.AddRow(ButtonRow(openLog), 4);

            var reset = new PillButton
            {
                Text = "恢复出厂设置",
                Size = new Size(130, 30),
                Margin = new Padding(0, 8, 0, 0),
                FillColor = Color.FromArgb(120, 32, 32),
                HoverColor = Color.FromArgb(150, 40, 40),
                DownColor = Color.FromArgb(100, 26, 26),
            };
            reset.Enabled = monitor != null && monitor.DdcSupported;
            reset.Click += async (_, _) => await FactoryResetAsync();
            danger.AddRow(ButtonRow(reset), 4);

            AddCard(stack, danger);
        }

        /// <summary>把按钮放进一行流式容器，避免 Dock=Top 把按钮拉成整行宽。</summary>
        private static FlowLayoutPanel ButtonRow(params Control[] buttons)
        {
            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 12, 0, 0),
                BackColor = Theme.Card,
            };
            foreach (var button in buttons)
            {
                button.Margin = new Padding(0, 0, 8, 0);
                row.Controls.Add(button);
            }
            return row;
        }

        private static Label Cell(string text, Color color, Font font, bool right = false)
        {
            return new Label
            {
                Text = text,
                ForeColor = color,
                Font = font,
                BackColor = Color.Transparent,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                TextAlign = right ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
        }

        private void BuildLogPage()
        {
            var (_, stack) = NewPage();

            var card = new Card();
            card.AddTitle("运行日志", "同时写入 " + AppLog.FilePath);

            _logBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(18, 18, 20),
                ForeColor = Theme.Text2,
                Font = Theme.Mono,
                Dock = DockStyle.Top,
                Height = 380,
                WordWrap = false,
            };
            card.AddRow(_logBox, 10);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 12, 0, 0),
                BackColor = Theme.Card,
            };

            var clear = new PillButton { Text = "清空显示", Size = new Size(90, 30) };
            clear.MakeSecondary();
            clear.Click += (_, _) =>
            {
                _logText.Clear();
                if (_logBox != null) _logBox.Text = string.Empty;
            };
            buttons.Controls.Add(clear);

            var open = new PillButton { Text = "打开日志文件", Size = new Size(110, 30) };
            open.MakeSecondary();
            open.Click += (_, _) => OpenLogFolder();
            buttons.Controls.Add(open);

            card.AddRow(buttons, 0);
            AddCard(stack, card);

            RenderLog();
        }

        private void RenderLog()
        {
            if (_logBox == null) return;
            _logBox.Text = _logText.ToString();
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }

        private void OnLogAppended(string line) => _logQueue.Enqueue(line);

        private void FlushLog()
        {
            if (_logQueue.IsEmpty) return;

            bool added = false;
            while (_logQueue.TryDequeue(out string? line))
            {
                _logText.AppendLine(line);
                added = true;
            }
            if (!added) return;

            // 只保留最近 1200 行，避免长跑后越来越卡
            if (_logText.Length > 200_000)
            {
                string all = _logText.ToString();
                int cut = all.IndexOf('\n', Math.Max(0, all.Length - 120_000));
                _logText.Clear();
                if (cut >= 0) _logText.Append(all.Substring(cut + 1));
            }

            RenderLog();
        }

        // ================================================================ 数据流

        private async Task RefreshAsync()
        {
            if (_detecting) return;
            _detecting = true;

            SetBusy(true, "正在检测显示器…");
            CloseController();

            try
            {
                string? previousKey = _selected?.SnapshotKey;
                var monitors = await Task.Run(() => MonitorDetector.DetectAll());

                _monitors = monitors;

                if (monitors.Count == 0)
                {
                    _selected = null;
                    _monitorPicker.SetChoices(Array.Empty<Choice>());
                    _monitorPicker.Enabled = false;
                    _statusDot.Set(Theme.Red, "未检测到显示器");
                    SetBusy(false, "未检测到任何显示器");
                    RebuildPages();
                    return;
                }

                _monitorPicker.Enabled = true;
                _monitorPicker.SetChoices(
                    monitors.Select((m, i) => new Choice($"{i + 1}. {m.DisplayLabel}", i)),
                    select: 0);

                var target = monitors.FirstOrDefault(m => m.SnapshotKey == previousKey)
                             ?? monitors.FirstOrDefault(m => m.IsPrimary)
                             ?? monitors[0];

                await SelectMonitorAsync(target, rebuild: true);
            }
            catch (Exception ex)
            {
                AppLog.Exception("检测显示器", ex);
                _statusDot.Set(Theme.Red, "检测失败");
                SetBusy(false, "检测失败：" + ex.Message);
            }
            finally
            {
                _detecting = false;
            }
        }

        private async Task SelectMonitorAsync(MonitorInfo monitor, bool rebuild = true)
        {
            CloseController();
            _selected = monitor;

            int index = _monitors.IndexOf(monitor);
            if (index >= 0) _monitorPicker.Select(index, silent: true);

            if (rebuild) RebuildPages();

            // 首次见到这块屏：把当前值存成初始快照，之后随时可回退
            var existing = SnapshotStore.Get(monitor.SnapshotKey);
            if (existing == null)
            {
                var auto = MonitorSnapshot.From(monitor);
                if (auto.Values.Count > 0)
                {
                    SnapshotStore.Save(auto);
                    if (rebuild) RebuildPages(); // 让快照区显示出来
                }
            }
            else if (existing.Version < MonitorSnapshot.CurrentVersion)
            {
                // 老版本快照缺项（例如后来才加入的黑电平）：用当前值补齐，不动已有项
                int before = existing.Values.Count;
                foreach (var feature in monitor.VcpFeatures.Values)
                {
                    if (!feature.Supported || feature.Current is not int current) continue;
                    if (!VcpCodes.PrimaryVcps.Contains(feature.Code)) continue;
                    if (feature.Code == VcpCodes.InputSource) continue;
                    existing.Values.TryAdd(feature.Code.ToString("X2"), current);
                }
                existing.Version = MonitorSnapshot.CurrentVersion;
                SnapshotStore.Save(existing);
                AppLog.Warn($"快照版本升级：{before} → {existing.Values.Count} 项");
                if (rebuild) RebuildPages();
            }

            _statusDot.Set(
                monitor.DdcSupported ? Theme.Green : Theme.Amber,
                monitor.DdcSupported ? "DDC/CI 已连接" : "DDC/CI 不可用");

            SetBusy(false, $"{monitor.DisplayLabel} · {monitor.ResolutionLabel} @ {monitor.RefreshLabel}");
            await Task.CompletedTask;
        }

        private void CloseController()
        {
            if (_controller == null) return;
            var ctrl = _controller;
            _controller = null;
            try { ctrl.Dispose(); } catch { }
        }

        private async Task<MonitorController?> EnsureControllerAsync()
        {
            if (_selected == null) return null;
            if (_controller != null && _controller.IsOpen) return _controller;

            var monitor = _selected;
            var ctrl = await Task.Run(() =>
            {
                var c = new MonitorController(monitor);
                c.Open();
                return c;
            });

            if (!ReferenceEquals(_selected, monitor))
            {
                ctrl.Dispose();
                return null;
            }

            _controller = ctrl;
            return ctrl;
        }

        // ================================================================ 写入

        private void Kick(byte code)
        {
            if (!_debounces.TryGetValue(code, out var debounce))
            {
                debounce = new Debounce(250, () => _ = ApplyFromRowAsync(code));
                _debounces[code] = debounce;
            }
            debounce.Kick();
        }

        private SliderRow? RowFor(byte code) => code switch
        {
            VcpCodes.Brightness => _brightnessRow,
            VcpCodes.Contrast => _contrastRow,
            VcpCodes.AudioVolume => _volumeRow,
            VcpCodes.Sharpness => _sharpnessRow,
            VcpCodes.RedGain => _redRow,
            VcpCodes.GreenGain => _greenRow,
            VcpCodes.BlueGain => _blueRow,
            _ => null,
        };

        private async Task ApplyFromRowAsync(byte code)
        {
            var row = RowFor(code);
            var monitor = _selected;
            if (row == null || monitor == null) return;

            int value = row.Slider.Value;
            SetHeader(row, value.ToString(), Theme.Amber);

            await _ddcLock.WaitAsync();
            try
            {
                // 期间用户又拖了：这次结果已经过期，丢弃
                if (row.IsDisposed || row.Slider.Value != value) return;

                var ctrl = await EnsureControllerAsync();
                if (ctrl == null) return;

                var result = await Task.Run(() => ctrl.SetAndVerify(code, value));
                if (!ReferenceEquals(_selected, monitor)) return;

                SetHeader(row, result.Success ? value.ToString() : $"{value} ✕",
                    result.Success ? Theme.Text : Theme.Red);
                _statusLabel.Text = result.Describe();
            }
            catch (Exception ex)
            {
                AppLog.Exception($"写入 {VcpCodes.GetName(code)}", ex);
                SetHeader(row, "失败", Theme.Red);
                _statusLabel.Text = "写入失败：" + ex.Message;
            }
            finally
            {
                _ddcLock.Release();
            }
        }

        private static void SetHeader(SliderRow row, string text, Color color)
        {
            if (row.IsDisposed || row.Header.IsDisposed) return;
            row.Header.Value = text;
            row.Header.ValueColor = color;
        }

        private async Task ApplyColorTemperatureAsync()
        {
            var monitor = _selected;
            var picker = _tempPicker;
            if (monitor == null || picker == null) return;

            await _ddcLock.WaitAsync();
            try
            {
                var ctrl = await EnsureControllerAsync();
                if (ctrl == null) return;

                int value = picker.SelectedValue;
                var result = await Task.Run(() => ctrl.SetAndVerify(VcpCodes.ColorTemperature, value));
                _statusLabel.Text = result.Describe();

                if (!result.Success)
                {
                    MessageBox.Show(this, result.Describe(), "色温设置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                AppLog.Exception("设置色温", ex);
                MessageBox.Show(this, ex.Message, "色温设置失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _ddcLock.Release();
            }
        }

        private async Task ResetGainsAsync()
        {
            var monitor = _selected;
            if (monitor == null) return;

            await _ddcLock.WaitAsync();
            try
            {
                var ctrl = await EnsureControllerAsync();
                if (ctrl == null) return;

                foreach (var (code, row) in new[]
                         {
                             (VcpCodes.RedGain, _redRow),
                             (VcpCodes.GreenGain, _greenRow),
                             (VcpCodes.BlueGain, _blueRow),
                         })
                {
                    if (row == null || !monitor.Supports(code)) continue;
                    int max = row.Slider.Maximum;
                    var result = await Task.Run(() => ctrl.SetAndVerify(code, max));
                    if (result.Success)
                    {
                        row.Slider.SetValueSilently(max);
                        row.Header.Value = max.ToString();
                        row.Header.ValueColor = Theme.Text;
                    }
                    _statusLabel.Text = result.Describe();
                }
            }
            finally
            {
                _ddcLock.Release();
            }
        }

        private async Task SwitchInputAsync()
        {
            var monitor = _selected;
            var picker = _inputPicker;
            if (monitor == null || picker == null) return;

            var confirm = MessageBox.Show(
                this,
                $"确定要把「{monitor.DisplayLabel}」切换到 {picker.SelectedText} 吗？\n\n"
                + "如果该接口没有信号，屏幕会一直黑着，需要用显示器上的物理按钮切回来。",
                "切换输入源",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.OK) return;

            await _ddcLock.WaitAsync();
            try
            {
                var ctrl = await EnsureControllerAsync();
                if (ctrl == null) return;

                var result = await Task.Run(() => ctrl.SetRaw(VcpCodes.InputSource, (uint)picker.SelectedValue));
                _statusLabel.Text = result.Describe();
                if (!result.Success)
                    MessageBox.Show(this, result.Describe(), "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _ddcLock.Release();
            }

            // 信号重新握手后再刷新一次
            await Task.Delay(2500);
            await RefreshAsync();
        }

        private async Task SaveSnapshotAsync()
        {
            var monitor = _selected;
            if (monitor == null) return;

            var snapshot = MonitorSnapshot.From(monitor);

            // 用控制器再读一遍真实值，避免把检测时的旧数据存进快照
            await _ddcLock.WaitAsync();
            try
            {
                var ctrl = await EnsureControllerAsync();
                if (ctrl != null)
                {
                    foreach (byte code in snapshot.ToCodes().Keys.ToList())
                    {
                        int? current = await Task.Run(() => ctrl.Read(code));
                        if (current.HasValue) snapshot.Values[code.ToString("X2")] = current.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Exception("读取当前值", ex);
            }
            finally
            {
                _ddcLock.Release();
            }

            if (snapshot.Values.Count == 0)
            {
                MessageBox.Show(this, "没有可保存的 VCP 值。", "保存快照", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SnapshotStore.Save(snapshot);
            _statusLabel.Text = "已保存快照：" + snapshot.Describe();
            RebuildPages();
        }

        private async Task RestoreSnapshotAsync()
        {
            var monitor = _selected;
            if (monitor == null) return;

            var snapshot = SnapshotStore.Get(monitor.SnapshotKey);
            if (snapshot == null || snapshot.Values.Count == 0) return;

            var confirm = MessageBox.Show(
                this,
                $"将把 {snapshot.Values.Count} 项设置还原到 {snapshot.TakenAt:yyyy-MM-dd HH:mm} 的状态。\n\n继续吗？",
                "还原快照",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.OK) return;

            await _ddcLock.WaitAsync();
            try
            {
                var ctrl = await EnsureControllerAsync();
                if (ctrl == null) return;

                var codes = snapshot.ToCodes();
                var results = await Task.Run(() => ctrl.Restore(codes));

                int ok = results.Count(r => r.Success);
                _statusLabel.Text = $"已还原 {ok}/{results.Count} 项";

                // 界面数值回填
                foreach (var result in results.Where(r => r.Success && r.After.HasValue))
                {
                    var row = RowFor(result.Code);
                    if (row == null) continue;
                    row.Slider.SetValueSilently(result.After!.Value);
                    row.Header.Value = result.After.Value.ToString();
                    row.Header.ValueColor = Theme.Text;
                }

                if (ok < results.Count)
                {
                    string detail = string.Join("\n", results.Where(r => !r.Success).Select(r => "· " + r.Describe()));
                    MessageBox.Show(this, "部分项目未能还原：\n\n" + detail, "还原快照", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _ddcLock.Release();
            }
        }

        private async Task FactoryResetAsync()
        {
            var monitor = _selected;
            if (monitor == null) return;

            var confirm = MessageBox.Show(
                this,
                $"确定要把「{monitor.DisplayLabel}」恢复出厂设置吗？\n\n"
                + "所有图像设置都会被重置，包括亮度、对比度、色温和 RGB 增益。\n"
                + "如果之前存过快照，之后可以用「还原到快照」恢复。",
                "恢复出厂设置",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes) return;

            await _ddcLock.WaitAsync();
            try
            {
                var ctrl = await EnsureControllerAsync();
                if (ctrl == null) return;

                var result = await Task.Run(() => ctrl.FactoryReset());
                _statusLabel.Text = result.Describe();

                if (result.Success)
                    MessageBox.Show(this, "命令已发送，显示器可能会闪一下。", "完成",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, result.Describe(), "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _ddcLock.Release();
            }

            await Task.Delay(2000);
            await RefreshAsync();
        }

        // ================================================================ 杂项

        private void SetBusy(bool busy, string status)
        {
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            _statusLabel.Text = status;
            if (busy) _statusDot.Set(Theme.Amber, "工作中…");
            UseWaitCursor = busy;
        }

        private void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(AppLog.Directory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppLog.Directory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法打开目录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5)
            {
                e.Handled = true;
                _ = RefreshAsync();
            }
            else if (e.Control && e.KeyCode == Keys.D1) ShowPage(PageImage);
            else if (e.Control && e.KeyCode == Keys.D2) ShowPage(PageColor);
            else if (e.Control && e.KeyCode == Keys.D3) ShowPage(PageGame);
            else if (e.Control && e.KeyCode == Keys.D4) ShowPage(PageInput);
            else if (e.Control && e.KeyCode == Keys.D5) ShowPage(PageDiag);
            else if (e.Control && e.KeyCode == Keys.D6) ShowPage(PageLog);
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            AppLog.Appended -= OnLogAppended;
            _logTimer.Stop();
            foreach (var debounce in _debounces.Values) debounce.Dispose();
            _debounces.Clear();

            // GPU 层必须还原，否则退出后桌面会一直带着调色
            try
            {
                if (_selected != null && GammaState.HasBaseline(_selected.DeviceName))
                    GammaState.Restore(_selected.DeviceName);
                ColorMatrix.Shutdown();
            }
            catch (Exception ex)
            {
                AppLog.Exception("退出时还原 GPU 调色", ex);
            }

            _watcher?.Dispose();
            CloseController();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _watcher?.Dispose();
                _watcher = null;
                _gameExitTimer.Dispose();
                _logTimer.Dispose();
                _ddcLock.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryEnableDarkTitleBar();
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void TryEnableDarkTitleBar()
        {
            try
            {
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+)，19 = 更早的预览值
                if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
            }
            catch
            {
                // 老系统没有这个属性，保持默认标题栏
            }
        }
    }

    /// <summary>防抖：滑块停下来 250ms 才真正写 DDC，避免拖动时刷几十次 EEPROM。</summary>
    internal sealed class Debounce : IDisposable
    {
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Action _action;

        public Debounce(int intervalMs, Action action)
        {
            _action = action;
            _timer = new System.Windows.Forms.Timer { Interval = intervalMs };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                _action();
            };
        }

        public void Kick()
        {
            _timer.Stop();
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
