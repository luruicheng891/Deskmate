using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskMate.Core.Models;
using DeskMate.Interop;

namespace DeskMate.App.Views;

/// <summary>
/// Dock 悬停预览：把一个应用当前打开的窗口用 DWM 实时缩略图排成一排。
/// 缩略图由 DWM 在合成阶段直接搬运源窗口画面，所以它会动、也不占我们的 CPU。
/// </summary>
public sealed partial class WindowPreviewWindow : Window
{
    private const int MaxThumbnails = 6;
    private const double MoreWidth = 46;
    private const double BoxWidth = 236;
    private const double BoxHeight = 132;
    private const double Gap = 12;
    private const double ContentPadding = 10;
    private const double LabelHeight = 22;

    /// <summary>卡片标题行右侧那个 × 的大小。放在标题行而不是缩略图上：DWM 的画面是合成在窗口内容之上的，画在它里面的东西会被盖掉。</summary>
    private const double CloseWidth = 22;

    private const double CloseHeight = 16;

    private readonly List<Entry> _entries = new();

    private double _dpiScale = 1.0;

    /// <summary>这一次是挂在哪个图标的哪个边上；DWM 报完真实尺寸后要拿它重新居中一次。</summary>
    private double _anchorCenterX;
    private double _anchorTopY;

    /// <summary>超出 6 张时的「+N」，跟着卡片一起重排。</summary>
    private TextBlock? _moreLabel;

    /// <summary>构造预览窗口。</summary>
    public WindowPreviewWindow() => InitializeComponent();

    /// <summary>预览被收起（鼠标离开、或点完一下）。</summary>
    public event EventHandler? DismissRequested;

    /// <summary>用户点了某一张缩略图，要求切到那个窗口。</summary>
    public event EventHandler<AppWindowInfo>? WindowChosen;

    /// <summary>用户点了某一张卡片标题行右侧的 ×，要求关掉那一个窗口。</summary>
    public event EventHandler<AppWindowInfo>? CloseRequested;

    /// <summary>鼠标是否停在预览上，Dock 用它决定要不要延后收起。</summary>
    public bool IsPointerInside { get; private set; }

    /// <summary>
    /// 自检用：交出每张卡片的两个热区（切窗口 / 关闭）。
    /// 这两个矩形一旦重叠，点 × 会先被当成「切到这个窗口」，看起来就是 × 没反应。
    /// </summary>
    public IReadOnlyList<(Rect Card, Rect Close)> HitRectsForSelfCheck()
        => _entries.Select(entry => (entry.Bounds, entry.CloseBounds)).ToList();

    /// <summary>当前是否正在展示预览。</summary>
    public bool IsShowing { get; private set; }

