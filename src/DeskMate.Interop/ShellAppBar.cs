using System.Runtime.InteropServices;

namespace DeskMate.Interop;

/// <summary>
/// AppBar API（<c>SHAppBarMessage</c>）封装：边缘注册与系统任务栏的自动隐藏开关。
/// <para>
/// 与 <see cref="AppBarFullscreenNotifier"/> 的分工：那边只注册一个隐形窗口收全屏通知，
/// 不申请屏幕边缘；这里负责把一个真实窗口<strong>注册成底边 AppBar</strong> —— 注册后外壳会自动把
/// 工作区收缩掉这条带子，最大化窗口、Dock 的定位（它们都读 <c>WorkArea</c>）全部自动避让。
/// </para>
/// <para>
/// <c>ABM_SETSTATE</c> 写的是「用户在 Windows 设置里打开任务栏自动隐藏」这个全局设置本身，
/// 等价且可逆 —— 调用方必须先用 <see cref="GetTaskbarState"/> 记下原始状态，退出时按原值恢复。
/// </para>
/// </summary>
public static class ShellAppBar
{
    public const uint AbmNew = 0x00000000;
    public const uint AbmRemove = 0x00000001;
    public const uint AbmQueryPos = 0x00000002;
    public const uint AbmSetPos = 0x00000003;
    public const uint AbmGetState = 0x00000004;
    public const uint AbmGetTaskBarPos = 0x00000005;
    public const uint AbmSetState = 0x0000000A;

    /// <summary>底边。</summary>
    public const uint AbeBottom = 3;

    /// <summary>回调消息的 wParam：AppBar 应该重新申请位置。</summary>
    public const uint AbnPosChanged = 1;

    /// <summary>回调消息的 wParam：有全屏应用出现/退出（另有 <see cref="AppBarFullscreenNotifier"/> 在听）。</summary>
    public const uint AbnFullscreenApp = 2;

    /// <summary>回调消息的 wParam：任务栏自动隐藏状态被外部改了。</summary>
    public const uint AbnStateChange = 4;

    /// <summary>任务栏状态位：自动隐藏。</summary>
    public const uint AbsAutoHide = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct AppBarData
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

    /// <summary>发一条 AppBar 消息。返回值随消息而定：注册类消息非零 = 成功。</summary>
    public static UIntPtr Send(uint message, ref AppBarData data) => SHAppBarMessage(message, ref data);

    /// <summary>构造一条基础数据。注册过的窗口调任何消息都要带上当初注册的 <paramref name="hwnd"/>。</summary>
    public static AppBarData NewData(IntPtr hwnd, uint callbackMessage = 0) => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = hwnd,
        uCallbackMessage = callbackMessage,
    };

    /// <summary>取系统任务栏当前状态（自动隐藏位）。explorer 没跑时返回 0。</summary>
    public static uint GetTaskbarState()
    {
        var data = NewData(IntPtr.Zero);
        return (uint)SHAppBarMessage(AbmGetState, ref data);
    }

    /// <summary>
    /// 设置系统任务栏的自动隐藏。这是全局设置（等价于用户在 Windows 设置里切换那个开关），
    /// 调用方负责在合适的时机按原始状态恢复。
    /// </summary>
    public static bool SetTaskbarAutoHide(IntPtr hwnd, bool autoHide)
    {
        var data = NewData(hwnd);
        data.lParam = autoHide ? 1 : 0;
        return SHAppBarMessage(AbmSetState, ref data) != UIntPtr.Zero;
    }

    /// <summary>取系统任务栏的屏幕矩形（物理像素）。explorer 没跑时失败。</summary>
    public static bool TryGetTaskbarRect(out WindowInterop.WinRect rect)
    {
        var data = NewData(IntPtr.Zero);
        var ok = SHAppBarMessage(AbmGetTaskBarPos, ref data) != UIntPtr.Zero;
        rect = data.rc;
        return ok;
    }

    /// <summary>注册一条自定义消息，AppBar 回调与 <c>TaskbarCreated</c> 都要走它。</summary>
    public static uint RegisterMessage(string name) => RegisterWindowMessageW(name);
}
