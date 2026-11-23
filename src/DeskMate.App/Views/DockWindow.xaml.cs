using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DeskMate.App.Controls;
using DeskMate.App.Services;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeskMate.App.Views;

/// <summary>
/// Dock 栏窗口：无边框、可透明、置顶、不抢焦点，用 SystemParameters.WorkArea 定位。
/// v1.0 只铺主屏（多屏预留：<see cref="Reposition"/> 改成按目标屏计算即可）。
/// </summary>
public partial class DockWindow : Window
{
    /// <summary>启动台拖拽到 Dock 时携带的数据格式（值为应用路径）。</summary>
    public const string AppPathFormat = "DeskMate.AppPath";

    // 悬停热区：鼠标贴到屏幕边缘这条带子里就唤出 Dock。
    // 5px 太窄 —— 光标要正好压在那几行像素上才算，用起来就是「来回蹭几下才出」。
    private const double HotZoneDip = 14;

    // Dock 与屏幕边缘的常规间距；接管模式下贴边，间距归零。
    private const double EdgeGap = 8;
    private const int WmDisplayChange = 0x001E;

    /// <summary>任务栏的窗口类名，贴边时要盯它的 Z 序。</summary>
    private const string TaskbarClassName = "Shell_TrayWnd";
    private const double NeighborNearShift = 9;
    private const double NeighborFarShift = 4.5;

    /// <summary>拖拽中被拿起的那一枚压到多透明：压太狠就完全看不见了，太轻又读不出「被拿起来了」。</summary>
    private const double DraggedGhostOpacity = 0.35;

    /// <summary>被拿起的图标向上抬的像素：和透明一起构成「被拎起来了」的手感。</summary>
    private const double DraggedLiftPixels = 6;
    private const int NeighborReach = 2;

    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan NeighborDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromMilliseconds(800);

    // 热区轮询：一次 GetCursorPos 很便宜，50ms 已经察觉不到延迟。
    private static readonly TimeSpan HotZoneInterval = TimeSpan.FromMilliseconds(50);

    // 贴边压住任务栏时的 Z 序轮询。100ms 足够：光标从屏幕边缘走到 Dock 上再按下，
    // 人手的一次点击远慢于此，所以抢在点击之前把 Dock 抬回置顶层。
    private static readonly TimeSpan TaskbarOrderInterval = TimeSpan.FromMilliseconds(100);

    // 悬停 160ms 才出预览：鼠标扫过去的瞬间就弹会非常吵，但 450ms 用起来就是「明显卡了一下」。
    // 窗口枚举改成与这段等待并行（见 SchedulePreview），所以缩短延迟不会再叠上 EnumWindows 的时间。
    private static readonly TimeSpan PreviewShowDelay = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan PreviewHideDelay = TimeSpan.FromMilliseconds(260);

    private readonly DockViewModel _viewModel;
    private readonly IConfigService _config;
    private readonly ILogger<DockWindow> _logger;
    private readonly WindowPreviewWindow _preview;
    private readonly DispatcherTimer _hotZoneTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private readonly DispatcherTimer _previewShowTimer;
    private readonly DispatcherTimer _previewHideTimer;
    private readonly DispatcherTimer _taskbarOrderTimer;

    /// <summary>
    /// 桌面 Z 序变化钩子。贴边时 Dock 与任务栏在同一条带子里抢位置，explorer 抢走的那一刻
    /// 点在那儿的鼠标消息是给任务栏的 —— 100 毫秒一次的轮询最快也要空出这一截，用户读到的就是
    /// 「Dock 点一下没反应，过两秒再点才打开」。听事件是为了把这段空隙压到几毫秒。
    /// 轮询照旧留着当兜底（事件可能因为我们的线程正忙而晚到）。
    /// </summary>
    private readonly DesktopOrderWatcher _desktopOrder = new();

    // Dock 压在任务栏那条带子里时才需要盯 Z 序，其他情况完全不跑。
    private bool _overlapsTaskbar;
    private bool _raisedAboveTaskbar;
    private IntPtr _taskbarHwnd;


    private bool _userAllowedVisible = true;
    private bool _slidIn = true;
    private bool _attachedToDesktop;
    private bool _desktopLayerReported;
    private int _hoveredIndex = -1;
    private Point _dragStart;

    /// <summary>按住左键时为真：气泡事件里 e.LeftButton 在 Button 捕获后会失真，按下状态自己记。</summary>
    private bool _dragPressed;
    private HwndSource? _source;
    private DockItemViewModel? _dragCandidate;

    /// <summary>正在被拖的那一枚（实时换位与落定都以它为准）。</summary>
    private DockItemViewModel? _dragItem;

    /// <summary>拖拽进行中：悬停让位动画与窗口预览都要闭嘴，落定后交给 ReorderAsync 持久化。</summary>
    private bool _dragging;

    private DockItemControl? _pendingPreviewControl;
    private DockItemViewModel? _pendingPreviewItem;
    private Task<IReadOnlyList<AppWindowInfo>>? _pendingPreviewWindows;

