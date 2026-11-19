using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using static MultiBar.Native;

namespace MultiBar;

public sealed class BarWindow : Window
{
    public const double BarHeight = 48;
    const string DragFormat = "MultiBar.Entry";

    public BarConfig Cfg { get; }
    App Host => App.Instance;

    IntPtr _hwnd;
    uint _callbackMsg;
    bool _registered, _docking;
    MonitorInfo? _monitor;

    readonly Grid _root = new();
    readonly Border _topLine = new() { Height = 1, VerticalAlignment = VerticalAlignment.Top };
    readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _clock = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    readonly Border _clockPlate = new() { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Height = 40 };
    readonly TextBlock _time = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock _date = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock _hint = new() { FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Text = "Clique com o botão direito ou arraste apps para cá" };
    readonly Border _dropMarker = new() { Width = 2, Height = 28, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    readonly StackPanel _tray = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    PlateButton? _start;
    readonly DispatcherTimer _clockTimer;

    List<BarButton> _buttons = new();
    Point _dragStart;
    BarButton? _dragCandidate;
    bool _dragged;
    IntPtr _fgAtMouseDown;

    public BarWindow(BarConfig cfg)
    {
        Cfg = cfg;
        Title = "MultiBar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Transparent;
        Width = 800; Height = BarHeight;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        AllowDrop = true;
        UseLayoutRounding = true;

        _clock.Children.Add(_time);
        _clock.Children.Add(_date);
        _clockPlate.Child = _clock;
        _clockPlate.MouseEnter += (_, _) => _clockPlate.Background = Theme.Hover;
        _clockPlate.MouseLeave += (_, _) => _clockPlate.Background = Brushes.Transparent;
        _clockPlate.Background = Brushes.Transparent;
        _clockPlate.MouseLeftButtonUp += (_, _) => Keys.Chord(0x5B, 0x4E); // Win+N: notifications + calendar

        _overlay.Children.Add(_dropMarker);
        _root.Children.Add(_items);
        _root.Children.Add(_hint);
        _right.Children.Add(_tray);
        _right.Children.Add(_clockPlate);
        _root.Children.Add(_right);
        _root.Children.Add(_overlay);
        _root.Children.Add(_topLine);
        Content = _root;

        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateClock(), Dispatcher);

        MouseRightButtonUp += OnRightClick;
        DragOver += OnDragOver;
        DragLeave += (_, _) => HideDropHints();
        Drop += OnDrop;

        ApplyLayout();
        ApplyTheme();
        Refresh(force: true);
    }

