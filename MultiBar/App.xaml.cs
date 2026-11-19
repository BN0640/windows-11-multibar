using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using static MultiBar.Native;

namespace MultiBar;

public partial class App : Application
{
    public static App Instance => (App)Current;

    Mutex? _single;
    public AppConfig Config { get; private set; } = new();
    public WindowTracker Tracker { get; private set; } = null!;
    public List<BarWindow> Bars { get; } = new();

    /// <summary>Windows whose minimize/restore we caused; their events must not cascade.</summary>
    readonly Dictionary<IntPtr, DateTime> _suppressed = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, "MultiBar.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ex) =>
        {
            Trace.WriteLine(ex.Exception);
            try
            {
                System.IO.Directory.CreateDirectory(AppConfig.Folder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppConfig.Folder, "error.log"), $"{DateTime.Now:s} {ex.Exception}\n\n");
            }
            catch { }
            ex.Handled = true;
        };

        Theme.Load();
        Theme.Watch();
        Theme.Changed += () => { foreach (var b in Bars) b.ApplyTheme(); };

        Config = AppConfig.Load();
        if (Config.Bars.Count == 0)
        {
            var mons = Monitors.All();
            var mon = mons.FirstOrDefault(m => !m.Primary) ?? mons.First();
            Config.Bars.Add(new BarConfig { Monitor = mon.Device });
            SaveConfig();
        }

        Tracker = new WindowTracker(Dispatcher);
        Tracker.Changed += () => { foreach (var b in Bars) b.Refresh(); };
        Tracker.ForegroundTick += () => { foreach (var b in Bars) b.UpdateStates(); };
        Tracker.MinimizeChanged += OnMinimizeChanged;

        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            foreach (var b in Bars) b.Dock();
        });

        // Undo a hidden taskbar left behind by a crash, or hide it now if configured.
        if (Config.HideWindowsTaskbar) ApplyTaskbarHiding();
        else if (Config.SavedTaskbarState != null)
        {
            WindowsTaskbar.Hide(Config.SavedTaskbarState);
            WindowsTaskbar.Restore();
            Config.SavedTaskbarState = null;
            SaveConfig();
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WindowsTaskbar.Restore();
        SessionEnding += (_, _) => WindowsTaskbar.Restore();

        foreach (var bc in Config.Bars) OpenBar(bc);
    }

    void ApplyTaskbarHiding()
    {
        Config.SavedTaskbarState = WindowsTaskbar.Hide(Config.SavedTaskbarState);
        SaveConfig();
    }

    public void SetHideWindowsTaskbar(bool hide)
    {
        Config.HideWindowsTaskbar = hide;
        if (hide) ApplyTaskbarHiding();
        else
        {
            WindowsTaskbar.Restore();
            Config.SavedTaskbarState = null;
            SaveConfig();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { foreach (var b in Bars) b.Dock(); });
    }

    void OpenBar(BarConfig bc)
    {
        var bar = new BarWindow(bc);
        Bars.Add(bar);
        bar.Show();
    }

    public void SaveConfig()
    {
        try { Config.Save(); }
        catch (Exception ex) { MessageBox.Show("Não consegui salvar a configuração.\n\n" + ex.Message, "MultiBar"); }
    }

    public void AddBar(MonitorInfo mon)
    {
        var bc = new BarConfig { Monitor = mon.Device };
        Config.Bars.Add(bc);
        SaveConfig();
        OpenBar(bc);
    }

    public void RemoveBar(BarWindow bar)
    {
        if (Config.Bars.Count <= 1)
        {
            if (MessageBox.Show("Esta é a última barra. Remover e sair do MultiBar?", "MultiBar", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Config.Bars.Remove(bar.Cfg);
            SaveConfig();
            Quit();
            return;
        }
        Config.Bars.Remove(bar.Cfg);
        SaveConfig();
        Bars.Remove(bar);
        bar.Close();
    }

    public void Quit()
    {
        foreach (var b in Bars.ToList()) b.Close();
        Tracker.Dispose();
        if (WindowsTaskbar.Active)
        {
            WindowsTaskbar.Restore();
            Config.SavedTaskbarState = null;
            SaveConfig();
        }
        Shutdown();
    }

    public void RefreshAll() { foreach (var b in Bars) b.Refresh(force: true); }

    /// <summary>Finds an item (and the list that holds it) anywhere in the config.</summary>
    public (List<ItemConfig> list, ItemConfig item, BarConfig bar)? Locate(string id)
    {
        foreach (var bar in Config.Bars)
        {
            foreach (var it in bar.Items)
            {
                if (it.Id == id) return (bar.Items, it, bar);
                if (it.IsGroup)
                    foreach (var m in it.Members)
                        if (m.Id == id) return (it.Members, m, bar);
            }
        }
        return null;
    }

    // ---------------- Groups ----------------

    void Suppress(IntPtr h) => _suppressed[h] = DateTime.UtcNow.AddMilliseconds(1500);

    bool IsSuppressed(IntPtr h)
    {
        if (!_suppressed.TryGetValue(h, out var until)) return false;
        if (DateTime.UtcNow < until) return true;
        _suppressed.Remove(h);
        return false;
    }

    public void GroupClick(ItemConfig group, IntPtr foregroundAtClick)
    {
        var windows = AppMatcher.WindowsOf(group, Tracker.Windows).ToList();
        var missing = group.Members.Where(m => !Tracker.Windows.Any(w => AppMatcher.Matches(m, w))).ToList();

        if (windows.Count == 0)
        {
            foreach (var m in group.Members) Launcher.Launch(m);
            return;
        }

        bool groupInFront = windows.Any(w => w.Hwnd == foregroundAtClick && !w.Minimized);
        bool allVisible = windows.All(w => !w.Minimized);
        if (groupInFront && allVisible && missing.Count == 0)
        {
            MinimizeGroup(group);
            return;
        }

        RestoreGroup(group);
        foreach (var m in missing) Launcher.Launch(m);
    }

    public void MinimizeGroup(ItemConfig group)
    {
        foreach (var w in AppMatcher.WindowsOf(group, Tracker.Windows))
        {
            if (IsIconic(w.Hwnd)) continue;
            Suppress(w.Hwnd);
            WindowOps.Minimize(w.Hwnd);
        }
    }

    public void RestoreGroup(ItemConfig group)
    {
        // Activate in reverse member order so the first member ends up on top.
        var ordered = group.Members
            .SelectMany(m => AppMatcher.WindowsOf(m, Tracker.Windows))
            .DistinctBy(w => w.Hwnd)
            .Reverse()
            .ToList();
        foreach (var w in ordered)
        {
            Suppress(w.Hwnd);
            WindowOps.Activate(w.Hwnd);
        }
    }

    public void CloseGroup(ItemConfig group)
    {
        foreach (var w in AppMatcher.WindowsOf(group, Tracker.Windows)) WindowOps.Close(w.Hwnd);
    }

    IEnumerable<ItemConfig> LinkedGroups() =>
        Config.Bars.SelectMany(b => b.Items).Where(i => i.IsGroup && i.LinkMinimize && i.Members.Count > 1);

    void OnMinimizeChanged(IntPtr hwnd, bool minimized)
    {
        if (IsSuppressed(hwnd)) return;
        var w = Tracker.Get(hwnd);
        if (w == null) return;

        var groups = LinkedGroups().Where(g => AppMatcher.Matches(g, w)).ToList();
        if (groups.Count == 0) return;

        var others = groups
            .SelectMany(g => AppMatcher.WindowsOf(g, Tracker.Windows))
            .Where(o => o.Hwnd != hwnd)
            .DistinctBy(o => o.Hwnd)
            .ToList();
        Suppress(hwnd);

        if (minimized)
        {
            foreach (var o in others.Where(o => !IsIconic(o.Hwnd)))
            {
                Suppress(o.Hwnd);
                WindowOps.Minimize(o.Hwnd);
            }
            return;
        }

        var restored = others.Where(o => IsIconic(o.Hwnd)).ToList();
        foreach (var o in restored)
        {
            Suppress(o.Hwnd);
            ShowWindowAsync(o.Hwnd, SW_SHOWNOACTIVATE);
        }
        // Once the restore animations end, lift the others above unrelated windows
        // and keep the one the user restored on top.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            foreach (var o in others)
                SetWindowPos(o.Hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        };
        timer.Start();
    }
}

