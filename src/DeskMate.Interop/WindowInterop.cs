using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskMate.Interop;

/// <summary>
/// 窗口扩展样式、位置与前台查询相关的 Win32 封装。
/// 仅面向 win-x64，因此直接使用 GetWindowLongPtr/SetWindowLongPtr（32 位系统需退回 GetWindowLong）。
/// </summary>
public static class WindowInterop
{
    /// <summary>取/设扩展样式的索引。</summary>
    public const int GWL_EXSTYLE = -20;

    /// <summary>取/设普通样式的索引。</summary>
    public const int GWL_STYLE = -16;

    /// <summary>不在任务栏出现，也不参与 Alt+Tab。</summary>
    public const long WS_EX_TOOLWINDOW = 0x00000080L;

    /// <summary>点击不激活窗口，Dock 必须带它否则抢走前台焦点。</summary>
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    /// <summary>分层窗口，配合 Per-Pixel Alpha 实现整体透明。</summary>
    public const long WS_EX_LAYERED = 0x00080000L;

    /// <summary>鼠标消息穿透到下层窗口。</summary>
    public const long WS_EX_TRANSPARENT = 0x00000020L;

    /// <summary>强制在任务栏显示（贴桌面层模式需要撤销它）。</summary>
    public const long WS_EX_APPWINDOW = 0x00040000L;

    /// <summary>子窗口样式，SetParent 到 WorkerW 时需要切换。</summary>
    public const long WS_CHILD = 0x40000000L;

    /// <summary>弹出窗口样式，贴桌面层时需要摘除。</summary>
    public const long WS_POPUP = 0x80000000L;

    /// <summary>标题栏样式位（WS_BORDER | WS_DLGFRAME），用于区分真全屏与普通最大化窗口。</summary>
    public const long WS_CAPTION = 0x00C00000L;

    /// <summary>插入到 Z 序最顶。</summary>
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>取消置顶。</summary>
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    /// <summary>插到 Z 序最底（贴桌面层用）。</summary>
    public static readonly IntPtr HWND_BOTTOM = new(1);

    /// <summary>不改尺寸。</summary>
    public const uint SWP_NOSIZE = 0x0001;

    /// <summary>不改位置。</summary>
    public const uint SWP_NOMOVE = 0x0002;

    /// <summary>不改 Z 序。</summary>
    public const uint SWP_NOZORDER = 0x0004;

    /// <summary>不激活窗口。</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>通知非客户区已变化，样式改动后必须带。</summary>
    public const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>显示窗口。</summary>
    public const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out WinRect lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    /// <summary>读取扩展样式位。</summary>
    public static long GetExtendedStyle(IntPtr hwnd) => GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();

    /// <summary>读取普通样式位。</summary>
    public static long GetStyle(IntPtr hwnd) => GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();

