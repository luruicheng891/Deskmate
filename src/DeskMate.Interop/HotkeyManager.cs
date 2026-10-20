using System.Windows.Interop;

namespace DeskMate.Interop;

/// <summary>
/// 全局快捷键：把 "Alt+Space" 解析成修饰键 + 虚拟键码，注册到隐藏窗口的
/// <see cref="HwndSource"/>，并在 WM_HOTKEY 到来时回调对应动作。
/// </summary>
public static class HotkeyManager
{
    /// <summary>WM_HOTKEY。</summary>
    public const int WmHotkey = 0x0400;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkSpace = 0x20;
    private const uint VkReturn = 0x0D;
    private const uint VkEscape = 0x1B;
    private const uint VkTab = 0x09;
    private const uint VkBack = 0x08;
    private const uint VkDelete = 0x2E;
    private const uint VkInsert = 0x2D;
    private const uint VkHome = 0x24;
    private const uint VkEnd = 0x23;
    private const uint VkPrior = 0x21;
    private const uint VkNext = 0x22;
    private const uint VkLeft = 0x25;
    private const uint VkUp = 0x26;
    private const uint VkRight = 0x27;
    private const uint VkDown = 0x28;
    private const uint VkSnapshot = 0x2C;
    private const uint VkF1 = 0x70;

    private static readonly Dictionary<string, uint> SpecialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = VkSpace,
        ["空格"] = VkSpace,
        ["enter"] = VkReturn,
        ["return"] = VkReturn,
        ["esc"] = VkEscape,
        ["escape"] = VkEscape,
        ["tab"] = VkTab,
        ["backspace"] = VkBack,
        ["delete"] = VkDelete,
        ["del"] = VkDelete,
        ["insert"] = VkInsert,
        ["home"] = VkHome,
        ["end"] = VkEnd,
        ["pageup"] = VkPrior,
        ["pagedown"] = VkNext,
        ["left"] = VkLeft,
        ["up"] = VkUp,
        ["right"] = VkRight,
        ["down"] = VkDown,
        ["printscreen"] = VkSnapshot,
        ["prtsc"] = VkSnapshot,
    };

    private static readonly Dictionary<string, uint> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alt"] = ModAlt,
        ["ctrl"] = ModControl,
        ["control"] = ModControl,
        ["shift"] = ModShift,
        ["win"] = ModWin,
        ["windows"] = ModWin,
        ["super"] = ModWin,
    };

    private static readonly Dictionary<int, Action> _callbacks = new();
    private static HwndSource? _source;
    private static int _nextId = 0x5000;

    /// <summary>把快捷键投递到指定窗口过程。必须由承载托盘的隐藏窗口调用一次。</summary>
    public static void Attach(HwndSource source)
    {
        Detach();
        _source = source;
        source.AddHook(WndProc);
    }

    /// <summary>摘除消息钩子并注销全部快捷键。</summary>
    public static void Detach()
    {
        if (_source is null)
        {
            _callbacks.Clear();
            return;
        }

        try
        {
            Unregister();
            _source.RemoveHook(WndProc);
        }
        catch (ObjectDisposedException)
        {
            // 宿主窗口已经销毁：钩子与热键随 HWND 一起失效，这里不需要再做处理。
        }
        finally
        {
            _source = null;
        }
    }

    /// <summary>
    /// 注册一个全局快捷键。
    /// </summary>
    /// <param name="hotkey">形如 "Alt+Space" / "Ctrl+Shift+D"。</param>
    /// <param name="callback">命中时的动作，在 UI 线程执行。</param>
    /// <param name="error">失败原因，成功为 null。</param>
    /// <returns>是否注册成功；被其它程序占用时返回 false。</returns>
    public static bool Register(string hotkey, Action callback, out string? error)
    {
        error = null;

        if (_source is null)
        {
            error = "快捷键宿主窗口尚未就绪。";
            return false;
        }

        if (!TryParse(hotkey, out var modifiers, out var virtualKey))
        {
            error = $"无法识别的快捷键“{hotkey}”，请使用 Alt/Ctrl/Shift/Win 加一个按键。";
            return false;
        }

        if (_callbacks.Count >= 16)
        {
            error = "已注册快捷键数量达到上限。";
            return false;
        }

        var id = _nextId++;
        // MOD_NOREPEAT 防止按住不放时反复触发，Windows 7+ 必须显式带上。
        if (!RegisterHotKey(_source.Handle, id, modifiers | ModNoRepeat, virtualKey))
        {
            error = $"快捷键“{hotkey}”已被系统或其它程序占用。";
            return false;
        }

        _callbacks[id] = callback;
        return true;
    }

    /// <summary>注销全部快捷键。</summary>
    public static void Unregister()
    {
        if (_source is null)
        {
            _callbacks.Clear();
            return;
        }

        foreach (var id in _callbacks.Keys)
        {
            UnregisterHotKey(_source.Handle, id);
        }

        _callbacks.Clear();
    }

    /// <summary>
    /// 解析快捷键文本。要求至少带一个修饰键，避免把 "J" 这种单键注册成全局拦截。
    /// </summary>
    /// <param name="hotkey">快捷键文本。</param>
    /// <param name="modifiers">MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN 的组合。</param>
    /// <param name="virtualKey">虚拟键码。</param>
    /// <returns>解析是否成功。</returns>
    public static bool TryParse(string? hotkey, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return false;
        }

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (Modifiers.TryGetValue(part, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (virtualKey != 0)
            {
                // 已经有主键了，说明写了两个非修饰键。
                return false;
            }

            if (!TryResolveKey(part, out virtualKey))
            {
                return false;
            }
        }

        return modifiers != 0 && virtualKey != 0;
    }

    private static bool TryResolveKey(string key, out uint virtualKey)
    {
        virtualKey = 0;

        if (SpecialKeys.TryGetValue(key, out var special))
        {
            virtualKey = special;
            return true;
        }

        if (key.Length == 1 && (char.IsLetterOrDigit(key[0])))
        {
            var upper = char.ToUpperInvariant(key[0]);
            // A-Z / 0-9 的虚拟键码就是其大写 ASCII 码。
            virtualKey = upper;
            return true;
        }

        if (key.Length is 2 or 3 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key.AsSpan(1), out var number)
            && number is >= 1 and <= 24)
        {
            virtualKey = VkF1 + (uint)(number - 1);
            return true;
        }

        return false;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            callback();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
