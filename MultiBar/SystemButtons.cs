using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MultiBar;

/// <summary>Plain taskbar button with the Windows 11 hover plate (Start, tray chevron, quick settings).</summary>
public sealed class PlateButton : Grid
{
    readonly Border _plate = new() { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    readonly ScaleTransform _scale = new(1, 1);
    bool _hover;

    public PlateButton(UIElement content, double width, string tooltip, Action onClick)
    {
        Width = width;
        Height = BarButton.ButtonHeight;
        Margin = new Thickness(2, 0, 2, 0);
        Background = Brushes.Transparent;
        var holder = new ContentControl
        {
            Content = content, Focusable = false, IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _scale,
        };
        Children.Add(_plate);
        Children.Add(holder);
        ToolTip = new ToolTip { Content = tooltip, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, VerticalOffset = -4 };
        MouseEnter += (_, _) => { _hover = true; Paint(); };
        MouseLeave += (_, _) => { _hover = false; _scale.ScaleX = _scale.ScaleY = 1; Paint(); };
        PreviewMouseLeftButtonDown += (_, _) => { _scale.ScaleX = _scale.ScaleY = 0.88; _plate.Background = Theme.Pressed; };
        MouseLeftButtonUp += (_, e) => { e.Handled = true; _scale.ScaleX = _scale.ScaleY = 1; Paint(); onClick(); };
        MouseRightButtonUp += (_, e) => e.Handled = false;
        Paint();
    }

    public void Paint()
    {
        _plate.Background = _hover ? Theme.Hover : Brushes.Transparent;
        _plate.BorderBrush = _hover ? Theme.ActiveBorder : Brushes.Transparent;
    }

    static FontFamily Glyphs => new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static TextBlock Glyph(string glyph, double size = 16) => new()
    {
        Text = glyph, FontFamily = Glyphs, FontSize = size, Foreground = Theme.Foreground,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>Windows 11 logo: four squares with the blue gradient.</summary>
    public static UIElement WindowsLogo()
    {
        var geo = new GeometryGroup();
        const double s = 10.4, gap = 1.2;
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 2; x++)
                geo.Children.Add(new RectangleGeometry(new Rect(x * (s + gap), y * (s + gap), s, s), 0.8, 0.8));
        return new Path
        {
            Data = geo,
            Width = 2 * s + gap, Height = 2 * s + gap,
            Fill = new LinearGradientBrush(Color.FromRgb(0x5B, 0xD0, 0xFF), Color.FromRgb(0x0A, 0x6C, 0xD8), new Point(0, 0), new Point(1, 1)),
        };
    }

    /// <summary>Network + volume icons side by side, like the Windows 11 quick settings button.</summary>
    public static UIElement QuickSettingsIcons()
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(Glyph("", 15));                      // Wi-Fi
        var vol = Glyph("", 15); vol.Margin = new Thickness(9, 0, 0, 0);  // Volume
        p.Children.Add(vol);
        return p;
    }
}
