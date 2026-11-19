using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static MultiBar.Native;

namespace MultiBar;

/// <summary>
/// Live window previews shown when hovering a bar button, like the Windows 11 taskbar.
/// The previews are DWM thumbnails, so they are live and cost nothing to draw.
/// </summary>
public static class ThumbnailPopup
{
    static ThumbWindow? _window;
    static BarButton? _pending;
    static bool _pendingTop;
    static readonly DispatcherTimer ShowTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    static readonly DispatcherTimer HideTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    static ThumbnailPopup()
    {
        ShowTimer.Tick += (_, _) => { ShowTimer.Stop(); if (_pending != null) Show(_pending, _pendingTop); };
        HideTimer.Tick += (_, _) => HideNow();
    }

    static bool Visible => _window?.IsVisible == true;

    public static void HoverButton(BarButton b, bool barOnTop)
    {
        HideTimer.Stop();
        if (!b.Entry.Windows.Any(w => IsWindow(w.Hwnd))) { if (Visible) HideNow(); return; }
        _pending = b;
        _pendingTop = barOnTop;
        if (Visible) { ShowTimer.Stop(); Show(b, barOnTop); }   // moving along the bar: switch at once
        else { ShowTimer.Stop(); ShowTimer.Start(); }
    }

    public static void LeaveButton()
    {
        ShowTimer.Stop();
        _pending = null;
        if (Visible) { HideTimer.Stop(); HideTimer.Start(); }
    }

    internal static void EnterPopup() => HideTimer.Stop();
    internal static void LeavePopup() { HideTimer.Stop(); HideTimer.Start(); }

    public static void HideNow()
    {
        ShowTimer.Stop();
        HideTimer.Stop();
        _pending = null;
        _window?.HidePreviews();
    }

    static void Show(BarButton b, bool barOnTop)
    {
        var wins = b.Entry.Windows.Where(w => IsWindow(w.Hwnd)).Take(8).ToList();
        if (wins.Count == 0) { HideNow(); return; }
        _window ??= new ThumbWindow();
        _window.ShowFor(b, wins, barOnTop);
    }
}

internal sealed class ThumbWindow : Window
{
    const double MaxThumbW = 220, MaxThumbH = 130, MinCardW = 170;

    readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal };
    readonly List<(IntPtr thumb, Border slot)> _thumbs = new();
    IntPtr _hwnd;

    public ThumbWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000; Top = -32000;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        Content = new Border { Padding = new Thickness(6), Child = _cards };
        MouseEnter += (_, _) => ThumbnailPopup.EnterPopup();
        MouseLeave += (_, _) => ThumbnailPopup.LeavePopup();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ExStyle(_hwnd) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(_hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            return IntPtr.Zero;
        });
        int round = 2; DwmSetWindowAttribute(_hwnd, 33, ref round, 4);          // rounded corners
        int dark = Theme.Dark ? 1 : 0; DwmSetWindowAttribute(_hwnd, 20, ref dark, 4);
    }

    public void ShowFor(BarButton button, List<TrackedWindow> wins, bool barOnTop)
    {
        Background = (Brush)Application.Current.Resources["Menu.Background"];
        ClearThumbs();
        _cards.Children.Clear();

        if (!IsVisible) Show();
        var thumbs = new List<(IntPtr thumb, Size size, TrackedWindow w)>();
        foreach (var w in wins)
        {
            if (DwmRegisterThumbnail(_hwnd, w.Hwnd, out var t) != 0) continue;
            DwmQueryThumbnailSourceSize(t, out var src);
            thumbs.Add((t, new Size(src.cx, src.cy), w));
        }

        foreach (var (thumb, src, w) in thumbs)
        {
            // Minimized windows sometimes only expose a sliver; show the app icon instead.
            bool usable = src.Width > 50 && src.Height > 50;
            double scale = usable ? Math.Min(MaxThumbW / src.Width, MaxThumbH / src.Height) : 1;
            var slot = new Border
            {
                Width = usable ? Math.Max(40, src.Width * scale) : MaxThumbW * 0.6,
                Height = usable ? Math.Max(30, src.Height * scale) : MaxThumbH * 0.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            };
            if (!usable)
            {
                slot.Child = new Image { Source = Icons.For(w, 64), Width = 40, Height = 40 };
                DwmUnregisterThumbnail(thumb);
            }
            else _thumbs.Add((thumb, slot));
            _cards.Children.Add(Card(w, slot, button));
        }

        UpdateLayout();
        Place(button, barOnTop);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, UpdateThumbRects);
    }

    Border Card(TrackedWindow w, Border slot, BarButton button)
    {
        var icon = new Image { Source = Icons.For(w, 32), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(w.Title) ? button.Entry.Name : w.Title,
            Foreground = Theme.Foreground, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(4), Background = Brushes.Transparent,
            Child = PlateButton.Glyph("", 10), Visibility = Visibility.Hidden, Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Fechar janela",
        };
        close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; WindowOps.Close(w.Hwnd); ThumbnailPopup.HideNow(); };

        var header = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(icon);
        header.Children.Add(close);
        header.Children.Add(title);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(slot);

        var card = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 8), Margin = new Thickness(2, 0, 2, 0),
            Background = Brushes.Transparent, MinWidth = MinCardW, Child = stack,
        };
        card.Width = Math.Max(MinCardW, slot.Width + 16);
        IntPtr fgAtDown = IntPtr.Zero;
        card.MouseEnter += (_, _) => { card.Background = Theme.Hover; close.Visibility = Visibility.Visible; };
        card.MouseLeave += (_, _) => { card.Background = Brushes.Transparent; close.Visibility = Visibility.Hidden; };
        card.PreviewMouseDown += (_, _) => fgAtDown = App.Instance.Tracker.Foreground;
        card.MouseUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Middle) WindowOps.Close(w.Hwnd);
            else if (e.ChangedButton == MouseButton.Left)
            {
                if (w.Hwnd == fgAtDown && !IsIconic(w.Hwnd)) WindowOps.Minimize(w.Hwnd);
                else WindowOps.Activate(w.Hwnd);
            }
            else return;
            ThumbnailPopup.HideNow();
        };
        return card;
    }

    void Place(BarButton button, bool barOnTop)
    {
        var dpi = VisualTreeHelper.GetDpi(button);
        var topLeft = button.PointToScreen(new Point(0, 0));
        double bw = button.ActualWidth * dpi.DpiScaleX, bh = button.ActualHeight * dpi.DpiScaleY;
        var content = (FrameworkElement)Content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int w = (int)Math.Ceiling(content.DesiredSize.Width * dpi.DpiScaleX), h = (int)Math.Ceiling(content.DesiredSize.Height * dpi.DpiScaleY);
        int gap = (int)(10 * dpi.DpiScaleY);

        var hmon = MonitorFromWindow(new WindowInteropHelper(Window.GetWindow(button)!).Handle, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        GetMonitorInfo(hmon, ref mi);
        var mb = mi.rcMonitor;

        int x = (int)(topLeft.X + bw / 2 - w / 2.0);
        x = Math.Clamp(x, mb.Left + 8, Math.Max(mb.Left + 8, mb.Right - w - 8));
        int y = barOnTop ? (int)(topLeft.Y + bh + gap) : (int)(topLeft.Y - h - gap);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    void UpdateThumbRects()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        foreach (var (thumb, slot) in _thumbs)
        {
            if (!slot.IsLoaded) continue;
            var p = slot.TranslatePoint(new Point(0, 0), this);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
                rcDestination = new RECT(
                    (int)Math.Round(p.X * dpi.DpiScaleX), (int)Math.Round(p.Y * dpi.DpiScaleY),
                    (int)Math.Round((p.X + slot.ActualWidth) * dpi.DpiScaleX), (int)Math.Round((p.Y + slot.ActualHeight) * dpi.DpiScaleY)),
                opacity = 255,
                fVisible = true,
            };
            DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    void ClearThumbs()
    {
        foreach (var (thumb, _) in _thumbs) DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }

    public void HidePreviews()
    {
        ClearThumbs();
        if (IsVisible) Hide();
    }
}
