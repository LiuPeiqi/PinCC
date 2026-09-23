using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PinCC.UI;

/// <summary>Rectangle in physical (device) pixels of the virtual screen.</summary>
public readonly record struct Px(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public int CX => X + W / 2;
    public int CY => Y + H / 2;
    public bool Intersects(Px o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
    public Px Inflate(int d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);
    public Px Offset(int dx, int dy) => new(X + dx, Y + dy, W, H);
    public long Overlap(Px o) => Math.Max(0L, Math.Min(Right, o.Right) - Math.Max(X, o.X)) * Math.Max(0L, Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y));

    /// <summary>Moves this rect inside <paramref name="area"/> (keeps size when it fits).</summary>
    public Px ClampInto(Px area)
    {
        int w = Math.Min(W, area.W), h = Math.Min(H, area.H);
        return new(Math.Clamp(X, area.X, area.Right - w), Math.Clamp(Y, area.Y, area.Bottom - h), w, h);
    }
}

/// <summary>
/// A monitor: bounds and work area in physical px and its effective DPI scale. <see cref="Id"/> is the monitor's
/// device-interface path (stable when \\.\DISPLAYn names get renumbered); <see cref="Device"/> is the GDI name.
/// </summary>
public sealed record Screen(string Id, string Device, Px Bounds, Px Work, double Scale, bool Primary)
{
    public int Px(double dip) => (int)Math.Round(dip * Scale);
    public double Dip(int px) => px / Scale;
}

/// <summary>Monitor enumeration (the process is PerMonitorV2-aware, so all values are physical px).</summary>
public static class Screens
{
    static List<Screen>? _cache;
    static DateTime _cachedAt;

    /// <summary>All monitors (cached briefly; <see cref="Invalidate"/> on display changes).</summary>
    public static List<Screen> All()
    {
        if (_cache != null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(2)) return _cache;
        var list = new List<Screen>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { list.Add(FromHandle(h)); return true; }, IntPtr.Zero);
        _cachedAt = DateTime.UtcNow;
        return _cache = list;
    }

    public static void Invalidate() => _cache = null;

    public static Screen Primary => All().FirstOrDefault(s => s.Primary) ?? All()[0];

    public static Screen? ById(string? id) => id == null ? null : All().FirstOrDefault(s => s.Id == id) ?? All().FirstOrDefault(s => s.Device == id);

    public static Screen At(int x, int y)
    {
        var h = Native.MonitorFromPoint(new Native.POINT { X = x, Y = y }, 2);
        var mi = Info(h);
        return All().FirstOrDefault(s => s.Device == mi.szDevice) ?? FromHandle(h);
    }

    /// <summary>The monitor showing most of <paramref name="r"/> (nearest if none).</summary>
    public static Screen Of(Px r)
    {
        var best = All().MaxBy(s => s.Bounds.Overlap(r))!;
        return best.Bounds.Overlap(r) > 0 ? best : At(r.CX, r.CY);
    }

    public static Screen UnderCursor()
    {
        Native.GetCursorPos(out var p);
        return At(p.X, p.Y);
    }

    static Native.MONITORINFOEX Info(IntPtr h)
    {
        var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(h, ref mi);
        return mi;
    }

    static Screen FromHandle(IntPtr h)
    {
        var mi = Info(h);
        uint dpi = 96;
        try { Native.GetDpiForMonitor(h, 0, out dpi, out _); } catch { /* pre-8.1: stay at 96 */ }
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        var id = Native.EnumDisplayDevices(mi.szDevice, 0, ref dd, 1) && dd.DeviceID is { Length: > 0 } dev ? dev : mi.szDevice; // EDD_GET_DEVICE_INTERFACE_NAME
        return new Screen(id, mi.szDevice, ToPx(mi.rcMonitor), ToPx(mi.rcWork), dpi / 96.0, (mi.dwFlags & 1) != 0);
    }

    static Px ToPx(Native.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}

/// <summary>
/// Hidden top-level window (not message-only, so it also receives broadcasts such as WM_DISPLAYCHANGE and
/// WM_SETTINGCHANGE). Hosts global hotkeys and the clipboard listener; survives modal loops unlike thread messages.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    readonly HwndSource _src;
    public event Action<int, IntPtr, IntPtr>? Message;
    public IntPtr Handle => _src.Handle;

    public MessageWindow()
    {
        _src = new HwndSource(new HwndSourceParameters("PinCC.Messages")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
            ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW,
            Width = 0, Height = 0,
        });
        _src.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) => { Message?.Invoke(msg, w, l); return IntPtr.Zero; });
    }

    public void Dispose() => _src.Dispose();
}

