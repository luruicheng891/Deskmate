using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeskMate.App.Views;

/// <summary>
/// 启动台：按屏幕 70% 居中的悬浮面板 + 搜索 + 按行虚拟化的应用网格。
/// 窗口只隐藏不关闭，二次打开只是重新显示，磁贴与图标都还在内存里（需求：二次打开 &lt; 100ms）。
/// </summary>
public partial class LauncherWindow : Window
{
    private const double TileCellWidth = 120;

    /// <summary>面板相对屏幕的占比（需求：不铺满全屏）。</summary>
    private const double PanelRatio = 0.70;

    // 窗口内边距 26×2 加上内容网格边距 40×2。
    private const double ContentHorizontalMargin = 132;

    // 面板透明度完全跟着 Dock 的滑条，取值范围也与 Dock 一致：
    // 之前给启动台单设过一个 0.55 的下限，结果整块面板变成屏幕中间的一块白板。
    private const double MinPanelOpacity = 0.2;

    // 模糊半径：太小看不出「磨砂」，太大图标后面的字会糊成一片。与 Dock 的圆角/投影同一套参数。
    private const double FrostBlurRadius = 22;

    /// <summary>抓底图与模糊都按 1/2 分辨率做，铺回面板时看不出差别的。</summary>
    private const int FrostDownscale = 2;

    private readonly LauncherViewModel _viewModel;
    private readonly IConfigService _config;
    private readonly ILogger<LauncherWindow> _logger;
    private int _visibilityGeneration;

    /// <summary>用户真的呼出过（预热就不必再做了，也免得预热的收尾把真界面收掉）。</summary>
    private bool _openedByUser;
    private bool _contextMenuOpen;
    private Point _dragStart;
    private AppTileViewModel? _dragCandidate;

    /// <summary>构造启动台窗口。</summary>
    public LauncherWindow(LauncherViewModel viewModel, IConfigService config, ILogger<LauncherWindow> logger)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _config = config;
        _logger = logger;
        DataContext = viewModel;

        _viewModel.AddAppRequested += OnAddAppRequested;
        _config.ConfigChanged += (_, _) => ApplyBackdrop();
        SizeChanged += (_, _) => UpdateColumns();
        Deactivated += OnDeactivated;

