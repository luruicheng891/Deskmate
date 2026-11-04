using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// 听「顶层窗口这一排的顺序变了」。
/// <para>
/// Dock 贴边压住任务栏那条带子时，explorer 会不停把 <c>Shell_TrayWnd</c> 重新抬回置顶组最上面；
/// 被它抢走的那一小段里，鼠标消息是给任务栏的，Dock 上的图标点不动 —— 用户读到的就是
/// 「点一下没反应，过几秒再点才打开」。靠轮询发现这件事最快也要等下一个轮询周期，
/// 所以这里装一条钩子：Z 序一被动过就马上回调用方，让它立刻复核并把自己抬回去。
/// </para>
/// <para>
/// 回调只给「该复核了」这一个信号，不下结论：到底是不是被任务栏压着，仍由调用方问 <see cref="WindowInterop.IsAbove"/>。
/// 桌面级的重排事件很密（切窗口、拖窗口都会发），所以回调里一个非托管调用都不做。
/// </para>
/// </summary>
public sealed class DesktopOrderWatcher
{
    private const uint EventObjectReorder = 0x8004;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjidWindow = 0;
    private const int ChildIdSelf = 0;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    // 委托必须存在字段里：非托管侧只拿得到函数指针，委托被 GC 回收后下一次回调就是野指针。
    private readonly WinEventDelegate _callback;
    private IntPtr _hook;
    private Action? _onChanged;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>构造监听器（此时还没装钩子，调 <see cref="Start"/> 才开始听）。</summary>
    public DesktopOrderWatcher() => _callback = OnEvent;

    /// <summary>钩子是否已经装上。</summary>
    public bool IsRunning => _hook != IntPtr.Zero;

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

        _hook = SetWinEventHook(
            EventObjectReorder, EventObjectReorder, IntPtr.Zero, _callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    /// <summary>停止监听并卸钩子。没在听就是空操作。</summary>
    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _onChanged = null;
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint eventTime)
    {
        // 只要「窗口排次」这一条；重排事件的 hwnd 是被重排的那个父窗口（顶层窗口就是桌面本身）。
        if (idObject != ObjidWindow || idChild != ChildIdSelf)
        {
            return;
        }

        _onChanged?.Invoke();
    }
}
