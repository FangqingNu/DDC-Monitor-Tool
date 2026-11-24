using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DdcMonitorTool.UI
{
    /// <summary>圆角分组卡片：macOS 那种"无边框 + 大圆角 + 内部留白"的容器。</summary>
    internal sealed class Card : TableLayoutPanel
    {
        public Card()
        {
            ColumnCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            RowCount = 0;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Top;
            BackColor = Theme.Card;
            Padding = new Padding(18, 15, 18, 16);
            Margin = new Padding(0, 0, 0, 14);
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void AddRow(Control control, int topMargin = 8)
        {
            RowCount++;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Dock = DockStyle.Top;
            control.Margin = new Padding(0, topMargin, 0, 0);
            Controls.Add(control, 0, RowCount - 1);
        }

        public void AddTitle(string title, string? subtitle = null)
        {
            var panel = new Panel
            {
                Height = string.IsNullOrEmpty(subtitle) ? 24 : 44,
                Dock = DockStyle.Top,
                BackColor = Theme.Card,
            };

            var titleLabel = new Label
            {
                Text = title,
                Font = Theme.H2,
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(0, 0),
            };
            panel.Controls.Add(titleLabel);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = new Label
                {
                    Text = subtitle,
                    Font = Theme.Small,
                    ForeColor = Theme.Text3,
                    AutoSize = true,
                    Location = new Point(0, 24),
                };
                panel.Controls.Add(sub);
            }

            AddRow(panel, 0);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRounded(e.Graphics, rect, 14, BackColor);
            Theme.DrawRounded(e.Graphics, rect, 14, Theme.Line);
        }
    }

    /// <summary>左标题 + 右数值的一行。数值颜色可用来表示"未应用/失败"。</summary>
    internal sealed class LabeledValue : Control
    {
        private string _caption = string.Empty;
        private string _value = string.Empty;
        private Color _valueColor = Theme.Text;

        public LabeledValue()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 22;
            Font = Theme.Body;
        }

        public string Caption
        {
            get => _caption;
            set { _caption = value; Invalidate(); }
        }

        public string Value
        {
            get => _value;
            set { _value = value; Invalidate(); }
        }

        public Color ValueColor
        {
            get => _valueColor;
            set { _valueColor = value; Invalidate(); }
        }

        public bool CaptionBold { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            int half = Width / 2;
            var left = new Rectangle(0, 0, half, Height);
            var right = new Rectangle(half, 0, Width - half, Height);

            TextRenderer.DrawText(g, _caption, CaptionBold ? Theme.BodyBold : Font, left, Theme.Text2,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, _value, Font, right, _valueColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>
    /// 细轨滑块。WinForms 自带 TrackBar 是 90 年代观感，且无法深色化，
    /// 所以自绘：4px 轨道 + 圆形滑块，拖动即时回调。
    /// </summary>
    internal sealed class ThinSlider : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _dragging;
        private bool _hover;

        public ThinSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Height = 26;
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public event EventHandler? ValueChanged;

        public int Minimum
        {
            get => _minimum;
            set
            {
                _minimum = value;
                if (_maximum < _minimum) _maximum = _minimum;
                if (_value < _minimum) _value = _minimum;
                Invalidate();
            }
        }

        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(value, _minimum);
                if (_value > _maximum) _value = _maximum;
                Invalidate();
            }
        }

        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, _minimum, _maximum);
                if (clamped == _value) return;
                _value = clamped;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>不触发 ValueChanged 地设置（用于程序回填当前值）。</summary>
        public void SetValueSilently(int value)
        {
            _value = Math.Clamp(value, _minimum, _maximum);
            Invalidate();
        }

        private int ThumbSize => Math.Max(10, this.Scaled(14));
        private int TrackHeight => Math.Max(2, this.Scaled(4));
        private int SidePad => ThumbSize / 2;

        private int ValueToX(int value)
        {
            int span = Math.Max(1, Width - SidePad * 2);
            double t = _maximum > _minimum ? (value - _minimum) / (double)(_maximum - _minimum) : 0;
            return SidePad + (int)Math.Round(span * t);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int cy = Height / 2;
            int trackW = Math.Max(1, Width - SidePad * 2);
            int th = TrackHeight;

            var trackRect = new Rectangle(SidePad, cy - th / 2, trackW, th);
            Theme.FillRounded(g, trackRect, th, Theme.Line);

            int thumbX = ValueToX(_value);
            int fillW = thumbX - SidePad;
            if (fillW > 1)
            {
                var fillRect = new Rectangle(SidePad, cy - th / 2, fillW, th);
                Theme.FillRounded(g, fillRect, th, Enabled ? Theme.Accent : Theme.Text3);
            }

            int ts = ThumbSize;
            var thumbRect = new Rectangle(thumbX - ts / 2, cy - ts / 2, ts, ts);

            if (Focused || _hover || _dragging)
            {
                using var glow = new SolidBrush(Color.FromArgb(40, Theme.Accent));
                g.FillEllipse(glow, thumbRect.X - 4, thumbRect.Y - 4, ts + 8, ts + 8);
            }

            using (var brush = new SolidBrush(Enabled ? Color.FromArgb(245, 245, 247) : Color.FromArgb(120, 120, 128)))
                g.FillEllipse(brush, thumbRect);
            using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0)))
                g.DrawEllipse(pen, thumbRect);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            _dragging = true;
            Capture = true;
            Focus();
            ApplyFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) ApplyFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int step = e.Shift ? 10 : 1;
            switch (e.KeyCode)
            {
                case Keys.Left: Value -= step; e.Handled = true; break;
                case Keys.Right: Value += step; e.Handled = true; break;
                case Keys.Home: Value = _minimum; e.Handled = true; break;
                case Keys.End: Value = _maximum; e.Handled = true; break;
            }
        }

        private void ApplyFromX(int x)
        {
            int span = Math.Max(1, Width - SidePad * 2);
            double t = Math.Clamp((x - SidePad) / (double)span, 0, 1);
            Value = _minimum + (int)Math.Round(t * (_maximum - _minimum));
        }
    }

    /// <summary>标题 + 数值 + 滑块，三件套。</summary>
    internal sealed class SliderRow : Panel
    {
        public LabeledValue Header { get; }
        public ThinSlider Slider { get; }

        public SliderRow(string caption)
        {
            Dock = DockStyle.Top;
            Height = 52;
            Margin = new Padding(0);
            BackColor = Theme.Card;

            Header = new LabeledValue { Caption = caption, Dock = DockStyle.Top, Height = 22 };
            Slider = new ThinSlider { Dock = DockStyle.Top, Height = 26 };

            Controls.Add(Slider);
            Controls.Add(Header);
        }
    }

    /// <summary>圆角按钮。默认实心强调色，也可以做成次级（灰底）或纯文字。</summary>
    internal class PillButton : Button
    {
        private bool _hover;
        private bool _down;

        public Color FillColor { get; set; } = Theme.Accent;
        public Color HoverColor { get; set; } = Theme.AccentHover;
        public Color DownColor { get; set; } = Theme.AccentDown;
        public Color TextColor { get; set; } = Color.White;
        public int Radius { get; set; } = 8;
        public bool Ghost { get; set; }

        public PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent;
            Font = Theme.Body;
            Height = 32;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }

        public void MakeSecondary()
        {
            FillColor = Theme.CardAlt;
            HoverColor = Theme.Hover;
            DownColor = Theme.CardAlt;
            TextColor = Theme.Text;
        }

        public void MakeGhost()
        {
            Ghost = true;
            TextColor = Theme.Accent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            // Button 不支持透明背景，圆角外的区域必须自己用父容器底色铺掉，
            // 否则会露出上次绘制残留。
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            if (Ghost)
            {
                if (_hover || _down)
                    Theme.FillRounded(g, rect, Radius, Theme.CardAlt);
            }
            else
            {
                Color fill = !Enabled ? Color.FromArgb(48, 48, 52)
                    : _down ? DownColor
                    : _hover ? HoverColor
                    : FillColor;
                Theme.FillRounded(g, rect, Radius, fill);
            }

            Color textColor = Enabled ? TextColor : Theme.Text3;
            TextRenderer.DrawText(g, Text, Font, rect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    }

    internal sealed class Choice
    {
        public string Text { get; }
        public int Value { get; }
        public Choice(string text, int value)
        {
            Text = text;
            Value = value;
        }
        public override string ToString() => Text;
    }

    /// <summary>点击弹出深色菜单的选择按钮。比 ComboBox 好控制外观，也不会在深色主题下白一块。</summary>
    internal sealed class DropDownButton : PillButton
    {
        private readonly List<Choice> _choices = new();

        public event EventHandler? SelectionChanged;

        public int SelectedValue { get; private set; }
        public string SelectedText { get; private set; } = "—";

        public DropDownButton()
        {
            MakeSecondary();
            Font = Theme.Body;
            Height = 30;
        }

        public IReadOnlyList<Choice> Choices => _choices;

        public void SetChoices(IEnumerable<Choice> choices, int? select = null)
        {
            _choices.Clear();
            _choices.AddRange(choices);

            if (select.HasValue)
            {
                Select(select.Value, silent: true);
            }
            else if (_choices.Count > 0)
            {
                Select(_choices[0].Value, silent: true);
            }
            else
            {
                SelectedText = "无可用项";
                Text = SelectedText + "  ▾";
                Enabled = false;
                Invalidate();
            }
        }

        public void Select(int value, bool silent = false)
        {
            var match = _choices.Find(c => c.Value == value);
            if (match == null) return;

            bool changed = SelectedValue != value || SelectedText != match.Text;
            SelectedValue = value;
            SelectedText = match.Text;
            Text = match.Text + "  ▾";
            Invalidate();

            if (changed && !silent) SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (_choices.Count == 0) return;

            var menu = new ContextMenuStrip
            {
                Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
                ShowImageMargin = false,
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                Font = Theme.Body,
                Padding = new Padding(0, 4, 0, 4),
            };

            foreach (var choice in _choices)
            {
                var item = new ToolStripMenuItem(choice.Text)
                {
                    ForeColor = choice.Value == SelectedValue ? Theme.Accent : Theme.Text,
                    BackColor = Theme.Card,
                    Padding = new Padding(10, 4, 10, 4),
                };
                int captured = choice.Value;
                item.Click += (_, _) => Select(captured);
                menu.Items.Add(item);
            }

            menu.Show(this, new Point(0, Height + 4));
        }
    }

    /// <summary>侧边栏导航项。选中态是浅灰圆角块，和 macOS 设置一致。</summary>
    internal sealed class NavItem : Control
    {
        private bool _selected;
        private bool _hover;

        public NavItem(string text)
        {
            Text = text;
            Height = 34;
            Dock = DockStyle.Top;
            Cursor = Cursors.Hand;
            Font = Theme.Body;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 2, Width - 1, Height - 5);

            if (_selected)
                Theme.FillRounded(g, rect, 7, Theme.CardAlt);
            else if (_hover)
                Theme.FillRounded(g, rect, 7, Color.FromArgb(30, 30, 34));

            var textRect = new Rectangle(11, rect.Y, rect.Width - 22, rect.Height);
            TextRenderer.DrawText(g, Text, _selected ? Theme.BodyBold : Font, textRect,
                _selected ? Theme.Text : Theme.Text2,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    }

    /// <summary>iOS 风格开关。</summary>
    internal sealed class Switch : Control
    {
        private bool _checked;
        private bool _hover;

        public Switch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(42, 24);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public event EventHandler? CheckedChanged;

        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int h = Math.Min(Height - 2, this.Scaled(24));
            int w = Math.Min(Width - 2, this.Scaled(42));
            int top = (Height - h) / 2;
            var track = new Rectangle((Width - w) / 2, top, w, h);

            Color fill = _checked
                ? (_hover ? Theme.AccentHover : Theme.Accent)
                : (_hover ? Theme.Hover : Color.FromArgb(58, 58, 64));
            Theme.FillRounded(g, track, h, fill);

            int knob = h - this.Scaled(4);
            int left = _checked ? track.Right - knob - this.Scaled(2) : track.Left + this.Scaled(2);
            using (var brush = new SolidBrush(Color.FromArgb(250, 250, 252)))
                g.FillEllipse(brush, left, top + this.Scaled(2), knob, knob);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; }
        }
    }

    /// <summary>左文字 + 右开关的一行。</summary>
    internal sealed class ToggleRow : Panel
    {
        public Switch Toggle { get; }

        public ToggleRow(string caption)
        {
            Dock = DockStyle.Top;
            Height = 30;
            BackColor = Theme.Card;
            Margin = new Padding(0);

            var label = new Label
            {
                Text = caption,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Theme.Text2,
                BackColor = Color.Transparent,
                Font = Theme.Body,
            };

            Toggle = new Switch { Dock = DockStyle.Right, Width = 44 };

            Controls.Add(label);
            Controls.Add(Toggle);
        }
    }

    /// <summary>极简文本输入框（WinForms 没有内置的 InputBox）。</summary>
    internal static class Prompt
    {
        public static string? Show(IWin32Window? owner, string title, string label, string initial = "")
        {
            using var form = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(366, 152),
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                Font = Theme.Body,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
            };

            var caption = new Label
            {
                Text = label,
                Location = new Point(18, 18),
                AutoSize = true,
                ForeColor = Theme.Text2,
                BackColor = Color.Transparent,
            };

            var input = new TextBox
            {
                Text = initial,
                Location = new Point(18, 46),
                Width = 326,
                BackColor = Color.FromArgb(44, 44, 48),
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Theme.Body,
            };

            var ok = new PillButton
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Location = new Point(176, 94),
                Size = new Size(80, 32),
            };

            var cancel = new PillButton
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(264, 94),
                Size = new Size(80, 32),
            };
            cancel.MakeSecondary();

            form.Controls.AddRange(new Control[] { caption, input, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            form.Shown += (_, _) => { input.Focus(); input.SelectAll(); };

            return form.ShowDialog(owner) == DialogResult.OK ? input.Text.Trim() : null;
        }
    }

    /// <summary>发丝分隔线。</summary>
    internal sealed class Separator : Panel
    {
        public Separator()
        {
            Height = 1;
            Dock = DockStyle.Top;
            BackColor = Theme.Line;
            Margin = new Padding(0, 10, 0, 10);
        }
    }

    /// <summary>小圆点 + 文字的可用性提示。</summary>
    internal sealed class StatusDot : Control
    {
        private Color _color = Theme.Text3;
        private string _label = string.Empty;

        public StatusDot()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 18;
            Font = Theme.Small;
        }

        public void Set(Color color, string label)
        {
            _color = color;
            _label = label;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = this.Scaled(7);
            g.FillEllipse(new SolidBrush(_color), 0, (Height - d) / 2, d, d);
            TextRenderer.DrawText(g, _label, Font, new Rectangle(d + 6, 0, Width - d - 6, Height),
                _color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
