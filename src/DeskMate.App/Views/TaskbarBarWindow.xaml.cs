using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMate.Core.Interfaces;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.Views;

/// <summary>
/// 自建任务栏：屏幕右下角一小条（输入法 · 网络 · 音量 · 时钟），与 Dock 悬浮在同一条底边线上。
/// <para>
/// 定位不占工作区：系统任务栏切到自动隐藏后工作区就是整屏，这条栏贴着右下角悬浮，
/// 与 Dock 的 8px 悬浮间隙对齐 —— 最大化窗口照常铺满，栏和 Dock 一样压在它们上面（置顶轮询维持）。
/// </para>
/// <para>
/// 自动隐藏的任务栏有个绕不开的行为：鼠标碰到屏幕底边就会滑出来。接管期间用
/// <see cref="WindowMoveWatcher"/> 盯着它的位置 —— 滑出是一次（或一段连续的）<c>SetWindowPos</c>，
/// 事件一到就把它整条按回屏幕外，用户基本看不见那一帧；16ms 轮询做兜底。想要原生任务栏
/// （托盘图标）时，在设置里关掉这个开关即可，退出 DeskMate 会自动恢复接管前的状态。
/// </para>
/// </summary>
public sealed partial class TaskbarBarWindow : Window
{
    /// <summary>栏的高度（DIP），与系统任务栏的托盘区同高。</summary>
    private const double BarHeightDip = 40;

    /// <summary>与 Dock 相同的悬浮间隙：底边线和左右边距都按这个留。</summary>
    private const double FloatingGapDip = 8;

    /// <summary>任务栏的窗口类名，滑出压制与 Z 序比较都以它为基准。</summary>
    private const string TaskbarClassName = "Shell_TrayWnd";

    /// <summary>
    /// 自动隐藏的任务栏在屏内保留的「探出条」高度（物理像素）。隐藏态它留 2px 在屏幕边上看不见，
    /// 探出的部分超过这个阈值才算滑出来了，才需要按回去。
    /// </summary>
    private const int RevealSliverPx = 6;

    /// <summary>
    /// Z 序与滑出兜底轮询间隔。压制走事件（<see cref="WindowMoveWatcher"/>），这里只是兜底
    /// （事件可能因为线程正忙而晚到）与置顶维持。
    /// </summary>
    private static readonly TimeSpan OrderInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>状态轮询：时钟/输入法/音量每跳必查，网络（一次 WLAN 查询）隔跳查。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly IConfigService _config;
    private readonly ILogger<TaskbarBarWindow> _logger;
    private readonly DispatcherTimer _orderTimer;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _clipTimer;
    private readonly WindowMoveWatcher _moveWatcher = new();
    private int _tickCount;

    private IntPtr _hwnd;
    private uint _taskbarCreatedMessage;
    private IntPtr _taskbarHwnd;

    /// <summary>接管自动隐藏前记下的系统任务栏原始状态，退出时按它恢复。</summary>
    private uint _savedTaskbarState;
    private bool _autoHideActive;

    /// <summary>任务栏是否已被 DWM 隐身。隐身成功后就不用再跟外壳抢位置了。</summary>
    private bool _taskbarCloaked;

    /// <summary>cloak 被外壳拒绝时的后备：任务栏窗口已被 SW_HIDE。外壳重新显示就再藏回去。</summary>
    private bool _taskbarHidden;

    /// <summary>「保留托盘」模式：不显示自建小条，只把原生任务栏裁到剩右侧托盘区。</summary>
    private bool _keepTray;

    /// <summary>上次实际裁剪的窗口内矩形；只有变了才重新 SetWindowRgn，免得每轮都触发任务栏重绘。</summary>
    private (int Left, int Width, int Height)? _lastClip;

    /// <summary>音量轮询缓存：状态没变就不动 UI，避免每半秒重画一遍。</summary>
    private bool _volumeKnown;
    private int _lastVolumePercent = -1;
    private bool _lastMuted;

    /// <summary>网络轮询缓存，同上。</summary>
    private bool _networkKnown;
    private string _lastNetworkTip = string.Empty;

