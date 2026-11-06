using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace Pace;

/// Compact flyout anchored to the notification area.
public partial class PopoverWindow : Window
{
    readonly UsageViewModel _vm;
    readonly Action _quit;
    long _hiddenAt;
    System.Drawing.Point _anchor; // cursor position when the popover opened

    public PopoverWindow(UsageViewModel vm, Action quit)
    {
        InitializeComponent();
        _vm = vm; _quit = quit;
        DataContext = vm;
        vm.PropertyChanged += OnVmChanged;
        Deactivated += (_, _) => HidePopover();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HidePopover(); };
    }

    /// Tray click toggles. A click on the tray icon first deactivates (hides) the popover, so ignore a
    /// re-show that lands right after a hide; otherwise the popover would reopen instead of closing.
    public void Toggle()
    {
        if (IsVisible) { HidePopover(); return; }
        if (Environment.TickCount64 - _hiddenAt < 300) return;
        ShowPopover();
    }

    public void ShowPopover()
    {
        Theme.Apply(Resources);
        _vm.Touch(); // re-read startup state (Task Manager can change it) and countdowns
        _anchor = System.Windows.Forms.Cursor.Position;
        Opacity = 0;
        Show();
        UpdateLayout();
        Place();
        Activate();
        Focus();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    void HidePopover()
    {
        if (!IsVisible) return;
        _hiddenAt = Environment.TickCount64;
        Hide();
    }

    /// Next to the tray: near where it was opened along the taskbar, flush with the taskbar edge.
    /// Uses the cursor position captured at open time, so re-placing after a content change
    /// (e.g. clicking Refresh inside the popover) never moves it sideways.
    void Place()
    {
        var cur = _anchor;
        var screen = System.Windows.Forms.Screen.FromPoint(cur);
        var wa = screen.WorkingArea; var b = screen.Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = ActualWidth * dpi.DpiScaleX, h = ActualHeight * dpi.DpiScaleY;

        // Opened from the tray: follow the cursor. Opened otherwise (relaunch, keyboard): use the tray corner.
        var onTaskbar = !wa.Contains(cur);
        double x = onTaskbar ? Math.Clamp(cur.X - w / 2, wa.Left, wa.Right - w) : wa.Right - w;
        double y = wa.Bottom - h;                                       // taskbar at bottom (default)
        double side = onTaskbar ? Math.Clamp(cur.Y - h / 2, wa.Top, wa.Bottom - h) : wa.Bottom - h;
        if (wa.Top > b.Top) y = wa.Top;                                 // top
        else if (wa.Left > b.Left) { x = wa.Left; y = side; }           // left
        else if (wa.Right < b.Right) { x = wa.Right - w; y = side; }    // right
        Left = x / dpi.DpiScaleX;
        Top = y / dpi.DpiScaleY;
    }

    void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        // Subtle spin on the refresh glyph while a request is in flight.
        if (_vm.Loading && Spin.HasAnimatedProperties == false)
            Spin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        else if (!_vm.Loading && Spin.HasAnimatedProperties)
            Spin.BeginAnimation(RotateTransform.AngleProperty, null);
        if (IsVisible) Dispatcher.BeginInvoke(() => { if (IsVisible) Place(); }); // height can change (error line, rows)
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = _vm.RefreshAsync();

    void Web_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true });
        HidePopover();
    }

    void Quit_Click(object sender, RoutedEventArgs e) => _quit();
}

/// Follows Windows' app theme (dark default) and accent color.
static class Theme
{
    public static void Apply(ResourceDictionary r)
    {
        var light = Reg(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme") is 1;
        var accent = AccentColor(light) ?? (light ? Rgb(0x00, 0x7A, 0xFF) : Rgb(0x0A, 0x84, 0xFF)); // blue fallback

        Set(r, "Surface", light ? Rgb(0xF4, 0xF4, 0xF4) : Rgb(0x26, 0x26, 0x28));
        Set(r, "Border", light ? Color.FromArgb(0x26, 0, 0, 0) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        Set(r, "Primary", light ? Color.FromArgb(0xD9, 0, 0, 0) : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        Set(r, "Secondary", light ? Color.FromArgb(0x80, 0, 0, 0) : Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        Set(r, "Separator", light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        Set(r, "Track", light ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        Set(r, "Hover", light ? Color.FromArgb(0x12, 0, 0, 0) : Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
        Set(r, "Field", light ? Colors.White : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        Set(r, "Accent", accent);
        Set(r, "AccentSoft", Color.FromArgb(0x26, accent.R, accent.G, accent.B)); // accentColor.opacity(0.15)
        Set(r, "Orange", light ? Rgb(0xFF, 0x95, 0x00) : Rgb(0xFF, 0x9F, 0x0A));
        Set(r, "Red", light ? Rgb(0xFF, 0x3B, 0x30) : Rgb(0xFF, 0x45, 0x3A));
    }

    /// Windows stores the accent shades in AccentPalette (8 × RGBA): [light3, light2, light1, base, dark1, ...].
    /// Dark mode uses a lighter shade for contrast, light mode a darker one, matching Windows' own controls.
    static Color? AccentColor(bool light) =>
        Reg(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette") is byte[] { Length: >= 32 } p
            ? Rgb(p[(light ? 4 : 1) * 4], p[(light ? 4 : 1) * 4 + 1], p[(light ? 4 : 1) * 4 + 2])
            : null;

    static object? Reg(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name);
    }

    static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
    static void Set(ResourceDictionary r, string key, Color c) { var b = new SolidColorBrush(c); b.Freeze(); r[key] = b; }
}
