using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 顶层窗口「动了 / 出现了 / 隐藏了」的即时报：外壳把某个窗口挪位或显示的每一步都会触发
/// <c>EVENT_OBJECT_LOCATIONCHANGE</c> / <c>EVENT_OBJECT_SHOW</c>，比任何轮询都快。
/// <para>
/// 当前用途：压住自动隐藏任务栏的滑出 —— explorer 把任务栏滑上屏幕是一次（或一段连续的）
/// <c>SetWindowPos</c>，事件到达的瞬间调用方就能把它按回去，用户基本看不见那一帧。
/// </para>
/// <para>
/// 事件量大（每个顶层窗口的每次挪动都是一条），回调只带 hwnd，调用方自己过滤
/// （比如只认 <c>Shell_TrayWnd</c>）。回调在安装钩子的线程上派发，必须挂在有消息泵的线程上。
/// </para>
/// </summary>
public sealed class WindowMoveWatcher
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;

    private const uint WinEventOutOfContext = 0x0000;

    /// <summary>只关心别的进程：自己的窗口挪动（动画之类）不需要。</summary>
    private const uint WinEventSkipOwnProcess = 0x0002;

    private const int ObjidWindow = 0;
    private const int ChildIdSelf = 0;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // 委托必须存在字段里：非托管侧只拿得到函数指针，委托被 GC 回收后下一次回调就是野指针。
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>构造监听器（此时还没装钩子，调 <see cref="Start"/> 才开始听）。</summary>
    public WindowMoveWatcher() => _callback = OnEvent;

    /// <summary>某个顶层窗口动了。参数是发生变化的 hwnd，由调用方过滤目标。</summary>
    public event Action<IntPtr>? WindowChanged;

    /// <summary>开始监听。回调在<b>调用本方法的线程</b>（须有消息泵）上派发。已经在听就是空操作。</summary>
    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWinEventHook(
            EventObjectShow, EventObjectLocationChange, IntPtr.Zero, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    /// <summary>停止监听并卸钩子。没在听就是空操作。</summary>
    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            _ = UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint eventTime)
    {
        // 只要「窗口本身」的挪动：控件内部的对象（菜单项、图标……）也会发这些事件，量大得多。
        if (idObject != ObjidWindow || idChild != ChildIdSelf || hwnd == IntPtr.Zero)
        {
            return;
        }

        WindowChanged?.Invoke(hwnd);
    }
}
