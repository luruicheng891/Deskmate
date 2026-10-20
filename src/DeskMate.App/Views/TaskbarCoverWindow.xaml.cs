using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskMate.App.Services;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.Views;

/// <summary>
/// 任务栏遮罩：一条贴在屏幕左下角、盖住任务栏左侧图标区（开始、搜索、任务视图、应用按钮）的条。
/// <para>
/// 是「盖住」不是「关掉」—— 不写任何系统设置，任务栏进程照跑，右侧托盘与时钟完全不受影响；
/// Dock 也还在它自己的位置，只是多了一条栏把系统那半截挡住。
/// </para>
/// <para>
/// 遮罩的填充是<strong>桌面壁纸在那一条上的原图</strong>：从「当前真正在画壁纸的那个桌面窗口」
/// （<c>Progman</c>，或者动态壁纸的 <c>WorkerW</c>）里裁出来，不模糊、也不受应用窗口影响，
/// 看上去就是桌面自然延伸到了屏幕底边，而任务栏左边那一坨图标被原样盖在下面。
/// <para>
/// 填的是<strong>帧差自适应</strong>取来的：<see cref="BackdropPump"/> 在后台线程上抓一条带子、
/// 和上一帧逐字节比，变了才重画，并按「壁纸有没有在动」把节奏在
/// <see cref="FastestFrame"/>~<see cref="SlowestFrame"/> 之间来回调。动态壁纸下它会跟着动（最多落后
/// 一档），静态壁纸下退到 30 秒一次、和不花钱一样 —— 以前那个写死 30 秒的版本在动态壁纸上就是一张定格。
/// </para>
/// </para>
/// </summary>
public sealed partial class TaskbarCoverWindow : Window
{
    /// <summary>任务栏的窗口类名，Z 序比较以它为基准。</summary>
    private const string TaskbarClassName = "Shell_TrayWnd";

    /// <summary>与 Dock 之间留的缝，遮罩永远不越过 Dock 的左沿。</summary>
    private const double DockClearance = 8;

    /// <summary>
    /// Z 序轮询间隔。遮罩是「看」的，任务栏抢回去的那一瞬间用户就会看到图标闪一下，
    /// 所以按帧率盯（Dock 那条只关心点击，100ms 足够；这里不行）。
    /// </summary>
    private static readonly TimeSpan OrderInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// 壁纸确实在动时的节奏下限。一次整屏 PrintWindow 实测 13~29 ms，所以这一档的代价是
    /// 「后台线程大约十分之一的时间」；再快就要拿肉眼几乎看不出好处的延迟去换 CPU，不值。
    /// </summary>
    private static readonly TimeSpan FastestFrame = TimeSpan.FromMilliseconds(300);

    /// <summary>壁纸连续几帧一模一样时一路退让到的上限（就是以前写死的那个 30 秒）。</summary>
    private static readonly TimeSpan SlowestFrame = TimeSpan.FromSeconds(30);

    private readonly IConfigService _config;
    private readonly DockWindow _dock;
    private readonly ILogger<TaskbarCoverWindow> _logger;
    private readonly DispatcherTimer _orderTimer;
    private readonly BackdropFramePacer _pacer = new(FastestFrame, SlowestFrame);
    private readonly BackdropPump _pump;

    /// <summary>
    /// 这条带子在屏幕上的哪个位置、多大（物理像素）。取帧在后台线程，不能直接读 WPF 的属性，
    /// 所以由 UI 线程整条换掉一条不可变记录，那边只会读到完整的一份。
    /// </summary>
    private BackdropTarget? _target;

    /// <summary>现在填的是不是「什么都没取到」的兜底底色，用来避免锁屏期间每帧重设一遍。</summary>
    private bool _usingFallback;

    /// <summary>上一次检查时是不是已经抬到任务栏上面了，只为了「重新抬起来」这句日志不重复刷。</summary>
    private bool _raisedAboveTaskbar;

    /// <summary>构造遮罩窗口。</summary>
    public TaskbarCoverWindow(IConfigService config, DockWindow dock, ILogger<TaskbarCoverWindow> logger)
    {
        InitializeComponent();

        _config = config;
        _dock = dock;
        _logger = logger;

        _orderTimer = new DispatcherTimer { Interval = OrderInterval };
        _orderTimer.Tick += (_, _) => KeepAboveTaskbar();

        _pump = new BackdropPump(GrabFrame, PaintBackdrop, _pacer, Dispatcher, logger);
    }