public static class Native
{
    public const int WM_NCMOUSELEAVE = 0x02A2;
    public const int WM_ACTIVATE = 0x0006, WM_SYSCOMMAND = 0x0112, WM_NCLBUTTONDBLCLK = 0x00A3, WM_TIMECHANGE = 0x001E,
        WM_WTSSESSION_CHANGE = 0x02B1, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, WA_INACTIVE = 0;
    public const int WM_WINDOWPOSCHANGING = 0x0046, WM_WINDOWPOSCHANGED = 0x0047, WM_DISPLAYCHANGE = 0x007E,
        WM_SETTINGCHANGE = 0x001A, WM_POWERBROADCAST = 0x0218, WM_CLIPBOARDUPDATE = 0x031D, SPI_SETWORKAREA = 0x002F;
    public const int WM_NCHITTEST = 0x0084, WM_SIZING = 0x0214, WM_MOVING = 0x0216, WM_ENTERSIZEMOVE = 0x0231,
        WM_EXITSIZEMOVE = 0x0232, WM_HOTKEY = 0x0312, WM_DPICHANGED = 0x02E0, WM_MOUSEACTIVATE = 0x0021;
    public const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
        HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    public const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5,
        WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;
    public const int MA_NOACTIVATE = 3;
    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2), HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);
    [DllImport("user32")] public static extern IntPtr MonitorFromPoint(POINT pt, int flags);
    [DllImport("shcore")] public static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32")] static extern IntPtr BeginDeferWindowPos(int n);
    [DllImport("user32")] static extern IntPtr DeferWindowPos(IntPtr info, IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32")] static extern bool EndDeferWindowPos(IntPtr info);
    [DllImport("winmm", CharSet = CharSet.Unicode)] static extern bool PlaySound(string sound, IntPtr module, uint flags);
    [DllImport("shell32")] static extern int SHQueryUserNotificationState(out int state);

    public static Px WindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static double WindowScale(IntPtr hwnd)
    {
        try { var d = GetDpiForWindow(hwnd); return d == 0 ? 1 : d / 96.0; } catch { return 1; }
    }

    /// <summary>
    /// Places a window at an exact physical rect without activating it. A window takes the DPI of the monitor that
    /// holds most of it, so when the target monitor's DPI differs we first move the window's *center* onto the
    /// target (WM_DPICHANGED is handled there), then set the exact rect, and verify once.
    /// </summary>
    public static void Place(IntPtr hwnd, Px r)
    {
        const uint f = SWP_NOZORDER | SWP_NOACTIVATE;
        var target = Screens.Of(r);
        for (int attempt = 0; attempt < 2 && Math.Abs(WindowScale(hwnd) - target.Scale) > 0.001; attempt++)
        {
            var cur = WindowRect(hwnd);
            SetWindowPos(hwnd, IntPtr.Zero, r.CX - cur.W / 2, r.CY - cur.H / 2, 0, 0, f | SWP_NOSIZE);
        }
        SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.W, r.H, f);
    }

    /// <summary>Raises a window to the top of its band (topmost or normal) without activating it.</summary>
    public static void Raise(IntPtr hwnd, bool topmost) =>
        SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>
    /// WPF answers WM_DPICHANGED with SetWindowPos without SWP_NOACTIVATE (dotnet/wpf#7561), which would activate an
    /// inactive card that merely moved to another monitor. Our hook runs before WPF's and adds the flag.
    /// </summary>
    public static HwndSourceHook NoActivateOnDpiChange()
    {
        bool changing = false;
        return (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WM_DPICHANGED) changing = true;
            else if (msg == WM_WINDOWPOSCHANGING && changing)
            {
                var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                wp.flags |= SWP_NOACTIVATE;
                Marshal.StructureToPtr(wp, lParam, false);
            }
            else if (msg == WM_WINDOWPOSCHANGED) changing = false;
            return IntPtr.Zero;
        };
    }


    /// <summary>Moves several windows at once (same-DPI moves only); falls back to one by one.</summary>
    public static void PlaceMany(IReadOnlyList<(IntPtr Hwnd, Px Rect)> moves)
    {
        if (moves.Count == 0) return;
        var info = BeginDeferWindowPos(moves.Count);
        foreach (var (h, r) in moves)
            if (info != IntPtr.Zero) info = DeferWindowPos(info, h, IntPtr.Zero, r.X, r.Y, r.W, r.H, SWP_NOZORDER | SWP_NOACTIVATE);
        if (info == IntPtr.Zero || !EndDeferWindowPos(info))
            foreach (var (h, r) in moves) SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.W, r.H, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static bool IsOwnedBy(IntPtr hwnd, IntPtr owner) => hwnd != IntPtr.Zero && GetWindow(hwnd, 4) == owner; // GW_OWNER

    /// <summary>
    /// A window that really runs full screen — a game, a video, F11 browsing, a presentation — as (monitor Device, hwnd);
    /// ("*", 0) when Windows reports presentation/D3D full-screen mode; null otherwise. Maximized windows (which also
    /// cover a monitor that has no taskbar) and our own windows never count.
    /// </summary>
    public static (string Device, IntPtr Hwnd)? FullScreen()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4) return ("*", IntPtr.Zero); // D3D full screen, presentation
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsZoomed(fg)) return null;
            GetWindowThreadProcessId(fg, out var pid);
            if (pid == Environment.ProcessId) return null;
            var style = GetWindowLongPtr(fg, -16).ToInt64(); // GWL_STYLE
            if ((style & 0x00C00000) == 0x00C00000) return null; // has a caption: a normal window, however large
            var cls = new System.Text.StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
            var r = WindowRect(fg);
            var s = Screens.Of(r);
            return r.X <= s.Bounds.X && r.Y <= s.Bounds.Y && r.Right >= s.Bounds.Right && r.Bottom >= s.Bounds.Bottom ? (s.Device, fg) : null;
        }
        catch { return null; }
    }

    /// <summary>Brings a window to the front of its band without activating it (a normal window is lifted above other apps).</summary>
    public static void Front(IntPtr hwnd, bool topmost)
    {
        Raise(hwnd, true);
        if (!topmost) SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Puts a window right behind <paramref name="other"/> (used to step aside for a full-screen app).</summary>
    public static void Behind(IntPtr hwnd, IntPtr other) =>
        SetWindowPos(hwnd, other, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>The Windows "reminder" sound (async), falling back to the default beep.</summary>
    public static void ReminderSound()
    {
        try { if (PlaySound("Notification.Reminder", IntPtr.Zero, 0x00010000 | 0x0001 | 0x0002)) return; } catch { } // SND_ALIAS|SND_ASYNC|SND_NODEFAULT
        System.Media.SystemSounds.Asterisk.Play();
    }

    public static void AddExStyle(IntPtr hwnd, int bits) =>
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | (uint)bits));
}
