using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMate.App.ViewModels;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeskMate.App.Views;

/// <summary>
/// 文件夹管理窗口。支持拖入文件夹、卡片拖拽排序、预览与重命名；
/// 全程只读写 config.json 里的路径引用。
/// </summary>
public partial class FolderManagerWindow : Window
{
    /// <summary>卡片拖拽排序的数据格式。</summary>
    private const string CardIdFormat = "DeskMate.FolderCardId";

    private readonly FolderManagerViewModel _viewModel;
    private readonly ILogger<FolderManagerWindow> _logger;
    private readonly FilePreviewWindow _filePreview;
    private Point _dragStart;
    private FolderCardViewModel? _dragCandidate;
    private bool _closingForReal;

    // 「按住空格」期间为 true：松手（或被人抢走前台）时据此把放大窗收掉。
    private bool _popoutHeld;

    /// <summary>
    /// 这一下鼠标手势是从哪个条目起头的、什么时候。
    /// <para>
    /// 为什么需要：单击会就地开内容窗格 → 卡片区的可用宽度变了 → 列数变了 → 卡片挪位置。
    /// 于是双击的第二下常常落到卡片之间那道缝、或者隔壁那张卡片上，用户看到的就是
    /// 「双击收藏里的 md 一点反应都没有」。双击该认「第一下点中的是谁」，不是「此刻光标底下是谁」。
    /// </para>
    /// </summary>
    private object? _gestureItem;

    private long _gestureAtTicks;

    // 淡出收尾是异步的，靠这个代际号判断「这次收尾还要不要执行」：
    // 淡出没结束就又呼出窗口时，旧收尾必须作废，否则会把刚显示出来的界面又 Hide 掉。
    private int _visibilityGeneration;

    /// <summary>构造文件夹管理窗口。</summary>
    public FolderManagerWindow(
        FolderManagerViewModel viewModel,
        FilePreviewWindow filePreview,
        ILogger<FolderManagerWindow> logger)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _logger = logger;
        _filePreview = filePreview;
        DataContext = viewModel;

        viewModel.PickFolderRequested += OnPickFolderRequested;
        viewModel.PickFileRequested += OnPickFileRequested;
        viewModel.PickCreateLocationRequested += OnPickCreateLocationRequested;
        viewModel.GroupDeleteConfirmRequested += OnGroupDeleteConfirmRequested;
        viewModel.RelocateRequested += OnRelocateRequested;
        viewModel.BatchDeleteRequested += OnBatchDeleteRequested;
        viewModel.FilePopoutRequested += OnFilePopoutRequested;
        viewModel.EntryRenameRequested += OnEntryRenameRequested;
        viewModel.EntryDeleteConfirmRequested += OnEntryDeleteConfirmRequested;

        // 放大窗自己收到「松手」时（焦点在它那边），主窗这边的按住记录也要跟着结掉。
        _filePreview.HoldReleased += () => _popoutHeld = false;

