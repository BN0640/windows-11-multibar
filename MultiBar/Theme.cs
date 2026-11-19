using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MultiBar;

/// <summary>Windows 11 taskbar colors, following the system light/dark setting and accent color.</summary>
public static class Theme
{
    public static bool Dark { get; private set; } = true;
    public static Color Accent { get; private set; } = Color.FromRgb(0x4C, 0xC2, 0xFF);

    public static event Action? Changed;

    public static Color BarTint => Dark ? Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1C) : Color.FromArgb(0xD9, 0xEE, 0xEE, 0xEE);
    public static Brush BarTopBorder => B(Dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
    public static Brush Foreground => B(Dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A));
    public static Brush Hover => B(Dark ? Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));
    public static Brush Pressed => B(Dark ? Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x08, 0x00, 0x00, 0x00));
    public static Brush ActiveBg => B(Dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0B, 0x00, 0x00, 0x00));
    public static Brush ActiveBorder => B(Dark ? Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0x00, 0x00, 0x00));
    public static Brush RunningPill => B(Dark ? Color.FromArgb(0x8B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x72, 0x00, 0x00, 0x00));
    public static Brush AccentBrush => B(Accent);
    public static Brush GroupBg => B(Dark ? Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));

    static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static void Load()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("SystemUsesLightTheme") is int light) Dark = light == 0;
        }
        catch { }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 32)
            {
                // Palette: Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3 (RGBA each).
                int i = Dark ? 1 : 4;
                Accent = Color.FromRgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
            }
        }
        catch { }
        ApplyResources();
    }

    public static void ApplyResources()
    {
        var r = Application.Current.Resources;
        r["Menu.Background"] = B(Dark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xF9, 0xF9, 0xF9));
        r["Menu.Border"] = B(Dark ? Color.FromArgb(0x33, 0x00, 0x00, 0x00) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        r["Menu.Foreground"] = Foreground;
        r["Menu.Hover"] = B(Dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
        r["Menu.Disabled"] = B(Dark ? Color.FromArgb(0x5D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x5C, 0x00, 0x00, 0x00));
        r["Menu.Separator"] = B(Dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));
        r["Accent"] = AccentBrush;
        r["Window.Background"] = B(Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
        r["Control.Background"] = B(Dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Colors.White);
        r["Control.Border"] = B(Dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
    }

    public static void Watch()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color) return;
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                bool wasDark = Dark; var accent = Accent;
                Load();
                if (wasDark != Dark || accent != Accent) Changed?.Invoke();
            });
        };
    }
}
