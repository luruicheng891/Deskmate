using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 前台窗口的输入法中英状态：跨进程读取与切换。
/// <para>
/// 路线是经典的一套 —— <c>GetGUIThreadInfo</c> 拿前台线程的焦点窗口，
/// <c>ImmGetDefaultIMEWnd</c> 拿它挂着的默认 IME 窗口，再向那个窗口跨进程发
/// <c>WM_IME_CONTROL</c> 读开关（<c>IMC_GETOPENSTATUS</c>）与转换模式
/// （<c>IMC_GETCONVERSIONMODE</c>）。微软拼音等主流 IME 都响应这一套；个别第三方 IME
/// 不回消息时按「未知」处理（<see cref="ImeState.Available"/> = false）。
/// </para>
/// </summary>
public static class ImeStatus
{
    /// <param name="HasImeLayout">前台线程是不是挂着中文语系的键盘布局；false 时调用方应该把指示器藏起来。</param>
    /// <param name="Available">IME 窗口响应了查询（ false = 读不到，按未知显示）。</param>
    /// <param name="Open">IME 开着（关着 = 英文直接输入）。</param>
    /// <param name="Native">转换模式是本地语（中文），关着 = IME 开着但打的是英文。</param>
    public sealed record ImeState(bool HasImeLayout, bool Available, bool Open, bool Native);

    private const uint WmImeControl = 0x0283;

    private const IntPtr ImcGetConversionMode = (IntPtr)1;
    private const IntPtr ImcSetConversionMode = (IntPtr)2;
    private const IntPtr ImcGetOpenStatus = (IntPtr)5;
    private const IntPtr ImcSetOpenStatus = (IntPtr)6;

    /// <summary>IME_CMODE_NATIVE：本地语（中文）模式位。</summary>
    private const long ImeCmodeNative = 0x0001;

    /// <summary>目标窗口挂死时立刻放弃，别把 UI 线程拖住。</summary>
    private const uint SmtoAbortIfHung = 0x0002;

    private const uint ImeQueryTimeoutMs = 100;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr ActiveHwnd;
        public IntPtr FocusHwnd;
        public IntPtr CaptureHwnd;
        public IntPtr MenuOwnerHwnd;
        public IntPtr MoveSizeHwnd;
        public IntPtr CaretHwnd;
        public WindowInterop.WinRect CaretRect;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    public static ImeState? Probe()
    {
        var foreground = WindowInterop.GetForeground();
        if (foreground == IntPtr.Zero)
        {
            return null;
        }

        var thread = GetWindowThreadProcessId(foreground, out _);

        // 中文语系 LCID 全族（0x0404/0x0804/0x0C04/…）低字节的低半字节都是 4；
        // 前台是英文键盘布局时就没有「中英」可言，指示器直接藏掉。
        var language = GetKeyboardLayout(thread).ToInt64() & 0xFFFF;
        if ((language & 0xFF) != 0x04)
        {
            return new ImeState(false, false, false, false);
        }

        var imeWindow = GetForegroundImeWindow(thread, foreground);
        if (imeWindow == IntPtr.Zero)
        {
            return new ImeState(true, false, false, false);
        }

        if (SendMessageTimeout(imeWindow, WmImeControl, ImcGetOpenStatus, IntPtr.Zero, SmtoAbortIfHung, ImeQueryTimeoutMs, out var open) == IntPtr.Zero)
        {
            return new ImeState(true, false, false, false);
        }

        var openStatus = open != IntPtr.Zero;
        if (SendMessageTimeout(imeWindow, WmImeControl, ImcGetConversionMode, IntPtr.Zero, SmtoAbortIfHung, ImeQueryTimeoutMs, out var mode) == IntPtr.Zero)
        {
            return new ImeState(true, openStatus, false, false);
        }

        return new ImeState(true, true, openStatus, (mode.ToInt64() & ImeCmodeNative) != 0);
    }

    /// <summary>
    /// 把前台窗口的 IME 切到本地语（中文）或英文。
    /// 中文路径与系统 Shift 切换同义：转换模式置 NATIVE 位，并把 IME 打开；
    /// 英文路径只清 NATIVE 位（IME 保持打开，等价 Shift 切英文）。
    /// 个别 IME 不响应 <c>WM_IME_CONTROL</c> 写入，返回 false 由调用方兜底。
    /// </summary>
    public static bool TrySetNative(bool native)
    {
        var foreground = WindowInterop.GetForeground();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var thread = GetWindowThreadProcessId(foreground, out _);
        var imeWindow = GetForegroundImeWindow(thread, foreground);
        if (imeWindow == IntPtr.Zero)
        {
            return false;
        }

        if (SendMessageTimeout(imeWindow, WmImeControl, ImcGetConversionMode, IntPtr.Zero, SmtoAbortIfHung, ImeQueryTimeoutMs, out var mode) == IntPtr.Zero)
        {
            return false;
        }

        var target = native ? (mode.ToInt64() | ImeCmodeNative) : (mode.ToInt64() & ~ImeCmodeNative);
        if (SendMessageTimeout(imeWindow, WmImeControl, ImcSetConversionMode, (IntPtr)target, SmtoAbortIfHung, ImeQueryTimeoutMs, out _) == IntPtr.Zero)
        {
            return false;
        }

        if (native)
        {
            _ = SendMessageTimeout(imeWindow, WmImeControl, ImcSetOpenStatus, (IntPtr)1, SmtoAbortIfHung, ImeQueryTimeoutMs, out _);
        }

        return true;
    }

    /// <summary>前台线程焦点窗口挂着的默认 IME 窗口；拿不到返回零。</summary>
    private static IntPtr GetForegroundImeWindow(uint thread, IntPtr foreground)
    {
        var focus = foreground;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(thread, ref info) && info.FocusHwnd != IntPtr.Zero)
        {
            focus = info.FocusHwnd;
        }

        return ImmGetDefaultIMEWnd(focus);
    }
}