    /// <summary>构造 Dock 窗口。</summary>
    public DockWindow(DockViewModel viewModel, IConfigService config, WindowPreviewWindow preview, ILogger<DockWindow> logger)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _config = config;
        _preview = preview;
        _logger = logger;
        DataContext = viewModel;

        _hotZoneTimer = new DispatcherTimer { Interval = HotZoneInterval };
        _hotZoneTimer.Tick += OnHotZoneTick;

        _autoHideTimer = new DispatcherTimer { Interval = AutoHideDelay };
        _autoHideTimer.Tick += OnAutoHideTick;

        _previewShowTimer = new DispatcherTimer { Interval = PreviewShowDelay };
        _previewShowTimer.Tick += OnPreviewShowTick;

        // 收起预览用「反复检查」而不是一次性定时器：从图标滑到预览上，中间要穿过 Dock 的透明边距
        // 和两者之间那 6px 间隙，那一段两边都不算悬停 —— 一次性定时器恰好会在你走到一半时把预览收掉，
        // 表现就是「开了两个 Word，鼠标移到预览上却点不到那一张」。
        _previewHideTimer = new DispatcherTimer { Interval = PreviewHideDelay };
        _previewHideTimer.Tick += (_, _) =>
        {
            if (_preview.IsPointerInside || IsMouseOver)
            {
                return;
            }

            _previewHideTimer.Stop();
            _preview.Dismiss();
        };

        _taskbarOrderTimer = new DispatcherTimer { Interval = TaskbarOrderInterval };
        _taskbarOrderTimer.Tick += (_, _) => KeepAboveTaskbar();

        _config.ConfigChanged += OnConfigChanged;
        _viewModel.RelocateRequested += OnRelocateRequested;
        _viewModel.RenameRequested += OnRenameRequested;
        _viewModel.IconRequested += OnIconRequested;

        // WorkArea 变了要跟着挪：自建任务栏注册成 AppBar 后外壳会把工作区收缩掉那条带子，
        // Dock（读 WorkArea 定位）必须立刻让到栏的上面，不能等下一次尺寸变化才动。
        SystemParameters.StaticPropertyChanged += OnSystemPropertyChanged;

        _preview.DismissRequested += (_, _) => _preview.Dismiss();
        _preview.WindowChosen += (_, window) => _viewModel.ActivateWindow(window);
        _preview.CloseRequested += (_, window) => _viewModel.CloseWindow(window);

        // MouseEnter 是直接事件，不冒泡，所以用类级别的路由事件从图标控件收集悬停信息。
        AddHandler(DockItemControl.HoverChangedEvent, new RoutedEventHandler(OnItemHoverChanged));

        Chrome.MouseEnter += (_, _) => _autoHideTimer.Stop();
        Chrome.MouseLeave += (_, _) => ScheduleAutoHide();

        // 增删图标会改变 ActualWidth，必须重新居中；SizeChanged 是事件而非虚方法，不能 override。
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Dock 两端的按钮被按下：由 AppShell 决定呼出启动台。</summary>
    public event EventHandler? LauncherRequested;

    /// <summary>左端的文件夹按钮被按下：由 AppShell 决定呼出文件夹管理。</summary>
    public event EventHandler? FolderManagerRequested;

    /// <summary>由 AppShell 下发：用户开关 与 全屏让路 合成后的最终可见性。</summary>
    public void SetVisibility(bool visible)
    {
        _userAllowedVisible = visible;

        if (!visible)
        {
            _hotZoneTimer.Stop();
            _autoHideTimer.Stop();
            _previewShowTimer.Stop();
            _previewHideTimer.Stop();
            _taskbarOrderTimer.Stop();
            _desktopOrder.Stop();
            _overlapsTaskbar = false;
            _preview.Dismiss();
            _viewModel.StopRunningPolling();
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        var wasHidden = !IsVisible;
        if (wasHidden)
        {
            Show();
        }

        // 隐藏时 _overlapsTaskbar 被清零、盯任务栏 Z 序的定时器也停了（见上面的 !visible 分支）。
        // 只 Show() 的话它们不会自己回来：Dock 贴边压在任务栏那条带子里，explorer 一把任务栏抬回去，
        // Dock 就被盖住，收不到悬停和点击 —— 退出全屏后"鼠标放上去没反应"就是这个。
        // Reposition 会重算 _overlapsTaskbar、重启定时器并立刻抬一次。
        if (wasHidden)
        {
            Reposition();
        }

        _viewModel.StartRunningPolling();
        SyncAutoHideMode();
    }

    /// <summary>退出时关闭窗口并停掉所有定时器。</summary>
    public void CloseFromShell()
    {
        _hotZoneTimer.Stop();
        _autoHideTimer.Stop();
        _taskbarOrderTimer.Stop();
        _viewModel.StopRunningPolling();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_TOOLWINDOW：不进任务栏与 Alt+Tab；WS_EX_NOACTIVATE：点 Dock 不把前台焦点从游戏里抢走。
        WindowInterop.ApplyToolWindowStyle(hwnd);

        // WPF 没有暴露「工作区变了」的事件，分辨率/缩放/任务栏位置变化只能听 WM_DISPLAYCHANGE。
        // 这里存下 HwndSource：窗口销毁时 Handle 已变 0，届时再 FromHwnd 会直接抛 ArgumentException。
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WindowHook);
    }

