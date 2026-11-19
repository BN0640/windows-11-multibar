using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using static MultiBar.Native;

namespace MultiBar;

public static class Launcher
{
    public static void Launch(ItemConfig item)
    {
        try
        {
            if (item.IsGroup)
            {
                foreach (var m in item.Members) Launch(m);
                return;
            }
            // Path wins when both are set: an AUMID taken from a window (e.g. a web app) may not be launchable.
            if (!string.IsNullOrEmpty(item.Aumid) && string.IsNullOrEmpty(item.Path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + item.Aumid) { UseShellExecute = true });
                return;
            }
            if (!string.IsNullOrEmpty(item.Path))
            {
                var psi = new ProcessStartInfo(item.Path) { UseShellExecute = true, Arguments = item.Arguments ?? "" };
                var dir = System.IO.Path.GetDirectoryName(item.Path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não consegui abrir \"{item.Name}\".\n\n{ex.Message}", "MultiBar");
        }
    }

    /// <summary>Builds an item from a dropped file or shortcut.</summary>
    public static ItemConfig FromFile(string path)
    {
        var item = new ItemConfig
        {
            Kind = ItemKind.App,
            Path = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
        };
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = ResolveShortcut(path);
            if (target != null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) item.MatchPath = target;
            item.DataDir = AppMatcher.ParseDataDir(ShortcutArguments(path));
        }
        else if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) item.Name = desc.Trim();
            }
            catch { }
        }
        return item;
    }

    public static string? ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch { return null; }
    }

    static string? ShortcutArguments(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            string args = shell.CreateShortcut(lnk).Arguments;
            return args;
        }
        catch { return null; }
    }

    /// <summary>Everything in the Start menu's "All apps" list, Store apps included.</summary>
    public static List<ItemConfig> InstalledApps()
    {
        var result = new List<ItemConfig>();
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application")!;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic fi in folder.Items())
            {
                string name = fi.Name;
                string parsing = fi.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing)) continue;
                var item = new ItemConfig { Kind = ItemKind.App, Name = name };
                bool isFile = parsing.Length > 2 && parsing[1] == ':' || parsing.StartsWith("\\\\");
                if (isFile)
                {
                    item.Path = parsing;
                    if (parsing.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) item.MatchPath = ResolveShortcut(parsing);
                }
                else
                {
                    item.Aumid = parsing;
                    try
                    {
                        string? target = fi.ExtendedProperty("System.Link.TargetParsingPath") as string;
                        if (!string.IsNullOrWhiteSpace(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            item.MatchPath = target;
                    }
                    catch { }
                    // Desktop apps without a shortcut use their exe path (with a known-folder GUID) as AUMID.
                    if (item.MatchPath == null && parsing.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        item.MatchPath = ExpandKnownFolder(parsing);
                }
                result.Add(item);
            }
        }
        catch { }
        return result.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static string? ExpandKnownFolder(string path)
    {
        if (!path.StartsWith("{")) return path;
        var close = path.IndexOf('}');
        if (close < 0) return null;
        var guid = path.Substring(1, close - 1).ToUpperInvariant();
        string? root = guid switch
        {
            "6D809377-6AF0-444B-8957-A3773F02200E" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E" => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "1AC14E77-02E7-4E5D-B744-2EB1AE5198B7" => Environment.GetFolderPath(Environment.SpecialFolder.System),
            "F38BF404-1D43-42F2-9305-67DE0B28FC23" => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            _ => null,
        };
        return root == null ? null : root + path[(close + 1)..];
    }
}

public static class Icons
{
    static readonly Dictionary<string, ImageSource?> Cache = new();

    public static ImageSource? For(ItemConfig item, int px) =>
        (item.IconPath != null ? FromFile(item.IconPath, px) : null) ??
        (item.Aumid != null ? Get("shell:AppsFolder\\" + item.Aumid, px) ?? (item.Path != null ? Get(item.Path, px) : null)
        : item.Path != null ? Get(item.Path, px) : null);

    /// <summary>Loads a custom .ico/.png, picking the .ico frame closest to the wanted size.</summary>
    static ImageSource? FromFile(string path, int px)
    {
        var key = "file:" + path.ToLowerInvariant() + "|" + px;
        if (Cache.TryGetValue(key, out var img)) return img;
        try
        {
            var full = Environment.ExpandEnvironmentVariables(path);
            var decoder = BitmapDecoder.Create(new Uri(full), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            img = decoder.Frames
                .OrderBy(f => f.PixelWidth >= px ? 0 : 1)
                .ThenBy(f => Math.Abs(f.PixelWidth - px))
                .First();
            img.Freeze();
        }
        catch { img = null; }
        Cache[key] = img;
        return img;
    }

    public static ImageSource? For(TrackedWindow w, int px) =>
        (w.Aumid != null ? Get("shell:AppsFolder\\" + w.Aumid, px) : null)
        ?? (w.ExePath != null ? Get(w.ExePath, px) : null);

    public static ImageSource? Get(string parsingName, int px)
    {
        var key = parsingName.ToLowerInvariant() + "|" + px;
        if (Cache.TryGetValue(key, out var img)) return img;
        img = Load(parsingName, px);
        Cache[key] = img;
        return img;
    }

    static ImageSource? Load(string parsingName, int px)
    {
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            // SIIGBF_ICONONLY
            int hr = factory.GetImage(new SIZE { cx = px, cy = px }, 0x4, out var hbm);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try { return FromHBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
    }

    static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero) return null;
        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
        if (ds.dsBm.bmBitsPixel != 32) return null;
        var buf = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, buf, 0, buf.Length);
        if (ds.dsBmih.biHeight > 0)
        {
            // Bottom-up DIB: flip rows.
            var flipped = new byte[buf.Length];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(buf, y * stride, flipped, (h - 1 - y) * stride, stride);
            buf = flipped;
        }
        // The shell hands back either premultiplied or straight alpha. A color channel above its alpha is
        // impossible when premultiplied, so it tells us the format. Treating straight alpha as premultiplied
        // is what produced light halos around icons such as VS Code's.
        bool straight = false, anyAlpha = false;
        for (int i = 0; i < buf.Length; i += 4)
        {
            byte a = buf[i + 3];
            if (a != 0) anyAlpha = true;
            if (buf[i] > a || buf[i + 1] > a || buf[i + 2] > a) { straight = true; if (anyAlpha) break; }
        }
        var format = !anyAlpha ? PixelFormats.Bgr32 : straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32;
        var bmp = BitmapSource.Create(w, h, 96, 96, format, null, buf, stride);
        bmp.Freeze();
        return bmp;
    }
}

public static class WindowOps
{
    public static void Activate(IntPtr h)
    {
        if (IsIconic(h)) ShowWindowAsync(h, SW_RESTORE);
        if (!SetForegroundWindow(h))
        {
            // Foreground lock workaround: a synthetic Alt press lets us take the foreground.
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(h);
        }
        BringWindowToTop(h);
    }

    public static void Minimize(IntPtr h) => ShowWindowAsync(h, SW_MINIMIZE);
    public static void Close(IntPtr h) => PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}

public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "MultiBar";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(Name, false);
        }
    }
}