    /// <summary>构造自建任务栏窗口。</summary>
    public TaskbarBarWindow(IConfigService config, ILogger<TaskbarBarWindow> logger)
    {
        InitializeComponent();

        _config = config;
        _logger = logger;

        _orderTimer = new DispatcherTimer { Interval = OrderInterval };
        _orderTimer.Tick += (_, _) =>
        {
            KeepAboveTaskbar();
            EnsureTaskbarSuppressed();
        };

        _tickTimer = new DispatcherTimer { Interval = TickInterval };
        _tickTimer.Tick += (_, _) => OnTick();

        // 「保留托盘」模式的裁剪轮询：任务栏矩形变了（explorer 重启、DPI/分辨率变化）就重新裁。
        // 平时矩形不变，这一轮就是白查一次 GetWindowRect —— 便宜，无所谓。
        _clipTimer = new DispatcherTimer { Interval = TickInterval };
        _clipTimer.Tick += (_, _) => ClipTaskbarToTray();

        _moveWatcher.WindowChanged += OnWindowMoved;

        SizeChanged += (_, _) => PositionBar();
        Closed += (_, _) => Disable();
    }

    /// <summary>由 AppShell 下发：功能开关 与 全屏让路 合成后的最终可见性。</summary>
    public void SetVisible(bool visible, bool keepTray = false)
    {
        var wantClip = visible && keepTray;

        // 从「保留托盘」切走（关开关、切回普通模式）：先把任务栏的完整形状还回去。
        if (!wantClip && _keepTray)
        {
            RestoreTaskbarRegion();
        }

        _keepTray = wantClip;

        if (!visible)
        {
            _orderTimer.Stop();
            _tickTimer.Stop();
            _clipTimer.Stop();
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (_keepTray)
        {
            // 托盘已有时钟/输入法/网络/音量，自建小条不显示；但要一个活的 Dispatcher 跑裁剪轮询。
            if (IsVisible)
            {
                Hide();
            }

            _orderTimer.Stop();
            _tickTimer.Stop();

            // 从「隐藏整个任务栏」切过来的话，任务栏还处于 SW_HIDE / cloak 态 ——
            // 先把它显示回来，裁剪才有东西可裁。自动隐藏保持用户自己的设置不动。
            var taskbar = TaskbarHwnd;
            if (taskbar != IntPtr.Zero)
            {
                if (_taskbarHidden)
                {
                    _ = WindowInterop.SetWindowShown(taskbar, true);
                    _taskbarHidden = false;
                    _logger.LogInformation("自建任务栏：切到保留托盘模式，已重新显示系统任务栏");
                }

                if (_taskbarCloaked)
                {
                    _ = WindowInterop.SetCloaked(taskbar, false) == 0;
                    _taskbarCloaked = false;
                }
            }

            _autoHideActive = false;
            _clipTimer.Start();
            ClipTaskbarToTray();
            return;
        }

        _lastClip = null;

        if (!IsVisible)
        {
            Height = BarHeightDip;
            Show();
        }

        EnsureAutoHide();
        _moveWatcher.Start();
        _orderTimer.Start();
        _tickTimer.Start();
        OnTick();
    }

    /// <summary>
    /// 功能关闭或退出时调用：停止压制、恢复系统任务栏的自动隐藏原状。
    /// <see cref="Closed"/> 也接到这里，重复调用是安全的。
    /// </summary>
    public void Disable()
    {
        _orderTimer.Stop();
        _tickTimer.Stop();
        _clipTimer.Stop();
        _moveWatcher.Stop();
        _volumeKnown = false;
        _networkKnown = false;

        if (IsVisible)
        {
            Hide();
        }

        // 打头一行：退出日志里能不能看到这行，直接区分「Disable 没跑到」和「跑到但没生效」。
        _logger.LogInformation("自建任务栏：开始恢复原生任务栏");

        try
        {
            // 「保留托盘」模式的裁剪要先还原 —— 任务栏必须以完整形状回来。
            RestoreTaskbarRegion();

            // 退出/关开关一律把原生任务栏彻底请回来：显示 + 取消自动隐藏。
            // 「恢复接管前的状态」在这里是个坑 —— 很多用户本来就开着自动隐藏，
            // 那样退出后任务栏还是藏着的，看起来就像 DeskMate 没恢复。
            if (_hwnd != IntPtr.Zero && ShellAppBar.SetTaskbarAutoHide(_hwnd, false))
            {
                _logger.LogInformation("自建任务栏：已取消系统任务栏的自动隐藏");
            }

            _autoHideActive = false;

            // 揭开要放在恢复显示之后：先揭开、后恢复的话，任务栏会以「正常显示」的姿态闪一下。
            var currentTaskbar = TaskbarHwnd;
            if (currentTaskbar != IntPtr.Zero)
            {
                if (_taskbarHidden && WindowInterop.SetWindowShown(currentTaskbar, true))
                {
                    _logger.LogInformation("自建任务栏：已重新显示系统任务栏窗口");
                }

                if (WindowInterop.SetCloaked(currentTaskbar, false) == 0)
                {
                    _logger.LogInformation("自建任务栏：已揭开系统任务栏窗口");
                }

                // 极端状态兜底：自动隐藏已取消、隐身已揭开，它还不可见就再显示一次。
                if (!WindowInterop.IsVisible(currentTaskbar))
                {
                    _ = WindowInterop.SetWindowShown(currentTaskbar, true);
                }
            }
        }
        finally
        {
            _taskbarCloaked = false;
            _taskbarHidden = false;
            _taskbarHwnd = IntPtr.Zero;
        }
    }

    /// <summary>源窗口句柄就绪：套工具窗样式并注册 TaskbarCreated 消息。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        WindowInterop.ApplyToolWindowStyle(_hwnd);
        if (HwndSource.FromHwnd(_hwnd) is { } source)
        {
            source.AddHook(WndProc);
        }

        _taskbarCreatedMessage = ShellAppBar.RegisterMessage("TaskbarCreated");
    }

