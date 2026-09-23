using System.Runtime.InteropServices;
using System.Windows.Input;

namespace PinCC.UI;

/// <summary>
/// Global hotkeys via RegisterHotKey on the hidden message window. Registration failure (another program already
/// owns the combination) is reported as a conflict; a small list of well-known app shortcuts is also flagged,
/// because RegisterHotKey cannot see keyboard-hook based shortcuts (QQ/WeChat screenshots, PowerToys…).
/// </summary>
public sealed class Hotkeys : IDisposable
{
    /// <summary>Action id → (setting key, label, default).</summary>
    /// <summary>Only "new card" is bound by default (a combination no common app uses); the rest are opt-in.</summary>
    public static readonly (string Key, string Label, string Default)[] Actions =
    {
        ("hk.new", "新建卡片", "Ctrl+Alt+Shift+N"),
        ("hk.paste", "用剪贴板新建", ""),
        ("hk.min", "最小化 / 恢复", ""),
        ("hk.collapse", "全部折叠 / 展开", ""),
        ("hk.tile", "平铺显示", ""),
    };

    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl+Alt+V"] = "Word / WPS 选择性粘贴", ["Ctrl+Alt+C"] = "Word / WPS 复制格式", ["Ctrl+Alt+N"] = "Word 草稿视图", ["Ctrl+Alt+T"] = "Word 商标符号",
        ["Ctrl+Alt+A"] = "QQ / 微信截图", ["Ctrl+Alt+Z"] = "QQ 打开消息", ["Ctrl+Alt+X"] = "QQ 截图（旧版）", ["Ctrl+Alt+Q"] = "QQ 锁定",
        ["Alt+A"] = "微信截图", ["Ctrl+Alt+W"] = "微信", ["Win+Alt+S"] = "便笺 Sticky Notes", ["Win+Alt+N"] = "OneNote",
        ["Win+Ctrl+T"] = "PowerToys 窗口置顶", ["Win+Shift+S"] = "系统截图", ["Win+Shift+T"] = "PowerToys 文字识别", ["Win+Shift+C"] = "PowerToys 取色器",
        ["Alt+Space"] = "PowerToys Run / 窗口菜单", ["Ctrl+Space"] = "输入法切换", ["Ctrl+Shift+Space"] = "输入法", ["Shift+Space"] = "输入法全/半角",
        ["Ctrl+OemPeriod"] = "输入法中/英文标点", ["Ctrl+Alt+Delete"] = "系统", ["Ctrl+Shift+Escape"] = "任务管理器", ["F12"] = "系统保留（调试）",
        ["F1"] = "Snipaste 截图", ["F3"] = "Snipaste 贴图",
    };

    readonly MessageWindow _sink;
    readonly Dictionary<int, Action> _handlers = new();
    int _nextId = 1;

    public Hotkeys(MessageWindow sink)
    {
        _sink = sink;
        _sink.Message += OnMessage;
    }

    /// <summary>Registers <paramref name="gesture"/>; returns null on success or a human-readable conflict.</summary>
    public string? Register(string gesture, Action handler)
    {
        if (!TryParse(gesture, out var mods, out var vk)) return "无法识别的快捷键";
        int id = _nextId++;
        if (!Native.RegisterHotKey(_sink.Handle, id, mods | Native.MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            return err == Native.ERROR_HOTKEY_ALREADY_REGISTERED ? "被其他程序占用（1409）" : $"注册失败（{err}）";
        }
        _handlers[id] = handler;
        return Known.TryGetValue(Normalize(gesture), out var who) ? $"已注册，但可能与「{who}」冲突" : null;
    }

    /// <summary>Checks a combination without keeping it (used by the settings recorder).</summary>
    public string? Probe(string gesture)
    {
        if (!TryParse(gesture, out var mods, out var vk)) return "无法识别的快捷键";
        const int probeId = 0xBFFF;
        if (!Native.RegisterHotKey(_sink.Handle, probeId, mods | Native.MOD_NOREPEAT, vk))
            return Marshal.GetLastWin32Error() == Native.ERROR_HOTKEY_ALREADY_REGISTERED ? "被其他程序占用（1409）" : "无法注册";
        Native.UnregisterHotKey(_sink.Handle, probeId);
        return Known.TryGetValue(Normalize(gesture), out var who) ? $"可能与「{who}」冲突" : null;
    }

    public void Clear()
    {
        foreach (var id in _handlers.Keys) Native.UnregisterHotKey(_sink.Handle, id);
        _handlers.Clear();
    }

    void OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var h)) h();
    }

    public void Dispose()
    {
        Clear();
        _sink.Message -= OnMessage;
    }

    // ---- gesture text <-> (modifiers, virtual key) ----

    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": case "windows": mods |= Native.MOD_WIN; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else if (!Enum.TryParse(raw, true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    /// <summary>"Ctrl+Alt+N" from a key event (null while only modifiers are held).</summary>
    public static string? Format(ModifierKeys mods, Key key)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None)
            return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>
    /// While a settings box records a shortcut: a low-level keyboard hook sees each chord before other programs'
    /// hotkeys or hooks consume it (so a combination QQ already owns can still be recorded and reported).
    /// <paramref name="onChord"/> returns true to swallow the keystroke.
    /// </summary>
    public static IDisposable CaptureChords(Func<string, bool> onChord) => new ChordHook(onChord);

    sealed class ChordHook : IDisposable
    {
        readonly Func<string, bool> _on;
        readonly LowLevelKeyboardProc _proc; // kept alive for the hook's lifetime
        IntPtr _hook;

        public ChordHook(Func<string, bool> on)
        {
            _on = on;
            _proc = Proc;
            _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(null), 0); // WH_KEYBOARD_LL
        }

        IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && (wParam == 0x100 || wParam == 0x104)) // WM_KEYDOWN, WM_SYSKEYDOWN
            {
                int vk = Marshal.ReadInt32(lParam);
                var mods = ModifierKeys.None;
                if (GetAsyncKeyState(0x11) < 0) mods |= ModifierKeys.Control;
                if (GetAsyncKeyState(0x12) < 0) mods |= ModifierKeys.Alt;
                if (GetAsyncKeyState(0x10) < 0) mods |= ModifierKeys.Shift;
                if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) mods |= ModifierKeys.Windows;
                var key = KeyInterop.KeyFromVirtualKey(vk);
                if (mods != ModifierKeys.None && Format(mods, key) is { } g && _on(g)) return new IntPtr(1);
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr module, uint thread);
        [DllImport("user32")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    }

    /// <summary>Canonical text of a gesture ("Win+Ctrl+Alt+Shift+Key"), for comparing bindings.</summary>
    public static string Normalize(string g)
    {
        if (!TryParse(g, out var mods, out _)) return g;
        var key = g.Split('+').Last().Trim();
        var parts = new List<string>();
        if ((mods & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Native.MOD_WIN) != 0) parts.Insert(0, "Win");
        if (TryParse(g, out _, out var vk) && vk == 0xBE) key = "OemPeriod";
        parts.Add(key.Length == 1 ? key.ToUpperInvariant() : key);
        return string.Join("+", parts);
    }
}
