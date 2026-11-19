using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using static MultiBar.Native;

namespace MultiBar;

public sealed class TrackedWindow
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; } = "";
    public string? ExePath { get; init; }
    public string? Aumid { get; init; }
    public IntPtr Monitor { get; set; }
    public bool Minimized { get; set; }
    /// <summary>--user-data-dir of Chromium/Electron apps: separates profiles of the same app.</summary>
    public string? DataDir { get; init; }
    /// <summary>Process that really owns the window (the app, not ApplicationFrameHost for Store apps).</summary>
    public uint Pid { get; init; }

    /// <summary>Key that groups windows of the same app.</summary>
    public string AppKey => (Aumid != null ? "aumid:" + Aumid.ToLowerInvariant()
        : ExePath != null ? "exe:" + ExePath.ToLowerInvariant()
        : "hwnd:" + Hwnd) + (DataDir != null ? "|" + DataDir.ToLowerInvariant() : "");
}

/// <summary>Keeps the list of taskbar-worthy top-level windows up to date.</summary>
public sealed class WindowTracker : IDisposable
{
    readonly Dispatcher _dispatcher;
    readonly List<IntPtr> _hooks = new();
    readonly WinEventProc _proc;
    readonly DispatcherTimer _debounce, _poll;
    readonly Dictionary<uint, (string? exe, string? aumid, DateTime at)> _procCache = new();
    readonly Dictionary<IntPtr, string?> _windowAumid = new();
    readonly uint _ownPid = (uint)Environment.ProcessId;

    public IReadOnlyList<TrackedWindow> Windows { get; private set; } = Array.Empty<TrackedWindow>();
    /// <summary>Last foreground window that is not one of ours.</summary>
    public IntPtr Foreground { get; private set; }

    public event Action? Changed;
    /// <summary>(hwnd, minimized) raised when a window starts minimizing or is restored.</summary>
    public event Action<IntPtr, bool>? MinimizeChanged;

    public WindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _proc = OnWinEvent;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => { _debounce!.Stop(); Refresh(); }, dispatcher);
        _debounce.Stop();
        _poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);

        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
        Hook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);

        var fg = GetForegroundWindow();
        if (!IsOwn(fg)) Foreground = fg;
        Refresh();
    }

    void Hook(uint min, uint max)
    {
        var h = SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only

        switch (ev)
        {
            case EVENT_SYSTEM_FOREGROUND:
                if (!IsOwn(hwnd)) Foreground = hwnd;
                break;
            case EVENT_SYSTEM_MINIMIZESTART:
                MinimizeChanged?.Invoke(hwnd, true);
                break;
            case EVENT_SYSTEM_MINIMIZEEND:
                MinimizeChanged?.Invoke(hwnd, false);
                break;
            case EVENT_OBJECT_DESTROY:
                _windowAumid.Remove(hwnd);
                break;
        }
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    bool IsOwn(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == _ownPid;
    }

    public void Refresh()
    {
        var list = new List<TrackedWindow>();
        var shell = GetShellWindow();
        EnumWindows((h, _) =>
        {
            try
            {
                if (h != shell && IsTaskbarWindow(h))
                {
                    var w = Describe(h);
                    if (w != null) list.Add(w);
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        // Keep cached process info small.
        if (_procCache.Count > 500) _procCache.Clear();

        bool changed = list.Count != Windows.Count || list.Where((w, i) =>
        {
            var o = Windows[i];
            return o.Hwnd != w.Hwnd || o.Title != w.Title || o.Monitor != w.Monitor || o.Minimized != w.Minimized;
        }).Any();
        Windows = list;
        if (changed) Changed?.Invoke();
        else ForegroundTick?.Invoke();
    }

    /// <summary>Raised on refreshes where only the foreground may have changed.</summary>
    public event Action? ForegroundTick;

    bool IsTaskbarWindow(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        long ex = ExStyle(h);
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & WS_EX_NOACTIVATE) != 0) return false;
        }
        if (IsCloaked(h)) return false;
        if (GetWindowTextLength(h) == 0) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == _ownPid) return false;
        var cls = GetClass(h);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow") return false;
        return true;
    }

    TrackedWindow? Describe(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        var (exe, procAumid) = ProcessInfo(pid);
        var dataDir = DataDirOf(pid);

        // UWP apps live inside ApplicationFrameHost; the real process owns a child CoreWindow.
        if (exe != null && exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint child = 0;
            EnumChildWindows(h, (c, _) =>
            {
                GetWindowThreadProcessId(c, out uint cp);
                if (cp != pid) { child = cp; return false; }
                return true;
            }, IntPtr.Zero);
            if (child != 0) { (exe, procAumid) = ProcessInfo(child); pid = child; }
            else pid = 0; // frame without its app process (suspended): nothing safe to kill
        }

        var aumid = WindowAumid(h) ?? procAumid;
        return new TrackedWindow
        {
            Hwnd = h,
            Title = GetWindowTitle(h),
            ExePath = exe,
            Aumid = aumid,
            DataDir = dataDir,
            Pid = pid,
            Monitor = Monitors.ForWindow(h),
            Minimized = IsIconic(h),
        };
    }

    string? WindowAumid(IntPtr h)
    {
        if (_windowAumid.TryGetValue(h, out var cached)) return cached;
        string? result = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(h, ref iid, out var store) == 0 && store != null)
            {
                var key = PKEY_AppUserModel_ID;
                if (store.GetValue(ref key, out var pv) == 0)
                {
                    if (pv.vt == 31 /*VT_LPWSTR*/ && pv.p != IntPtr.Zero) result = Marshal.PtrToStringUni(pv.p);
                    PropVariantClear(ref pv);
                }
                Marshal.ReleaseComObject(store);
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(result)) result = null;
        _windowAumid[h] = result;
        return result;
    }

    (string? exe, string? aumid) ProcessInfo(uint pid)
    {
        if (_procCache.TryGetValue(pid, out var c) && (DateTime.UtcNow - c.at).TotalSeconds < 30) return (c.exe, c.aumid);
        string? exe = null, aumid = null;
        var hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hp, 0, sb, ref size)) exe = sb.ToString();
                int len = 256;
                var ab = new StringBuilder(len);
                if (GetApplicationUserModelId(hp, ref len, ab) == 0) aumid = ab.ToString();
            }
            finally { CloseHandle(hp); }
        }
        _procCache[pid] = (exe, aumid, DateTime.UtcNow);
        return (exe, aumid);
    }

    readonly Dictionary<uint, string?> _dataDirCache = new();

    string? DataDirOf(uint pid)
    {
        if (_dataDirCache.TryGetValue(pid, out var cached)) return cached;
        if (_dataDirCache.Count > 500) _dataDirCache.Clear();
        string? dir = null;
        var cmd = CommandLineOf(pid);
        if (cmd != null) dir = AppMatcher.ParseDataDir(cmd);
        _dataDirCache[pid] = dir;
        return dir;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int ret);

    static string? CommandLineOf(uint pid)
    {
        var hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return null;
        var buf = Marshal.AllocHGlobal(65536);
        try
        {
            // ProcessCommandLineInformation (60): UNICODE_STRING followed by the text.
            if (NtQueryInformationProcess(hp, 60, buf, 65536, out _) != 0) return null;
            int lenBytes = Marshal.ReadInt16(buf);
            var text = Marshal.ReadIntPtr(buf, IntPtr.Size);
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, lenBytes / 2);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); CloseHandle(hp); }
    }

    public TrackedWindow? Get(IntPtr hwnd) => Windows.FirstOrDefault(w => w.Hwnd == hwnd);

    public void Dispose()
    {
        foreach (var h in _hooks) UnhookWinEvent(h);
        _hooks.Clear();
        _poll.Stop();
        _debounce.Stop();
    }
}

