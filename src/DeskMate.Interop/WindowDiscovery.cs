using System.Runtime.InteropServices;
using System.Text;

namespace DeskMate.Interop;

/// <summary>
/// 顶层窗口枚举：按进程找出「用户眼里算一个窗口」的候选，用于 Dock 的窗口切换与预览。
/// 过滤规则刻意模仿任务栏的判定，否则会把大量隐藏的工具窗、宿主窗也列出来。
/// </summary>
public static class WindowDiscovery
{
    private const int GwOwner = 4;
    private const int DwmwaCloaked = 14;

    /// <summary>一个可切换的顶层窗口。</summary>
    /// <param name="Handle">窗口句柄。</param>
    /// <param name="ProcessId">所属进程。</param>
    /// <param name="Title">窗口标题。</param>
    /// <param name="Bounds">屏幕矩形（物理像素）。</param>
    public readonly record struct WindowInfo(IntPtr Handle, uint ProcessId, string Title, WindowInterop.WinRect Bounds);

    /// <summary>商店（UWP）应用的顶层窗口类名：窗口框架由 ApplicationFrameHost.exe 提供，不是应用自己的进程。</summary>
    private const string FrameWindowClass = "ApplicationFrameWindow";

    /// <summary>框架窗口里真正承载应用内容的子窗口，它属于应用自己的进程。</summary>
    private const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>
    /// 取指定进程的全部可切换顶层窗口。
    /// <para>
    /// <c>EnumWindows</c> 本身按 Z 序自顶向下遍历，所以返回列表的第一个就是最上面的窗口，
    /// 点击切换时直接取第一个即可，不需要再排一次序。
    /// </para>
    /// <para>
    /// 只认可见窗口：应用自己 <c>hide()</c> 掉的窗口不能从外面 <c>ShowWindow</c> 捞回来。
    /// 缩回托盘的微信 4 就是这么处理的 —— 它的 widget 在 Qt 层已经是隐藏的，把原生窗口显示出来
    /// 只会得到一个没有内容的白框（实测踩过，比再开一个实例更糟）。这种状态交给启动流程。
    /// </para>
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetWindowsOfProcess(uint processId) => GetWindowsOfProcesses([processId]);

