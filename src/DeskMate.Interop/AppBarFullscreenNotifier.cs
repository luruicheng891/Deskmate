using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DeskMate.Interop;

/// <summary>
/// 听系统的「有全屏应用了 / 没了」通知：用一个看不见的窗口向外壳注册成 AppBar，
/// 外壳在全屏应用出现或退出时给所有已注册的 AppBar 发 <c>ABN_FULLSCREENAPP</c>。
/// 系统任务栏自己就是靠这条通知决定藏不藏的，口径和它一致。
/// <para>
/// 只注册、<strong>不调</strong> <c>ABM_SETPOS</c>：不申请屏幕边缘，不会挤占工作区。
/// 通知本身偶尔会误报或晚一步（此时前台窗口还没更新），所以调用方只能拿它当「该重新判一次」的信号，
/// 不能直接当结论 —— 判定仍然走 <see cref="FullscreenDetector"/>。
/// </para>
/// </summary>
public sealed class AppBarFullscreenNotifier : IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;

    /// <summary>wParam：全屏应用出现或退出；lParam 非 0 表示进入。</summary>
    private const int AbnFullscreenApp = 2;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly HwndSource _source;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreatedMessage;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public WindowInterop.WinRect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    /// <summary>
    /// 创建承载窗口并向外壳注册。必须在有消息泵的线程（UI 线程）上调用，
    /// 通知是作为窗口消息投递到这个线程的。
    /// </summary>
    public AppBarFullscreenNotifier()
    {
        _callbackMessage = RegisterWindowMessageW("DeskMate.AppBarCallback");
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        // 不带 WS_VISIBLE：窗口建出来就是隐藏的，也不进任务栏与 Alt+Tab。
        var parameters = new HwndSourceParameters("DeskMate AppBar 通知")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow | WsExNoActivate,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WindowHook);
        Register();
    }

    /// <summary>是否已经向外壳注册成功。失败时调用方只能靠前台事件与轮询。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>外壳报告全屏应用状态变了（参数 true = 有全屏应用出现）。在创建本对象的线程上触发。</summary>
    public event EventHandler<bool>? FullscreenAppChanged;

    /// <summary>注销并销毁承载窗口。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _source.RemoveHook(WindowHook);
        _source.Dispose();
    }

    private void Register()
    {
        if (IsRegistered)
        {
            return;
        }

        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        IsRegistered = SHAppBarMessage(AbmNew, ref data) != UIntPtr.Zero;
    }

    private void Unregister()
    {
        if (!IsRegistered)
        {
            return;
        }

        var data = NewData();
        _ = SHAppBarMessage(AbmRemove, ref data);
        IsRegistered = false;
    }

    private AppBarData NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = _source.Handle,
    };

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callbackMessage && wParam.ToInt64() == AbnFullscreenApp)
        {
            handled = true;
            FullscreenAppChanged?.Invoke(this, lParam != IntPtr.Zero);
        }
        else if (msg == _taskbarCreatedMessage)
        {
            // explorer 重启后原来的注册就丢了，必须重新来一次，否则从此再也收不到通知。
            IsRegistered = false;
            Register();
        }

        return IntPtr.Zero;
    }
}