/// <summary>Decides which windows belong to a pinned item.</summary>
public static class AppMatcher
{
    public static bool Matches(ItemConfig item, TrackedWindow w)
    {
        if (item.IsGroup) return item.Members.Any(m => Matches(m, w));
        // A separate profile (--user-data-dir) is a separate app: "Claude Pessoal" vs the work Claude.
        var itemDir = item.DataDir ?? ParseDataDir(item.Arguments);
        if (!string.Equals(NormalizeDir(itemDir), NormalizeDir(w.DataDir), StringComparison.OrdinalIgnoreCase)) return false;
        // When both sides have an AUMID it decides: web apps (Firefox/Chrome/Edge) share the browser's exe
        // but get their own AUMID, and must not be taken for the browser.
        if (item.Aumid != null && w.Aumid != null)
            return SameApp(item.Aumid, w.Aumid);
        var exe = ExeOf(item);
        return exe != null && w.ExePath != null && string.Equals(exe, w.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Same AUMID, or a variant of it: "Chrome.Profile1", "308046B0AF4A39CB;PrivateBrowsingAUMID".</summary>
    static bool SameApp(string itemAumid, string windowAumid) =>
        string.Equals(itemAumid, windowAumid, StringComparison.OrdinalIgnoreCase)
        || windowAumid.StartsWith(itemAumid + ".", StringComparison.OrdinalIgnoreCase)
        || windowAumid.StartsWith(itemAumid + ";", StringComparison.OrdinalIgnoreCase);

    static readonly System.Text.RegularExpressions.Regex DataDirArg =
        new(@"--user-data-dir=(?:""([^""]+)""|(\S+))", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string? ParseDataDir(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var m = DataDirArg.Match(commandLine);
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    static string? NormalizeDir(string? dir) =>
        string.IsNullOrWhiteSpace(dir) ? null : Environment.ExpandEnvironmentVariables(dir).Trim().TrimEnd('\\', '/');

    public static string? ExeOf(ItemConfig item)
    {
        if (item.MatchPath != null) return item.MatchPath;
        if (item.Path != null && item.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return item.Path;
        return null;
    }

    public static IEnumerable<TrackedWindow> WindowsOf(ItemConfig item, IEnumerable<TrackedWindow> all) =>
        all.Where(w => Matches(item, w));
}