    /// <summary>
    /// 窗口「算谁的」。普通窗口就是创建它的进程；商店应用（设置、计算器……）的顶层窗口却是
    /// ApplicationFrameHost.exe 建的，所有商店应用共用这一个进程 —— 按它归属的话，设置和计算器会被并成
    /// 同一个 Dock 图标，图标还是 ApplicationFrameHost 自己那个通用小窗口。
    /// 所以框架窗口要顺着它里面的 CoreWindow 找到应用自己的进程；找不到（应用被挂起）就保持原样。
    /// </summary>
    private static uint OwnerProcessId(IntPtr hwnd, uint rawProcessId)
    {
        if (!string.Equals(ClassOf(hwnd), FrameWindowClass, StringComparison.Ordinal))
        {
            return rawProcessId;
        }

        uint hosted = 0;
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (!string.Equals(ClassOf(child), CoreWindowClass, StringComparison.Ordinal))
            {
                return true;
            }

            GetWindowThreadProcessId(child, out var childProcessId);
            if (childProcessId == 0 || childProcessId == rawProcessId)
            {
                return true;
            }

            hosted = childProcessId;
            return false;
        }, IntPtr.Zero);

        return hosted != 0 ? hosted : rawProcessId;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(64);
        return GetClassNameW(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    /// <summary>
    /// 按进程号批量取窗口，返回顺序保持 Z 序。
    /// <para>
    /// 顺序刻意是「先问窗口是谁的，再问窗口长什么样」：<c>GetWindowThreadProcessId</c> 是本地读，
    /// 而 <see cref="IsSwitchable"/> 里的标题判定要给那个窗口本人发消息 —— 别人家的窗口只要当时没在泵消息，
    /// 那一句就能把整趟扫描按 <c>HungAppTimeout</c> 卡住。先按进程号筛掉绝大多数窗口，
    /// 我们就只对自己关心的那几个才付跨进程的代价。
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetWindowsOfProcesses(IEnumerable<uint> processIds)
    {
        var ids = new HashSet<uint>(processIds);
        var all = new List<WindowInfo>();
        if (ids.Count == 0)
        {
            return all;
        }

        EnumWindows((hwnd, _) =>
        {
            // 先只问「这窗口是谁的」（本地读，不发消息），不是我们要找的就立刻跳过。
            // GetClassNameW 同样是本地读，商店应用的框架窗要靠它认出来。
            GetWindowThreadProcessId(hwnd, out var rawProcessId);
            var owner = OwnerProcessId(hwnd, rawProcessId);
            if (!ids.Contains(owner) || !IsSwitchable(hwnd))
            {
                return true;
            }

            all.Add(new WindowInfo(hwnd, owner, ReadTitle(hwnd), ReadBounds(hwnd)));
            return true;
        }, IntPtr.Zero);

        return all;
    }

    /// <summary>
    /// 一次 <c>EnumWindows</c> 把所有可切换窗口按进程号分组。
    /// 给「哪些进程真的有窗口」这种全表判断用 —— 逐进程各扫一遍窗口表是 O(进程数 × 窗口数)。
    /// </summary>
    public static IReadOnlyDictionary<uint, IReadOnlyList<WindowInfo>> GetWindowsGroupedByProcess()
    {
        var grouped = new Dictionary<uint, IReadOnlyList<WindowInfo>>();

        EnumWindows((hwnd, _) =>
        {
            // 同 GetWindowsOfProcesses：能靠本地读挡掉的一律挡在前面，标题那一句跨进程消息留到最后。
            if (!IsSwitchable(hwnd))
            {
                return true;
            }

            var windowProcessId = OwnerProcessId(hwnd, RawProcessId(hwnd));

            if (!grouped.TryGetValue(windowProcessId, out var list))
            {
                list = new List<WindowInfo>();
                grouped[windowProcessId] = list;
            }

            ((List<WindowInfo>)list).Add(new WindowInfo(hwnd, windowProcessId, ReadTitle(hwnd), ReadBounds(hwnd)));
            return true;
        }, IntPtr.Zero);

        return grouped;
    }

    private static uint RawProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    /// <summary>当前前台窗口。</summary>
    public static IntPtr GetForeground() => WindowInterop.GetForeground();

    private static bool IsSwitchable(IntPtr hwnd)
    {
        if (!WindowInterop.IsVisible(hwnd))
        {
            return false;
        }

        var style = WindowInterop.GetStyle(hwnd);
        var exStyle = WindowInterop.GetExtendedStyle(hwnd);

        // 子窗口与所有者窗口（弹层、提示气泡）不算独立任务。
        if ((style & WindowInterop.WS_CHILD) == WindowInterop.WS_CHILD)
        {
            return false;
        }

        // 工具窗默认不进任务栏，除非它自己声明了 WS_EX_APPWINDOW。
        if ((exStyle & WindowInterop.WS_EX_TOOLWINDOW) == WindowInterop.WS_EX_TOOLWINDOW
            && (exStyle & WindowInterop.WS_EX_APPWINDOW) != WindowInterop.WS_EX_APPWINDOW)
        {
            return false;
        }

        if (GetWindow(hwnd, GwOwner) != IntPtr.Zero)
        {
            return false;
        }

        // 被 DWM 遮罩的窗口（虚拟桌面切走的、挂起的 UWP）看着在，其实点不到。
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        var bounds = ReadBounds(hwnd);
        if (bounds.Width <= 20 || bounds.Height <= 20)
        {
            return false;
        }

        // 没有标题的窗口（宿主窗、工具浮层）不算一个任务。这一句要跨进程问那个窗口本人，
        // 所以排在所有本地读取之后：只有真的像「一个任务窗口」的才付这个代价。
        return HasTitle(hwnd);
    }

    /// <summary>
    /// 窗口有没有标题。这一句要跨进程问那个窗口本人，所以排在所有本地读取之后：
    /// 只有真的「像一个任务窗口」的才付这个代价，别家的窗口我们一个都不问。
    /// <para>
    /// 也试过 <c>SendMessageTimeout(WM_GETTEXTLENGTH, SMTO_ABORTIFHUNG)</c> 给它加个硬超时，
    /// 实测不行：WPF 的窗口对这条消息回答 0 个字符，而 <c>GetWindowTextLength</c> 读到 13 个 ——
    /// 用超时那套会把所有 WPF 窗口判成「没有标题」而整个漏掉。系统自己的问法已经按
    /// <c>HungAppTimeout</c> 封顶，卡住的窗口最多让我们等那么久，本地窗口文本还拿得到。
    /// </para>
    /// </summary>
    private static bool HasTitle(IntPtr hwnd) => GetWindowTextLengthW(hwnd) > 0;

    private static string ReadTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        var buffer = new StringBuilder(length + 2);
        _ = GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static WindowInterop.WinRect ReadBounds(IntPtr hwnd)
        => WindowInterop.TryGetWindowRect(hwnd, out var rect) ? rect : default;
}
