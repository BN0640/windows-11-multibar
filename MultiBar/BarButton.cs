using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MultiBar;

public enum RunState { None, Running, Active }

/// <summary>One entry on the bar: a pinned app, a group or an unpinned running app.</summary>
public sealed class BarEntry
{
    public required string Key { get; init; }
    public ItemConfig? Item { get; init; }          // null for unpinned running apps
    public List<TrackedWindow> Windows { get; set; } = new();
    public string Name { get; set; } = "";
    public bool IsGroup => Item?.IsGroup == true;
}

/// <summary>Windows 11 taskbar button: rounded hover plate, 24px icon, running/active pill.</summary>
public sealed class BarButton : Grid
{
    public const double ButtonWidth = 44, ButtonHeight = 40, IconSize = 24;

    public BarEntry Entry { get; private set; }
    readonly Border _plate;
    readonly Border _pill;
    readonly ContentControl _content;
    readonly ScaleTransform _scale = new(1, 1);
    RunState _state = RunState.None;
    bool _hover, _pressed;

    public BarButton(BarEntry entry)
    {
        Entry = entry;
        Width = ButtonWidth;
        Height = ButtonHeight;
        Margin = new Thickness(2, 0, 2, 0);
        Background = Brushes.Transparent;
        SnapsToDevicePixels = true;

        _plate = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        _content = new ContentControl
        {
            Width = IconSize, Height = IconSize,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _scale,
            Focusable = false, IsTabStop = false,
        };
        _pill = new Border
        {
            Height = 3, Width = 6, CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed,
        };
        Children.Add(_plate);
        Children.Add(_content);
        Children.Add(_pill);

        MouseEnter += (_, _) => { _hover = true; Paint(); };
        MouseLeave += (_, _) => { _hover = false; _pressed = false; SetPressed(false); Paint(); };
        PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Left) { _pressed = true; SetPressed(true); Paint(); } };
        PreviewMouseUp += (_, _) => { _pressed = false; SetPressed(false); Paint(); };

        SetEntry(entry);
    }

    public void SetEntry(BarEntry entry)
    {
        Entry = entry;
        ToolTip = new ToolTip { Content = Tooltip(entry), Placement = System.Windows.Controls.Primitives.PlacementMode.Top, VerticalOffset = -4 };
        ToolTipService.SetInitialShowDelay(this, 500);
        _content.Content = entry.IsGroup ? GroupIcon(entry.Item!) : AppIcon(entry);
        Paint();
    }

    static string Tooltip(BarEntry e)
    {
        if (e.IsGroup)
            return $"{e.Name}\n" + string.Join("\n", e.Item!.Members.Select(m => "  • " + m.Name));
        if (e.Windows.Count == 1 && !string.IsNullOrWhiteSpace(e.Windows[0].Title) && e.Windows[0].Title != e.Name)
            return $"{e.Name} - {e.Windows[0].Title}";
        if (e.Windows.Count > 1) return $"{e.Name} - {e.Windows.Count} janelas";
        return e.Name;
    }

    static int IconPixels => (int)Math.Ceiling(IconSize * 2); // render crisp on high DPI

    static UIElement AppIcon(BarEntry e)
    {
        ImageSource? src = e.Item != null ? Icons.For(e.Item, IconPixels)
            : e.Windows.Count > 0 ? Icons.For(e.Windows[0], IconPixels) : null;
        if (src != null)
        {
            var img = new Image { Source = src, Width = IconSize, Height = IconSize };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }
        return new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 20,
            Foreground = Theme.Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>2x2 grid of member icons on a rounded tile, like a folder.</summary>
    static UIElement GroupIcon(ItemConfig group)
    {
        var tile = new Border { CornerRadius = new CornerRadius(5), Background = Theme.GroupBg, BorderBrush = Theme.ActiveBorder, BorderThickness = new Thickness(1), Padding = new Thickness(2) };
        var grid = new UniformGrid2x2();
        foreach (var m in group.Members.Take(4))
        {
            var src = Icons.For(m, 32);
            var img = new Image { Source = src, Width = 9, Height = 9, Margin = new Thickness(0.5) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            grid.Children.Add(img);
        }
        tile.Child = grid;
        return tile;
    }

    sealed class UniformGrid2x2 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid2x2() { Rows = 2; Columns = 2; }
    }

    public void SetState(RunState state)
    {
        if (_state == state) return;
        var old = _state;
        _state = state;
        // Running apps get the window previews instead of the tooltip.
        ToolTipService.SetIsEnabled(this, state == RunState.None);
        Paint();
        if (state != RunState.None)
        {
            double to = state == RunState.Active ? 16 : 6;
            double from = old == RunState.None ? 0 : _pill.ActualWidth > 0 ? _pill.ActualWidth : to;
            _pill.BeginAnimation(WidthProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(180))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    void SetPressed(bool p)
    {
        double to = p ? 0.85 : 1.0;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(p ? 80 : 160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    public void Paint()
    {
        Brush bg = Brushes.Transparent, border = Brushes.Transparent;
        if (_state == RunState.Active) { bg = Theme.ActiveBg; border = Theme.ActiveBorder; }
        if (_hover) { bg = _pressed ? Theme.Pressed : Theme.Hover; border = Theme.ActiveBorder; }
        _plate.Background = bg;
        _plate.BorderBrush = border;
        _pill.Visibility = _state == RunState.None ? Visibility.Collapsed : Visibility.Visible;
        _pill.Background = _state == RunState.Active ? Theme.AccentBrush : Theme.RunningPill;
    }

    public void ShowDropHint(bool on)
    {
        _plate.BorderBrush = on ? Theme.AccentBrush : Brushes.Transparent;
        if (!on) Paint();
    }
}