    /// <summary>由 AppShell 下发：用户开关与全屏让路合成后的最终可见性。</summary>
    public void SetVisibility(bool visible)
    {
        if (!visible || !ApplyGeometry())
        {
            _orderTimer.Stop();
            _pump.Stop();
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        Show();
        CaptureTarget();
        _orderTimer.Start();
        _pump.Start();
        KeepAboveTaskbar();
    }

    /// <summary>退出时真关。</summary>
    public void CloseFromShell()
    {
        _orderTimer.Stop();
        _pump.Dispose();
        Close();
    }

    /// <summary>给自检用：这一条现在是壁纸原图，还是「什么都没取到」的兜底底色。</summary>
    public bool IsFilledWithWallpaper => Backdrop.Background is ImageBrush;

    /// <summary>给自检用：当前节奏、最近一帧的耗时与已经重画了几帧。</summary>
    public string BackdropTimingText =>
        $"节奏 {_pump.CurrentDelay.TotalMilliseconds:F0} ms · 单帧 {_pump.LastGrabDelay.TotalMilliseconds:F1} ms · 重画 {_pump.PublishedFrames} 帧";

    /// <summary>
    /// 按任务栏实际占的那一条算位置与尺寸。返回 false 表示没东西可盖：
    /// 要么任务栏不在底部（用户自己设成了左侧/顶部或已自动隐藏），要么 Dock 就贴在左边、遮罩会让它点不到。
    /// </summary>
    private bool ApplyGeometry()
    {
        var work = SystemParameters.WorkArea;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var band = Math.Round(screenHeight - work.Bottom);

        if (band < 8)
        {
            return false;
        }

        var width = Math.Clamp(_config.Current.TaskbarCoverWidth, AppConfig.TaskbarCoverWidthMin, AppConfig.TaskbarCoverWidthMax);

        // Dock 可见时不让遮罩压到它上面：压住就等于把 Dock 的点击一起吃掉了。
        if (_dock.IsVisible && _dock.Left > 0)
        {
            width = Math.Min(width, (int)Math.Round(_dock.Left - DockClearance));
        }

        if (width < AppConfig.TaskbarCoverWidthMin)
        {
            _logger.LogDebug("任务栏遮罩：Dock 已经贴到左边，没有可盖的空间，跳过");
            return false;
        }

        Width = width;
        Height = band;
        Left = Math.Round(work.Left);
        Top = Math.Round(screenHeight - band);

        _logger.LogDebug(
            "任务栏遮罩：工作区底边 {WorkBottom}，遮罩 {Rect}",
            work.Bottom,
            $"{Left},{Top}-{Left + Width},{Top + Height}");

        return true;
    }

    /// <summary>
    /// 把几何交给取帧线程。<strong>只有真的换了位置或尺寸才 Invalidate</strong>：
    /// 全屏让路与配置变更都会重新调一遍 SetVisibility，每次都当成「上一帧不算数」的话，
    /// 节奏永远退让不到慢档，静态壁纸也会被按最快档一直抓。
    /// </summary>
    private void CaptureTarget()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var target = new BackdropTarget(
            (int)Math.Round(Left * dpi.DpiScaleX),
            (int)Math.Round(Top * dpi.DpiScaleY),
            Math.Max(1, (int)Math.Round(Width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(Height * dpi.DpiScaleY)),
            (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX),
            (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY));

        var previous = Volatile.Read(ref _target);
        Volatile.Write(ref _target, target);

        if (previous != target)
        {
            _pump.Invalidate();
        }
    }

    /// <summary>
    /// 取帧线程调用：从「当前真正在画壁纸的桌面窗口」里裁这一条带子。
    /// 候选要逐个试 —— 动态壁纸那层是 GPU 合成的，某些取法拿回来是纯黑，那时候退回 Progman
    /// 至少还是一张壁纸，总比糊一块黑上去强。
    /// </summary>
    private BandPixels? GrabFrame()
    {
        var target = Volatile.Read(ref _target);
        if (target is null)
        {
            return null;
        }

        foreach (var host in WindowInterop.WallpaperHosts(target.ScreenWidth, target.ScreenHeight))
        {
            var band = ScreenCapture.GrabBandFromWindow(host, target.Left, target.Top, target.Width, target.Height);
            if (band is not null && !band.IsBlank)
            {
                _logger.LogTrace("任务栏遮罩：壁纸取自桌面窗口 {Host}", host);
                return band;
            }
        }

        return null;
    }

    /// <summary>UI 线程：把这一帧刷上去。null = 一个候选都取不到（锁屏、安全桌面），退回主题底色至少不会露出任务栏。</summary>
    private void PaintBackdrop(BitmapSource? frame)
    {
        if (frame is not null)
        {
            Backdrop.Background = new ImageBrush(frame) { Stretch = Stretch.Fill };
            _usingFallback = false;
            return;
        }

        if (_usingFallback)
        {
            return;
        }

        _usingFallback = true;
        _logger.LogDebug("任务栏遮罩：桌面窗口都裁不出内容，退回主题底色");
        Backdrop.Background = TryFindResource("WindowBackgroundBrush") as Brush ?? Brushes.Transparent;
    }

    /// <summary>
    /// explorer 会不停把任务栏抬回置顶组最上面，要盖住就得一直盯着 Z 序。
    /// 判据是直接问 Z 序（<see cref="WindowInterop.IsAbove"/>），不是「我自己那个像素归谁」：
    /// 任务栏带逐像素透明，探测点落在透明段时 <c>WindowFromPoint</c> 会穿透返回我们自己，
    /// 于是明明被压着却判成没被压着，图标就闪出来了。
    /// </summary>
    private void KeepAboveTaskbar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible)
        {
            return;
        }

        var taskbar = WindowInterop.FindWindow(TaskbarClassName);
        if (taskbar == IntPtr.Zero || !WindowInterop.IsAbove(taskbar, hwnd))
        {
            _raisedAboveTaskbar = false;
            return;
        }

        WindowInterop.SetTopmost(hwnd, true);
        if (!_raisedAboveTaskbar)
        {
            _raisedAboveTaskbar = true;
            _logger.LogDebug("任务栏遮罩：被任务栏压住了，已重新抬到置顶层");
        }
    }

    /// <summary>这条带子在屏幕上的位置与尺寸，全是物理像素。</summary>
    private sealed record BackdropTarget(int Left, int Top, int Width, int Height, int ScreenWidth, int ScreenHeight);
}
