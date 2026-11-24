using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DdcMonitorTool.UI
{
    /// <summary>深色主题色板与绘图辅助。整体对齐 macOS 分组列表的观感。</summary>
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(11, 11, 13);
        public static readonly Color Sidebar = Color.FromArgb(19, 19, 22);
        public static readonly Color Card = Color.FromArgb(28, 28, 30);
        public static readonly Color CardAlt = Color.FromArgb(38, 38, 42);
        public static readonly Color Hover = Color.FromArgb(46, 46, 51);
        public static readonly Color Line = Color.FromArgb(44, 44, 48);
        public static readonly Color Text = Color.FromArgb(245, 245, 247);
        public static readonly Color Text2 = Color.FromArgb(168, 168, 176);
        public static readonly Color Text3 = Color.FromArgb(120, 120, 128);
        public static readonly Color Accent = Color.FromArgb(10, 132, 255);
        public static readonly Color AccentHover = Color.FromArgb(48, 152, 255);
        public static readonly Color AccentDown = Color.FromArgb(6, 106, 214);
        public static readonly Color Green = Color.FromArgb(48, 209, 88);
        public static readonly Color Amber = Color.FromArgb(255, 159, 10);
        public static readonly Color Red = Color.FromArgb(255, 69, 58);

        public static readonly Font H1 = new("Microsoft YaHei UI", 15.5f, FontStyle.Bold);
        public static readonly Font H2 = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
        public static readonly Font Body = new("Microsoft YaHei UI", 9f);
        public static readonly Font BodyBold = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
        public static readonly Font Small = new("Microsoft YaHei UI", 8.25f);
        public static readonly Font Mono = new("Consolas", 8.5f);

        public static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;

            int r = System.Math.Min(radius, System.Math.Min(rect.Width, rect.Height) / 2);
            if (r <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }

            int d = r * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d - 1, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d - 1, rect.Bottom - d - 1, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d - 1, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Rectangle rect, int radius, Color color)
        {
            using var path = Rounded(rect, radius);
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        public static void DrawRounded(Graphics g, Rectangle rect, int radius, Color color, int thickness = 1)
        {
            using var path = Rounded(rect, radius);
            using var pen = new Pen(color, thickness);
            g.DrawPath(pen, path);
        }

        /// <summary>按 DPI 缩放一个 96dpi 下的逻辑值。</summary>
        public static int Scaled(this Control control, int logical)
            => (int)System.Math.Round(logical * control.DeviceDpi / 96.0);
    }

    /// <summary>ContextMenuStrip 的深色渲染，避免托盘/下拉菜单突然白一块。</summary>
    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color MenuBorder => Theme.Line;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color MenuItemSelected => Theme.CardAlt;
        public override Color MenuItemSelectedGradientBegin => Theme.CardAlt;
        public override Color MenuItemSelectedGradientEnd => Theme.CardAlt;
        public override Color SeparatorDark => Theme.Line;
        public override Color SeparatorLight => Theme.Line;
    }
}
