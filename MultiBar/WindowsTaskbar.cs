using System.Runtime.InteropServices;
using System.Windows.Threading;
using static MultiBar.Native;

namespace MultiBar;

/// <summary>
/// Hides the Windows taskbars while MultiBar runs (auto-hide frees the space, SW_HIDE keeps them from
/// sliding in) and puts everything back on exit. "Peek" shows the main taskbar for a moment so the
/// system tray stays reachable.
/// </summary>
public static class WindowsTaskbar
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1;

    static bool _active;
    static int _originalState = -1;
    static DispatcherTimer? _timer;
    static DateTime _peekStarted;
    static bool _peeking;

    public static bool Active => _active;

    static IntPtr Main => FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);

    static List<IntPtr> All()
    {
        var list = new List<IntPtr>();
        var main = Main;
        if (main != IntPtr.Zero) list.Add(main);
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) list.Add(h);
        return list;
    }

    static int GetState()
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Main };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref abd).ToUInt32();
    }

    static void SetState(int state)
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Main, lParam = new IntPtr(state) };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }

    /// <summary>Hides the taskbars. Returns the taskbar state to restore later (persist it in case MultiBar dies).</summary>
    public static int Hide(int? savedOriginal)
    {
        if (Main == IntPtr.Zero) return savedOriginal ?? 0;
        if (!_active)
        {
            _originalState = savedOriginal ?? GetState();
            _active = true;
        }
        if ((GetState() & ABS_AUTOHIDE) == 0) SetState(_originalState | ABS_AUTOHIDE);
        foreach (var h in All()) ShowWindow(h, 0 /*SW_HIDE*/);

        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Enforce(), Dispatcher.CurrentDispatcher);
        _timer.Start();
        return _originalState;
    }

    /// <summary>Explorer re-shows its taskbar now and then (restart, display change); hide it again.</summary>
    static void Enforce()
    {
        if (!_active) return;
        if (_peeking)
        {
            if ((DateTime.UtcNow - _peekStarted).TotalSeconds < 2) return;
            var main = Main;
            GetCursorPos(out var p);
            GetWindowRect(main, out var r);
            var fg = GetForegroundWindow();
            bool overTaskbar = p.X >= r.Left && p.X < r.Right && p.Y >= r.Top - 4 && p.Y < r.Bottom;
            bool trayFocused = fg == main || GetClass(fg) is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Xaml_WindowedPopupClass";
            if (overTaskbar || trayFocused) return;
            _peeking = false;
        }
        if ((GetState() & ABS_AUTOHIDE) == 0) SetState(_originalState | ABS_AUTOHIDE);
        foreach (var h in All())
            if (IsWindowVisible(h)) ShowWindow(h, 0);
    }

    /// <summary>Shows the main taskbar (system tray) until the mouse leaves it.</summary>
    public static void Peek()
    {
        var main = Main;
        if (main == IntPtr.Zero) return;
        _peeking = true;
        _peekStarted = DateTime.UtcNow;
        ShowWindow(main, 8 /*SW_SHOWNA*/);
        // Win+B moves focus to the notification area, which slides the auto-hidden taskbar in.
        Keys.Chord(0x5B, 0x42);
    }

    public static void Restore()
    {
        if (!_active) return;
        _timer?.Stop();
        _active = false;
        _peeking = false;
        if (_originalState >= 0) SetState(_originalState);
        foreach (var h in All()) ShowWindow(h, 5 /*SW_SHOW*/);
    }
}

public static class Keys
{
    /// <summary>Presses the keys in order and releases them in reverse (e.g. Win+A).</summary>
    public static void Chord(params byte[] vks)
    {
        foreach (var vk in vks) keybd_event(vk, 0, 0, UIntPtr.Zero);
        foreach (var vk in vks.Reverse()) keybd_event(vk, 0, 2, UIntPtr.Zero);
    }
}
