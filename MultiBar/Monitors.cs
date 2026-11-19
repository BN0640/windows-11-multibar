using static MultiBar.Native;

namespace MultiBar;

public sealed record MonitorInfo(IntPtr Handle, string Device, RECT Bounds, RECT Work, bool Primary, int Number)
{
    public double Scale
    {
        get
        {
            try { if (GetDpiForMonitor(Handle, 0, out uint dx, out _) == 0) return dx / 96.0; } catch { }
            return 1.0;
        }
    }

    public string Label =>
        $"Monitor {Number} ({Bounds.Width}x{Bounds.Height}){(Primary ? " - principal" : "")}";
}

public static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var list = new List<(IntPtr h, MONITORINFOEX mi)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(h, ref mi)) list.Add((h, mi));
            return true;
        }, IntPtr.Zero);

        return list
            .Select(x => new MonitorInfo(x.h, x.mi.szDevice, x.mi.rcMonitor, x.mi.rcWork, (x.mi.dwFlags & 1) != 0, DeviceNumber(x.mi.szDevice)))
            .OrderBy(m => m.Number)
            .ToList();
    }

    static int DeviceNumber(string device)
    {
        var digits = new string(device.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    public static MonitorInfo? Find(string device) =>
        All().FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));

    public static IntPtr ForWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
        {
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(hwnd, ref wp))
            {
                var r = wp.rcNormalPosition;
                return MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
            }
        }
        return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    }
}
