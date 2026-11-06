using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Pace;

/// Notification-area icon. The tray can't show text, so the icon itself carries the session %
/// and the tooltip carries the session/week summary.
public sealed class Tray : IDisposable
{
    readonly NotifyIcon _icon = new() { Visible = false };
    Icon? _current;
    string _drawn = "";

    public Tray(Action toggle, Action refresh, Action quit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Pace", null, (_, _) => toggle());
        menu.Items.Add("Refresh now", null, (_, _) => refresh());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Pace", null, (_, _) => quit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) toggle(); };
        _icon.Visible = true;
    }

    public void Update(double? sessionPercent, bool showPercent, bool stale, string tooltip)
    {
        // NotifyIcon.Text is capped at 127 chars.
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        var size = SystemInformation.SmallIconSize.Width;
        var light = IsTaskbarLight();
        var key = $"{sessionPercent:0}|{showPercent}|{stale}|{light}|{size}";
        if (key == _drawn) return;
        _drawn = key;

        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            if (showPercent && sessionPercent is { } p) DrawPercent(g, size, p, stale);
            else DrawGauge(g, size, sessionPercent is { } q ? q / 100 : 0, tile: false, light);
        }
        var h = bmp.GetHicon();
        var next = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        _icon.Icon = next;
        _current?.Dispose();
        _current = next;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }

    // Palette: orange fill, cream needle, warm dark tile.
    static readonly Color Orange = Color.FromArgb(0xE3, 0x78, 0x4C);
    static readonly Color Cream = Color.FromArgb(0xF5, 0xEF, 0xE6);

    static Color Severity(double pct) => pct >= 90 ? Color.FromArgb(0xFF, 0x45, 0x3A) : pct >= 70 ? Color.FromArgb(0xFF, 0x9F, 0x0A) : Orange;

    /// Solid rounded badge in the severity color with the number scaled to fill it: readable at 16 px
    /// on both dark and light taskbars. Gray badge when the number is stale.
    static void DrawPercent(Graphics g, int size, double pct, bool stale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = stale ? Color.FromArgb(0x6E, 0x6E, 0x73) : Severity(pct);
        // 1 px inset so the badge doesn't touch neighbouring tray icons.
        var inset = Math.Max(1, size / 16f);
        using (var badge = RoundRect(inset, inset, size - 2 * inset, size - 2 * inset, size * 0.2f))
        using (var b = new SolidBrush(bg))
            g.FillPath(b, badge);

        // Text as a path so it can be scaled to the exact box (DrawString leaves large font padding).
        var text = ((int)Math.Clamp(pct, 0, 100)).ToString();
        using var path = new GraphicsPath();
        using var family = new FontFamily("Segoe UI");
        path.AddString(text, family, (int)System.Drawing.FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
        var r = path.GetBounds();
        // Padding inside the badge: digits fill ~60% of it, not edge to edge.
        float boxW = size * (text.Length > 2 ? 0.74f : text.Length > 1 ? 0.6f : 0.32f), boxH = size * 0.5f;
        var k = Math.Min(boxW / r.Width, boxH / r.Height);
        using var m = new Matrix();
        m.Translate(size / 2f, size / 2f);
        m.Scale(k, k);
        m.Translate(-(r.X + r.Width / 2), -(r.Y + r.Height / 2));
        path.Transform(m);
        // Dark text on amber (white on it is low-contrast), white otherwise.
        using var fg = new SolidBrush(!stale && pct is >= 70 and < 90 ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White);
        g.FillPath(fg, path);
    }

    /// 240° gauge track, filled arc, needle and hub, optionally on a rounded tile.
    /// Drawn in a 1024 canvas, y-down; the arc starts at 150° and sweeps clockwise.
    public static void DrawGauge(Graphics g, int size, double fill, bool tile, bool lightTaskbar = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(size / 1024f, size / 1024f);
        fill = Math.Clamp(fill, 0, 1);
        float cx = 512, cy = 554, r = 255;
        float stroke = 74;
        var track = Color.FromArgb(26, 255, 255, 255);
        var needle = Cream;
        if (tile)
        {
            using var path = RoundRect(100, 100, 824, 824, 185);
            using var shadow = new SolidBrush(Color.FromArgb(0x1E, 0x1A, 0x18));
            g.TranslateTransform(0, 10); g.FillPath(shadow, path); g.TranslateTransform(0, -10);
            using var bg = new SolidBrush(Color.FromArgb(0x2A, 0x24, 0x21));
            g.FillPath(bg, path);
        }
        else
        {
            // Tray glyph: no tile, so scale the gauge up and use the taskbar's foreground for contrast.
            cx = 512; cy = 590; r = 380; stroke = 150;
            needle = lightTaskbar ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
            track = lightTaskbar ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(80, 255, 255, 255);
        }
        var rect = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
        using (var p = new Pen(track, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(p, rect, 150, 240);
        if (fill > 0.005)
            using (var p = new Pen(tile ? Orange : Severity(fill * 100), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(p, rect, 150, (float)(240 * fill));
        var end = (150 + 240 * fill) * Math.PI / 180;
        var len = r - (tile ? 95 : 150);
        using (var p = new Pen(needle, tile ? 30 : 70) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(p, cx, cy, cx + (float)(Math.Cos(end) * len), cy + (float)(Math.Sin(end) * len));
        float hub = tile ? 44 : 90;
        using var hb = new SolidBrush(needle);
        g.FillEllipse(hb, cx - hub, cy - hub, 2 * hub, 2 * hub);
        g.ResetTransform();
    }

    static GraphicsPath RoundRect(float x, float y, float w, float h, float rad)
    {
        var p = new GraphicsPath();
        var d = rad * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static bool IsTaskbarLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is 1;
    }

    /// Writes the app icon (tile + 65% gauge) as a multi-size PNG-compressed .ico.
    /// Used once by `Pace.exe --export-icon <path>` to produce Assets\Pace.ico.
    public static void ExportIcon(string path)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var pngs = sizes.Select(s =>
        {
            using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) DrawGauge(g, s, 0.65, tile: true);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] % 256)); w.Write((byte)(sizes[i] % 256)); w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
}