    /// <summary>
    /// 把系统任务栏切到自动隐藏。接管前先记原始状态；用户本来就没开自动隐藏时才写设置，
    /// 原来就开着的只标记一下（退出时原样写回去，等于没动）。
    /// </summary>
    private void EnsureAutoHide()
    {
        if (_autoHideActive || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _savedTaskbarState = ShellAppBar.GetTaskbarState();
        if ((_savedTaskbarState & ShellAppBar.AbsAutoHide) != 0)
        {
            _autoHideActive = true;
            CloakTaskbar();
            _logger.LogInformation("自建任务栏：系统任务栏本来就是自动隐藏，不重复设置");
            return;
        }

        if (ShellAppBar.SetTaskbarAutoHide(_hwnd, true))
        {
            _autoHideActive = true;
            CloakTaskbar();
            _logger.LogInformation("自建任务栏：系统任务栏已切到自动隐藏（原始状态 {State:X}）", _savedTaskbarState);
        }
        else
        {
            _logger.LogWarning("自建任务栏：设置系统任务栏自动隐藏失败，原生任务栏会与本栏并存");
        }
    }

    /// <summary>
    /// 把任务栏整个隐身。光靠「滑出来就按回去」治不了外壳的粘性弹出：关闭/最小化应用时，
    /// 外壳为了让用户能从任务栏恢复窗口，会把任务栏拉出来并保持、还不断重新摆位 ——
    /// 挪位置就是在跟它的状态机打架，用户还是会看见。
    /// <para>
    /// 第一选择是 DWM cloak（连渲染都没有）；实测外壳会拒绝 cloaking 任务栏窗口，
    /// 那就退一步用 SW_HIDE 整个隐藏 —— 外壳对它的挪位与粘性显示全都渲染不出来，
    /// 它若调 ShowWindow 重新显示，窗口事件会立刻再藏回去。
    /// </para>
    /// </summary>
    private void CloakTaskbar()
    {
        var taskbar = TaskbarHwnd;
        if (taskbar == IntPtr.Zero)
        {
            return;
        }

        var hresult = WindowInterop.SetCloaked(taskbar, true);
        _taskbarCloaked = hresult == 0;
        if (_taskbarCloaked)
        {
            _logger.LogInformation("自建任务栏：已把系统任务栏窗口隐身（DWM Cloak）");
            return;
        }

        _logger.LogWarning("自建任务栏：任务栏隐身被外壳拒绝（HRESULT 0x{HResult:X8}），改用 SW_HIDE 隐藏", hresult);
        HideTaskbarWindow();
    }

    /// <summary>SW_HIDE 后备：整个隐藏任务栏窗口，让它对挪位与粘性显示免疫。</summary>
    private void HideTaskbarWindow()
    {
        var taskbar = TaskbarHwnd;
        if (taskbar == IntPtr.Zero)
        {
            return;
        }

        _ = WindowInterop.SetWindowShown(taskbar, false);
        _taskbarHidden = true;
        _logger.LogInformation("自建任务栏：已隐藏系统任务栏窗口（SW_HIDE），外壳重新显示时会立刻再藏回去");
    }

    /// <summary>
    /// 压制总入口：cloak 生效时任务栏怎么都渲染不出来，什么都不用做；SW_HIDE 后备下，
    /// 外壳每次重新显示（窗口事件或 16ms 兜底轮询发现）都立刻再藏回去；两样都没生效才按位置。
    /// </summary>
    private void EnsureTaskbarSuppressed()
    {
        if (!_autoHideActive || _taskbarCloaked)
        {
            return;
        }

        var taskbar = TaskbarHwnd;
        if (taskbar == IntPtr.Zero)
        {
            return;
        }

        if (_taskbarHidden)
        {
            if (WindowInterop.IsVisible(taskbar))
            {
                _logger.LogDebug("自建任务栏：外壳重新显示了任务栏，再藏回去");
                _ = WindowInterop.SetWindowShown(taskbar, false);
            }

            return;
        }

        ClampTaskbar();
    }

    /// <summary>任务栏句柄。explorer 重启后旧句柄失效，取不到时每次重找（FindWindow 很便宜）。</summary>
    private IntPtr TaskbarHwnd => _taskbarHwnd != IntPtr.Zero ? _taskbarHwnd : (_taskbarHwnd = WindowInterop.FindWindow(TaskbarClassName));

    /// <summary>
    /// 「保留托盘」的核心：把原生任务栏窗口<b>裁</b>成只剩右侧托盘区（SetWindowRgn）。
    /// 中间不是被遮住 —— 是物理上不存在，动态壁纸原样透出来；时钟、托盘图标、输入法指示照常可用。
    /// 区域是窗口内坐标（相对任务栏窗口左上角），任务栏滑上滑下、换屏幕都不影响。
    /// 自动隐藏保持用户自己的设置不动：贴底边唤出时也只看得到托盘那一段。
    /// </summary>
    private void ClipTaskbarToTray()
    {
        var taskbar = TaskbarHwnd;
        if (taskbar == IntPtr.Zero
            || !WindowInterop.TryGetWindowRect(taskbar, out var taskbarRect))
        {
            return;
        }

        var notify = WindowInterop.FindChildWindow(taskbar, WindowInterop.TrayNotifyClassName);
        if (notify == IntPtr.Zero || !WindowInterop.TryGetWindowRect(notify, out var notifyRect))
        {
            // 结构变化（explorer 重启瞬间、未来系统改动）时先不裁 —— 整条任务栏还在，不至于没托盘。
            return;
        }

        var left = notifyRect.Left - taskbarRect.Left;
        var clip = (Left: left, Width: taskbarRect.Width, Height: taskbarRect.Height);
        if (_lastClip == clip)
        {
            return;
        }

        _lastClip = clip;
        var region = WindowInterop.CreateRectRegion(left, 0, left + taskbarRect.Width, taskbarRect.Height);
        if (WindowInterop.SetWindowRegion(taskbar, region))
        {
            _logger.LogInformation(
                "自建任务栏：已把原生任务栏裁到只剩右侧托盘（窗口内 x≥{Left}，宽 {Width}×高 {Height}）",
                left, taskbarRect.Width, taskbarRect.Height);
        }
        else
        {
            _logger.LogWarning("自建任务栏：裁剪任务栏失败，保留托盘模式暂不生效");
            _lastClip = null;
        }
    }

    /// <summary>还原任务栏的完整窗口区域（切回普通模式、退出时调）。</summary>
    private void RestoreTaskbarRegion()
    {
        _lastClip = null;
        var taskbar = TaskbarHwnd;
        if (taskbar != IntPtr.Zero && WindowInterop.SetWindowRegion(taskbar, IntPtr.Zero))
        {
            _logger.LogInformation("自建任务栏：已还原原生任务栏的完整窗口区域");
        }
    }

    /// <summary>
    /// 滑出压制：自动隐藏的任务栏一离开「藏在屏幕外」的状态就把它整条按回屏幕下方。
    /// 由 <see cref="WindowMoveWatcher"/> 的位置事件即时触发，轮询兜底。没在接管时完全不动作。
    /// </summary>
    private void ClampTaskbar()
    {
        if (!_autoHideActive || _taskbarCloaked || _taskbarHidden)
        {
            return;
        }

        var taskbar = TaskbarHwnd;
        if (taskbar == IntPtr.Zero || !WindowInterop.TryGetWindowRect(taskbar, out var rect))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var screenBottom = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY);
        if (screenBottom - rect.Top <= RevealSliverPx)
        {
            return;
        }

        // 只动 y，把整条按到屏幕外；x、宽高保持不变。
        WindowInterop.SetWindowPosition(
            taskbar, IntPtr.Zero, rect.Left, screenBottom, 0, 0,
            WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOZORDER | WindowInterop.SWP_NOACTIVATE);
    }