    /// <summary>按位或写入扩展样式。</summary>
    public static void AddExtendedStyles(IntPtr hwnd, long styles)
        => SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(GetExtendedStyle(hwnd) | styles));

    /// <summary>按位与非清掉扩展样式。</summary>
    public static void RemoveExtendedStyles(IntPtr hwnd, long styles)
        => SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(GetExtendedStyle(hwnd) & ~styles));

    /// <summary>设置 Dock 不占任务栏、不抢焦点的样式组合。</summary>
    public static void ApplyToolWindowStyle(IntPtr hwnd)
    {
        AddExtendedStyles(hwnd, WS_EX_TOOLWINDOW);
        ApplyNoActivateStyle(hwnd);
        // 样式改动必须 FRAMECHANGED，否则要等下一次窗口尺寸变化才生效。
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>让窗口点击时不夺取前台焦点。</summary>
    public static void ApplyNoActivateStyle(IntPtr hwnd) => AddExtendedStyles(hwnd, WS_EX_NOACTIVATE);

    /// <summary>撤销 <see cref="ApplyNoActivateStyle"/>。</summary>
    public static void ClearNoActivateStyle(IntPtr hwnd) => RemoveExtendedStyles(hwnd, WS_EX_NOACTIVATE);

    /// <summary>置顶 / 取消置顶，不改变位置和大小，不激活窗口。</summary>
    public static bool SetTopmost(IntPtr hwnd, bool topmost)
        => SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>移动窗口到指定屏幕坐标（物理像素），不激活。</summary>
    public static bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height)
        => SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

    /// <summary>包装 SetWindowPos，供贴桌面层等需要完全控制的场景使用。</summary>
    public static bool SetWindowPosition(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags)
        => SetWindowPos(hwnd, insertAfter, x, y, width, height, flags);

    /// <summary>窗口是否可见。</summary>
    public static bool IsVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

    /// <summary>窗口是否处于最大化状态。</summary>
    public static bool IsMaximized(IntPtr hwnd) => IsZoomed(hwnd);

    /// <summary>窗口是否带标题栏。真全屏的游戏/视频没有，最大化浏览器有。</summary>
    public static bool HasCaption(IntPtr hwnd) => (GetStyle(hwnd) & WS_CAPTION) == WS_CAPTION;

    /// <summary>当前前台窗口句柄，失败返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr GetForeground() => GetForegroundWindow();

    /// <summary>取窗口屏幕矩形（物理像素）。</summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out WinRect rect)
    {
        rect = default;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
    }

    /// <summary>按窗口类名查找顶层窗口。</summary>
    public static IntPtr FindWindow(string className) => FindWindowW(className, null);

    /// <summary>
    /// 系统认为「两下算一次双击」的时间窗（毫秒，默认 500）。
    /// WPF 的 <c>SystemParameters</c> 只暴露了鼠标悬停时长，双击时长得自己问 user32。
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>双击时间窗（毫秒）。拿不到时退回默认的 500。</summary>
    public static int DoubleClickWindowMs
    {
        get
        {
            var ms = GetDoubleClickTime();
            return ms == 0 ? 500 : (int)ms;
        }
    }

    /// <summary>
    /// 列出「可能在画壁纸的桌面窗口」，按 Z 序自顶向下；<c>Progman</c> 永远排在最后当兜底。
    /// <para>
    /// 不能写死 <c>Progman</c>：装了动态壁纸（本机是 Wallpaper Engine）之后，屏幕上那幅是另一个
    /// <c>WorkerW</c> 画的，<c>Progman</c> 里还留着上一张静态壁纸 —— 从它身上裁一条盖到屏幕底边，
    /// 就是一片和周围完全对不上的颜色（实测踩过，换壁纸后左下角变成了一块棕褐色）。
    /// </para>
    /// <para>
    /// 判据：可见、铺满整屏、类名是 Progman/WorkerW、且自己不挂着 <c>SHELLDLL_DefView</c>
    /// —— 挂着图标那一层的窗口画的是桌面图标，背景透明，裁出来是块纯色。
    /// 调用方要<strong>逐个试</strong>：动态壁纸那层是 GPU 合成的，某些取法拿回来是纯黑，
    /// 那时候退回 Progman 至少还是一张壁纸。
    /// </para>
    /// </summary>
    public static IReadOnlyList<IntPtr> WallpaperHosts(int screenWidth, int screenHeight)
    {
        var hosts = new List<IntPtr>();

        // 委托必须是局部变量并被闭包引用着，EnumWindows 是同步遍历，调用期间它一直在栈上。
        EnumWindowsProc probe = (hwnd, _) =>
        {
            if (IsWallpaperLayer(hwnd, screenWidth, screenHeight))
            {
                hosts.Add(hwnd);
            }

            return true;
        };

        EnumWindows(probe, IntPtr.Zero);

        var progman = FindWindow(ProgmanClassName);
        if (progman != IntPtr.Zero && !hosts.Contains(progman))
        {
            hosts.Add(progman);
        }

        return hosts;
    }

    private static bool IsWallpaperLayer(IntPtr hwnd, int screenWidth, int screenHeight)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd))
        {
            return false;
        }

        var className = ClassNameOf(hwnd);
        if (className != ProgmanClassName && className != WorkerWClassName)
        {
            return false;
        }

        if (!TryGetWindowRect(hwnd, out var rect) || rect.Right - rect.Left < screenWidth || rect.Bottom - rect.Top < screenHeight)
        {
            return false;
        }

        return FindWindowEx(hwnd, IntPtr.Zero, DesktopIconsClassName, null) == IntPtr.Zero;
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(64);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private const string ProgmanClassName = "Progman";
    private const string WorkerWClassName = "WorkerW";
    private const string DesktopIconsClassName = "SHELLDLL_DefView";

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    /// <summary>
    /// <paramref name="candidate"/> 在 Z 序上是否压在 <paramref name="reference"/> 上面。
    /// <para>
    /// 不能用「某个像素归谁」来判断这件事：任务栏是带逐像素透明的分层窗口，
    /// 探测点落在它透明的那一段时 <c>WindowFromPoint</c> 会直接穿透、返回我们自己的窗口，
    /// 于是「明明被压着」却判成「没被压着」—— 图标照画，遮罩/Dock 却以为一切正常。
    /// Z 序链表没有这个歧义。
    /// </para>
    /// </summary>
    public static bool IsAbove(IntPtr candidate, IntPtr reference)
    {
        if (candidate == IntPtr.Zero || reference == IntPtr.Zero)
        {
            return false;
        }

        // 置顶组里通常只有寥寥几个窗口，走不了几步；加个上限防异常链表死循环。
        var walked = 0;
        for (var current = GetWindow(reference, GwHwndPrev); current != IntPtr.Zero; current = GetWindow(current, GwHwndPrev))
        {
            if (current == candidate)
            {
                return true;
            }

            if (++walked > MaxZWalk)
            {
                return false;
            }
        }

        return false;
    }

    private const uint GwHwndPrev = 3;
    private const int MaxZWalk = 64;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    /// <summary>取鼠标指针的物理像素坐标，Dock 自动隐藏的 5px 热区判定要用。</summary>
    public static bool TryGetCursorPhysicalPosition(out WinPoint position)
    {
        position = default;
        return GetCursorPos(ref position);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref WinPoint lpPoint);

    /// <summary>物理像素坐标点。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WinPoint
    {
        /// <summary>X 坐标。</summary>
        public int X;

        /// <summary>Y 坐标。</summary>
        public int Y;
    }

    /// <summary>窗口矩形，物理像素，Right/Bottom 为开区间。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WinRect
    {
        /// <summary>左边界。</summary>
        public int Left;

        /// <summary>上边界。</summary>
        public int Top;

        /// <summary>右边界。</summary>
        public int Right;

        /// <summary>下边界。</summary>
        public int Bottom;

        /// <summary>宽度。</summary>
        public readonly int Width => Right - Left;

        /// <summary>高度。</summary>
        public readonly int Height => Bottom - Top;

        /// <summary>逐字段比较，用于「窗口是否铺满显示器」判定。</summary>
        public readonly bool Equals(WinRect other)
            => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
    }
}