    // 首次布局完成后才有真实的 ActualWidth/Height，此时才能定位与隐藏偏移。
    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        await _viewModel.LoadAsync();
        ApplyOrientation();
        ApplyAppearance();
        Reposition();
        _viewModel.StartRunningPolling();
        SyncAutoHideMode();
        UpdateDesktopLayerState();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 只能用 OnSourceInitialized 时存下的 source：此刻 this.Handle 已经是 0。
        _source?.RemoveHook(WindowHook);
        _source = null;
        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _preview.Dismiss();

        _config.ConfigChanged -= OnConfigChanged;
        _viewModel.RelocateRequested -= OnRelocateRequested;
        _viewModel.RenameRequested -= OnRenameRequested;
        _viewModel.IconRequested -= OnIconRequested;
        _viewModel.StopRunningPolling();
        base.OnClosed(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        _dragStart = e.GetPosition(this);
        // 按下就掐掉预览：按住不动等起拖阈值的那段时间里，160ms 的预览计时器会先到点，
        // 拖一个正在运行的应用时预览会在手底下弹出来。
        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _preview.Dismiss();
        // 音乐控件位置固定（启动台、文件夹按钮之后的第三格），不参与拖拽。
        var candidate = ResolveItem(e.OriginalSource as DependencyObject);
        _dragCandidate = candidate is { IsMusicWidget: false } ? candidate : null;
        _dragPressed = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);

        if (_dragging)
        {
            // 拖完松手：吞掉这次抬起，免得落点正好压在别的图标上又触发一次单击启动。
            e.Handled = true;
        }

