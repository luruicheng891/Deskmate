using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 听「别的程序的顶层窗口关了 / 藏了 / 被遮罩了」。Dock 的运行区靠它在应用关掉的那一刻刷新，
/// 不用干等轮询：只轮询的话，应用关了之后图标最坏要多留一整个轮询周期。
/// <para>
/// 只是个「该刷新了」的信号，不带任何结论 —— 窗口有没有真的消失、进程退没退，仍由调用方重新取快照判断。
/// 事件量大（每个控件都是一个窗口），所以回调里先滤掉子窗口、跳过自己进程，剩下的才通知。
/// </para>
/// </summary>
public sealed class WindowChangeWatcher
{
    // EVENT_OBJECT_DESTROY .. EVENT_OBJECT_HIDE（中间夹着 SHOW，一并收；新开窗口也要让运行区补上图标）。
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectHide = 0x8003;

    // UWP / 被切到别的虚拟桌面的窗口不会销毁，而是被 DWM 遮罩（cloak），关闭它们走的是这一对。
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;

    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjidWindow = 0;
    private const int ChildIdSelf = 0;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // 委托必须存在字段里：非托管侧只拿得到函数指针，委托被 GC 回收后下一次回调就是野指针。
    private readonly WinEventDelegate _callback;
    private IntPtr _lifecycleHook;
    private IntPtr _cloakHook;
    private Action? _onChanged;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    /// <summary>构造监听器（此时还没装钩子，调 <see cref="Start"/> 才开始听）。</summary>
    public WindowChangeWatcher() => _callback = OnEvent;

    /// <summary>钩子是否已经装上。</summary>
    public bool IsRunning => _lifecycleHook != IntPtr.Zero;

    /// <summary>
    /// 开始监听。回调在<b>调用本方法的线程</b>上执行（WINEVENT_OUTOFCONTEXT），
    /// 所以必须在有消息泵的 UI 线程上调用。已经在听就只换回调。
    /// </summary>
    public void Start(Action onChanged)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        if (IsRunning)
        {
            return;
        }

        _lifecycleHook = SetWinEventHook(
            EventObjectDestroy, EventObjectHide, IntPtr.Zero, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);

        _cloakHook = SetWinEventHook(
            EventObjectCloaked, EventObjectUncloaked, IntPtr.Zero, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    /// <summary>停止监听并卸钩子。没在听就是空操作。</summary>
    public void Stop()
    {
        if (_lifecycleHook != IntPtr.Zero)
        {
            UnhookWinEvent(_lifecycleHook);
            _lifecycleHook = IntPtr.Zero;
        }

        if (_cloakHook != IntPtr.Zero)
        {
            UnhookWinEvent(_cloakHook);
            _cloakHook = IntPtr.Zero;
        }

        _onChanged = null;
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint eventTime)
    {
        // 只要「窗口本身」的事件：控件内部的对象（菜单项、滚动条……）也会发这几个事件，量大得多。
        if (idObject != ObjidWindow || idChild != ChildIdSelf || hwnd == IntPtr.Zero)
        {
            return;
        }

        // 子窗口（按钮、编辑框这些）开合不影响「有没有任务窗口」。
        // 已经销毁的窗口 GetParent 也返回 0，会放行，宁可多通知一次，调用方会合并。
        if (GetParent(hwnd) != IntPtr.Zero)
        {
            return;
        }

        _onChanged?.Invoke();
    }
}
