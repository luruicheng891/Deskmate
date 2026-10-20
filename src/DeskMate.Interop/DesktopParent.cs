using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 贴桌面层：把窗口挂到壁纸窗口 WorkerW 之下，使其表现得像桌面图标一样位于所有应用背后。
/// 实验性功能 —— 微软从未公开 0x052C 这条消息，Windows 11 24H2 之后桌面实现改为
/// XAML 岛，某些布局下会挂接失败，因此调用方必须容忍返回 false 并回落到普通置顶模式。
/// </summary>
public static class DesktopParent
{
    /// <summary>未公开消息：请求 Progman 派生出一层可承载子窗口的 WorkerW。</summary>
    private const uint WmSpawnWorker = 0x052C;

    private const uint SmtoNormal = 0x0000;
    private const int MaxWorkerWScan = 32;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    /// <summary>
    /// 找到可承载桌面层窗口的 WorkerW。
    /// 两种已知布局：Win11 里 WorkerW 是 Progman 的子窗口；Win10 里它是 Progman 之后的同级窗口。
    /// </summary>
    public static IntPtr? FindWorkerW()
    {
        var progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return null;
        }

        // 必须先触发这条消息，否则某些系统上根本不存在可挂载的 WorkerW。
        SendMessageTimeout(progman, WmSpawnWorker, new IntPtr(0xD), new IntPtr(1), SmtoNormal, 1000, out _);

        var asChild = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);

        // 挂着 SHELLDLL_DefView 的那个 WorkerW 是桌面图标层，挂上去会把图标遮住，只能要纯壁纸层。
        if (asChild != IntPtr.Zero && FindWindowExW(asChild, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
        {
            return asChild;
        }

        var scanned = 0;
        var previous = IntPtr.Zero;
        while (scanned++ < MaxWorkerWScan)
        {
            previous = FindWindowExW(IntPtr.Zero, previous, "WorkerW", null);
            if (previous == IntPtr.Zero)
            {
                break;
            }

            // 持有 SHELLDLL_DefView 的那个 WorkerW 是桌面本体（图标层），
            // 我们要的是它下面纯粹用来画壁纸的兄弟窗口，挂前者会把图标遮住。
            if (FindWindowExW(previous, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                return previous;
            }
        }

        return null;
    }

    /// <summary>
    /// 把窗口降为 WorkerW 的子窗口。需要同时改样式：顶层窗口直接 SetParent 进子窗口
    /// 会导致坐标按屏幕算、消息路由异常，所以 WS_POPUP 必须换成 WS_CHILD。
    /// <para>
    /// 关键约束：<b>任何一步失败都必须把样式改回去</b>。曾经的做法是先改样式再挂接，
    /// 挂接失败就留下一个「有 WS_CHILD、没有父窗口」的孤儿 —— 它既不在任务栏层也不在桌面层，
    /// 位置失控压到任务栏上，连 EnumWindows 都枚举不到它。
    /// </para>
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <returns>挂接成功返回 true；找不到 WorkerW 或 SetParent 失败返回 false，且窗口保持原样。</returns>
    public static bool Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var workerW = FindWorkerW();
        if (workerW is null)
        {
            // 连可挂载的层都没找到，此时一个字节都不该动这个窗口。
            return false;
        }

        var originalStyle = WindowInterop.GetStyle(hwnd);
        SetStyle(hwnd, originalStyle & ~WindowInterop.WS_POPUP | WindowInterop.WS_CHILD);

        if (SetParent(hwnd, workerW.Value) == IntPtr.Zero)
        {
            SetStyle(hwnd, originalStyle);
            return false;
        }

        WindowInterop.SetWindowPosition(hwnd, WindowInterop.HWND_BOTTOM, 0, 0, 0, 0,
            WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE | WindowInterop.SWP_FRAMECHANGED);
        return true;
    }

    /// <summary>
    /// 脱离桌面层，恢复为普通顶层窗口（父窗口置空即回到桌面根）。
    /// </summary>
    public static bool Detach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var style = WindowInterop.GetStyle(hwnd) & ~WindowInterop.WS_CHILD;
        SetStyle(hwnd, style);
        SetParent(hwnd, IntPtr.Zero);

        WindowInterop.SetWindowPosition(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOZORDER | WindowInterop.SWP_FRAMECHANGED);
        return true;
    }

    private static void SetStyle(IntPtr hwnd, long style)
    {
        _ = SetWindowLongPtrW(hwnd, WindowInterop.GWL_STYLE, new IntPtr(style));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