        RenameOverlay.IsVisibleChanged += OnRenameOverlayVisibleChanged;
        NewGroupOverlay.IsVisibleChanged += OnNewGroupOverlayVisibleChanged;
        RenameGroupOverlay.IsVisibleChanged += OnRenameGroupOverlayVisibleChanged;
        CreateEntryOverlay.IsVisibleChanged += OnCreateEntryOverlayVisibleChanged;
    }

    /// <summary>显示窗口；首次显示时才做载入与路径校验。</summary>
    public void ShowManager()
    {
        _visibilityGeneration++;

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();

        // 只淡内容层、不动整窗透明度：普通窗口被改了整窗 Opacity 会被 WPF 临时转成分层窗口，
        // 中间帧客户区没内容可合成，屏幕上就是一个黑框。关闭方向没有动画，见 OnClosing。
        WindowTransition.PlayContentIn(Panel);
        EnsureContentVisible();
        Left = Math.Round(Left);
        Top = Math.Round(Top);

        // 每次显示都重新校验一遍路径与文件数：窗口是单例、关掉只是隐藏，
        // 隔几小时再打开时卡片上的数据不该还是旧的。
        _ = ReloadSafelyAsync();
    }

    /// <summary>
    /// 淡入的兜底。上一次淡出的时钟如果还接管着 <c>Opacity</c>，赋本地值是不生效的，
    /// 窗口就会停在「标题栏在、内容一片空白」的样子 —— 用户报的空壳窗口就是这么来的。
    /// 过了淡入本该结束的时间还看不清，就把动画摘下来直接设成不透明。
    /// </summary>
    private void EnsureContentVisible()
    {
        var generation = _visibilityGeneration;
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        check.Tick += (_, _) =>
        {
            check.Stop();

            if (generation != _visibilityGeneration || !IsVisible || Panel.Opacity >= 0.99)
            {
                return;
            }

            Panel.BeginAnimation(UIElement.OpacityProperty, null);
            Panel.Opacity = 1;
            _logger.LogDebug("文件夹面板淡入没走表，已直接把内容层设为不透明");
        };

        check.Start();
    }

    /// <summary>
    /// 这些窗口在 DI 里是单例，而 WPF 的窗口一旦 Close 就永久不能再用（再 Show 会抛
    /// InvalidOperationException），所以「关闭」一律改成隐藏，只有退出程序才真关。
    /// <para>
    /// 关闭不放淡出动画：淡的是内容层，露出来的是窗口自己那块实心底色，屏幕上会先留下一个
    /// 「同样大小、里面空空的框」再消失（用户就是这么报的），而且 Hide 挂在动画收尾上，
    /// 收尾一旦没走表它就赖着不走。打开有动画就够了，关的时候用户要的是它马上不见。
    /// </para>
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closingForReal)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>退出时真正关闭窗口。</summary>
    public void CloseFromShell()
    {
        _closingForReal = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PickFolderRequested -= OnPickFolderRequested;
        _viewModel.PickFileRequested -= OnPickFileRequested;
        _viewModel.PickCreateLocationRequested -= OnPickCreateLocationRequested;
        _viewModel.GroupDeleteConfirmRequested -= OnGroupDeleteConfirmRequested;
        _viewModel.RelocateRequested -= OnRelocateRequested;
        _viewModel.BatchDeleteRequested -= OnBatchDeleteRequested;
        _viewModel.EntryRenameRequested -= OnEntryRenameRequested;
        _viewModel.EntryDeleteConfirmRequested -= OnEntryDeleteConfirmRequested;
        RenameOverlay.IsVisibleChanged -= OnRenameOverlayVisibleChanged;
        NewGroupOverlay.IsVisibleChanged -= OnNewGroupOverlayVisibleChanged;
        RenameGroupOverlay.IsVisibleChanged -= OnRenameGroupOverlayVisibleChanged;
        CreateEntryOverlay.IsVisibleChanged -= OnCreateEntryOverlayVisibleChanged;
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (TryBeginHoldPreview(e))
        {
            return;
        }

        // 焦点在输入框里时，Ctrl+A / Delete 属于编辑操作，不能抢去动卡片。
        var typing = Keyboard.FocusedElement is TextBox;

        if (!typing && e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _viewModel.SelectAllCardsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (!typing && e.Key == Key.F2)
        {
            _viewModel.RenameSelectedCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (!typing && e.Key == Key.Delete)
        {
            _viewModel.DeleteSelectionCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.SelectedCount > 0)
        {
            _viewModel.ClearSelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsNewGroupOpen)
        {
            _viewModel.CancelCreateGroup();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsRenameGroupOpen)
        {
            _viewModel.CancelRenameGroup();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsCreateEntryOpen)
        {
            _viewModel.CancelCreateEntry();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsPreviewOpen)
        {
            _viewModel.ClosePreview();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            // 没有浮层要收的时候，Esc 就是「关掉这个窗口」，与启动台同一套约定。
            // 直接 Hide()：窗口是单例、不能真关，而 OnClosing 拦下 X 之后做的也就是同一件事。
            Hide();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// 按住空格 = 把当前这份内容放大来看，松手就收（QuickLook 的那套手感）。
    /// 只在三个条件同时成立时才拦这一下：正在看某个文件的内容、鼠标确实还在这个窗口上、
    /// 焦点不在输入框或按钮上（空格在那两处是打字 / 激活按钮，抢走就成了 bug）。
    /// </summary>
    private bool TryBeginHoldPreview(KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.IsRepeat || _popoutHeld || _viewModel.FilePreview is not { } preview)
        {
            return false;
        }

        if (Keyboard.FocusedElement is TextBox or ButtonBase || !IsMouseOver)
        {
            return false;
        }

        // 已经开着放大窗（比如刚点过 ⤢）时不算「按住呼出」：松手不该把别人手动开的窗关掉。
        if (_filePreview.IsVisible)
        {
            return false;
        }

        _popoutHeld = true;

        // hold: true —— 不抢焦点，并且放大窗那一侧也备一条松手关闭的路（谁拿到那一下抬起不由我们决定）。
        _filePreview.ShowFor(preview, this, hold: true);
        e.Handled = true;
        return true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (e.Key == Key.Space && _popoutHeld)
        {
            EndHoldPreview();
            e.Handled = true;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // 按着空格被人抢了前台时收不到 KeyUp，得自己收尾 —— 但「被放大窗抢走」不算，
        // 那一下正是我们要的，收掉它就会让放大窗一闪就没。
        if (_popoutHeld && !_filePreview.IsActive)
        {
            EndHoldPreview();
        }
    }

    /// <summary>结束「按住空格」：把呼出来的放大窗收掉。</summary>
    private void EndHoldPreview()
    {
        if (!_popoutHeld)
        {
            return;
        }

        _popoutHeld = false;
        _filePreview.Dismiss();
    }

    /// <summary>
    /// 自检用：走一遍「按住开 → 松手关」。指针位置那一条判不了（自检不许挪用户的鼠标），
    /// 真正会坏的是「松手关不掉」：窗会赖在屏幕上。
    /// </summary>
    /// <returns>空串表示通过；否则说明卡在哪一步。</returns>
    public string SelfCheckHoldPreview()
    {
        if (_viewModel.FilePreview is not { } preview)
        {
            return "当前没有正在预览的文件";
        }

        if (_filePreview.IsVisible)
        {
            return "放大窗本来就是开着的，测不到「按住才呼出」";
        }

        _popoutHeld = true;
        _filePreview.ShowFor(preview, this, hold: true);

        if (!_filePreview.IsVisible)
        {
            _popoutHeld = false;
            return "按下去放大窗没出现（ShowFor 的 hold 分支没画出来）";
        }

        // 真的抬起只会落到「当前有焦点的那一侧」，所以两条路各走一次：
        // 放大窗自己收到就它自己收，主窗收到就主窗收，缺一条就会出现「松手关不掉」。
        RaiseSpaceKeyUp(_filePreview);

        if (_filePreview.IsVisible)
        {
            RaiseSpaceKeyUp(this);
        }

        return _filePreview.IsVisible ? "松手之后放大窗还留在屏幕上" : string.Empty;
    }

    private static void RaiseSpaceKeyUp(UIElement target)
    {
        // KeyEventArgs 必须要一个 PresentationSource（它从中拿 InputManager），窗口没挂到桌面上就跳过。
        if (Keyboard.PrimaryDevice is not { } device || PresentationSource.FromVisual(target) is not { } source)
        {
            return;
        }

        target.RaiseEvent(new KeyEventArgs(device, source, Environment.TickCount, Key.Space)
        {
            RoutedEvent = PreviewKeyUpEvent,
        });
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        // 第一下：只看目录内容，不启动资源管理器。
        if (e.ClickCount != 1)
        {
            return;
        }

        if (IsButtonHit(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var card = Resolve<FolderCardViewModel>(e.OriginalSource as DependencyObject);
        if (card is null)
        {
            // 搜索结果里文件卡片和文件夹卡片混在一页：点文件同样是「先看内容」，双击才打开。
            if (Resolve<DesktopFileItemViewModel>(e.OriginalSource as DependencyObject) is { } file)
            {
                RememberGesture(file);
                _viewModel.PreviewFile(file.Path);
                return;
            }

            // 点空白处清掉选中：不然批量状态会悄悄留着，下一次右键就作用在没想到的卡片上。
            _viewModel.ClearSelection();
            return;
        }

        RememberGesture(card);

        _viewModel.HandleCardClick(
            card,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void OnCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 起点新鲜时不按「蹭到按钮」挡掉：那一下是重排后别的卡片上的按钮挪到光标底下的，不是用户想按的。
        if (e.ClickCount != 2 || (IsButtonHit(e.OriginalSource as DependencyObject) && !HasFreshGesture()))
        {
            return;
        }

        var card = ResolveGesture<FolderCardViewModel>(e.OriginalSource as DependencyObject);
        if (card is not null)
        {
            card.Open();
            e.Handled = true;
        }
    }

    private void OnCardPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);

        // 从星星或「＋」上起手不该进入拖拽：那一下是按钮的，按住不动也不该把卡片拖出去。
        _dragCandidate = IsButtonHit(e.OriginalSource as DependencyObject)
            ? null
            : Resolve<FolderCardViewModel>(e.OriginalSource as DependencyObject);
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
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

        var card = _dragCandidate;
        _dragCandidate = null;

        // 一次拖拽同时带两种格式：内部排序认 CardIdFormat，外部程序（微信、浏览器上传区、
        // 资源管理器）只认 CF_HDROP，也就是 DataFormats.FileDrop。
        // 只带自定义格式的话，拖出去对方一律显示「不可放置」。
        var data = new DataObject();
        data.SetData(CardIdFormat, card.Id);
        data.SetData(DataFormats.FileDrop, _viewModel.DragPathsFor(card).ToArray());

        DragDrop.DoDragDrop(this, data, DragDropEffects.Move | DragDropEffects.Copy);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // 内部格式要先判：卡片自己就带着 FileDrop（为了能拖去外部），
        // 反过来判就会把「拖到另一张卡片上排序」误认成「再添加一次同一个文件夹」。
        e.Effects = e.Data.GetDataPresent(CardIdFormat)
            ? DragDropEffects.Move
            : e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;

        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        // 同上：先认内部排序格式。
        if (e.Data.GetDataPresent(CardIdFormat))
        {
            await ReorderFromDragAsync(e);
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            // 全量交给视图模型：它会把「加上了几个、忽略了几个、为什么」聚成一条提示。
            await _viewModel.AddPathsAsync(dropped);

            e.Handled = true;
        }
    }

    private async Task ReorderFromDragAsync(DragEventArgs e)
    {
        if (e.Data.GetData(CardIdFormat) is not string draggedId)
        {
            return;
        }

        var ids = _viewModel.Cards.Select(card => card.Id).ToList();
        var source = ids.IndexOf(draggedId);
        if (source < 0)
        {
            return;
        }

        var insertAt = DropIndexAt(e.GetPosition(CardsHost));
        ids.RemoveAt(source);
        if (insertAt > source)
        {
            insertAt--;
        }

        ids.Insert(Math.Clamp(insertAt, 0, ids.Count), draggedId);

        // 只提交当前可见顺序：其余文件夹由服务层顺延排在后面，不会产生重复 Order。
        await _viewModel.ReorderAsync(ids);
        e.Handled = true;
    }

    /// <summary>预览条目按下的起点与候选（拖出到外部用）。</summary>
    private Point _previewDragStart;
    private PreviewEntry? _previewDragCandidate;

    /// <summary>桌面分类条目按下的起点与候选。</summary>
    private Point _desktopDragStart;
    private DesktopFileItemViewModel? _desktopDragCandidate;

    private void OnPreviewEntryPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _previewDragStart = e.GetPosition(this);
        // 行尾的星星是按钮，按下它不该顺带把这一行变成拖拽候选
        _previewDragCandidate = IsButtonHit(e.OriginalSource as DependencyObject)
            ? null
            : Resolve<PreviewEntry>(e.OriginalSource as DependencyObject);
    }

    private void OnPreviewEntryMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _previewDragCandidate is not { } entry)
        {
            return;
        }

        if (!PastDragThreshold(e.GetPosition(this), _previewDragStart))
        {
            return;
        }

        _previewDragCandidate = null;
        StartFileDrag([entry.Path]);
    }

    private void OnDesktopFilePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _desktopDragStart = e.GetPosition(this);
        _desktopDragCandidate = IsButtonHit(e.OriginalSource as DependencyObject)
            ? null
            : Resolve<DesktopFileItemViewModel>(e.OriginalSource as DependencyObject);
    }

    private void OnDesktopFileMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _desktopDragCandidate is not { } file)
        {
            return;
        }

        if (!PastDragThreshold(e.GetPosition(this), _desktopDragStart))
        {
            return;
        }

        _desktopDragCandidate = null;
        StartFileDrag([file.Path]);
    }

    private static bool PastDragThreshold(Point position, Point start)
        => Math.Abs(position.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(position.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>
    /// 把真实路径交给系统：外部程序（微信、浏览器上传区、资源管理器）只认 CF_HDROP，
    /// 也就是 <see cref="DataFormats.FileDrop"/>，我们自己那套自定义格式它们看不懂。
    /// 效果只给 Copy：拖出去是复制一份引用给对方，DeskMate 自己不移动、不删除任何文件。
    /// </summary>
    private void StartFileDrag(IReadOnlyList<string> paths)
    {
        var real = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .ToArray();

        if (real.Length == 0)
        {
            return;
        }

        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, real);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
    }

    /// <summary>
    /// 卡片区量到了实际宽度就报给视图模型决定列数。挂在 ItemsControl 上而不是窗口上：
    /// 右边内容窗格一开、或者滚动条一出现，可用的宽度就变了，而这两个都改不了窗口的宽度。
    /// </summary>
    private void OnCardAreaSizeChanged(object sender, SizeChangedEventArgs e) => _viewModel.ReportCardAreaWidth(e.NewSize.Width);

    /// <summary>单击桌面分类里的文件：在右侧看内容。双击才是打开。</summary>
    private void OnDesktopFileClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || IsButtonHit(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (Resolve<DesktopFileItemViewModel>(e.OriginalSource as DependencyObject) is { } file)
        {
            RememberGesture(file);
            _viewModel.PreviewFile(file.Path);
            e.Handled = true;
        }
    }

    /// <summary>内容页上点「⤢」：把这一份放大成独立窗口看。</summary>
    private void OnFilePopoutRequested(object? sender, FilePreviewViewModel preview) => _filePreview.ShowFor(preview, this);

    /// <summary>双击桌面分类里的文件：与在桌面上双击等效，交给系统关联程序打开。</summary>
    private void OnDesktopFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonHit(e.OriginalSource as DependencyObject) && !HasFreshGesture())
        {
            return;
        }

        var file = ResolveGesture<DesktopFileItemViewModel>(e.OriginalSource as DependencyObject);
        if (file is not null)
        {
            file.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPreviewEntryClick(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonHit(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (Resolve<PreviewEntry>(e.OriginalSource as DependencyObject) is not { } entry)
        {
            return;
        }

        // 单击目录 = 走进它；单击文件 = 右边内容窗格看它。这一栏从头到尾都只是看，
        // 不会启动任何东西 —— 打开仍然是双击。
        RememberGesture(entry);

        if (entry.IsDirectory)
        {
            _viewModel.EnterFolder(entry);
        }
        else if (entry.HasPath)
        {
            _viewModel.PreviewFile(entry.Path);
        }
    }

    private void OnPreviewEntryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonHit(e.OriginalSource as DependencyObject) && !HasFreshGesture())
        {
            return;
        }

        // 列表与内容窗格现在同时出现，所以双击要认「这一下手势从哪儿起的」：
        // 第一下点开内容窗格会让卡片/行重排，第二下的光标底下可能已经不是刚才那个条目了。
        if (ResolveGesture<PreviewEntry>(e.OriginalSource as DependencyObject) is { } entry)
        {
            _viewModel.OpenPreviewEntry(entry);
            e.Handled = true;
        }
    }

    private void OnRenameOverlayVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (RenameOverlay.IsVisible)
        {
            RenameBox.Focus();
            RenameBox.SelectAll();
        }
    }

    private void OnNewGroupOverlayVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (NewGroupOverlay.IsVisible)
        {
            GroupNameBox.Focus();
            GroupNameBox.SelectAll();
        }
    }

    private void OnRenameGroupOverlayVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (RenameGroupOverlay.IsVisible)
        {
            RenameGroupBox.Focus();
            RenameGroupBox.SelectAll();
        }
    }

    private void OnCreateEntryOverlayVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (CreateEntryOverlay.IsVisible)
        {
            CreateEntryBox.Focus();
            Keyboard.Focus(CreateEntryBox);
        }
    }

    private void OnCreateEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.CommitCreateEntryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CancelCreateEntryCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>收藏卡片：单击先看（目录列内容、文件看它所在目录），与主视图同一套点法。</summary>
    private void OnFavoriteCardClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || IsButtonHit(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var card = Resolve<FavoriteCardViewModel>(e.OriginalSource as DependencyObject);
        if (card is not null)
        {
            RememberGesture(card);
            _viewModel.PreviewFavorite(card);
            e.Handled = true;
        }
    }

    /// <summary>双击收藏卡片才真的打开它。</summary>
    private void OnFavoriteCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (IsButtonHit(e.OriginalSource as DependencyObject) && !HasFreshGesture()))
        {
            return;
        }

        // 认手势起点：第一下点开内容窗格之后卡片会重排，第二下常常已经不在原来那张卡片上了。
        var card = ResolveGesture<FavoriteCardViewModel>(e.OriginalSource as DependencyObject);
        if (card is null)
        {
            LogUnresolvedDoubleClick("收藏页", e);
        }
        if (card is not null)
        {
            card.OpenCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 某一行的「重命名」：弹输入框。名字敲坏了由视图模型挡住，不改任何东西。
    /// </summary>
    private async void OnEntryRenameRequested(object? sender, PreviewEntry entry)
    {
        var name = TextInputWindow.Ask(
            "重命名",
            entry.IsDirectory
                ? $"把文件夹「{entry.Name}」改成（只改名字，不挪位置）："
                : $"把「{entry.Name}」改成（只改名字，不挪位置）：",
            entry.Name);

        if (name is not null)
        {
            await _viewModel.RenameEntryAsync(entry, name);
        }
    }

    /// <summary>
    /// 某一行的「移到回收站」：必须二次确认，而且默认按钮是「否」。
    /// 说清是进回收站（还能还原），不是被抹掉 —— 这是整个面板唯一会动到磁盘的按钮。
    /// </summary>
    private async void OnEntryDeleteConfirmRequested(object? sender, PreviewEntry entry)
    {
        var answer = MessageBox.Show(
            $"把「{entry.Name}」移到回收站？"
            + (entry.IsDirectory ? "\n\n这个文件夹连同它里面的东西会一起进回收站。" : string.Empty)
            + "\n\n文件进的是回收站，不是被抹掉：在回收站里右键「还原」就能回到原处。",
            "移到回收站",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.RecycleEntryAsync(entry);
        }
    }

    /// <summary>
    /// 记下手势的起点。每次单击都记一遍 —— 所以任何一次双击，它的第一下都会把更早的记录盖掉，
    /// 「先点 A、再双击 B」仍然是打开 B。
    /// </summary>
    private void RememberGesture(object? item)
    {
        _gestureItem = item;
        _gestureAtTicks = Environment.TickCount64;
    }

    /// <summary>手势起点还算数吗（在系统那个双击时间窗里）。</summary>
    private bool HasFreshGesture() =>
        _gestureItem is not null
        && Environment.TickCount64 - _gestureAtTicks <= WindowInterop.DoubleClickWindowMs;

    /// <summary>双击该作用在哪个条目上：优先手势起点，其次才是此刻命中的那个。</summary>
    private T? ResolveGesture<T>(DependencyObject? hit)
        where T : class
    {
        if (HasFreshGesture() && _gestureItem is T remembered)
        {
            return remembered;
        }

        return Resolve<T>(hit);
    }

    /// <summary>
    /// 双击没认出任何条目时留一行。这类「用户说点了没反应」的现场，
    /// 光看代码猜不出是事件没到、还是重排把光标底下的东西换掉了，日志里一行就能分清。
    /// </summary>
    private void LogUnresolvedDoubleClick(string area, MouseButtonEventArgs e)
    {
        _logger.LogDebug(
            "{Area}：双击没认出入口的条目（此刻光标底下是 {Hit}，手势起点记的是 {Gesture}）",
            area,
            e.OriginalSource?.GetType().Name ?? "null",
            _gestureItem?.GetType().Name ?? "无");
    }

    /// <summary>
    /// 给自检用：把「第二下的落点」换成卡片区自己（相当于重排之后光标已经不在那张卡片上），
    /// 看双击还认不认得第一下点中的那个条目。不启动任何外部程序，所以可以每次自检都跑。
    /// </summary>
    internal bool DoubleClickKeepsTheGestureItemForSelfCheck() =>
        _gestureItem is not null
        && ReferenceEquals(ResolveGesture<FavoriteCardViewModel>(FavoritesHost), _gestureItem);

    /// <summary>
    /// 命中点是不是落在一个真按钮上。卡片右下角的星星和「＋」用的还是卡片的数据上下文，
    /// 不加这道判断的话，点星星会顺带触发预览 / 选中 / 拖拽，看起来像「一点收藏就弹出预览」。
    /// </summary>
    private static bool IsButtonHit(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }

            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnRenameGroupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.CommitGroupRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CancelRenameGroupCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>批量移除前的确认：一次点掉十几条引用不该是无声的。</summary>
    private async void OnBatchDeleteRequested(object? sender, int count)
    {
        var answer = MessageBox.Show(
            $"确定从 DeskMate 移除选中的 {count} 条引用吗？\n\n" +
            "磁盘上的文件夹一个都不会被删除或移动，只是不再显示在这个面板里；之后可以从「添加文件夹」或「扫描桌面文件夹」再加回来。",
            "移除引用",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.RemoveSelectedAsync();
        }
    }

    /// <summary>失效引用的「重新定位」：弹目录框让用户挑现在的位置。</summary>
    private async void OnRelocateRequested(object? sender, string id)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择这个文件夹现在的位置（只改 DeskMate 里的引用，磁盘上不动任何东西）",
        };

        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.RelocateAsync(id, dialog.FolderName);
        }
    }

    /// <summary>
    /// 删分组前问一句。视图模型算好了会影响几个文件夹，这里只负责把这句话摊到用户面前：
    /// 「全部 / 未分组 / 桌面分类」这些内置视图它压根不会发这个事件。
    /// </summary>
    private async void OnGroupDeleteConfirmRequested(object? sender, GroupDeleteRequest request)
    {
        var answer = MessageBox.Show(
            $"确定删除分组「{request.GroupName}」吗？\n\n" +
            $"组内 {request.FolderCount} 个文件夹会退到「未分组」，引用关系保留，磁盘上一个文件都不动。\n" +
            "删错了可以马上点提示上的「撤销」。",
            "删除分组",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            await _viewModel.DeleteGroupAsync(request.GroupId);
        }
    }

    private void OnGroupNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 不合法时命令本身不会执行（CanExecute 已把「确认」置灰），这里只需要吃掉按键。
            _viewModel.CommitGroupCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ReloadSafelyAsync();

    /// <summary>
    /// 「更多」按钮：点一下把菜单放出来。这三个动作不常用，摊在工具栏上会把搜索框挤成几十像素，
    /// 而搜索框是这一页最该好点的东西。
    /// </summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } } button)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    /// <summary>搜索结果里双击：文件夹卡片开目录，文件卡片走系统关联程序。两类条目混在同一页。</summary>
    private void OnSearchHostDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsButtonHit(e.OriginalSource as DependencyObject) && !HasFreshGesture())
        {
            return;
        }

        // 搜索结果这一页也是「单击看、双击开」，同样要认手势起点（点开窗格会让卡片重排）。
        if (ResolveGesture<DesktopFileItemViewModel>(e.OriginalSource as DependencyObject) is { } file)
        {
            _viewModel.OpenDesktopFile(file);
            e.Handled = true;
            return;
        }

        if (ResolveGesture<FolderCardViewModel>(e.OriginalSource as DependencyObject) is { } card)
        {
            card.Open();
            e.Handled = true;
        }
    }

    private async Task ReloadSafelyAsync()
    {
        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "文件夹面板载入失败");
        }
    }

    private async void OnPickFolderRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要纳入 DeskMate 管理的文件夹（不会移动任何文件）",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // 多选时一次交进去：视图模型会重扫一次并只报一条结果。
        var picked = dialog.FolderNames.Length > 0 ? dialog.FolderNames : [dialog.FolderName];
        await _viewModel.AddPathsAsync(picked);
    }

    /// <summary>收藏页的「＋ 收藏文件或压缩包」：任何文件都能挑，收藏只记路径。</summary>
    private async void OnPickFileRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要收藏的文件或压缩包（DeskMate 只记路径，不复制、不移动原文件）",
            Filter = "所有文件|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await _viewModel.AddFavoritePathsAsync(dialog.FileNames);
    }

    /// <summary>新建浮层上的「换个位置…」：挑完目录回填，浮层保持打开，名字不用重打。</summary>
    private void OnPickCreateLocationRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "新建到哪里（只在这一层里放一个新条目，不动里面已有的东西）",
        };

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SetCreateEntryDirectory(dialog.FolderName);
        }
    }

    /// <summary>WrapPanel 网格里的插入点：找中心最接近光标的卡片，按其左右侧决定插在前还是插在后。</summary>
    private int DropIndexAt(Point position)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        var insertAfter = false;

        for (var index = 0; index < CardsHost.Items.Count; index++)
        {
            if (CardsHost.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            var center = container.TranslatePoint(
                new Point(container.ActualWidth / 2, container.ActualHeight / 2), CardsHost);

            var dx = center.X - position.X;
            var dy = center.Y - position.Y;
            var distance = (dx * dx) + (dy * dy);

            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = index;
            insertAfter = position.X > center.X;
        }

        return best < 0 ? CardsHost.Items.Count : insertAfter ? best + 1 : best;
    }

    /// <summary>沿树上溯，找到第一个以 T 为数据上下文的元素（卡片行、预览条目都用这个）。</summary>
    private static T? Resolve<T>(DependencyObject? source) where T : class
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: T data })
            {
                return data;
            }

            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

}