        RowsHost.PreviewMouseLeftButtonDown += OnTilePreviewMouseLeftButtonDown;
        RowsHost.MouseMove += OnTileMouseMove;
    }

    /// <summary>呼出启动台。</summary>
    public void ShowLauncher()
    {
        _visibilityGeneration++;
        _openedByUser = true;

        ApplyPanelGeometry();

        // 弹出方向以「鼠标当下所在的位置」为锚点，所以从 Dock 图标点出来和按快捷键呼出，
        // 面板都是朝各自那个方向长出来的。必须在 Show 之前问，Show 之后焦点/光标可能已经变了。
        var origin = GrowthOrigin();

        // 抓图必须在 Show 之前（否则会把面板自己抓进去），但模糊留到 Show 之后做：
        // 面板先出来、玻璃随后填上，这点时间正好被淡入动画盖住，按下去才不觉得delay。
        var raw = GrabBackdrop(new Rect(Left, Top, Width, Height));

        Opacity = 0;
        Show();
        UpdateColumns();

        // 色调层必须自己算：XAML 里那两层挂的是 WindowBackgroundBrush，它是不实的近白色
        // （浅色主题 #F7F8FA）。不覆盖的话整块面板就是一张白纸，底下抓好的玻璃直接被盖住。
        ApplyTransparency();

        // 开场动画等真出帧之后再挂：首帧可能被「窗口刚从隐藏里回来」拖住一百多毫秒，
        // 而动画走的是真实时间 —— 挂在 Show 上就是让人看半截开场。
        var generation = _visibilityGeneration;
        WindowWarmup.WhenNextFrame(() =>
        {
            if (generation == _visibilityGeneration)
            {
                WindowTransition.PlayLauncher(this, Panel, origin, showing: true);
            }
        });

        ApplyFrostedBackdrop(raw);

        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();

        // 启动后的预热已经把列表与图标备好的话，这里只后台对齐现场。再走一遍载入会把整片磁贴重铺，
        // 实测正好砸在开场动画中间 —— 400ms 里只出得到 11 帧，看着就是「卡一下才长出来」。
        if (_viewModel.IsLoaded)
        {
            // 每次呼出都在后台对齐一次现场：装完新软件不必重启 DeskMate 就能出现在这里。
            _ = _viewModel.RefreshAsync(report: false);
            return;
        }

        _ = LoadSafelyAsync();
    }

    /// <summary>
    /// 面板尺寸：主屏的 70%，带下限（需求：不铺满全屏）。呼出与首开预热都算这一个，
    /// 两边尺寸不一样就等于预热白做。
    /// </summary>
    private static Size PanelSize() => new(
        Math.Max(720, SystemParameters.PrimaryScreenWidth * PanelRatio),
        Math.Max(460, SystemParameters.PrimaryScreenHeight * PanelRatio));

    /// <summary>按屏幕 70% 居中摆好面板（位置取整：半个像素的偏移会让内容画在像素缝上）。</summary>
    private void ApplyPanelGeometry()
    {
        var size = PanelSize();
        Width = size.Width;
        Height = size.Height;
        Left = Math.Round(Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2));
        Top = Math.Round(Math.Max(0, (SystemParameters.PrimaryScreenHeight - Height) / 2));
    }

    /// <summary>
    /// 首开前的预热：量排布局之后，再以「全透明 + 不激活」真渲染一次。
    /// <para>
    /// 分层窗口（AllowsTransparency=True）的头一帧不只是布局：DWM 的重定向表面要建、
    /// 阴影那层着色器要现编译，实测 150~165ms。只做 Measure/Arrange 一点也省不掉 ——
    /// 它落在用户按下的那一刻，就是「屏幕先定住两百毫秒，然后面板啪地出现」。
    /// 整窗 Opacity=0 的分层窗口在屏幕上是什么都没有的，渲染完成立刻收掉。
    /// </para>
    /// <para>
    /// 收尾走「渲染完成 + 兜底定时器」两条路，谁先到算谁、只执行一次：置顶窗口赖在屏幕上不走
    /// 是比卡顿更糟的事故（它看不见，却会吃掉那一片的点击），所以不能只等 <c>ContentRendered</c>。
    /// </para>
    /// </summary>
    public void PreWarmLayout()
    {
        if (IsVisible || _openedByUser)
        {
            return;
        }

        ApplyPanelGeometry();
        WindowWarmup.PreWarmLayout(this, PanelSize());

        var generation = _visibilityGeneration;
        var done = false;

        var failsafe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        failsafe.Tick += (_, _) => Finish();

        void Finish()
        {
            if (done)
            {
                return;
            }

            done = true;
            ContentRendered -= OnWarmed;
            failsafe.Stop();
            ShowActivated = true;

            // 期间用户真的按下过（代际号变了）就这次预热说了不算，界面归 ShowLauncher 管。
            if (generation == _visibilityGeneration)
            {
                Hide();
            }
        }

        void OnWarmed(object? sender, EventArgs e) => Finish();

        Opacity = 0;
        ShowActivated = false;
        ContentRendered += OnWarmed;
        failsafe.Start();
        Show();
    }

    /// <summary>收起启动台（保留实例与已加载图标）。</summary>
    public void HideLauncher()
    {
        if (!IsVisible)
        {
            return;
        }

        var generation = _visibilityGeneration;

        // 收回面板中心 + 下沉 + 淡出，像被吸回 Dock 那一边。
        WindowTransition.PlayLauncher(this, Panel, new Point(0.5, 0.5), showing: false, onHidden: () =>
        {
            // 淡出期间用户又呼出的话代际号已经变了，不能把新界面关掉。
            if (generation == _visibilityGeneration)
            {
                Hide();
            }
        });
    }

    /// <summary>
    /// 动画原点，相对面板归一化：0,0 是面板左上角，1,1 是右下角。
    /// Dock 在屏幕底边、面板只占中间 70%，所以从 Dock 点进来时 Y 会大于 1 —— 越界是故意的，
    /// 缩放就以那个点在面板外的位置为锚，读起来才是「从图标那里长出来」。
    /// </summary>
    private Point GrowthOrigin()
    {
        if (!WindowInterop.TryGetCursorPhysicalPosition(out var cursor))
        {
            return new Point(0.5, 0.5);
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (cursor.X / dpi.DpiScaleX - Left) / Width;
        var y = (cursor.Y / dpi.DpiScaleY - Top) / Height;

        // 再远也要收个边界：原点跑到十万八千里外时，缩放等效于一次大幅平移，看着像窗口在飞。
        return new Point(Math.Clamp(x, -0.5, 1.5), Math.Clamp(y, -0.5, 1.5));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideLauncher();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            // 手动刷新要有回执（视图模型会播报新增/移除了几个），后台静默对齐才不播报。
            _viewModel.RefreshAppsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            OpenFirstTile();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    // 磁贴右键菜单会让窗口失焦，用标记挡住「失焦即关闭」，否则菜单一点就整个消失。
    private void OnTileMenuOpened(object sender, RoutedEventArgs e) => _contextMenuOpen = true;

    private void OnTileMenuClosed(object sender, RoutedEventArgs e) => _contextMenuOpen = false;

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_contextMenuOpen)
        {
            HideLauncher();
        }
    }

    private async void OnAddAppRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要加入启动台的应用或快捷方式",
            Filter = "程序与快捷方式|*.exe;*.lnk|所有文件|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // 一次调用而不是逐个 await：视图模型会把结果聚成一条提示，多选时才不会弹成一列重复的框。
        await _viewModel.AddAppsFromPathsAsync(dialog.FileNames);
    }

    private void OnTilePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragCandidate = e.OriginalSource is DependencyObject source ? ResolveTile(source) : null;
    }

    private void OnTileMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var tile = _dragCandidate;
        _dragCandidate = null;

        // 拖到 Dock 松手即可固定：DockWindow 认这个数据格式。
        DragDrop.DoDragDrop(this, new DataObject(DockWindow.AppPathFormat, tile.Path), DragDropEffects.Copy);
    }

    private async Task LoadSafelyAsync()
    {
        try
        {
            await _viewModel.LoadAsync();
            UpdateColumns();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动台加载应用列表失败");
        }
    }

    private void OpenFirstTile()
    {
        var first = _viewModel.Rows.FirstOrDefault()?.Tiles.FirstOrDefault();
        if (first is null)
        {
            return;
        }

        _viewModel.OpenTile(first);
        HideLauncher();
    }

    /// <summary>背景两件事（改配置时走这条同步路径）：重抓底图当玻璃，再叠一层跟着滑条的色调。</summary>
    private void ApplyBackdrop()
    {
        ApplyFrostedBackdrop(GrabBackdrop(new Rect(Left, Top, Width, Height)));
        ApplyTransparency();
    }

    /// <summary>面板背景跟着 Dock 的透明度滑条走，和 Dock 同一观感。</summary>
    private void ApplyTransparency()
    {
        if (TryFindResource("DockBackgroundBrush") is not SolidColorBrush baseBrush)
        {
            return;
        }

        var opacity = Math.Clamp(_config.Current.DockOpacity, MinPanelOpacity, 1.0);

        // 浅色主题的 DockBackgroundBrush 是近白色：背景再是个白页面时，模糊白底 + 白色调 = 一整块纯白，
        // 读不出「玻璃」。统一压一档亮度，让面板永远比背景暗一点，材质才立得住。
        const double Shade = 0.88;
        var color = baseBrush.Color;
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(opacity * 255),
            (byte)Math.Round(color.R * Shade),
            (byte)Math.Round(color.G * Shade),
            (byte)Math.Round(color.B * Shade)));
        brush.Freeze();

        // 阴影那层压在面板底下，它不跟着透明的话整块面板等于还是实心的。
        ShadowBorder.Background = brush;
        SurfaceBorder.Background = brush;
    }

    /// <summary>
    /// 毛玻璃：分层窗口（AllowsTransparency=True）不接受 DWM 的背景采样，
    /// 所以把面板后面那块屏幕抓下来、高斯模糊后当背景，上面再压一层主题色调。
    /// </summary>
    private void ApplyFrostedBackdrop(BitmapSource? raw)
    {
        if (raw is null)
        {
            FrostBorder.Background = null;
            return;
        }

        // 模糊是这里最贵的一步，排到 Background 优先级：面板与淡入动画先跑起来，
        // 玻璃在动画途中填上，看不出来 —— 不然按下 Dock 到面板出现之间就有一段可感知的停顿。
        var generation = _visibilityGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (generation != _visibilityGeneration)
            {
                return;
            }

            var blurred = Blur(raw);
            FrostBorder.Background = new ImageBrush(blurred) { Stretch = Stretch.Fill };
        });
    }

    /// <summary>
    /// 抓面板后面那块屏幕。每次呼出都重抓 —— 以前按「位置尺寸没变就复用」缓存过，
    /// 结果关掉挡在后面的应用再打开启动台，玻璃里还留着它。抓图本身很便宜（GDI BitBlt），
    /// 贵的模糊另算，所以这里只抓不糊。关闭毛玻璃或抓图失败时返回 null，面板退回半透明纯色。
    /// </summary>
    private BitmapSource? GrabBackdrop(Rect regionDip)
    {
        if (!_config.Current.BlurEnabled)
        {
            return null;
        }

        var started = Stopwatch.GetTimestamp();
        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelWidth = (int)Math.Round(regionDip.Width * dpi.DpiScaleX);
        var pixelHeight = (int)Math.Round(regionDip.Height * dpi.DpiScaleY);

        // 直接抓 1/2 分辨率：GDI 那边少搬 3/4 的字节，抓图本身从 ~23ms 降到 ~6ms。
        var raw = ScreenCapture.Grab(
            (int)Math.Round(regionDip.X * dpi.DpiScaleX),
            (int)Math.Round(regionDip.Y * dpi.DpiScaleY),
            pixelWidth,
            pixelHeight,
            FrostDownscale);

        _logger.LogDebug(
            "启动台毛玻璃：屏幕 {Region}x{Region2} → 抓到 {Captured}x{Captured2}，用时 {Elapsed} ms，{Result}",
            pixelWidth,
            pixelHeight,
            raw?.PixelWidth ?? 0,
            raw?.PixelHeight ?? 0,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0.#"),
            raw is null ? "失败（退回半透明纯色）" : "成功");

        return raw;
    }

    /// <summary>
    /// 高斯模糊。传进来的已经是 1/2 分辨率的抓图，这里再降一半做（屏幕的 1/4）：
    /// 像素少四分之三，UI 线程从 20ms 降到 5ms 上下 —— 模糊和淡入动画是同一次开场里跑的，
    /// 这 20ms 就是一帧半的空隙，肉眼读成「卡了一下」。
    /// 高斯之后图像已经没有高频，铺回面板时双线性放大看不出差别。
    /// 半径按这一趟的实际降采样倍数换算，模糊程度才和整分辨率一致。
    /// </summary>
    private static BitmapSource Blur(BitmapSource source)
    {
        const int blurPassDivisor = 2;
        var targetWidth = Math.Max(1, source.PixelWidth / blurPassDivisor);
        var targetHeight = Math.Max(1, source.PixelHeight / blurPassDivisor);

        var image = new System.Windows.Controls.Image
        {
            Source = source,
            Width = targetWidth,
            Height = targetHeight,
            Effect = new BlurEffect
            {
                Radius = FrostBlurRadius / (FrostDownscale * blurPassDivisor),
                KernelType = KernelType.Gaussian,
            },
        };

        var size = new Size(targetWidth, targetHeight);
        image.Measure(size);
        image.Arrange(new Rect(size));
        image.UpdateLayout();

        // 声明成 96 DPI、由 ImageBrush 铺满面板矩形，所以降分辨率只影响细节，不影响对齐。
        var target = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(image);
        target.Freeze();
        return target;
    }

    private void UpdateColumns()
    {
        if (ActualWidth > 0)
        {
            _viewModel.SetColumns((int)Math.Floor((ActualWidth - ContentHorizontalMargin) / TileCellWidth));
        }
    }

    private static AppTileViewModel? ResolveTile(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: AppTileViewModel tile })
            {
                return tile;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

}