    /// <summary>
    /// 展示一组窗口的缩略图。
    /// </summary>
    /// <param name="windows">同一应用当前打开的窗口（Z 序）。</param>
    /// <param name="anchorCenterX">Dock 图标中心 X（DIP，屏幕坐标）。</param>
    /// <param name="anchorTopY">Dock 顶边 Y（DIP），预览浮在它上方。</param>
    /// <param name="dpiScale">Dock 所在屏幕的 DPI 缩放，DIP 与物理像素互算要用。</param>
    public void ShowFor(IReadOnlyList<AppWindowInfo> windows, double anchorCenterX, double anchorTopY, double dpiScale)
    {
        var shown = windows.Take(MaxThumbnails).ToList();
        if (shown.Count == 0)
        {
            Clear();
            return;
        }

        // 同一批窗口再悬停一次：缩略图已经在 DWM 里挂好了，重新注册一遍才是那点白等的时间。
        if (IsShowing && IsSameSet(shown))
        {
            for (var index = 0; index < shown.Count; index++)
            {
                _entries[index].Label.Text = shown[index].Title;
            }

            _anchorCenterX = anchorCenterX;
            _anchorTopY = anchorTopY;
            Position();
            return;
        }

        Clear();
        _dpiScale = dpiScale <= 0 ? 1 : dpiScale;
        _anchorCenterX = anchorCenterX;
        _anchorTopY = anchorTopY;

        var x = ContentPadding;
        var tallest = 0d;

        foreach (var window in shown)
        {
            // 先按 16:10 占位排布，等 DWM 报出源窗口真实尺寸后在 AttachThumbnails 里精修。
            var (width, height) = FitTo(16, 10);

            var frame = new Border
            {
                Width = width,
                Height = height,
                Background = (Brush)FindResource("SurfaceHoverBrush"),
                BorderBrush = (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
            };

            Canvas.SetLeft(frame, x);
            Canvas.SetTop(frame, ContentPadding);
            Host.Children.Add(frame);

            var caption = new Grid { Width = width };
            caption.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = window.Title,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = LabelHeight,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(label, 0);

            var close = BuildCloseButton();
            Grid.SetColumn(close, 1);

            caption.Children.Add(label);
            caption.Children.Add(close);

            Canvas.SetLeft(caption, x);
            Canvas.SetTop(caption, ContentPadding + height + 3);
            Host.Children.Add(caption);

            _entries.Add(new Entry(window, new Rect(x, ContentPadding, width, height), frame, label, caption, close)
            {
                // AttachThumbnails 会按 DWM 报出的真实尺寸重算一遍；这里先给一份，
                // 免得它因为拿不到句柄提前返回时，× 的热区是空的。
                CloseBounds = new Rect(x + width - CloseWidth, ContentPadding + height + 3, CloseWidth, LabelHeight),
            });

            x += width + Gap;
            tallest = Math.Max(tallest, height);
        }

        if (windows.Count > shown.Count)
        {
            // 只画前 6 张而不说后面还有，用户会以为这个应用就只有 6 个窗口能切。
            _moreLabel = new TextBlock
            {
                Text = $"+{windows.Count - shown.Count}",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            };

            Canvas.SetLeft(_moreLabel, x);
            Canvas.SetTop(_moreLabel, ContentPadding + (tallest / 2));
            Host.Children.Add(_moreLabel);

            x += MoreWidth;
        }

        Width = x - Gap + ContentPadding;
        Height = ContentPadding + tallest + LabelHeight + ContentPadding * 2;

        Position();

        // Show 之后才有可用的 HWND，DWM 缩略图必须挂到真实句柄上。
        if (!IsVisible)
        {
            Show();
        }

        AttachThumbnails();
        IsShowing = true;
    }

    /// <summary>收起并释放全部缩略图。</summary>
    public void Dismiss()
    {
        Clear();
        IsShowing = false;

        if (IsVisible)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Clear();
        base.OnClosed(e);
    }

    private void OnHostMouseUp(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Host);

        foreach (var entry in _entries)
        {
            // × 在标题行里，而标题行的矩形和卡片热区是分开算的：先判 ×，否则点 × 会先被当成「切到这个窗口」。
            if (entry.CloseBounds.Contains(point))
            {
                CloseRequested?.Invoke(this, entry.Window);
                DismissRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (entry.Bounds.Contains(point))
            {
                WindowChosen?.Invoke(this, entry.Window);
                DismissRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }
    }

    /// <summary>卡片标题行右侧的 ×：关掉那一个窗口（不是切过去，也不是整个应用）。</summary>
    private Border BuildCloseButton()
    {
        var idle = (Brush)FindResource("SurfaceHoverBrush");
        var hover = (Brush)FindResource("InvalidBrush");

        var button = new Border
        {
            Width = CloseWidth,
            Height = CloseHeight,
            Margin = new Thickness(6, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Background = idle,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            },
        };

        // 悬停变色用事件而不是样式：这是代码里造的元素，写一套 DataTrigger 反而更长。
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = idle;

        ToolTipService.SetToolTip(button, "关闭这个窗口");

        return button;
    }

    private void OnHostMouseEnter(object sender, MouseEventArgs e) => IsPointerInside = true;

    /// <summary>
    /// 离开预览时只清标记，不直接把自己关掉。收不收由 Dock 那边的看门狗决定 ——
    /// 指针从预览滑回图标（一直没离开图标）时，这里一关就把「想再点一张」的路径掐断了。
    /// </summary>
    private void OnHostMouseLeave(object sender, MouseEventArgs e) => IsPointerInside = false;

    private void Position()
    {
        var work = SystemParameters.WorkArea;

        Left = Math.Clamp(_anchorCenterX - Width / 2, work.Left + 6, Math.Max(work.Left + 6, work.Right - Width - 6));
        Top = Math.Max(work.Top + 6, _anchorTopY - Height - 6);
    }

    private void AttachThumbnails()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ThumbnailViewer.ApplyModernFrame(hwnd);

        // DWM 要挂上句柄之后才报得出源窗口的真实比例，所以每张卡片的实际尺寸只能在这里定下来。
        // 关键是这里必须把每一张的 X 也一起重排：以前只改了宽高、没重算 X，
        // 于是「画出来的位置」和「点击热区」错开 —— 开两个 Word 时点第二张常常选中第一张。
        var x = ContentPadding;
        var tallest = 0d;

        foreach (var entry in _entries)
        {
            var viewer = new ThumbnailViewer(hwnd, entry.Window.Handle);
            if (!viewer.IsAvailable)
            {
                // 源窗口不允许被缩略（少数受保护内容），保留标题卡片至少还能点。
                viewer.Dispose();
                entry.Viewer = null;
            }
            else
            {
                entry.Viewer = viewer;
            }

            var (width, height) = entry.Viewer is { } thumb
                ? FitTo(thumb.SourceWidth, thumb.SourceHeight)
                : FitTo(16, 10);

            entry.Frame.Width = width;
            entry.Frame.Height = height;
            entry.Bounds = new Rect(x, ContentPadding, width, height);

            Canvas.SetLeft(entry.Frame, x);
            Canvas.SetTop(entry.Frame, ContentPadding);
            Canvas.SetLeft(entry.Caption, x);
            Canvas.SetTop(entry.Caption, ContentPadding + height + 3);
            entry.Caption.Width = width;

            // × 贴在标题行最右边，热区按同一套坐标算出来（不靠元素实际位置，省一次布局往返）。
            entry.CloseBounds = new Rect(x + width - CloseWidth, ContentPadding + height + 3, CloseWidth, LabelHeight);

            x += width + Gap;
            tallest = Math.Max(tallest, height);
        }

        if (_moreLabel is not null)
        {
            Canvas.SetLeft(_moreLabel, x);
            Canvas.SetTop(_moreLabel, ContentPadding + (tallest / 2));
            x += MoreWidth;
        }

        // 尺寸在这里才最终定下来，所以必须再居中一次：
        // 用改之前的宽度算出来的 Left 会让整排相对图标偏出去，右边那张可能被屏幕边缘裁掉。
        Width = x - Gap + ContentPadding;
        Height = ContentPadding + tallest + LabelHeight + ContentPadding * 2;
        Position();

        foreach (var entry in _entries)
        {
            entry.Viewer?.UpdateDestination(
                (int)Math.Round(entry.Bounds.X * _dpiScale),
                (int)Math.Round(ContentPadding * _dpiScale),
                (int)Math.Round(entry.Bounds.Width * _dpiScale),
                (int)Math.Round(entry.Bounds.Height * _dpiScale));
        }
    }

    /// <summary>这次要展示的还是同一批窗口吗（句柄逐个比对，顺序也要一致）。</summary>
    private bool IsSameSet(IReadOnlyList<AppWindowInfo> windows)
    {
        if (windows.Count != _entries.Count)
        {
            return false;
        }

        for (var index = 0; index < windows.Count; index++)
        {
            if (windows[index].Handle != _entries[index].Window.Handle)
            {
                return false;
            }
        }

        return true;
    }

    private static (double Width, double Height) FitTo(int sourceWidth, int sourceHeight)
    {
        var aspect = sourceWidth / (double)Math.Max(1, sourceHeight);
        if (aspect <= 0)
        {
            aspect = 1.6;
        }

        var width = BoxWidth;
        var height = width / aspect;

        if (height > BoxHeight)
        {
            height = BoxHeight;
            width = height * aspect;
        }

        return (Math.Round(width), Math.Round(height));
    }

    private void Clear()
    {
        foreach (var entry in _entries)
        {
            entry.Viewer?.Dispose();
        }

        _entries.Clear();
        _moreLabel = null;
        Host.Children.Clear();
    }

    private sealed class Entry(AppWindowInfo window, Rect bounds, Border frame, TextBlock label, Grid caption, Border close)
    {
        public AppWindowInfo Window { get; } = window;

        public Rect Bounds { get; set; } = bounds;

        public Border Frame { get; } = frame;

        public TextBlock Label { get; } = label;

        /// <summary>标题行（标题 + ×），整行跟着卡片重新排。</summary>
        public Grid Caption { get; } = caption;

        /// <summary>关掉这一个窗口的按钮。</summary>
        public Border Close { get; } = close;

        /// <summary>× 的点击热区，和卡片热区分开判，先判它。</summary>
        public Rect CloseBounds { get; set; }

        public ThumbnailViewer? Viewer { get; set; }
    }
}