        _dragPressed = false;
        _dragCandidate = null;
        _ = FinalizeDragAsync();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);

        // 用隧道事件 + 自己记录的按下状态起拖：气泡 MouseMove 里 e.LeftButton 在
        // Button 捕获鼠标后会失真成 Released（本机实测），拖拽判定永远过不去。
        if (_dragging)
        {
            DragMoved(e.GetPosition(ItemsHost));
            return;
        }

        if (!_dragPressed || _dragCandidate is null)
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        BeginDrag(_dragCandidate);
        DragMoved(e.GetPosition(ItemsHost));
    }

    /// <summary>进入拖拽：图标上浮（抬高 + 半透明），停掉轮询，防止快照重算在手底下重排容器。</summary>
    private void BeginDrag(DockItemViewModel dragged)
    {
        _dragCandidate = null;
        _dragItem = dragged;
        _dragging = true;
        // 窗口自己捕获鼠标：不捕获的话，按下点不在图标按钮上时没有捕获链，拖出窗口松手
        // 收不到 MouseUp，图标就永远卡在拎起状态。
        CaptureMouse();
        _viewModel.StopRunningPolling();
        _logger.LogInformation("Dock 起拖：{Name}（{Id}）", dragged.DisplayName, dragged.Id);
        // 悬停/按下的放大动画要掐掉：被拖的图标就在指针底下，拖起来还带放大又叠上抬升，很怪。
        ResetDraggedScale(dragged);
        ApplyDragVisual(ContainerOf(dragged));
    }

    /// <summary>拖动中：光标越过哪个缝隙，被拖的图标就实时换位到哪，其它图标当场让位。</summary>
    private void DragMoved(Point position)
    {
        var dragged = _dragItem;
        if (dragged is null)
        {
            return;
        }

        var target = LiveTargetIndex(dragged, position);
        if (target < 0)
        {
            return;
        }

        _viewModel.MoveItemLive(dragged.Id, target);
        // 容器跟着条目走（Move 不重建），但保险起见每次换位后重刷上浮外观。
        // 换位会让指针重新「进入」图标，悬停动画又会把图标顶到 1.35 倍，所以同步再复位一次。
        ResetDraggedScale(dragged);
        ApplyDragVisual(ContainerOf(dragged));
    }

    /// <summary>松手落定：把当前显示顺序交给 ReorderAsync 持久化，恢复外观与轮询。</summary>
    private async Task FinalizeDragAsync()
    {
        var dragged = _dragItem;
        if (!_dragging || dragged is null)
        {
            return;
        }

        _dragging = false;
        _dragItem = null;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        ClearDragVisual(ContainerOf(dragged));

        try
        {
            await _viewModel.ReorderAsync(_viewModel.Items.Select(item => item.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存拖拽后的 Dock 顺序失败");
        }

        // 落定时会重建条目，容器全是新的；指针正压在那儿，新控件一出生就吃掉一次悬停放大。
        // 先立刻复位，等布局落定后再补一次，保证松手后停在原尺寸。
        ResetDraggedScale(dragged);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_dragging)
            {
                ResetDraggedScale(dragged);
            }
        }));

        _viewModel.StartRunningPolling();
        _ = _viewModel.RefreshRunningStateAsync();
    }

    /// <summary>拖拽期间鼠标捕获被抢走（弹窗、Alt+Tab 等）：按松手收尾，别把图标卡在拎起状态。</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // LostMouseCapture 会冒泡：BeginDrag 把捕获从图标按钮抢到窗口的那一次也会路过这里
        // （OriginalSource 是按钮），把它当成拖拽中断就会当场把 _dragItem 清掉、拖动全程失灵。
        // 只有窗口自己丢了捕获（弹窗、Alt+Tab 抢走）才算异常中断。
        if (_dragging && e.OriginalSource == this)
        {
            _ = FinalizeDragAsync();
        }
    }

    /// <summary>被拖的图标：抬高几像素 + 半透明，视觉上是「被拎起来了」。</summary>
    private void ApplyDragVisual(FrameworkElement? container)
    {
        if (container is null)
        {
            return;
        }

        container.Opacity = DraggedGhostOpacity;
        container.RenderTransform = IsVertical
            ? new TranslateTransform(-DraggedLiftPixels, 0)
            : new TranslateTransform(0, -DraggedLiftPixels);
    }

    private void ClearDragVisual(FrameworkElement? container)
    {
        if (container is null)
        {
            return;
        }

        container.Opacity = 1;
        container.RenderTransform = null;
    }

    /// <summary>
    /// 把被拖图标复位成原尺寸。拖动时指针一直压在它上面，悬停（1.35 倍）和按下（1.15 倍）
    /// 的动画会停在放大后的值上，不手动复位就一直比原来大一圈。
    /// </summary>
    private void ResetDraggedScale(DockItemViewModel item)
    {
        if (ContainerOf(item) is { } container && FindItemControl(container) is { } control)
        {
            control.ResetHoverScale();
        }
    }

    /// <summary>在容器子树里找图标控件（模板套上之后容器里才有它）。</summary>
    private static DockItemControl? FindItemControl(DependencyObject? root)
    {
        switch (root)
        {
            case null:
                return null;
            case DockItemControl control:
                return control;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindItemControl(VisualTreeHelper.GetChild(root, index)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 把光标位置换算成被拖条目的目标下标：先找「光标落在哪个缝隙前」，再按分区收窄 ——
    /// 运行区临时项只在运行区里挪（位置跟启动时间走，拖出去也保不住），固定项只在固定区里挪。
    /// </summary>
    private int LiveTargetIndex(DockItemViewModel dragged, Point position)
    {
        var vertical = IsVertical;
        var axis = vertical ? position.Y : position.X;
        var gap = ItemsHost.Items.Count;

        for (var index = 0; index < ItemsHost.Items.Count; index++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            var center = container.TranslatePoint(
                new Point(container.ActualWidth / 2, container.ActualHeight / 2), ItemsHost);

            if (axis < (vertical ? center.Y : center.X))
            {
                gap = index;
                break;
            }
        }

        // 缝隙是「含被拖项」的插入点：落点在被拖项原位之前则原位起 index 直接用，之后则前移一位。
        var items = _viewModel.Items;
        var old = items.IndexOf(dragged);
        var target = gap <= old ? gap : gap - 1;

        var pinnedStart = -1;
        var musicIndex = -1;
        for (var index = 0; index < items.Count; index++)
        {
            if (pinnedStart < 0 && items[index].IsPinned)
            {
                pinnedStart = index;
            }

            if (musicIndex < 0 && items[index].IsMusicWidget)
            {
                musicIndex = index;
            }
        }

        if (pinnedStart < 0)
        {
            pinnedStart = items.Count;
        }

        // 音乐控件占 0 号位，谁都不许挪到它前面。
        var min = musicIndex >= 0 ? 1 : 0;

        if (dragged.IsPinned)
        {
            return Math.Clamp(target, Math.Max(pinnedStart, min), items.Count - 1);
        }

        // 运行区临时项：只能在 [min, pinnedStart-1] 里挪；区里只剩它一个时原地不动。
        var runningEnd = Math.Max(pinnedStart - 1, min);
        return Math.Clamp(target, min, runningEnd);
    }

    private FrameworkElement? ContainerOf(DockItemViewModel item)
    {
        var index = _viewModel.Items.IndexOf(item);
        return index < 0 ? null : ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
    }

    /// <summary>Dock 是否竖排（靠左/靠右停靠时）。</summary>
    private bool IsVertical => _config.Current.DockPosition is "Left" or "Right";

    private void OnLauncherButtonClick(object sender, RoutedEventArgs e) => LauncherRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddButtonClick(object sender, RoutedEventArgs e) => LauncherRequested?.Invoke(this, EventArgs.Empty);

    private void OnFolderButtonClick(object sender, RoutedEventArgs e) => FolderManagerRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>失效图标的「重新选择目标」：弹文件框挑一个新的程序或快捷方式。</summary>
    private async void OnRelocateRequested(object? sender, string id)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择这个图标要指向的程序或快捷方式",
            Filter = "程序与快捷方式|*.exe;*.lnk;*.url|所有文件|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.RelocateItemAsync(id, dialog.FileName);
        }
    }

    private void OnHotZoneTick(object? sender, EventArgs e)
    {
        if (!_userAllowedVisible || !_config.Current.AutoHideDock || _slidIn)
        {
            return;
        }

        if (IsCursorInHotZone())
        {
            _autoHideTimer.Stop();
            SlideIn();
        }
    }

    private void OnAutoHideTick(object? sender, EventArgs e)
    {
        _autoHideTimer.Stop();
        if (_config.Current.AutoHideDock && !Chrome.IsMouseOver)
        {
            SlideOut();
        }
    }

    private async void OnConfigChanged(object? sender, EventArgs e)
    {
        ApplyOrientation();
        ApplyAppearance();
        Reposition();
        UpdateDesktopLayerState();
        SyncAutoHideMode();

        // 图标尺寸变化要重取图标；尺寸没变时这个方法直接返回，不会抖动。
        await _viewModel.ApplyIconSizeAsync(_config.Current.DockIconSize);
    }

    // WM_DISPLAYCHANGE：分辨率或显示器布局变了，Dock 需要按新的工作区重新定位。
    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDisplayChange)
        {
            Dispatcher.BeginInvoke(Reposition);
        }

        return IntPtr.Zero;
    }

    private void OnItemHoverChanged(object sender, RoutedEventArgs e)
    {
        // 图标控件在事件参数里把自己带上来：sender 是挂处理器的窗口，Source 会被路由改写成外层容器。
        if (e is not DockItemHoverEventArgs { Control: { } control } || control.DataContext is not DockItemViewModel item)
        {
            return;
        }

        var index = _viewModel.Items.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        if (_dragging)
        {
            // 拖拽期间不响应悬停：让位动画会挪容器（插入点跟着漂），预览还会在你手底下弹出来。
            return;
        }

        if (control.IsPointerOver)
        {
            if (_hoveredIndex >= 0 && _hoveredIndex != index)
            {
                ShiftNeighbors(_hoveredIndex, entering: false);
            }

            _hoveredIndex = index;
            ShiftNeighbors(index, entering: true);

            SchedulePreview(control, item);
        }
        else
        {
            if (_hoveredIndex == index)
            {
                _hoveredIndex = -1;
            }

            ShiftNeighbors(index, entering: false);
            SchedulePreviewHide();
        }
    }

    private void SchedulePreview(DockItemControl control, DockItemViewModel item)
    {
        // 没在运行的应用弹预览没有意义（一个窗口都没有），直接不排程。
        if (!item.IsRunning)
        {
            _logger.LogDebug("预览不排程：{Name} 当前未被标记为运行中", item.DisplayName);
            _previewShowTimer.Stop();
            return;
        }

        _previewHideTimer.Stop();
        if (!ReferenceEquals(_pendingPreviewItem, item))
        {
            _pendingPreviewControl = control;
            _pendingPreviewItem = item;

            // 把 EnumWindows 挪到悬停计时之前并行跑：等 160ms 到了再查，预览就晚这么多。
            _pendingPreviewWindows = _viewModel.GetWindowsAsync(item.Id);
        }

        _previewShowTimer.Stop();
        _previewShowTimer.Start();
    }

    private void SchedulePreviewHide()
    {
        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        _previewHideTimer.Start();

        // 下一次悬停要重新问一遍窗口列表，否则刚关掉的窗口还会在预览里躺着。
        _pendingPreviewWindows = null;
    }

    private async void OnPreviewShowTick(object? sender, EventArgs e)
    {
        _previewShowTimer.Stop();

        var control = _pendingPreviewControl;
        var item = _pendingPreviewItem;
        if (control is null || item is null || !control.IsPointerOver)
        {
            _logger.LogDebug("预览取消：到点时指针已经不在图标上");
            return;
        }

        try
        {
            // 枚举在悬停一开始就排到后台去了，到点时通常已经算完，这段等待等于没有。
            var windows = await (_pendingPreviewWindows ?? _viewModel.GetWindowsAsync(item.Id));
            _pendingPreviewWindows = null;
            if (windows.Count == 0)
            {
                // 「在运行但一个可见窗口都没有」是用户最容易误判成「预览坏了」的一种状态，留个证据。
                _logger.LogDebug("预览 {Name}：进程在，但没有可见窗口", item.DisplayName);
                return;
            }

            // 等回来时鼠标可能已经移走了，别在用户已经点到别处之后才弹出预览。
            if (!control.IsPointerOver)
            {
                _logger.LogDebug("预览 {Name}：{Count} 个窗口，但指针已移开，放弃弹出", item.DisplayName, windows.Count);
                return;
            }

            var offset = control.TranslatePoint(new Point(control.ActualWidth / 2, 0), this);
            _preview.ShowFor(windows, Left + offset.X, Top + offset.Y, VisualTreeHelper.GetDpi(this).DpiScaleX);
            _logger.LogDebug("预览 {Name}：已弹出 {Count} 个窗口", item.DisplayName, windows.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成窗口预览失败：{Name}", item.DisplayName);
        }
    }

    /// <summary>
    /// 相邻图标向外让位：左右各 1 格移 9px、2 格移 4.5px，形成挤压感。
    /// 用 RenderTransform 而不是改 Margin，是为了让动画走合成器而不是触发重排（保 60FPS）。
    /// </summary>
    private void ShiftNeighbors(int index, bool entering)
    {
        var vertical = IsVertical;
        var property = vertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;

        for (var ring = 1; ring <= NeighborReach; ring++)
        {
            var distance = ring == 1 ? NeighborNearShift : NeighborFarShift;

            foreach (var sign in new[] { -1, 1 })
            {
                var target = index + (sign * ring);
                if (target < 0 || target >= ItemsHost.Items.Count)
                {
                    continue;
                }

                if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(target) is not FrameworkElement container)
                {
                    continue;
                }

                // 每个容器一份 TranslateTransform：没有、或者是样式里那种被冻结的共享实例，就新建一个。
                // 对冻结的 Freezable 调 BeginAnimation 会抛 InvalidOperationException，让位与预览一起失效。
                if (container.RenderTransform is not TranslateTransform transform || transform.IsFrozen)
                {
                    transform = new TranslateTransform();
                    container.RenderTransform = transform;
                }

                var animation = new DoubleAnimation(entering ? distance * sign : 0, NeighborDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };

                transform.BeginAnimation(property, animation);
            }
        }
    }

    private void OnChromeDragEnter(object sender, DragEventArgs e) => SetDropEffect(e);

    private void OnChromeDragOver(object sender, DragEventArgs e)
    {
        // 内部排序已经不走 OLE 拖拽（自己实现的实时换位），这里的 DragOver 只服务外部文件投放。
        SetDropEffect(e);
    }

    private static void SetDropEffect(DragEventArgs e)
    {
        // 内部排序不走 OLE 拖拽（自己实现的实时换位），这里只认外部来源。
        e.Effects = e.Data.GetDataPresent(AppPathFormat)
            ? DragDropEffects.Copy
            : e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Link
                : DragDropEffects.None;

        e.Handled = true;
    }

    private async void OnChromeDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(AppPathFormat) is string appPath && !string.IsNullOrWhiteSpace(appPath))
        {
            var added = await _viewModel.AddItemAsync(appPath);
            _viewModel.Notify(added
                ? $"已固定 {Path.GetFileNameWithoutExtension(appPath)} 到 Dock"
                : "这个应用本来就在 Dock 上了", added ? ToastKind.Success : ToastKind.Info);
            Reposition();
            e.Handled = true;
            return;
        }

        // 从资源管理器直接拖 .exe / .lnk / .url 到 Dock 上，是最顺手的一种添加方式。
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            var accepted = 0;
            foreach (var path in dropped.Where(IsLaunchable))
            {
                if (await _viewModel.AddItemAsync(path))
                {
                    accepted++;
                }
            }

            var skipped = dropped.Length - accepted;
            if (accepted == 0)
            {
                _viewModel.Notify("Dock 接受 .exe、.lnk、.url 和文件夹；其他类型这次没加上", ToastKind.Warning);
            }
            else
            {
                _viewModel.Notify(skipped > 0
                    ? $"已固定 {accepted} 项，另有 {skipped} 项没加上（类型不支持或本来就在 Dock 上）"
                    : $"已固定 {accepted} 项到 Dock", skipped > 0 ? ToastKind.Warning : ToastKind.Success);
            }

            Reposition();
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// Dock 收的东西：可启动项（exe / lnk / url）以及文件夹本身。
    /// 文件夹是用户最自然的拖拽预期 —— 常用目录钉在底边，点一下就是资源管理器打开它。
    /// </summary>
    private static bool IsLaunchable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            return true;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    private DockItemViewModel? ResolveItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: DockItemViewModel item })
            {
                return item;
            }

            source = source is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    /// <summary>右键「重命名」：小输入框改 Dock 上显示的名字。</summary>
    private async void OnRenameRequested(object? sender, string id)
    {
        var item = _viewModel.Items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null)
        {
            return;
        }

        var name = TextInputWindow.Ask(
            "重命名 Dock 图标",
            "只改 Dock 上显示的名字，磁盘上的程序与快捷方式文件名不会变。",
            item.DisplayName,
            text => text.Trim().Length == 0 ? "名字不能是空的" : null);

        if (name is not null)
        {
            await _viewModel.RenameItemAsync(id, name);
        }
    }

    /// <summary>右键「更改图标」：挑一个 .ico / .exe / .dll 当图标来源。</summary>
    private async void OnIconRequested(object? sender, string id)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择一个图标来源（.ico / .exe / .dll）",
            Filter = "图标与可执行文件|*.ico;*.exe;*.dll|所有文件|*.*",
        };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.SetIconAsync(id, dialog.FileName);
        }
    }

    private void ApplyOrientation()
    {
        var vertical = IsVertical;
        _viewModel.IsVertical = vertical;

        RootStrip.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

        var buttonMargin = vertical ? new Thickness(0, 3, 0, 3) : new Thickness(3, 0, 3, 0);
        LauncherButton.Margin = buttonMargin;
        FolderButton.Margin = buttonMargin;
        AddButton.Margin = buttonMargin;

        ItemsHost.ItemsPanel = (ItemsPanelTemplate)Resources[vertical ? "VerticalDockPanel" : "HorizontalDockPanel"];
        ItemsHost.ItemContainerStyle = (Style)Resources[vertical ? "VerticalContainerStyle" : "HorizontalContainerStyle"];
    }

    private void ApplyAppearance()
    {
        var opacity = Math.Clamp(_config.Current.DockOpacity, 0.2, 1.0);

        // 只让背景半透明：若把 Opacity 设到整个窗口，图标和文字会跟着一起发虚。
        if (TryFindResource("DockBackgroundBrush") is SolidColorBrush baseBrush)
        {
            var color = baseBrush.Color;
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(opacity * 255), color.R, color.G, color.B));
            brush.Freeze();
            Backdrop.Background = brush;
        }

        // 说明：AllowsTransparency=True 的分层窗口不接受 DWM 背景模糊，
        // 所以「毛玻璃」在这里是用圆角 + 柔和外投影近似，真正的背景模糊留给 v1.1。
        if (_config.Current.BlurEnabled)
        {
            Backdrop.Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 0.35,
                Color = Colors.Black,
            };
            Backdrop.CornerRadius = new CornerRadius(18);
        }
        else
        {
            Backdrop.Effect = null;
            Backdrop.CornerRadius = new CornerRadius(8);
        }
    }

    private void SyncAutoHideMode()
    {
        if (!_config.Current.AutoHideDock)
        {
            _hotZoneTimer.Stop();
            _autoHideTimer.Stop();
            SlideIn();
            return;
        }

        _hotZoneTimer.Start();

        if (IsCursorInHotZone())
        {
            SlideIn();
        }
        else
        {
            SlideOut();
        }
    }

    private void ScheduleAutoHide()
    {
        if (_config.Current.AutoHideDock && _userAllowedVisible)
        {
            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }
    }

    private bool IsCursorInHotZone()
    {
        if (!WindowInterop.TryGetCursorPhysicalPosition(out var physical))
        {
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = physical.X / dpi.DpiScaleX;
        var y = physical.Y / dpi.DpiScaleY;
        var work = SystemParameters.WorkArea;

        // 整条屏幕边都算热区，不再要求光标同时落在 Dock 的水平跨度内：
        // Dock 只占中间那一段，逼人在屏幕底部找位置，就是「蹭几下才出来」的主因。
        // 这与 Windows 自己隐藏任务栏的触发方式一致。
        return _config.Current.DockPosition switch
        {
            "Left" => x <= work.Left + HotZoneDip,
            "Right" => x >= work.Right - HotZoneDip,
            _ => y >= work.Bottom - HotZoneDip,
        };
    }

    private void SlideIn()
    {
        if (_slidIn)
        {
            return;
        }

        _slidIn = true;
        AnimateSlide(0, 0);
    }

    private void SlideOut()
    {
        if (!_slidIn)
        {
            return;
        }

        _slidIn = false;
        var (x, y) = HiddenOffset();
        AnimateSlide(x, y);
    }

    private (double X, double Y) HiddenOffset()
    {
        // 藏到屏幕外时多退 8px，把投影边缘也带走，避免残留一条亮边。
        return _config.Current.DockPosition switch
        {
            "Left" => (-(ActualWidth + 8), 0),
            "Right" => (ActualWidth + 8, 0),
            _ => (0, ActualHeight + 8),
        };
    }

    private void AnimateSlide(double x, double y)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, SlideDuration) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, SlideDuration) { EasingFunction = ease });
    }

    private void OnSystemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
        {
            Reposition();
        }
    }

    private void Reposition()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var work = SystemParameters.WorkArea;

        // 「保留托盘」模式：任务栏被裁得只剩右侧托盘段，中间是纯壁纸 ——
        // Dock 直接落到底边，跟右侧托盘站在同一条水平线上，不悬在工作区底边的半空里。
        // 工作区仍被（裁过的）任务栏收缩着，所以这里必须用屏幕物理下沿。
        var keepTray = _config.Current.TaskbarBarEnabled && _config.Current.TaskbarKeepTray;

        // 贴边 = 压到屏幕物理下沿，也就是盖在任务栏那一条上。任务栏图标默认靠左，
        // Dock 水平居中正好落在它的空白中段，既不挡图标也不占停靠区。
        // 例外一：贴桌面层时 Dock 排在任务栏后面，压到物理底边等于彻底看不见，只能退回工作区底边。
        // 例外二：「隐藏整个任务栏」的模式接管了整条底边，压到物理底边会盖在栏上面，同样退回工作区底边
        //         （「保留托盘」模式中间没有栏，反而需要贴底 —— 见上面的 keepTray）。
        var flush = !_attachedToDesktop
                    && (keepTray
                        || (_config.Current.DockFlushToEdge && !_config.Current.TaskbarBarEnabled));
        var gap = flush ? 0 : EdgeGap;
        var bottom = flush ? Math.Max(work.Bottom, SystemParameters.PrimaryScreenHeight) : work.Bottom;

        switch (_config.Current.DockPosition)
        {
            case "Left":
                Left = work.Left + gap;
                Top = work.Top + Math.Max(0, (work.Height - ActualHeight) / 2);
                break;
            case "Right":
                Left = work.Right - ActualWidth - gap;
                Top = work.Top + Math.Max(0, (work.Height - ActualHeight) / 2);
                break;
            default:
                Left = work.Left + Math.Max(0, (work.Width - ActualWidth) / 2);
                Top = bottom - ActualHeight - gap;
                break;
        }

        // 位置取整：半个像素的偏移会让整窗内容都画在像素缝上，图标和分隔线看着就是糊的。
        Left = Math.Round(Left);
        Top = Math.Round(Top);

        // 只有真的压进任务栏那条带子时才需要盯 Z 序，其他情况这个定时器根本不跑。
        _overlapsTaskbar = flush && Top + ActualHeight > work.Bottom + 1;
        if (_overlapsTaskbar)
        {
            _taskbarOrderTimer.Start();
            _desktopOrder.Start(KeepAboveTaskbar);
            KeepAboveTaskbar();
        }
        else
        {
            _taskbarOrderTimer.Stop();
            _desktopOrder.Stop();
            _raisedAboveTaskbar = false;
        }

        // 定位是这类问题（压到任务栏、跑出屏幕）唯一的现场证据，留一行日志比事后猜快得多。
        _logger.LogDebug(
            "Dock 定位 {Position}：工作区 {Work}，窗口 {Rect}，可见 {Visible}，贴桌面层 {Layer}，贴边 {Flush}",
            _config.Current.DockPosition,
            $"{work.Left},{work.Top}-{work.Right},{work.Bottom}",
            $"{Left},{Top}-{Left + ActualWidth},{Top + ActualHeight}",
            IsVisible,
            _attachedToDesktop,
            flush);
    }

    /// <summary>
    /// 贴边时 Dock 摆在任务栏那条带子里，而 explorer 会不停把 Shell_TrayWnd 重新抬回置顶组最上面
    /// —— 于是 Dock 的图标（正好在那 48px 里）既收不到点击也收不到悬停，表现就是
    /// 「刚启动几秒能用，之后整个 Dock 像死了」。轮询到任务栏确实排在 Dock 上面时，抬一次。
    /// </summary>
    private void KeepAboveTaskbar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible || !_overlapsTaskbar)
        {
            return;
        }

        _taskbarHwnd = WindowInterop.FindWindow(TaskbarClassName);
        if (_taskbarHwnd == IntPtr.Zero)
        {
            return;
        }

        // 直接问 Z 序，不问像素：任务栏是带逐像素透明的分层窗口，探测点落在它透明的那一段时
        // WindowFromPoint 会穿透返回我们自己，「明明被压着」却判成「没被压着」。
        var stolen = WindowInterop.IsAbove(_taskbarHwnd, hwnd);

        if (!stolen)
        {
            _raisedAboveTaskbar = false;
            return;
        }

        WindowInterop.SetTopmost(hwnd, true);
        if (!_raisedAboveTaskbar)
        {
            _raisedAboveTaskbar = true;
            _logger.LogDebug("Dock 贴边：Z 序被任务栏抢走了，已把 Dock 重新抬到置顶层");
        }
    }

    private void UpdateDesktopLayerState()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_config.Current.DesktopLayerMode)
        {
            if (_attachedToDesktop)
            {
                return;
            }

            Topmost = false;
            _attachedToDesktop = DesktopParent.Attach(hwnd);
            if (!_attachedToDesktop)
            {
                // 挂不上就退回普通置顶模式，设置项不该让 Dock 直接消失。
                _logger.LogWarning("贴桌面层挂接失败，退回普通置顶模式");
                Topmost = true;

                // 这个开关的效果本来就看不见摸不着，成没成得说一句。只报一次：
                // 配置每次广播都会走到这里，每次都念就成了刷屏。
                if (!_desktopLayerReported)
                {
                    _desktopLayerReported = true;
                    _viewModel.Notify(
                        "贴桌面层没能挂上（这个模式依赖未公开的系统消息，新版 Windows 常不支持），已退回普通置顶。",
                        ToastKind.Warning);
                }
            }
            else if (!_desktopLayerReported)
            {
                _desktopLayerReported = true;
                _viewModel.Notify(
                    "Dock 已沉到桌面图标那一层：它会在所有窗口后面，点桌面才看得到。想换回来在设置 → 外观 里关掉。",
                    ToastKind.Info);
            }

            return;
        }

        if (_attachedToDesktop)
        {
            DesktopParent.Detach(hwnd);
            _attachedToDesktop = false;
            Topmost = true;
        }

        // 关掉之后再打开，允许重新报一次结果。
        _desktopLayerReported = false;
    }

}
