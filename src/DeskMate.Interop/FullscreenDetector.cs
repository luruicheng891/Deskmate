using System.Runtime.InteropServices;
using System.Text;

namespace DeskMate.Interop;

/// <summary>
/// 全屏检测：判定前台窗口是否铺满所在显示器，并可监听前台切换事件。
/// Dock 靠它在游戏/视频/演示时自动让路。
/// </summary>
public static class FullscreenDetector
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint MonitorDefaultToNearest = 0x0002;
    private const int DwmwaCloaked = 14;

    /// <summary>
    /// 这些类名对应的窗口铺满屏幕属于系统常态，不是用户在用全屏应用：
    /// Progman/WorkerW 是桌面与壁纸层，Shell_TrayWnd 是任务栏，
    /// CoreWindow 系是开始菜单/通知中心一类系统 UI。
    /// </summary>
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "Windows.UIComposition.WindowCore",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
    };

    private static WinEventDelegate? _callback;
    private static IntPtr _hook;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public WindowInterop.WinRect rcMonitor;
        public WindowInterop.WinRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>当前前台窗口是否处于全屏。</summary>
    public static bool IsForegroundFullscreen() => IsFullscreen(WindowInterop.GetForeground());

    /// <summary>
    /// 判定规则：窗口<b>覆盖</b>整个显示器（需求七的「窗口矩形 == 显示器矩形」在实际游戏上必须放宽成包含关系）。
    /// 用相等会漏：游戏跑非原生分辨率、DWM 的不可见边框、DPI 取整，矩形都会差几像素，
    /// 表现就是全屏时 Dock 直接盖在游戏上面。
    /// 另加三条必要排除：DWM 已 cloaked 的挂起 UWP 窗口、系统外壳窗口、以及带标题栏的普通最大化窗口
    /// （任务栏自动隐藏时它也能铺满整屏，不排除的话用户一最大化浏览器 Dock 就没了）。
    /// </summary>
    public static bool IsFullscreen(IntPtr hwnd) => Evaluate(hwnd).Fullscreen;

    /// <summary>
    /// 「这个窗口算不算全屏、为什么」。排障专用：用户报「全屏时 Dock 还挡着」时，
    /// 日志里这一行直接指出是被哪一条挡下的，不用靠猜。
    /// </summary>
    public static string Explain(IntPtr hwnd) => Evaluate(hwnd).Reason;

    private static (bool Fullscreen, string Reason) Evaluate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return (false, "没有前台窗口");
        }

        if (!WindowInterop.IsVisible(hwnd))
        {
            return (false, "前台窗口不可见");
        }

        if (IsShellWindow(hwnd))
        {
            return (false, "是系统外壳窗口（桌面 / 任务栏 / 开始菜单那一类）");
        }

        if (IsOwnProcessWindow(hwnd))
        {
            return (false, "是我们自己的窗口");
        }

        // 被 DWM 遮罩的窗口（切换到别的虚拟桌面、或挂起的 UWP）矩形依然铺满，必须排除。
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return (false, "被 DWM 遮罩（在别的虚拟桌面，或是挂起的 UWP）");
        }

        if (!WindowInterop.TryGetWindowRect(hwnd, out var windowRect))
        {
            return (false, "取不到窗口矩形");
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return (false, "取不到它所在的显示器");
        }

        var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref mi))
        {
            return (false, "GetMonitorInfo 失败");
        }

        // 覆盖即算全屏：允许窗口比显示器大（DWM 不可见边框）或差几像素。
        if (!CoversMonitor(windowRect, mi.rcMonitor))
        {
            return (false, $"没铺满显示器：窗口 [{windowRect.Left},{windowRect.Top},{windowRect.Right},{windowRect.Bottom}]"
                + $" / 显示器 [{mi.rcMonitor.Left},{mi.rcMonitor.Top},{mi.rcMonitor.Right},{mi.rcMonitor.Bottom}]");
        }

        if (WindowInterop.IsMaximized(hwnd) && WindowInterop.HasCaption(hwnd))
        {
            // 铺满了却被这一条挡下，是最值得进日志的一种：说明判据可能偏严。
            return (false, "铺满了，但被当成「最大化 + 带标题栏」的普通窗口");
        }

        return (true, "铺满整个显示器、不是外壳窗口、也没带标题栏");
    }

    /// <summary>
    /// 纯几何那一条：窗口矩形<strong>覆盖</strong>显示器矩形才算，不要求相等。
    /// 判相等的写法在真游戏上会漏 —— 非原生分辨率、DWM 的不可见边框、DPI 取整都会差几像素，
    /// 表现就是「全屏时 Dock 直接盖在画面上」。
    /// </summary>
    public static bool CoversMonitor(WindowInterop.WinRect window, WindowInterop.WinRect monitor)
        => window.Left <= monitor.Left
            && window.Top <= monitor.Top
            && window.Right >= monitor.Right
            && window.Bottom >= monitor.Bottom;

    /// <summary>
    /// 开始监听前台窗口切换。回调在<b>调用本方法的线程</b>上执行（WINEVENT_OUTOFCONTEXT），
    /// 因此必须在有消息泵 UI 线程上调用。
    /// </summary>
    public static void StartWatching(Action<IntPtr> onForegroundChanged)
    {
        StopWatching();

        var foregroundChanged = onForegroundChanged ?? throw new ArgumentNullException(nameof(onForegroundChanged));

        // 委托必须存到静态字段：非托管侧只拿得到函数指针，一旦委托被 GC 回收，
        // 下次回调就是野指针，表现为随机的原生崩溃。
        _callback = (_, eventType, hwnd, _, _, _, _) =>
        {
            if (eventType == EventSystemForeground)
            {
                foregroundChanged(hwnd);
            }
        };

        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback,
                                0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    /// <summary>取消监听并释放钩子。</summary>
    public static void StopWatching()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _callback = null;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 && ShellWindowClasses.Contains(buffer.ToString());
    }

    private static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == (uint)Environment.ProcessId;
    }
}