    /// <summary>位置事件：只认任务栏窗口。屏幕上任何窗口的挪动都会到这儿，先比对再干活。</summary>
    private void OnWindowMoved(IntPtr hwnd)
    {
        if (hwnd == TaskbarHwnd)
        {
            EnsureTaskbarSuppressed();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            // explorer 重启：任务栏是全新窗口（必然没被隐身），重新锁定并隐身；
            // 自动隐藏是持久设置，重启后依旧生效，不需要重设。
            _logger.LogInformation("自建任务栏：检测到 explorer 重启，重新锁定任务栏窗口");
            _taskbarHwnd = IntPtr.Zero;
            _taskbarCloaked = false;
            CloakTaskbar();
            ClampTaskbar();
        }

        return IntPtr.Zero;
    }

    /// <summary>系统任务栏真被压不住盖到头上时重新抬回置顶层（判据与遮罩同一套：问 Z 序，不问像素归属）。</summary>
    private void KeepAboveTaskbar()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        var taskbar = TaskbarHwnd;
        if (taskbar != IntPtr.Zero && WindowInterop.IsAbove(taskbar, _hwnd))
        {
            WindowInterop.SetTopmost(_hwnd, true);
        }
    }

    /// <summary>
    /// 右下角落位：底边线与 Dock 相同（工作区底边上方 8px），右边贴屏幕右缘再留 8px。
    /// 宽度随内容自适应，内容变了（图标显隐）要重新落位，所以每跳检查一次。
    /// </summary>
    private void PositionBar()
    {
        if (!IsVisible || ActualWidth <= 0)
        {
            return;
        }

        var work = SystemParameters.WorkArea;
        var left = Math.Round(SystemParameters.PrimaryScreenWidth - FloatingGapDip - ActualWidth);
        var top = Math.Round(work.Bottom - FloatingGapDip - Height);

        // 只在真的变了才动窗口，避免每半秒一次无谓的 SetWindowPos。
        if (Math.Abs(Left - left) < 0.5 && Math.Abs(Top - top) < 0.5)
        {
            return;
        }

        Left = left;
        Top = top;
    }

    private void OnTick()
    {
        _tickCount++;
        UpdateClock();
        UpdateIme();
        UpdateVolume();
        PositionBar();

        if (_tickCount % 4 == 0)
        {
            UpdateNetwork();
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTimeText.Text = now.ToString("HH:mm");
        ClockDateText.Text = now.ToString("yyyy/M/d");
    }

    private void UpdateIme()
    {
        var state = ImeStatus.Probe();
        if (state is null || !state.HasImeLayout)
        {
            // 前台是英文键盘布局（或没有前台窗口）：没有中英可言，藏掉。
            ImeHost.Visibility = Visibility.Collapsed;
            return;
        }

        ImeHost.Visibility = Visibility.Visible;
        if (!state.Available)
        {
            ImeText.Text = "英";
            ImeText.Opacity = 0.4;
            ImeText.Foreground = (Brush)FindResource("TextPrimaryBrush");
            ImeHost.ToolTip = "输入法状态未知（当前输入法不响应查询）";
            return;
        }

        ImeText.Opacity = 1;
        if (state.Open && state.Native)
        {
            ImeText.Text = "中";
            ImeText.Foreground = (Brush)FindResource("AccentBrush");
            ImeHost.ToolTip = "中文（单击切到英文）";
        }
        else
        {
            ImeText.Text = "英";
            ImeText.Foreground = (Brush)FindResource("TextPrimaryBrush");
            ImeHost.ToolTip = "英文（单击切到中文）";
        }
    }

    private void OnImeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var state = ImeStatus.Probe();
        if (state is null || !state.Available)
        {
            return;
        }

        var wantNative = !(state.Open && state.Native);
        if (ImeStatus.TrySetNative(wantNative))
        {
            UpdateIme();
        }
    }

    private void UpdateVolume()
    {
        if (!AudioEndpoint.TryGetVolume(out var percent, out var muted))
        {
            _volumeKnown = false;
            VolumeHost.Visibility = Visibility.Collapsed;
            return;
        }

        VolumeHost.Visibility = Visibility.Visible;
        if (_volumeKnown && percent == _lastVolumePercent && muted == _lastMuted)
        {
            return;
        }

        _volumeKnown = true;
        _lastVolumePercent = percent;
        _lastMuted = muted;

        VolumeWave.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        VolumeMuteX.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        VolumeHost.ToolTip = muted
            ? $"已静音（原音量 {percent}%）—— 单击取消静音，滚轮调节"
            : $"音量 {percent}% —— 单击静音，滚轮调节";
    }

    private void OnVolumeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_volumeKnown && AudioEndpoint.TrySetMute(!_lastMuted))
        {
            UpdateVolume();
        }
    }

    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!_volumeKnown)
        {
            return;
        }

        // 一格滚轮 = 2%，与系统音量面板的一小格手感一致。
        var target = Math.Clamp(_lastVolumePercent + Math.Sign(e.Delta) * 2, 0, 100);
        if (AudioEndpoint.TrySetVolume(target))
        {
            _ = AudioEndpoint.TrySetMute(false);
            UpdateVolume();
        }
    }

    private void UpdateNetwork()
    {
        var wifi = WifiProbe.GetConnectedSignal();
        var connected = System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

        string tip;
        if (wifi is not null)
        {
            WiredIcon.Visibility = Visibility.Collapsed;
            WifiIcon.Visibility = Visibility.Visible;
            WifiArc1.Opacity = 1;
            WifiArc2.Opacity = wifi.StrengthPercent >= 55 ? 1 : 0.25;
            WifiArc3.Opacity = wifi.StrengthPercent >= 80 ? 1 : 0.25;
            tip = $"Wi-Fi：{wifi.Ssid}（信号 {wifi.StrengthPercent}%）";
        }
        else if (connected)
        {
            WifiIcon.Visibility = Visibility.Collapsed;
            WiredIcon.Visibility = Visibility.Visible;
            tip = "已连接（有线网络）";
        }
        else
        {
            WiredIcon.Visibility = Visibility.Collapsed;
            WifiIcon.Visibility = Visibility.Visible;
            WifiArc1.Opacity = 0.3;
            WifiArc2.Opacity = 0.3;
            WifiArc3.Opacity = 0.3;
            tip = "网络未连接";
        }

        if (_networkKnown && tip == _lastNetworkTip)
        {
            return;
        }

        _networkKnown = true;
        _lastNetworkTip = tip;
        NetworkHost.ToolTip = $"{tip} —— 单击打开系统网络设置";
    }

    private void OnNetworkClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            _ = Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "打开系统网络设置失败");
        }
    }
}
