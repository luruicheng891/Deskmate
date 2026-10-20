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

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

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
    public static IReadOnlyList<WindowInfo> GetWindowsOfProcess(uint processId)
    {
        var results = new List<WindowInfo>(8);

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (windowProcessId != processId || !IsSwitchable(hwnd))
            {
                return true;
            }

            results.Add(new WindowInfo(hwnd, processId, ReadTitle(hwnd), ReadBounds(hwnd)));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    /// <summary>按进程号批量取窗口，返回顺序保持 Z 序。</summary>
    public static IReadOnlyList<WindowInfo> GetWindowsOfProcesses(IEnumerable<uint> processIds)
    {
        var ids = new HashSet<uint>(processIds);
        var all = new List<WindowInfo>();

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (!ids.Contains(windowProcessId) || !IsSwitchable(hwnd))
            {
                return true;
            }

            all.Add(new WindowInfo(hwnd, windowProcessId, ReadTitle(hwnd), ReadBounds(hwnd)));
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
            GetWindowThreadProcessId(hwnd, out var windowProcessId);
            if (!IsSwitchable(hwnd))
            {
                return true;
            }

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

    /// <summary>当前前台窗口。</summary>
    public static IntPtr GetForeground() => WindowInterop.GetForeground();

    private static bool IsSwitchable(IntPtr hwnd)
    {
        if (!WindowInterop.IsVisible(hwnd))
        {
            return false;
        }

        if (GetWindowTextLengthW(hwnd) == 0)
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
        return bounds.Width > 20 && bounds.Height > 20;
    }

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
