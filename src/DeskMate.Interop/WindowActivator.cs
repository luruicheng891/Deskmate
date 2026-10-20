using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 窗口激活、最小化与恢复。
/// </summary>
public static class WindowActivator
{
    private const int SwShow = 5;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const byte VkMenu = 0x12;
    private const uint KeyeventKeyup = 0x0002;
    private const uint WmClose = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>窗口是否被最小化。</summary>
    public static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);

    /// <summary>
    /// 关掉一个窗口：给它的消息队列投 <c>WM_CLOSE</c>，等价于用户自己点标题栏的 ×。
    /// <para>
    /// 刻意不用 <c>TerminateProcess</c> 那种"直接杀掉"：那样未保存的文档会连带没了，
    /// 后台任务也来不及清理。走 WM_CLOSE 的话，程序自己决定要不要弹「保存吗」，
    /// 一个窗口有多个文档时它也能按自己的逻辑处理。所以这里返回的是"消息投出去了"，
    /// 不代表窗口一定已经没了。
    /// </para>
    /// </summary>
    public static bool Close(IntPtr hwnd) => hwnd != IntPtr.Zero && PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// 把窗口带到前台；最小化的先恢复。
    /// <para>
    /// Windows 有「前台锁定」：非前台进程不能随便抢焦点。更坑的是 <c>SetForegroundWindow</c>
    /// 可能返回 true 却只是闪了一下任务栏按钮，所以这里一律以「前台句柄真的变成它了」为准，
    /// 而不是信返回值。依次尝试：直接设前台 → 补发一次 Alt 键（让系统认为是用户输入，
    /// 任务栏与切换器都在用这招）→ 退回 <c>SwitchToThisWindow</c>（Alt+Tab 走的路）。
    /// </para>
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }

        if (SetForegroundWindow(hwnd) && IsNowForeground(hwnd))
        {
            return true;
        }

        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyeventKeyup, UIntPtr.Zero);

        if (SetForegroundWindow(hwnd) && IsNowForeground(hwnd))
        {
            return true;
        }

        TrySwitch(hwnd);

        return IsNowForeground(hwnd);
    }

    /// <summary>最小化窗口。</summary>
    public static bool Minimize(IntPtr hwnd) => hwnd != IntPtr.Zero && ShowWindow(hwnd, SwMinimize);

    /// <summary>
    /// 任务栏式的点击语义：已经在前台就收起，否则切过去。
    /// </summary>
    /// <param name="hwnd">目标窗口。</param>
    /// <returns>实际执行的动作（activate / minimize / activate-blocked），供日志使用。</returns>
    /// <remarks>
    /// 即使前台切换被系统挡下，也返回 activate-blocked 而不是 nothing ——
    /// 调用方据此判断「这个应用有窗口、我已经处理过了」，绝不该再去起一个新实例。
    /// </remarks>
    public static string ToggleForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "nothing";
        }

        if (WindowInterop.GetForeground() == hwnd && !IsIconic(hwnd))
        {
            return Minimize(hwnd) ? "minimize" : "nothing";
        }

        return Activate(hwnd) ? "activate" : "activate-blocked";
    }

    private static bool IsNowForeground(IntPtr hwnd) => WindowInterop.GetForeground() == hwnd;

    private static bool TrySwitch(IntPtr hwnd)
    {
        try
        {
            SwitchToThisWindow(hwnd, true);
            return WindowInterop.GetForeground() == hwnd;
        }
        catch (DllNotFoundException)
        {
            // 极老的系统没有这个导出；调用方会看到激活失败，不该因此崩掉。
            return false;
        }
    }
}