    // ---------------- Window plumbing / AppBar ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = ExStyle(_hwnd) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        ApplyBackdrop();
        Dock();
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        Unregister();
        base.OnClosed(e);
    }

    void Unregister()
    {
        if (!_registered) return;
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
    }

    public void Dock()
    {
        if (_hwnd == IntPtr.Zero || _docking) return;
        _docking = true;
        try
        {
            _monitor = Monitors.Find(Cfg.Monitor);
            if (_monitor == null)
            {
                // Monitor disconnected: hide until it comes back.
                Unregister();
                Hide();
                return;
            }
            if (!IsVisible) Show();

            if (!_registered)
            {
                _callbackMsg = RegisterWindowMessage("MultiBar.AppBar." + Cfg.Id);
                var reg = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _callbackMsg };
                SHAppBarMessage(ABM_NEW, ref reg);
                _registered = true;
            }

            int h = (int)Math.Round(BarHeight * _monitor.Scale);
            var b = _monitor.Bounds;
            var abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = _hwnd,
                uEdge = Cfg.Edge == BarEdge.Top ? ABE_TOP : ABE_BOTTOM,
                rc = Cfg.Edge == BarEdge.Top ? new RECT(b.Left, b.Top, b.Right, b.Top + h) : new RECT(b.Left, b.Bottom - h, b.Right, b.Bottom),
            };
            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            if (Cfg.Edge == BarEdge.Top) abd.rc.Bottom = abd.rc.Top + h;
            else abd.rc.Top = abd.rc.Bottom - h;
            SHAppBarMessage(ABM_SETPOS, ref abd);

            SetWindowPos(_hwnd, HWND_TOPMOST, abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Refresh();
        }
        finally { _docking = false; }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x21, WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0, WM_ACTIVATE = 0x6, WM_WINDOWPOSCHANGED = 0x47;
        if (_registered && msg == _callbackMsg)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, Dock);
                    break;
                case ABN_FULLSCREENAPP:
                    OnFullscreen(lParam != IntPtr.Zero);
                    break;
            }
            handled = true;
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(3); // MA_NOACTIVATE
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                Dispatcher.BeginInvoke(DispatcherPriority.Background, Dock);
                break;
            case WM_ACTIVATE when _registered:
            {
                var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
                SHAppBarMessage(ABM_ACTIVATE, ref abd);
                break;
            }
            case WM_WINDOWPOSCHANGED when _registered:
            {
                var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
                SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd);
                break;
            }
        }
        return IntPtr.Zero;
    }

    void OnFullscreen(bool opened)
    {
        bool onMyMonitor = false;
        if (opened && _monitor != null)
        {
            var fg = GetForegroundWindow();
            onMyMonitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) == _monitor.Handle;
        }
        if (opened && onMyMonitor)
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        else
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    static bool TransparencyEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch { return true; }
    }

    void ApplyBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;
        var tint = Theme.BarTint;
        bool blur = TransparencyEnabled();
        // Acrylic blur behind the window, tinted like the Windows 11 taskbar.
        var accent = new AccentPolicy
        {
            AccentState = blur ? 4 : 0, // ACCENT_ENABLE_ACRYLICBLURBEHIND
            AccentFlags = 2,
            GradientColor = (tint.A << 24) | (tint.B << 16) | (tint.G << 8) | tint.R,
        };
        int size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }

        // Without blur, paint the tint ourselves (opaque). With blur, keep a 1/255 alpha so clicks still hit the bar.
        _root.Background = new SolidColorBrush(blur ? Color.FromArgb(1, 0, 0, 0) : Color.FromRgb(tint.R, tint.G, tint.B));
    }

    public void ApplyTheme()
    {
        _topLine.Background = Theme.BarTopBorder;
        _time.Foreground = _date.Foreground = _hint.Foreground = Theme.Foreground;
        _dropMarker.Background = Theme.AccentBrush;
        _start = null;
        BuildTray();
        ApplyBackdrop();
        Refresh(force: true);
    }

    void ApplyLayout()
    {
        _topLine.VerticalAlignment = Cfg.Edge == BarEdge.Top ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        bool center = Cfg.Alignment == BarAlignment.Center;
        _items.HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _items.Margin = center ? new Thickness(0) : new Thickness(12, 0, 0, 0);
        _hint.HorizontalAlignment = _items.HorizontalAlignment;
        _hint.Margin = center ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        _clockPlate.Visibility = Cfg.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        BuildTray();
        if (Cfg.ShowClock) { UpdateClock(); _clockTimer.Start(); } else _clockTimer.Stop();
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        var t = now.ToString("t");
        if (_time.Text != t) _time.Text = t;
        var d = now.ToString("d");
        if (_date.Text != d) _date.Text = d;
        _clockPlate.ToolTip ??= new ToolTip();
        ((ToolTip)_clockPlate.ToolTip).Content = now.ToString("D");
    }

    PlateButton NewStartButton() =>
        // Windows always opens Start on the main monitor's taskbar, whichever bar is clicked.
        new(PlateButton.WindowsLogo(), BarButton.ButtonWidth, "Iniciar", () => Keys.Chord(0x5B)); // Win key

    void BuildTray()
    {
        _tray.Children.Clear();
        _tray.Visibility = Cfg.ShowTray ? Visibility.Visible : Visibility.Collapsed;
        if (!Cfg.ShowTray) return;
        _tray.Children.Add(new PlateButton(PlateButton.Glyph("", 12), 32, "Mostrar ícones ocultos", WindowsTaskbar.Peek));
        _tray.Children.Add(new PlateButton(PlateButton.QuickSettingsIcons(), 64, "Configurações rápidas (rede, volume)", () => Keys.Chord(0x5B, 0x41))); // Win+A
    }

    // ---------------- Content ----------------

    List<BarEntry> BuildEntries()
    {
        var all = Host.Tracker?.Windows ?? (IReadOnlyList<TrackedWindow>)Array.Empty<TrackedWindow>();
        var entries = new List<BarEntry>();
        var claimed = new HashSet<IntPtr>();

        foreach (var item in Cfg.Items)
        {
            var wins = AppMatcher.WindowsOf(item, all).ToList();
            foreach (var w in wins) claimed.Add(w.Hwnd);
            entries.Add(new BarEntry { Key = "pin:" + item.Id, Item = item, Windows = wins, Name = item.Name });
        }

        if (Cfg.ShowRunningWindows && _monitor != null)
        {
            // Windows pinned on any bar are shown there; others appear on the bar of their monitor.
            var pinnedElsewhere = Host.Config.Bars.Where(b => b != Cfg).SelectMany(b => b.Items).ToList();
            foreach (var g in all.Where(w => w.Monitor == _monitor.Handle && !claimed.Contains(w.Hwnd))
                                 .Where(w => !pinnedElsewhere.Any(p => AppMatcher.Matches(p, w)))
                                 .GroupBy(w => w.AppKey))
            {
                var wins = g.ToList();
                entries.Add(new BarEntry { Key = "run:" + g.Key, Windows = wins, Name = AppName(wins[0]) });
            }
        }
        return entries;
    }

    static readonly Dictionary<string, string> NameCache = new();
    static string AppName(TrackedWindow w)
    {
        if (w.ExePath == null) return w.Title;
        if (NameCache.TryGetValue(w.ExePath, out var n)) return n;
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(w.ExePath).FileDescription;
            n = string.IsNullOrWhiteSpace(desc) ? Path.GetFileNameWithoutExtension(w.ExePath) : desc.Trim();
        }
        catch { n = Path.GetFileNameWithoutExtension(w.ExePath); }
        return NameCache[w.ExePath] = n;
    }

    public void Refresh(bool force = false)
    {
        var entries = BuildEntries();
        bool same = !force && entries.Count == _buttons.Count && entries.Select(e => e.Key).SequenceEqual(_buttons.Select(b => b.Entry.Key));
        if (same)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var old = _buttons[i].Entry;
                bool titlesChanged = old.Windows.Count != entries[i].Windows.Count ||
                                     old.Windows.Zip(entries[i].Windows).Any(p => p.First.Title != p.Second.Title);
                if (titlesChanged) _buttons[i].SetEntry(entries[i]);
                else old.Windows = entries[i].Windows;
            }
        }
        else
        {
            _items.Children.Clear();
            if (Cfg.ShowStartButton) _items.Children.Add(_start ??= NewStartButton());
            _buttons = entries.Select(CreateButton).ToList();
            foreach (var b in _buttons) _items.Children.Add(b);
        }
        _hint.Visibility = _buttons.Count == 0 && !Cfg.ShowStartButton ? Visibility.Visible : Visibility.Collapsed;
        UpdateStates();
    }

    BarButton CreateButton(BarEntry e)
    {
        var b = new BarButton(e);
        b.PreviewMouseLeftButtonDown += (_, ev) =>
        {
            _dragStart = ev.GetPosition(this);
            _dragCandidate = b;
            _dragged = false;
            _fgAtMouseDown = Host.Tracker.Foreground;
        };
        b.MouseMove += (_, ev) =>
        {
            if (_dragCandidate != b || ev.LeftButton != MouseButtonState.Pressed) return;
            var p = ev.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < 8 && Math.Abs(p.Y - _dragStart.Y) < 8) return;
            _dragged = true;
            _dragCandidate = null;
            StartDrag(b);
        };
        b.MouseLeftButtonUp += (_, ev) =>
        {
            if (_dragged || _dragCandidate != b) { _dragCandidate = null; return; }
            _dragCandidate = null;
            ev.Handled = true;
            OnClick(b, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };
        b.MouseUp += (_, ev) =>
        {
            if (ev.ChangedButton == MouseButton.Middle) { ev.Handled = true; LaunchNew(b.Entry); }
        };
        b.MouseRightButtonUp += (_, ev) => { ev.Handled = true; ThumbnailPopup.HideNow(); ShowItemMenu(b); };
        b.MouseEnter += (_, _) => ThumbnailPopup.HoverButton(b, Cfg.Edge == BarEdge.Top);
        b.MouseLeave += (_, _) => ThumbnailPopup.LeaveButton();
        b.PreviewMouseDown += (_, _) => ThumbnailPopup.HideNow();
        return b;
    }

    /// <summary>
    /// The bar never takes focus (like the real taskbar), but a WPF menu only closes on an outside click
    /// when its owner is the foreground window, so take the foreground just while the menu is open.
    /// </summary>
    void OpenMenu(ContextMenu menu)
    {
        SetForegroundWindow(_hwnd);
        menu.IsOpen = true;
    }

    public void UpdateStates()
    {
        var fg = Host.Tracker?.Foreground ?? IntPtr.Zero;
        foreach (var b in _buttons)
        {
            var wins = b.Entry.Windows;
            RunState s = wins.Count == 0 ? RunState.None
                : wins.Any(w => w.Hwnd == fg && !IsIconic(w.Hwnd)) ? RunState.Active
                : RunState.Running;
            b.SetState(s);
        }
    }

    // ---------------- Clicks ----------------

    void OnClick(BarButton b, bool newInstance)
    {
        var e = b.Entry;
        if (newInstance) { LaunchNew(e); return; }
        if (e.IsGroup) { Host.GroupClick(e.Item!, _fgAtMouseDown); return; }

        var wins = e.Windows.Where(w => IsWindow(w.Hwnd)).ToList();
        if (wins.Count == 0) { LaunchNew(e); return; }
        if (wins.Count == 1) { Toggle(wins[0].Hwnd); return; }
        ShowWindowList(b, wins);
    }

    void Toggle(IntPtr h)
    {
        if (h == _fgAtMouseDown && !IsIconic(h)) WindowOps.Minimize(h);
        else WindowOps.Activate(h);
    }

    static void LaunchNew(BarEntry e)
    {
        if (e.Item != null) { Launcher.Launch(e.Item); return; }
        var w = e.Windows.FirstOrDefault();
        if (w == null) return;
        if (w.Aumid != null && w.ExePath?.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) == true)
            Launcher.Launch(new ItemConfig { Aumid = w.Aumid, Name = e.Name });
        else if (w.ExePath != null)
            Launcher.Launch(new ItemConfig { Path = w.ExePath, Name = e.Name });
    }

    void ShowWindowList(BarButton b, List<TrackedWindow> wins)
    {
        var menu = NewMenu(b);
        foreach (var w in wins)
        {
            var mi = Item(string.IsNullOrWhiteSpace(w.Title) ? b.Entry.Name : w.Title, () => Toggle(w.Hwnd));
            if (w.Hwnd == _fgAtMouseDown) mi.FontWeight = FontWeights.SemiBold;
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Minimizar todas", () => wins.ForEach(w => WindowOps.Minimize(w.Hwnd))));
        menu.Items.Add(Item("Restaurar todas", () => Enumerable.Reverse(wins).ToList().ForEach(w => WindowOps.Activate(w.Hwnd))));
        OpenMenu(menu);
    }

    // ---------------- Menus ----------------

    ContextMenu NewMenu(UIElement target) => new()
    {
        PlacementTarget = target,
        Placement = Cfg.Edge == BarEdge.Top ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top,
    };

    static MenuItem Item(string header, Action? onClick, bool enabled = true, bool? check = null)
    {
        var mi = new MenuItem { Header = header, IsEnabled = enabled };
        if (check.HasValue) mi.IsChecked = check.Value;
        if (onClick != null) mi.Click += (_, _) => onClick();
        return mi;
    }

    static MenuItem Sub(string header, params object[] children)
    {
        var mi = new MenuItem { Header = header };
        foreach (var c in children) mi.Items.Add(c);
        return mi;
    }

    void Changed()
    {
        Host.SaveConfig();
        Host.RefreshAll();
    }

    /// <summary>"End task" like Task Manager: kills the processes (and their children) that own these windows.</summary>
    static void AddKill(ContextMenu menu, List<TrackedWindow> windows, string header)
    {
        // Never offer to kill Explorer (it is the Windows shell) or MultiBar itself.
        var pids = windows
            .Where(w => w.Pid != 0 && w.Pid != Environment.ProcessId)
            .Where(w => w.ExePath == null || !w.ExePath.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase))
            .Select(w => (int)w.Pid).Distinct().ToList();
        if (pids.Count == 0) return;

        var mi = Item(header, () =>
        {
            var failed = new List<string>();
            foreach (var pid in pids)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    p.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { } // already gone
                catch (Exception ex) { failed.Add($"PID {pid}: {ex.Message}"); }
            }
            if (failed.Count > 0)
                MessageBox.Show("Não consegui finalizar:\n\n" + string.Join("\n", failed) +
                    "\n\nSe o app roda como administrador, finalize pelo Gerenciador de Tarefas.", "MultiBar");
        });
        mi.Icon = PlateButton.Glyph("", 12); // X
        menu.Items.Add(mi);
    }

    /// <summary>"Recentes" section from the app's Windows jump list, like the real taskbar.</summary>
    static void AddRecent(ContextMenu menu, BarEntry e)
    {
        var w = e.Windows.FirstOrDefault();
        var appId = JumpLists.AppIdFor(e.Item?.Aumid ?? w?.Aumid, e.Item != null ? AppMatcher.ExeOf(e.Item) ?? w?.ExePath : w?.ExePath);
        var recent = JumpLists.Recent(appId);
        // Apps pinned by path may register their jump list under the exe's AUMID instead.
        if (recent.Count == 0 && w?.Aumid != null && w.Aumid != appId) recent = JumpLists.Recent(w.Aumid);
        if (recent.Count == 0) return;

        menu.Items.Add(new MenuItem { Header = "Recentes", IsEnabled = false, FontSize = 12 });
        foreach (var r in recent)
        {
            var mi = Item(r.Name, () =>
            {
                try { Process.Start(new ProcessStartInfo(r.Target) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show($"Não consegui abrir \"{r.Name}\".\n\n{ex.Message}", "MultiBar"); }
            });
            mi.ToolTip = r.Target;
            var icon = Icons.Get(r.Target, 32);
            if (icon != null) mi.Icon = new Image { Source = icon, Width = 16, Height = 16 };
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
    }

    void ShowItemMenu(BarButton b)
    {
        var e = b.Entry;
        var menu = NewMenu(b);
        var item = e.Item;

        if (item == null)
        {
            AddRecent(menu, e);
            menu.Items.Add(Item(e.Name, () => LaunchNew(e)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Fixar nesta barra", () => PinRunning(e, Cfg.Items.Count)));
            menu.Items.Add(Item(e.Windows.Count > 1 ? "Fechar todas as janelas" : "Fechar janela", () => e.Windows.ForEach(w => WindowOps.Close(w.Hwnd))));
            AddKill(menu, e.Windows, "Finalizar tarefa");
        }
        else if (item.IsGroup)
        {
            bool running = e.Windows.Count > 0;
            menu.Items.Add(Item("Abrir / mostrar todos", () => { Host.RestoreGroup(item); foreach (var m in item.Members.Where(m => !Host.Tracker.Windows.Any(w => AppMatcher.Matches(m, w)))) Launcher.Launch(m); }));
            menu.Items.Add(Item("Minimizar todos", () => Host.MinimizeGroup(item), running));
            menu.Items.Add(Item("Fechar todos", () => Host.CloseGroup(item), running));
            AddKill(menu, e.Windows, "Finalizar tarefas do grupo");
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Minimizar e restaurar juntos", () => { item.LinkMinimize = !item.LinkMinimize; Changed(); }, check: item.LinkMinimize));
            menu.Items.Add(Item("Renomear grupo...", () => RenameGroup(item)));
            menu.Items.Add(Item("Adicionar apps ao grupo...", () => AddAppsTo(item.Members)));
            if (item.Members.Count > 0)
            {
                var remove = Sub("Tirar do grupo");
                foreach (var m in item.Members.ToList())
                    remove.Items.Add(Item(m.Name, () => MoveOutOfGroup(item, m)));
                menu.Items.Add(remove);
                var open = Sub("Abrir só um");
                foreach (var m in item.Members.ToList())
                    open.Items.Add(Item(m.Name, () => Launcher.Launch(m)));
                menu.Items.Add(open);
            }
            menu.Items.Add(Item("Desagrupar", () => Ungroup(item)));
            menu.Items.Add(new Separator());
            AddMoveItems(menu, item);
            menu.Items.Add(Item("Remover grupo da barra", () => { Cfg.Items.Remove(item); Changed(); }));
        }
        else
        {
            AddRecent(menu, e);
            menu.Items.Add(Item(item.Name, () => Launcher.Launch(item)));
            menu.Items.Add(new Separator());
            var groups = Cfg.Items.Where(i => i.IsGroup).ToList();
            var toGroup = Sub("Adicionar ao grupo");
            foreach (var g in groups)
                toGroup.Items.Add(Item(g.Name, () => { Cfg.Items.Remove(item); g.Members.Add(item); Changed(); }));
            if (groups.Count > 0) toGroup.Items.Add(new Separator());
            toGroup.Items.Add(Item("Novo grupo...", () => NewGroupFrom(item)));
            menu.Items.Add(toGroup);
            AddMoveItems(menu, item);
            var otherBars = Host.Bars.Where(x => x != this).ToList();
            if (otherBars.Count > 0)
            {
                var move = Sub("Mover para outra barra");
                foreach (var ob in otherBars)
                    move.Items.Add(Item(ob.Label, () => { Cfg.Items.Remove(item); ob.Cfg.Items.Add(item); Changed(); }));
                menu.Items.Add(move);
            }
            menu.Items.Add(Item("Desafixar da barra", () => { Cfg.Items.Remove(item); Changed(); }));
            if (e.Windows.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item(e.Windows.Count > 1 ? "Fechar todas as janelas" : "Fechar janela", () => e.Windows.ForEach(w => WindowOps.Close(w.Hwnd))));
            AddKill(menu, e.Windows, "Finalizar tarefa");
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(BarSettingsMenu());
        OpenMenu(menu);
    }

    void AddMoveItems(ContextMenu menu, ItemConfig item)
    {
        int i = Cfg.Items.IndexOf(item);
        menu.Items.Add(Item("Mover para a esquerda", () => { Cfg.Items.RemoveAt(i); Cfg.Items.Insert(i - 1, item); Changed(); }, i > 0));
        menu.Items.Add(Item("Mover para a direita", () => { Cfg.Items.RemoveAt(i); Cfg.Items.Insert(i + 1, item); Changed(); }, i >= 0 && i < Cfg.Items.Count - 1));
    }

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var menu = NewMenu(this);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        var taskmgr = Item("Gerenciador de Tarefas", () =>
        {
            try { Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("Não consegui abrir o Gerenciador de Tarefas.\n\n" + ex.Message, "MultiBar"); }
        });
        taskmgr.Icon = PlateButton.Glyph("", 14); // Fluent "task manager" glyph
        menu.Items.Add(taskmgr);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Adicionar apps...", () => AddAppsTo(Cfg.Items)));
        menu.Items.Add(Item("Novo grupo...", () => NewGroupFrom(null)));
        menu.Items.Add(new Separator());
        foreach (var x in BarSettingsItems()) menu.Items.Add(x);
        OpenMenu(menu);
    }

    MenuItem BarSettingsMenu() => Sub("Configurações da barra", BarSettingsItems().ToArray());

    IEnumerable<object> BarSettingsItems()
    {
        var monitors = Monitors.All();
        var mon = Sub("Monitor");
        foreach (var m in monitors)
            mon.Items.Add(Item(m.Label, () => { Cfg.Monitor = m.Device; Host.SaveConfig(); Unregister(); Dock(); Refresh(force: true); }, check: m.Device == Cfg.Monitor));
        yield return mon;

        yield return Sub("Posição",
            Item("Inferior", () => SetEdge(BarEdge.Bottom), check: Cfg.Edge == BarEdge.Bottom),
            Item("Superior", () => SetEdge(BarEdge.Top), check: Cfg.Edge == BarEdge.Top));
        yield return Sub("Alinhamento",
            Item("Centro", () => SetAlign(BarAlignment.Center), check: Cfg.Alignment == BarAlignment.Center),
            Item("Esquerda", () => SetAlign(BarAlignment.Left), check: Cfg.Alignment == BarAlignment.Left));
        yield return Item("Mostrar janelas abertas neste monitor", () => { Cfg.ShowRunningWindows = !Cfg.ShowRunningWindows; Changed(); }, check: Cfg.ShowRunningWindows);
        yield return Item("Mostrar relógio", () => { Cfg.ShowClock = !Cfg.ShowClock; Host.SaveConfig(); ApplyLayout(); }, check: Cfg.ShowClock);
        yield return Item("Mostrar botão Iniciar", () => { Cfg.ShowStartButton = !Cfg.ShowStartButton; Host.SaveConfig(); Refresh(force: true); }, check: Cfg.ShowStartButton);
        yield return Item("Mostrar área de notificação", () => { Cfg.ShowTray = !Cfg.ShowTray; Host.SaveConfig(); BuildTray(); }, check: Cfg.ShowTray);
        yield return new Separator();

        var add = Sub("Nova barra");
        foreach (var m in monitors) add.Items.Add(Item(m.Label, () => Host.AddBar(m)));
        yield return add;
        yield return Item("Remover esta barra", () => Host.RemoveBar(this));
        yield return new Separator();
        yield return Item("Esconder a barra do Windows", () => Host.SetHideWindowsTaskbar(!Host.Config.HideWindowsTaskbar), check: Host.Config.HideWindowsTaskbar);
        yield return Item("Iniciar com o Windows", () => AutoStart.Enabled = !AutoStart.Enabled, check: AutoStart.Enabled);
        yield return Item("Abrir arquivo de configuração", () =>
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{AppConfig.FilePath}\"") { UseShellExecute = true });
        });
        yield return Item("Sair do MultiBar", () => Host.Quit());
    }

    public string Label => (Monitors.Find(Cfg.Monitor)?.Label ?? Cfg.Monitor) + (Cfg.Edge == BarEdge.Top ? " - superior" : "");

    void SetEdge(BarEdge edge)
    {
        Cfg.Edge = edge;
        Host.SaveConfig();
        ApplyLayout();
        Dock();
    }

    void SetAlign(BarAlignment a)
    {
        Cfg.Alignment = a;
        Host.SaveConfig();
        ApplyLayout();
    }

    // ---------------- Editing ----------------

    void AddAppsTo(List<ItemConfig> target)
    {
        var picked = AppPickerWindow.Pick(this);
        if (picked.Count == 0) return;
        target.AddRange(picked);
        Changed();
    }

    void NewGroupFrom(ItemConfig? first)
    {
        var name = InputDialog.Ask(this, "Novo grupo", "Nome do grupo:", "Grupo");
        if (name == null) return;
        var group = new ItemConfig { Kind = ItemKind.Group, Name = name };
        if (first != null)
        {
            int i = Cfg.Items.IndexOf(first);
            Cfg.Items.Remove(first);
            group.Members.Add(first);
            Cfg.Items.Insert(Math.Max(0, i), group);
        }
        else Cfg.Items.Add(group);

        var more = AppPickerWindow.Pick(this, $"Escolha os apps do grupo \"{name}\"");
        group.Members.AddRange(more);
        Changed();
    }

    void RenameGroup(ItemConfig g)
    {
        var name = InputDialog.Ask(this, "Renomear grupo", "Nome do grupo:", g.Name);
        if (name == null) return;
        g.Name = name;
        Changed();
    }

    void MoveOutOfGroup(ItemConfig g, ItemConfig m)
    {
        g.Members.Remove(m);
        Cfg.Items.Insert(Cfg.Items.IndexOf(g) + 1, m);
        Changed();
    }

    void Ungroup(ItemConfig g)
    {
        int i = Cfg.Items.IndexOf(g);
        Cfg.Items.RemoveAt(i);
        Cfg.Items.InsertRange(i, g.Members);
        Changed();
    }

    void PinRunning(BarEntry e, int index)
    {
        var w = e.Windows.FirstOrDefault();
        if (w == null) return;
        var item = new ItemConfig { Kind = ItemKind.App, Name = e.Name };
        if (w.ExePath?.Contains("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) == true && w.Aumid != null)
            item.Aumid = w.Aumid;
        else
        {
            item.Path = w.ExePath;
            item.MatchPath = w.ExePath;
            // Keep the window's AUMID so a pinned web app only matches its own windows.
            item.Aumid = w.Aumid;
        }
        Cfg.Items.Insert(Math.Clamp(index, 0, Cfg.Items.Count), item);
        Changed();
    }

    // ---------------- Drag & drop ----------------

    void StartDrag(BarButton b)
    {
        var data = new DataObject(DragFormat, Cfg.Id + "|" + b.Entry.Key);
        try { DragDrop.DoDragDrop(b, data, DragDropEffects.Move); }
        catch { }
        HideDropHints();
    }

    void HideDropHints()
    {
        _dropMarker.Visibility = Visibility.Collapsed;
        foreach (var b in _buttons) b.ShowDropHint(false);
    }

    BarButton? GroupUnder(Point p)
    {
        foreach (var b in _buttons)
        {
            if (!b.Entry.IsGroup) continue;
            var pos = b.TranslatePoint(new Point(0, 0), this);
            // Middle half of the button means "drop into the group".
            if (p.X >= pos.X + b.ActualWidth * 0.25 && p.X <= pos.X + b.ActualWidth * 0.75) return b;
        }
        return null;
    }

    /// <summary>Index among Cfg.Items where a drop at p would insert.</summary>
    int InsertIndex(Point p, out double markerX)
    {
        var pinned = _buttons.Where(b => b.Entry.Item != null).ToList();
        markerX = pinned.Count == 0 ? (_items.TranslatePoint(new Point(0, 0), this).X) : 0;
        for (int i = 0; i < pinned.Count; i++)
        {
            var pos = pinned[i].TranslatePoint(new Point(0, 0), this);
            if (p.X < pos.X + pinned[i].ActualWidth / 2)
            {
                markerX = pos.X - 2;
                return Cfg.Items.IndexOf(pinned[i].Entry.Item!);
            }
            markerX = pos.X + pinned[i].ActualWidth + 1;
        }
        return Cfg.Items.Count;
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        bool internalDrag = e.Data.GetDataPresent(DragFormat);
        if (!files && !internalDrag) { e.Effects = DragDropEffects.None; e.Handled = true; return; }

        var p = e.GetPosition(this);
        HideDropHints();
        var group = GroupUnder(p);
        string? sourceKey = internalDrag ? (e.Data.GetData(DragFormat) as string)?.Split('|', 2)[1] : null;
        bool draggingGroup = sourceKey != null && Host.Locate(sourceKey.Replace("pin:", ""))?.item.IsGroup == true;
        if (group != null && !draggingGroup && "pin:" + group.Entry.Item!.Id != sourceKey)
        {
            group.ShowDropHint(true);
        }
        else
        {
            InsertIndex(p, out var x);
            Canvas.SetLeft(_dropMarker, x);
            Canvas.SetTop(_dropMarker, (ActualHeight - _dropMarker.Height) / 2);
            _dropMarker.Visibility = Visibility.Visible;
        }
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(this);
        HideDropHints();
        var group = GroupUnder(p);
        int index = InsertIndex(p, out _);

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var items = paths.Select(Launcher.FromFile).ToList();
            if (group != null) group.Entry.Item!.Members.AddRange(items);
            else Cfg.Items.InsertRange(Math.Clamp(index, 0, Cfg.Items.Count), items);
            Changed();
            return;
        }

        if (e.Data.GetData(DragFormat) is not string raw) return;
        var parts = raw.Split('|', 2);
        var sourceBar = Host.Bars.FirstOrDefault(b => b.Cfg.Id == parts[0]);
        var key = parts[1];

        if (key.StartsWith("run:"))
        {
            var entry = sourceBar?._buttons.FirstOrDefault(b => b.Entry.Key == key)?.Entry;
            if (entry == null) return;
            if (group != null)
            {
                int before = Cfg.Items.Count;
                PinRunning(entry, Cfg.Items.Count);
                if (Cfg.Items.Count > before)
                {
                    var pinned = Cfg.Items[^1];
                    Cfg.Items.RemoveAt(Cfg.Items.Count - 1);
                    group.Entry.Item!.Members.Add(pinned);
                    Changed();
                }
            }
            else PinRunning(entry, index);
            return;
        }

        var found = Host.Locate(key["pin:".Length..]);
        if (found == null) return;
        var (list, item, _) = found.Value;
        if (group != null && !item.IsGroup && group.Entry.Item != item)
        {
            list.Remove(item);
            group.Entry.Item!.Members.Add(item);
            Changed();
            return;
        }

        int oldIndex = Cfg.Items.IndexOf(item);
        list.Remove(item);
        if (oldIndex >= 0 && oldIndex < index) index--;
        Cfg.Items.Insert(Math.Clamp(index, 0, Cfg.Items.Count), item);
        Changed();
    }
}

