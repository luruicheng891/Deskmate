using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMate.App.Services;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.ViewModels;

/// <summary>
/// 文件夹管理面板的视图模型。所有增删改都只作用于 config.json 里的路径引用。
/// </summary>
public sealed partial class FolderManagerViewModel : ObservableObject
{
    private const string AllTabId = "all";
    private const string UngroupedTabId = "none";
    private const string FavoritesTabId = "fav";
    private const int PreviewLimit = 50;
    private const int DebounceMs = 150;

    /// <summary>需求 M3：预设 8 色标签。</summary>
    public static readonly IReadOnlyList<string> ColorPalette =
    [
        "#F59E0B", "#EF4444", "#10B981", "#3B82F6",
        "#8B5CF6", "#EC4899", "#14B8A6", "#64748B",
    ];

    private readonly IFolderService _folders;
    private readonly IAppLauncherService _launcher;
    private readonly IDesktopFileService _desktopFiles;
    private readonly IFileOperationService _files;
    private readonly ILogger<FolderManagerViewModel> _logger;
    private readonly DispatcherTimer _debounce;
    private readonly Dispatcher _dispatcher;
    private readonly List<FolderItem> _all = new();

    private FolderCardViewModel? _contextCard;
    private FolderCardViewModel? _renameTarget;
    private CancellationTokenSource? _countCts;

    // 预览面板的下钻状态：点进子目录之后要能一层层退回去。
    private readonly Stack<string> _previewBack = new();
    private string _previewPath = string.Empty;

    // 预览是从哪张卡片点进来的。下钻到子目录之后，「＋ 新建」建的东西仍然要算回这张卡片的条目数。
    private FolderCardViewModel? _previewCard;

    /// <summary>构造文件夹管理视图模型。</summary>
    public FolderManagerViewModel(
        IFolderService folders,
        IAppLauncherService launcher,
        IDesktopFileService desktopFiles,
        IFileOperationService files,
        ILogger<FolderManagerViewModel> logger)
    {
        _folders = folders;
        _launcher = launcher;
        _desktopFiles = desktopFiles;
        _files = files;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += OnDebounceTick;

        // 默认勾选要在建列表时就算好：字段初值已经是 Options[0]，再赋一次相同引用不会触发变更通知。
        CreateEntryTypes = NewEntryCatalog.Options
            .Select(option => new NewEntryOptionViewModel(this, option)
            {
                IsSelected = ReferenceEquals(option, SelectedCreateEntryType),
            })
            .ToList();

        // 桌面分类是虚拟视图，视图模型活着的时候监听就一直在。
        _desktopFiles.Changed += OnDesktopFilesChanged;
    }

    /// <summary>需要用户选一个本地目录时由窗口响应。</summary>
    public event EventHandler? PickFolderRequested;

    /// <summary>收藏页的「＋ 收藏文件」：让窗口弹一个任意文件的挑选框。</summary>
    public event EventHandler? PickFileRequested;

    /// <summary>删分组前请窗口问一句：会连带影响到几个文件夹，得让用户点头。</summary>
    public event EventHandler<GroupDeleteRequest>? GroupDeleteConfirmRequested;

    /// <summary>需要窗口为某条失效引用弹出「选新位置」的目录框，参数是卡片 Id。</summary>
    public event EventHandler<string>? RelocateRequested;

    /// <summary>提示文本（失败/结果）。</summary>
    public event EventHandler<NoticeEventArgs>? UserNotice;

    /// <summary>某一行的「重命名」：请窗口弹输入框（要当场校验非法名与重名）。</summary>
    public event EventHandler<PreviewEntry>? EntryRenameRequested;

    /// <summary>某一行的「移到回收站」：请窗口先二次确认，那一下是不可逆感最强的操作。</summary>
    public event EventHandler<PreviewEntry>? EntryDeleteConfirmRequested;

    /// <summary>当前展示的卡片。</summary>
    public ObservableCollection<FolderCardViewModel> Cards { get; } = new();

    /// <summary>顶部 Tab：全部 / 未分组 / 各分组。</summary>
    public ObservableCollection<GroupTabViewModel> Tabs { get; } = new();

    /// <summary>右键「换分组」子菜单。</summary>
    public ObservableCollection<GroupTab> GroupMenu { get; } = new();

    /// <summary>预览面板条目。</summary>
    public ObservableCollection<PreviewEntry> PreviewItems { get; } = new();

    /// <summary>当前桌面分类里的文件。</summary>
    public ObservableCollection<DesktopFileItemViewModel> DesktopFiles { get; } = new();

    /// <summary>「★ 收藏」页里的条目：目录、文件、压缩包都算一条。</summary>
    public ObservableCollection<FavoriteCardViewModel> FavoriteCards { get; } = new();

    /// <summary>
    /// 搜索结果：文件夹卡片与桌面文件卡片混在同一个网格里，按类型自动选模板。
    /// 混在一页是因为用户搜的时候想的是「这东西在哪」，而不是「它属于哪个视图」。
    /// </summary>
    public ObservableCollection<object> SearchHits { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchView))]
    [NotifyPropertyChangedFor(nameof(IsFolderView))]
    [NotifyPropertyChangedFor(nameof(IsDesktopView))]
    [NotifyPropertyChangedFor(nameof(ViewHint))]
    private string _keyword = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDesktopView))]
    [NotifyPropertyChangedFor(nameof(IsFolderView))]
    [NotifyPropertyChangedFor(nameof(IsFavoritesView))]
    [NotifyPropertyChangedFor(nameof(IsSearchView))]
    [NotifyPropertyChangedFor(nameof(ViewHint))]
    private string _selectedTabId = AllTabId;

    /// <summary>
    /// 搜索状态：只要输入了词（收藏页除外，那一页是就地筛自己那几条），
    /// 结果就跨「受管文件夹 + 桌面文件」两类，而不是只在当前 Tab 里筛。
    /// </summary>
    public bool IsSearchView => Keyword.Trim().Length > 0 && !IsFavoritesView;

    /// <summary>当前选中的是桌面虚拟分类（图片 / 压缩包 / 文档 / 其他）。</summary>
    public bool IsDesktopView => !IsSearchView && DesktopFileRules.IsCategoryTab(SelectedTabId);

    /// <summary>当前是收藏视图。</summary>
    public bool IsFavoritesView => SelectedTabId == FavoritesTabId;

    /// <summary>当前是文件夹引用视图（全部 / 未分组 / 用户分组）。</summary>
    public bool IsFolderView => !IsSearchView && !IsDesktopView && !IsFavoritesView;

    /// <summary>
    /// 主区正在浏览某个文件夹的内部（像资源管理器那样直接走进去），而不是停在卡片网格。
    /// 右边那一栏此时是内容窗格，两边同时可见 —— 用户要的正是「列表和预览一起出现」。
    /// </summary>
    public bool IsBrowsing => IsPreviewOpen;

    /// <summary>正在浏览的这一层（空串 = 没在浏览）。</summary>
    public string BrowsePath => _previewPath;

    /// <summary>面包屑那行的层名。</summary>
    public string BrowseTitle => _previewPath.Length == 0 ? string.Empty : LeafName(_previewPath);

    /// <summary>返回按钮上写的那个字：上一层的名字，没有上一层就是「回文件夹列表」。</summary>
    public string BackTitle => _previewBack.Count > 0 ? LeafName(_previewBack.Peek()) : "文件夹列表";

    /// <summary>
    /// 面包屑：从盘符到当前层的每一段。点哪段就直接跳到哪一层 ——
    /// 深四五层时「按 ← 数着退回去」不是人干的事。
    /// </summary>
    public IReadOnlyList<BreadcrumbItem> Breadcrumbs => BuildBreadcrumbs(_previewPath);

    /// <summary>底部操作提示，跟着当前视图换。</summary>
    public string ViewHint => IsBrowsing
        ? "单击目录 = 走进它 · 单击文件 = 右边看内容（列表留着，两边同时看） · 双击 = 交给系统打开 · ← 先收内容窗格，再退一层 · 按住可拖到微信 / 浏览器上传区"
        : IsSearchView
            ? "搜索结果跨受管文件夹与桌面文件 · 单击看内容 · 双击打开 · 没找到要的文件的话，Alt+Space 的全局搜索还能搜进受管目录里面"
        : IsFavoritesView
            ? "单击卡片先看内容（目录列它里面的东西，文件直接看它的内容） · 双击才打开 · 点实心 ★ 取消收藏 · 右键可打开所在位置"
            : IsDesktopView
                ? "单击文件即在右边看内容 · 双击即用系统关联程序打开，与在桌面上双击一致 · 按住可拖到微信 / 浏览器上传区 · 右下角 ☆ 收藏这个文件"
                : "单击卡片即走进它（左边列它内部、右边可看内容） · Ctrl 加选 / Shift 连选 · 双击用资源管理器打开 · 按住可拖到外部程序 · F2 改名 · Delete 移除 · 右下角 ★ 收藏、＋ 新建";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupNameError))]
    [NotifyCanExecuteChangedFor(nameof(CommitGroupCommand))]
    private string _newGroupName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewGroupVisible))]
    private bool _isNewGroupOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameGroupError))]
    [NotifyCanExecuteChangedFor(nameof(CommitGroupRenameCommand))]
    private string _renameGroupText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameGroupVisible))]
    private bool _isRenameGroupOpen;

    /// <summary>正在改名的分组 Id，空串表示浮层没在改任何东西。</summary>
    private string _renameGroupTargetId = string.Empty;

    /// <summary>刚删掉的分组名与组内文件夹 Id，供「撤销」用（只保一次，且只在本次会话内）。</summary>
    private (string Name, List<string> FolderIds)? _deletedGroup;

    /// <summary>重命名分组浮层是否可见。</summary>
    public bool IsRenameGroupVisible => IsRenameGroupOpen;

    /// <summary>
    /// 改名时的实时提示。比对时把分组自己排除掉，否则「名字没改，直接点保存」会被自己的旧名判成重名。
    /// </summary>
    public string RenameGroupError
    {
        get
        {
            var name = GroupNameRules.Normalize(RenameGroupText);
            return name.Length == 0
                ? string.Empty
                : GroupNameRules.Validate(name, TakenGroupNames(excluding: _renameGroupTargetId)) ?? string.Empty;
        }
    }

    /// <summary>新建分组浮层是否可见。</summary>
    public bool IsNewGroupVisible => IsNewGroupOpen;

    /// <summary>
    /// 分组名的实时提示。一个字都没输入时故意不提示「不能为空」——
    /// 浮层刚弹出来就报错太吵，那种情况交给置灰的「确认」按钮表达。
    /// </summary>
    public string GroupNameError => GroupNameRules.Normalize(NewGroupName).Length == 0
        ? string.Empty
        : GroupNameRules.Validate(GroupNameRules.Normalize(NewGroupName), TakenGroupNames()) ?? string.Empty;

    [ObservableProperty]
    private string _previewTitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowsing))]
    [NotifyPropertyChangedFor(nameof(ViewHint))]
    private bool _isPreviewOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameVisible))]
    private bool _isRenameOpen;

    /// <summary>
    /// 当前正在看内容的文件；null 表示右边那块内容窗格没开。
    /// 它开不开与左边那一页无关：左边可以是卡片网格、收藏列表、分类页，也可以是某个文件夹的列表。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilePreviewOpen))]
    private FilePreviewViewModel? _filePreview;

    /// <summary>右边正在显示文件内容。</summary>
    public bool IsFilePreviewOpen => FilePreview is not null;

    /// <summary>要把内容放大成独立窗口时由窗口响应。</summary>
    public event EventHandler<FilePreviewViewModel>? FilePopoutRequested;

    /// <summary>
    /// 卡片宽度：由窗口报进来的「卡片区实测宽度」算出能放几列，再把宽度平分。
    /// 以前三种卡片各写各的 212 / 256 DIP，结果右边内容窗格一开（520 DIP），
    /// 左边刚好只塞得下一列 —— 大片空白留在中间，用户读成「地方明明够，为什么不排两排」。
    /// </summary>
    public double CardItemWidth => _cardItemWidth;

    /// <summary>卡片区至少能放一列多宽，才考虑挤成两列。</summary>
    public const double CardMinWidth = 196;

    /// <summary>卡片之间的水平缝（模板里那个 Margin 的右半边）。</summary>
    private const double CardGap = 12;

    private double _cardItemWidth = 256;

    /// <summary>
    /// 窗口报来卡片区的实际可用宽度（已经扣掉滚动条），这里决定列数。
    /// </summary>
    public void ReportCardAreaWidth(double width)
    {
        if (width < 60)
        {
            return; // 还没布局，或者整块被收起来了：别把卡片宽度算成 0 把界面压扁
        }

        var columns = Math.Max(1, (int)((width + CardGap) / (CardMinWidth + CardGap)));
        var item = Math.Floor(width / columns) - CardGap;

        if (Math.Abs(item - _cardItemWidth) < 1)
        {
            return;
        }

        _cardItemWidth = item;
        OnPropertyChanged(nameof(CardItemWidth));
    }

    /// <summary>重命名浮层是否可见。</summary>
    public bool IsRenameVisible => IsRenameOpen;

    /// <summary>载入卡片、分组与校验结果。</summary>
    public async Task LoadAsync()
    {
        await _folders.ValidateAllAsync();
        // 面板可能几小时才打开一次，桌面分类要按现场重扫，不能只靠监听。
        _desktopFiles.Refresh();
        RebuildSource();
        RebuildTabs();
        ApplyFilter();
        await LoadCountsAsync();
    }

    partial void OnKeywordChanged(string value)
    {
        // 开始搜索就离开原来那一页：搜索结果和卡片网格是同一块区域，
        // 人还留在某个文件夹里的话，搜出来的东西会被列表挡住，看着像搜索没反应。
        // 内容窗格同理 —— 预览是「随用随走」的，语境换了这一眼就算看完了，不该等用户去点 ×。
        if (value.Trim().Length > 0)
        {
            ClosePreview();
        }

        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        ApplyFilter();
    }

    /// <summary>清空搜索词。</summary>
    [RelayCommand]
    public void ClearKeyword() => Keyword = string.Empty;

    /// <summary>选择 Tab。</summary>
    [RelayCommand]
    public void SelectTab(GroupTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        // 点一下**已经选中**的「全部」也要能回主界面：人钻进子文件夹之后，第一反应就是再按一次「全部」，
        // 而 SelectedTabId 没变 → 属性变更通知根本不会响，那一下就成了空操作。
        ClosePreview();

        SelectedTabId = tab.Id;
    }

    partial void OnSelectedTabIdChanged(string value)
    {
        // 换 Tab 就是换语境：浏览要退出，内容窗格也要收。
        // 原来只在「正在浏览」时才收，结果从「图片」切到「压缩包」时，右边还挂着上一张图 ——
        // 用户要的口径是预览随用随走，离开原位就自己关掉，不用手动点 ×。
        ClosePreview();

        SyncTabSelection();
        ApplyFilter();
    }

    /// <summary>点「添加」：让窗口弹出文件夹选择框。</summary>
    [RelayCommand]
    public void RequestPickFolder() => PickFolderRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>把一个目录加入管理（只写引用）。</summary>
    public Task AddPathAsync(string path) => AddPathsAsync([path]);

    /// <summary>
    /// 批量加入，并且只报一条结果：逐个弹提示的话，拖十个文件夹进来就是屏幕上一列重复的字。
    /// 某一项失败（路径不存在、没权限）不影响其余项，但要把少了几项说清楚。
    /// </summary>
    public async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var added = new List<string>();
        string? firstError = null;

        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                // 拖进来的可能是文件、失效路径或暂时打不开的网盘目录：
                // 这里必须数进「没加上」并报得出原因，不能悄悄丢掉让用户反复拖。
                firstError ??= $"{LeafName(path)}：不是文件夹，或者已经不存在了";
                continue;
            }

            try
            {
                var item = await _folders.AddAsync(path);
                added.Add(item.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "添加文件夹失败 {Path}", path);
                firstError ??= ex.Message;
            }
        }

        RebuildSource();
        RebuildTabs();
        ApplyFilter();
        await LoadCountsAsync();

        var skipped = paths.Count - added.Count;
        var summary = added.Count switch
        {
            0 => $"一个都没加上：{firstError ?? "路径不可用"}",
            1 => $"已加入管理：{added[0]}（只引用路径，磁盘上没有改动）",
            <= 3 => $"已加入管理：{string.Join("、", added)}",
            _ => $"已加入管理 {added.Count} 个文件夹",
        };
        var kind = added.Count == 0
            ? ToastKind.Error
            : skipped > 0 ? ToastKind.Warning : ToastKind.Success;

        if (skipped > 0 && added.Count > 0)
        {
            summary += $"，另有 {skipped} 项没加上";
        }

        OnNotice(summary, kind);
    }

    /// <summary>右键：请求为这条失效引用挑一个新位置（目录框由窗口弹）。</summary>
    public void BeginRelocate(FolderCardViewModel card) => RelocateRequested?.Invoke(this, card.Id);

    /// <summary>窗口选好新路径后回来落盘：只改配置里的引用，磁盘上什么都不动。</summary>
    public async Task RelocateAsync(string id, string newPath)
    {
        try
        {
            var item = await _folders.RelocateAsync(id, newPath);
            RebuildSource();
            RebuildTabs();
            ApplyFilter();

            var card = Cards.FirstOrDefault(candidate => candidate.Id == item.Id);
            if (card is not null)
            {
                _ = LoadCountForAsync(card);
            }

            OnNotice($"已重新定位到：{item.RealPath}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重新定位文件夹失败 {Id}", id);
            OnNotice($"重新定位失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把桌面上还没管理的文件夹补进来。首次启动那次没点「加入管理」，后来想加就有这条，
    /// 不用再回去改 config.json。
    /// </summary>
    [RelayCommand]
    public async Task ScanDesktopAsync()
    {
        var found = await _folders.ScanDesktopFoldersAsync();
        var known = new HashSet<string>(_all.Select(item => item.RealPath), StringComparer.OrdinalIgnoreCase);
        var fresh = found.Where(path => !known.Contains(path)).ToList();

        if (fresh.Count == 0)
        {
            OnNotice(
                found.Count == 0
                    ? "桌面上没有待加入的文件夹（这里只数文件夹，散落的文件看「图片 / 文档」那些标签）。"
                    : $"桌面上的 {found.Count} 个文件夹都已经在管理里了。",
                ToastKind.Info);
            return;
        }

        await AddPathsAsync(fresh);
    }

    /// <summary>双击卡片：在资源管理器中打开该目录。</summary>
    public void OpenCard(FolderCardViewModel card)
    {
        try
        {
            _launcher.OpenInExplorer(card.RealPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开文件夹失败 {Path}", card.RealPath);
            OnNotice($"打不开「{card.DisplayName}」：{ex.Message}");
        }
    }

    /// <summary>单击卡片：在右侧面板里预览这个目录的内容。</summary>
    public void PreviewCard(FolderCardViewModel card)
    {
        _previewBack.Clear();
        _previewCard = card;
        ShowPreview(card.RealPath);
    }

    /// <summary>点目录条目：直接在面板里下钻进去，不再开一个资源管理器窗口。</summary>
    public void EnterFolder(PreviewEntry entry)
    {
        if (!entry.IsDirectory || entry.Path.Length == 0
            || string.Equals(entry.Path, _previewPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _previewBack.Push(_previewPath);
        ShowPreview(entry.Path);
    }

    /// <summary>列表里双击某一项：目录 = 在资源管理器里打开它（单击已经会走进来了），文件 = 用默认程序打开。</summary>
    public void OpenPreviewEntry(PreviewEntry entry) => OpenExternally(entry.Path, entry.Name);

    /// <summary>双击桌面分类里的文件：交给系统关联程序打开，桌面上的文件本身一个都不动。</summary>
    public void OpenDesktopFile(DesktopFileItemViewModel item) => OpenExternally(item.Path, item.Name);

    /// <summary>
    /// 「真的打开」这一件事的唯一出口。
    /// <para>
    /// 系统配了默认程序就交给它；没配（这台机器上 <c>assoc .md</c> 就查无此人）不是失败，
    /// 而是得给用户一条走得通的路 —— DeskMate 自己能看的就直接走内置查看器放大打开，
    /// 两边都不行才把原因和下一步说清楚。
    /// </para>
    /// </summary>
    private void OpenExternally(string path, string displayName)
    {
        if (path.Length == 0)
        {
            return;
        }

        // 留一行现场：用户报「双击没反应」时，这行能一眼分清是「手势没走到这儿」还是「走到了但没关联」。
        var hasHandler = _launcher.HasDefaultProgram(path);
        _logger.LogInformation("打开请求：{Path}（系统关联程序：{Handler}）", path, hasHandler ? "有" : "无");

        if (!hasHandler)
        {
            if (FilePreviewRules.CanPreview(path))
            {
                PreviewFile(path);

                if (FilePreview is { } preview)
                {
                    // 双击要的是「打开来看」，窄栏那块临时预览不够，直接放大成独立窗口。
                    FilePopoutRequested?.Invoke(this, preview);
                }

                OnNotice($"这台机器上没给「{Path.GetExtension(path)}」配默认程序，已改用 DeskMate 内置查看器打开。", ToastKind.Info);
            }
            else
            {
                OnNotice($"这台机器上没有能打开「{displayName}」的默认程序。右键可以「在资源管理器中显示」，或先在 Windows 设置里给它选一个关联程序。", ToastKind.Info);
            }

            return;
        }

        try
        {
            // 单击负责在面板里走（进目录 / 看内容），双击负责跳出去（交给系统）。
            _launcher.Launch(path);

            // 已经交给源程序了，那块「临时看一眼」就没有存在的理由 —— 别让它继续盖着这个文件。
            DropContent();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开条目失败 {Path}", path);
            OnNotice($"打不开「{displayName}」：{ex.Message}");
        }
    }

    /// <summary>
    /// 请窗口为这一行弹改名输入框。改名要真动磁盘上的文件，所以输入框与校验都交给窗口，
    /// 视图模型只接最终结果（<see cref="RenameEntryAsync"/>）。
    /// </summary>
    public void RequestEntryRename(PreviewEntry entry)
    {
        if (entry.HasPath)
        {
            EntryRenameRequested?.Invoke(this, entry);
        }
    }

    /// <summary>某一行的「在资源管理器中显示」。</summary>
    public void RevealPreviewEntry(PreviewEntry entry)
    {
        if (!entry.HasPath)
        {
            return;
        }

        try
        {
            _launcher.OpenInExplorer(entry.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "定位预览条目失败 {Path}", entry.Path);
            OnNotice($"跳不到它的位置：{ex.Message}");
        }
    }

    /// <summary>请窗口二次确认「把这一行移到回收站」。真正的删除只在用户点头之后发生（<see cref="RecycleEntryAsync"/>）。</summary>
    public void RequestEntryRecycle(PreviewEntry entry)
    {
        if (entry.HasPath)
        {
            EntryDeleteConfirmRequested?.Invoke(this, entry);
        }
    }

    /// <summary>
    /// 提交改名：只改名字、绝不挪位置；重名或非法字符一律拒绝并说明原因。
    /// 改完之后收藏里那条旧路径要跟着换，右边正看着它的话窗格要收掉，列表重数一遍。
    /// </summary>
    public async Task RenameEntryAsync(PreviewEntry entry, string newName)
    {
        string target;
        try
        {
            target = _files.Rename(entry.Path, newName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "改名失败 {Path}", entry.Path);
            OnNotice($"没改成：{ex.Message}");
            return;
        }

        if (SamePath(target, entry.Path))
        {
            return; // 名字一个字符都没变，什么都不做
        }

        await _folders.ReplaceFavoriteAsync(entry.Path, target);

        if (FilePreview is not null && SamePath(FilePreview.FullPath, entry.Path))
        {
            DropContent();
        }

        RebuildBrowse();
        OnNotice($"已改名为「{Path.GetFileName(target)}」", ToastKind.Success);
    }

    /// <summary>
    /// 用户点头之后真的移到回收站（走 shell，所以从回收站还能捞回来）。
    /// 被取消就什么都不做；成功后把指向它的收藏一并撤掉，免得留下一条点开就报错的引用。
    /// </summary>
    public async Task RecycleEntryAsync(PreviewEntry entry)
    {
        bool removed;
        try
        {
            removed = _files.Recycle(entry.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "移到回收站失败 {Path}", entry.Path);
            OnNotice($"没删成：{ex.Message}");
            return;
        }

        if (!removed)
        {
            return;
        }

        if (_folders.IsFavorite(entry.Path))
        {
            await _folders.RemoveFavoriteAsync(entry.Path);
        }

        if (FilePreview is not null && SamePath(FilePreview.FullPath, entry.Path))
        {
            DropContent();
        }

        RebuildBrowse();
        OnNotice($"已把「{entry.Name}」移到回收站，想恢复在回收站里右键「还原」", ToastKind.Success);
    }

    /// <summary>把当前这一层重新数一遍（改名/删除之后列表要立刻对上磁盘上的样子）。</summary>
    private void RebuildBrowse()
    {
        if (!IsPreviewOpen || _previewPath.Length == 0)
        {
            return;
        }

        // 先清掉：ShowPreview 有一条「同路径就不重举」的快速路径，那是给返回用的，
        // 而这里要的恰恰是重举。
        PreviewItems.Clear();
        ShowPreview(_previewPath);
    }

    /// <summary>右键：到资源管理器里选中该文件，只跳转不改动。</summary>
    public void RevealDesktopFile(DesktopFileItemViewModel item)
    {
        try
        {
            _launcher.OpenInExplorer(item.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "定位桌面文件失败 {Path}", item.Path);
            OnNotice($"定位不到「{item.Name}」：{ex.Message}");
        }
    }

    private void OnDesktopFilesChanged(object? sender, EventArgs e)
    {
        // FileSystemWatcher 的回调跑在线程池线程上，而集合只允许 UI 线程改。
        _dispatcher.BeginInvoke(RefreshView);
    }

    /// <summary>
    /// 按最新的桌面快照与搜索词重算当前视图。
    /// 监听回调转回 UI 线程后走这里；单测里没有消息循环，也用这里直接驱动防抖之后的那一步。
    /// </summary>
    public void RefreshView()
    {
        RebuildTabs();
        ApplyFilter();
    }

    /// <summary>
    /// 返回。顺序是「先收内容窗格 → 再一层层退出下钻过的目录 → 最后退出浏览回到卡片网格」，
    /// 这样每一下都有明确去处，不会出现「按一次 ← 就从某个文件跳到了上一层的上一层」。
    /// </summary>
    [RelayCommand]
    public void GoBackPreview()
    {
        if (FilePreview is not null)
        {
            PreviewFolderOf(FilePreview);
            return;
        }

        if (_previewBack.Count > 0)
        {
            ShowPreview(_previewBack.Pop());
            return;
        }

        ClosePreview();
    }

    /// <summary>退出浏览：关掉内容窗格与目录列表，主区回到卡片网格。</summary>
    [RelayCommand]
    public void ClosePreview()
    {
        FilePreview?.ReleasePdf();
        FilePreview = null;
        _previewBack.Clear();
        _previewPath = string.Empty;
        PreviewItems.Clear();
        IsPreviewOpen = false;
        RaiseBrowseChanged();
    }

    /// <summary>
    /// 只收起右边的内容窗格，左边的列表/卡片一概不动 —— 「这一眼算看完了」。
    /// <para>
    /// 故意不 <c>ReleasePdf()</c>：同一份预览可能正开在 ⤢ 放大窗里，那边还要靠这个会话翻页；
    /// 面板这边不再引用它之后，交给回收就行。整个语境换掉（<see cref="ClosePreview"/>）才真的释放。
    /// </para>
    /// </summary>
    private void DropContent()
    {
        if (FilePreview is not null)
        {
            FilePreview = null;
        }
    }

    private void ShowPreview(string path)
    {
        // 从内容页退回「正在看的这一层」时不重新枚举：列表还挂在 ItemsSource 上，
        // 重新 Clear + Add 会把滚动条甩回顶部，用户读成「返回把我看的位置弄丢了」。
        if (FilePreview is not null && PreviewItems.Count > 0 && SamePath(_previewPath, path))
        {
            FilePreview.ReleasePdf();
            FilePreview = null;
            PreviewTitle = $"{LeafName(path)}　·　{path}";
            IsPreviewOpen = true;
            RaiseBrowseChanged();
            return;
        }

        PreviewItems.Clear();
        FilePreview = null;
        _previewPath = path;
        PreviewTitle = $"{LeafName(path)}　·　{path}";
        RaiseBrowseChanged();

        try
        {
            var entries = EnumeratePreview(path).ToList();
            var truncated = entries.Count > PreviewLimit;

            foreach (var entry in entries.Take(PreviewLimit))
            {
                PreviewItems.Add(entry);
            }

            // 截断必须说出来：只画 50 条而不告诉后面还有，用户会认为这个目录就只有 50 项。
            if (truncated)
            {
                PreviewItems.Add(new PreviewEntry(this, "仅显示前 50 项 · 双击卡片可在资源管理器里看全部", string.Empty, false));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "预览目录失败 {Path}", path);
            OnNotice($"读不到该目录：{ex.Message}");
        }

        if (PreviewItems.Count == 0)
        {
            PreviewItems.Add(new PreviewEntry(this, "（空目录或无读取权限）", string.Empty, false));
        }

        IsPreviewOpen = true;
        RaiseBrowseChanged();
    }

    /// <summary>面包屑那几项都是从 <see cref="_previewPath"/> 与栈里现算的，改了要一起通知。</summary>
    private void RaiseBrowseChanged()
    {
        OnPropertyChanged(nameof(BrowsePath));
        OnPropertyChanged(nameof(BrowseTitle));
        OnPropertyChanged(nameof(BackTitle));
        OnPropertyChanged(nameof(Breadcrumbs));
    }

    /// <summary>把一个路径拆成「盘符 → … → 当前层」的面包屑段。</summary>
    private static IReadOnlyList<BreadcrumbItem> BuildBreadcrumbs(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var chain = new List<(string Display, string Path)>();

        try
        {
            var current = new DirectoryInfo(path.TrimEnd('\\', '/'));

            while (current is not null)
            {
                chain.Add((current.Name, current.FullName));
                current = current.Parent;
            }
        }
        catch (Exception)
        {
            // 路径怪（非法字符、被移走的网络盘）：宁可不给面包屑，也别让主区崩掉。
            return [];
        }

        chain.Reverse();

        // 最后一段就是当前层，它后面不该再拖一个「›」。
        return [.. chain.Select((crumb, index) => new BreadcrumbItem(crumb.Display, crumb.Path, index == chain.Count - 1))];
    }

    /// <summary>点面包屑的某一段 = 直接跳到那一层。</summary>
    [RelayCommand]
    public void BrowseTo(BreadcrumbItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (IsPreviewOpen && SamePath(_previewPath, item.Path))
        {
            return;
        }

        if (!Directory.Exists(item.Path))
        {
            OnNotice($"这一层已经打不开了：{item.Path}", ToastKind.Info);
            return;
        }

        // 栈按「目标层的祖先链」重建，而不是把刚才那层压进去：跳完之后 ← 才是继续往上退，
        // 而不是被弹回刚才深钻的那个角落里。
        _previewBack.Clear();

        foreach (var crumb in BuildBreadcrumbs(item.Path).SkipLast(1))
        {
            _previewBack.Push(crumb.Path);
        }

        ShowPreview(item.Path);
    }

    private static string LeafName(string path)
    {
        var name = new DirectoryInfo(path).Name;
        return name.Length > 0 ? name : path;
    }

    // ==================== 文件内容预览 ====================

    /// <summary>
    /// 看一个文件的内容：只在右边开内容窗格，**不动左边那一页**。
    /// 左边摆什么取决于用户原来在看什么 —— 从收藏 / 桌面分类 / 搜索结果点进来时，
    /// 左边就该继续是那一页（把主区换成文件所在的桌面，等于把人正在看的收藏列表顶掉了）；
    /// 只有本来就在浏览某个文件夹时，那块列表才是它的家。
    /// </summary>
    public void PreviewFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            OnNotice("这个文件已经打不开了（可能被移走或删掉）。", ToastKind.Info);
            return;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;

        // 本来就在浏览、而且文件不在这层：换层并压栈（← 才退得回来）。
        // 同一层里翻文件不压栈，胶囊条与 ◀ ▶ 都不该多出一个「返回」台阶。
        if (IsPreviewOpen && !SamePath(_previewPath, directory))
        {
            _previewBack.Push(_previewPath);
            ShowPreview(directory);
        }

        var preview = new FilePreviewViewModel(this, _logger, path);

        FilePreview = preview;
        PreviewTitle = $"{Path.GetFileName(path)}　·　{directory}";
        RaiseBrowseChanged();
        MarkSelected(path);

        preview.SetSiblings(SiblingFilesOf(directory));
        preview.Load();
    }

    /// <summary>
    /// 把列表里正在看内容的那一行标成选中。列表和内容现在同时出现，
    /// 不标的话右边那块内容对不上「它是列表里的哪一个」。
    /// </summary>
    private void MarkSelected(string path)
    {
        foreach (var entry in PreviewItems)
        {
            entry.IsSelected = entry.HasPath && SamePath(entry.Path, path);
        }
    }

    /// <summary>收起右边的内容窗格（主区的列表不动）。</summary>
    [RelayCommand]
    public void CloseContent()
    {
        if (FilePreview is { } preview)
        {
            PreviewFolderOf(preview);
        }
    }

    /// <summary>面包屑那行的「在资源管理器里打开这一层」：想跳出到系统里时用这一下。</summary>
    [RelayCommand]
    public void OpenBrowseInExplorer()
    {
        if (_previewPath.Length == 0)
        {
            return;
        }

        try
        {
            _launcher.OpenInExplorer(_previewPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "在资源管理器里打开失败 {Path}", _previewPath);
            OnNotice($"打不开资源管理器：{ex.Message}");
        }
    }

    /// <summary>
    /// 收起内容窗格。左边如果是某个文件夹的列表就留着（← 的第一下），
    /// 如果本来就是收藏 / 分类 / 搜索结果那一页，那更不能动 —— 收起窗格不该顺手换页。
    /// </summary>
    public void PreviewFolderOf(FilePreviewViewModel preview)
    {
        preview.ReleasePdf();
        FilePreview = null;

        if (!IsPreviewOpen)
        {
            return;
        }

        PreviewTitle = $"{LeafName(_previewPath)}　·　{_previewPath}";
        RaiseBrowseChanged();
    }

    /// <summary>内容页里的「打开」：交给系统关联程序，与在资源管理器里双击一致。</summary>
    public void OpenPreviewedFile(FilePreviewViewModel preview) => OpenExternally(preview.FullPath, preview.Name);

    /// <summary>内容页里的「在资源管理器中显示」。</summary>
    public void RevealPreviewedFile(FilePreviewViewModel preview)
    {
        try
        {
            _launcher.OpenInExplorer(preview.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "定位预览中的文件失败 {Path}", preview.FullPath);
            OnNotice($"跳不到它的位置：{ex.Message}");
        }
    }

    /// <summary>把内容放大成独立窗口（窄栏里看不下的长文与 PDF 用它）。</summary>
    public void RequestFilePopout(FilePreviewViewModel preview) => FilePopoutRequested?.Invoke(this, preview);

    /// <summary>
    /// 同层里可以翻的文件。跳过隐藏/系统项与 desktop.ini 这类系统自己维护的文件 ——
    /// 翻到它们上面对谁都没用。
    /// </summary>
    private static IReadOnlyList<string> SiblingFilesOf(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(path => !DesktopFileRules.ShouldIgnore(
                    Path.GetFileName(path),
                    new FileInfo(path).Attributes))
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // 没权限、或者目录正好被人删了：翻不了就不给按钮，别把异常甩到界面上。
            return [];
        }
    }

    /// <summary>F2：改选中那一张卡片的显示名。</summary>
    [RelayCommand]
    public void RenameSelected()
    {
        var card = Cards.FirstOrDefault(entry => _selection.Contains(entry.Id));
        if (card is null)
        {
            OnNotice("先选一张卡片，再按 F2。", ToastKind.Info);
            return;
        }

        if (_selection.Count > 1)
        {
            OnNotice("改名一次只能对一张卡片，先单击选中它。", ToastKind.Info);
            return;
        }

        BeginRename(card);
    }

    /// <summary>右键：开始重命名显示名。</summary>
    public void BeginRename(FolderCardViewModel card)
    {
        SetContextCard(card);
        _renameTarget = card;
        RenameText = card.DisplayName;
        IsRenameOpen = true;
    }

    /// <summary>提交重命名（只改显示名）。</summary>
    [RelayCommand]
    public async Task CommitRenameAsync()
    {
        if (_renameTarget is null)
        {
            return;
        }

        try
        {
            await _folders.RenameDisplayAsync(_renameTarget.Id, RenameText);
            _renameTarget.DisplayName = string.IsNullOrWhiteSpace(RenameText)
                ? new DirectoryInfo(_renameTarget.RealPath).Name
                : RenameText.Trim();
            OnNotice($"显示名已改成「{_renameTarget.DisplayName}」，磁盘上的文件夹名不变", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重命名失败");
            OnNotice($"重命名失败：{ex.Message}");
        }
        finally
        {
            IsRenameOpen = false;
            _renameTarget = null;
        }
    }

    /// <summary>取消重命名。</summary>
    [RelayCommand]
    public void CancelRename()
    {
        IsRenameOpen = false;
        _renameTarget = null;
    }

    /// <summary>
    /// 右键：移除引用（磁盘目录不动）。选中多张时先问一句 ——
    /// 一次点掉十几条引用不该是无声无息发生的。
    /// </summary>
    [RelayCommand]
    public async Task RemoveContextCardAsync()
    {
        var targets = CurrentTargets();
        if (targets.Count == 0)
        {
            return;
        }

        if (targets.Count > 1)
        {
            BatchDeleteRequested?.Invoke(this, targets.Count);
            return;
        }

        await RemoveAsync(targets);
    }

    /// <summary>确认后（或键盘 Delete 之后）移除当前选中的那些引用。</summary>
    public Task RemoveSelectedAsync() => RemoveAsync(CurrentTargets());

    /// <summary>键盘 Delete：删掉选中的引用；只选一张就直接删，多张先问。</summary>
    [RelayCommand]
    public Task DeleteSelectionAsync()
    {
        var targets = CurrentTargets();

        switch (targets.Count)
        {
            case 0:
                OnNotice("先选一张卡片（单击即可，Ctrl 加选、Shift 连选）。", ToastKind.Info);
                return Task.CompletedTask;
            case 1:
                return RemoveAsync(targets);
            default:
                BatchDeleteRequested?.Invoke(this, targets.Count);
                return Task.CompletedTask;
        }
    }

    private async Task RemoveAsync(IReadOnlyList<FolderCardViewModel> targets)
    {
        foreach (var card in targets)
        {
            if (!await GuardAsync("移除失败", () => _folders.RemoveAsync(card.Id)))
            {
                return;
            }
        }

        ClearSelection();
        RebuildSource();
        RebuildTabs();
        ApplyFilter();
        _contextCard = null;

        OnNotice(
            targets.Count == 1
                ? $"已移除「{targets[0].DisplayName}」，磁盘上的目录没有改动"
                : $"已移除 {targets.Count} 条引用，磁盘上的目录一个都没动",
            ToastKind.Success);
    }

    /// <summary>
    /// 改动没写进磁盘时的统一收场。配置服务在写失败时已经把内存退回磁盘上那份
    /// （云盘占用、磁盘满、文件被别的程序锁住都会这样），这里重载列表并把话说清楚：
    /// 「看着删掉了、重启又回来」比直接报错更难理解。
    /// </summary>
    private async Task<bool> GuardAsync(string failurePrefix, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Prefix}", failurePrefix);
            OnNotice($"{failurePrefix}：{ex.Message}，已退回改动前的样子");
            await LoadAsync();
            return false;
        }
    }

    private readonly HashSet<string> _selection = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectionAnchorId;
    private string _baseStatus = string.Empty;

    /// <summary>请窗口确认「移除这 N 条引用」。一次点掉十几条不该是无声的。</summary>
    public event EventHandler<int>? BatchDeleteRequested;

    /// <summary>当前选中几张卡片。</summary>
    public int SelectedCount => _selection.Count;

    /// <summary>
    /// 单击卡片：普通单击是单选 + 开预览，Ctrl 加选/去选，Shift 从上次的位置连选。
    /// 把八个文件夹归进一个组要右键八次，是这套面板最劝退的地方。
    /// </summary>
    public void HandleCardClick(FolderCardViewModel card, bool ctrl, bool shift)
    {
        if (shift && _selectionAnchorId is not null)
        {
            var ids = Cards.Select(entry => entry.Id).ToList();
            var from = ids.IndexOf(_selectionAnchorId);
            var to = ids.IndexOf(card.Id);

            if (from >= 0 && to >= 0)
            {
                if (!ctrl)
                {
                    _selection.Clear();
                }

                for (var index = Math.Min(from, to); index <= Math.Max(from, to); index++)
                {
                    _selection.Add(ids[index]);
                }

                SyncSelection();
                return;
            }
        }

        if (ctrl)
        {
            if (!_selection.Remove(card.Id))
            {
                _selection.Add(card.Id);
            }

            _selectionAnchorId = card.Id;
            SyncSelection();
            return;
        }

        _selection.Clear();
        _selection.Add(card.Id);
        _selectionAnchorId = card.Id;
        SyncSelection();
        PreviewCard(card);
    }

    /// <summary>Ctrl+A：选中当前视图里能看见的全部卡片。</summary>
    [RelayCommand]
    public void SelectAllCards()
    {
        _selection.Clear();
        foreach (var card in Cards)
        {
            _selection.Add(card.Id);
        }

        _selectionAnchorId = Cards.LastOrDefault()?.Id;
        SyncSelection();
    }

    /// <summary>清掉选中状态（Esc、批量操作之后）。</summary>
    public void ClearSelection()
    {
        _selection.Clear();
        _selectionAnchorId = null;
        SyncSelection();
    }

    private void SyncSelection()
    {
        foreach (var card in Cards)
        {
            card.IsSelected = _selection.Contains(card.Id);
        }

        OnPropertyChanged(nameof(SelectedCount));
        SetStatus(_baseStatus);
    }

    /// <summary>状态栏文字。选中卡片时追加一句，让「接下来右键会作用在几个上」一目了然。</summary>
    private void SetStatus(string text)
    {
        _baseStatus = text;
        StatusText = _selection.Count > 0
            ? $"{text} · 已选 {_selection.Count} 张，右键可批量处理"
            : text;
    }

    /// <summary>
    /// 操作对象。三种进来路都要能落到正确的卡片上：
    /// 多选后右键其中一张（整批）、多选后走键盘（整批）、只选中一张或只右键一张（那一张）。
    /// </summary>
    /// <summary>
    /// 拖到 DeskMate 外面（微信、浏览器上传区、资源管理器）时要带走的路径：
    /// 选中了多张就一次拖多个，否则只拖按住的那一张。
    /// </summary>
    public IReadOnlyList<string> DragPathsFor(FolderCardViewModel card)
        => _selection.Count > 1 && _selection.Contains(card.Id)
            ? Cards.Where(entry => _selection.Contains(entry.Id)).Select(entry => entry.RealPath).ToArray()
            : [card.RealPath];

    private IReadOnlyList<FolderCardViewModel> CurrentTargets()
    {
        if (_selection.Count > 1)
        {
            return Cards.Where(card => _selection.Contains(card.Id)).ToList();
        }

        if (_contextCard is not null)
        {
            return [_contextCard];
        }

        var only = Cards.FirstOrDefault(card => _selection.Contains(card.Id));
        return only is null ? [] : [only];
    }

    /// <summary>右键：设置颜色标签；传 null 清除。选中多张时一起改。</summary>
    [RelayCommand]
    public async Task ApplyColorAsync(string? color)
    {
        var targets = CurrentTargets();

        foreach (var card in targets)
        {
            if (!await GuardAsync("设置颜色失败", () => _folders.UpdateColorAsync(card.Id, color)))
            {
                return;
            }

            card.Color = color;
        }

        if (targets.Count > 1)
        {
            OnNotice(
                color is null
                    ? $"已清除 {targets.Count} 张卡片的颜色标签"
                    : $"已给 {targets.Count} 张卡片加上颜色标签",
                ToastKind.Success);
        }
    }

    /// <summary>右键：换分组。TabId 为 "none" 表示未分组。选中多张时一起挪。</summary>
    [RelayCommand]
    public async Task ApplyGroupAsync(GroupTab? tab)
    {
        var targets = CurrentTargets();
        if (targets.Count == 0 || tab is null)
        {
            return;
        }

        var groupId = tab.Id is AllTabId or UngroupedTabId ? null : tab.Id;

        foreach (var card in targets)
        {
            if (!await GuardAsync("换分组失败", () => _folders.UpdateGroupAsync(card.Id, groupId)))
            {
                return;
            }

            card.GroupId = groupId;
        }

        RebuildTabs();
        ApplyFilter();

        if (targets.Count > 1)
        {
            OnNotice($"已把 {targets.Count} 个文件夹归到「{tab.Name}」", ToastKind.Success);
        }
    }

    /// <summary>新建分组。</summary>
    [RelayCommand]
    public void BeginCreateGroup()
    {
        NewGroupName = string.Empty;
        IsNewGroupOpen = true;
    }

    /// <summary>取消新建分组。</summary>
    [RelayCommand]
    public void CancelCreateGroup() => IsNewGroupOpen = false;

    /// <summary>确认新建分组：名字去首尾空格后写进配置，并把视图切到新分组上。</summary>
    [RelayCommand(CanExecute = nameof(CanCommitGroup))]
    public async Task CommitGroupAsync()
    {
        var name = GroupNameRules.Normalize(NewGroupName);
        var error = GroupNameRules.Validate(name, TakenGroupNames());
        if (error is not null)
        {
            OnNotice(error);
            return;
        }

        var group = await _folders.AddGroupAsync(name);
        RebuildTabs();
        ApplyFilter();
        SelectedTabId = group.Id;
        OnNotice($"已新建分组「{group.Name}」", ToastKind.Success);

        IsNewGroupOpen = false;
        NewGroupName = string.Empty;
    }

    private bool CanCommitGroup()
        => GroupNameRules.Validate(GroupNameRules.Normalize(NewGroupName), TakenGroupNames()) is null;

    // 「全部」「未分组」是内置视图，允许重名就会出现两个一模一样的 Tab，所以一并算占用。
    private IEnumerable<string> TakenGroupNames(string? excluding = null)
        => ReservedViewNames.Concat(_folders.GetGroups()
            .Where(group => group.Id != excluding)
            .Select(group => group.Name));

    private static readonly string[] ReservedViewNames = ["全部", "未分组", "★ 收藏"];

    /// <summary>右键 Tab 或点「重命名当前分组」：把浮层打开并填上现名。</summary>
    [RelayCommand]
    public void BeginRenameGroup(GroupTabViewModel? tab)
    {
        var target = tab?.Id ?? SelectedTabId;

        if (tab is null && SelectedTabId.Length > 0)
        {
            target = SelectedTabId;
        }

        if (target is AllTabId or UngroupedTabId or FavoritesTabId || DesktopFileRules.IsCategoryTab(target))
        {
            OnNotice("「全部」「★ 收藏」「未分组」和桌面分类都是内置视图，不能改名。", ToastKind.Info);
            return;
        }

        var group = _folders.GetGroups().FirstOrDefault(item => item.Id == target);
        if (group is null)
        {
            OnNotice("这个分组已经不在了。", ToastKind.Info);
            return;
        }

        _renameGroupTargetId = group.Id;
        RenameGroupText = group.Name;
        IsRenameGroupOpen = true;
    }

    /// <summary>取消改名。</summary>
    [RelayCommand]
    public void CancelRenameGroup()
    {
        IsRenameGroupOpen = false;
        _renameGroupTargetId = string.Empty;
    }

    /// <summary>提交分组改名。组内卡片记的是分组 Id，所以不需要动任何文件夹。</summary>
    [RelayCommand(CanExecute = nameof(CanCommitGroupRename))]
    public async Task CommitGroupRenameAsync()
    {
        var name = GroupNameRules.Normalize(RenameGroupText);
        var error = GroupNameRules.Validate(name, TakenGroupNames(excluding: _renameGroupTargetId));
        if (error is not null)
        {
            OnNotice(error);
            return;
        }

        try
        {
            await _folders.RenameGroupAsync(_renameGroupTargetId, name);
            RebuildTabs();
            OnNotice($"分组已改名为「{name}」，组里的文件夹一个都没动", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重命名分组失败");
            OnNotice($"重命名分组失败：{ex.Message}");
        }
        finally
        {
            IsRenameGroupOpen = false;
            _renameGroupTargetId = string.Empty;
        }
    }

    private bool CanCommitGroupRename()
        => GroupNameRules.Validate(
            GroupNameRules.Normalize(RenameGroupText),
            TakenGroupNames(excluding: _renameGroupTargetId)) is null;

    /// <summary>
    /// 请求删除分组。视图模型只负责判断能不能删、报出会影响到几个文件夹，
    /// 「要不要删」由窗口弹窗问用户。
    /// </summary>
    [RelayCommand]
    public void RequestDeleteGroup(GroupTabViewModel? tab)
    {
        var target = tab?.Id ?? SelectedTabId;

        if (target is AllTabId or UngroupedTabId or FavoritesTabId)
        {
            OnNotice("「全部」「★ 收藏」「未分组」是内置视图，不能删除。", ToastKind.Info);
            return;
        }

        // 桌面分类也是内置视图，而且它压根不是分组，走到 RemoveGroupAsync 只会白跑一趟。
        if (DesktopFileRules.IsCategoryTab(target))
        {
            OnNotice("桌面文件分类是内置视图，不能删除。", ToastKind.Info);
            return;
        }

        var group = _folders.GetGroups().FirstOrDefault(item => item.Id == target);
        if (group is null)
        {
            OnNotice("这个分组已经不在了。", ToastKind.Info);
            return;
        }

        GroupDeleteConfirmRequested?.Invoke(
            this,
            new GroupDeleteRequest(group.Id, group.Name, _all.Count(item => item.GroupId == group.Id)));
    }

    /// <summary>用户确认之后真正删除，并把这一步记成可撤销。</summary>
    public async Task DeleteGroupAsync(string groupId)
    {
        var group = _folders.GetGroups().FirstOrDefault(item => item.Id == groupId);
        if (group is null)
        {
            return;
        }

        var members = _all.Where(item => item.GroupId == groupId).Select(item => item.Id).ToList();

        if (!await GuardAsync("删除分组失败", () => _folders.RemoveGroupAsync(groupId)))
        {
            return;
        }

        if (SelectedTabId == groupId)
        {
            SelectedTabId = AllTabId;
        }

        RebuildSource();
        RebuildTabs();
        ApplyFilter();

        _deletedGroup = (group.Name, members);
        OnNotice(
            $"已删除分组「{group.Name}」，组内 {members.Count} 个文件夹退到未分组（文件夹本身没动）",
            ToastKind.Success,
            "撤销",
            RestoreDeletedGroup);
    }

    private async void RestoreDeletedGroup()
    {
        var snapshot = _deletedGroup;
        if (snapshot is null)
        {
            return;
        }

        _deletedGroup = null;
        var (name, members) = snapshot.Value;

        var group = await _folders.AddGroupAsync(name);
        foreach (var id in members)
        {
            await _folders.UpdateGroupAsync(id, group.Id);
        }

        RebuildSource();
        RebuildTabs();
        ApplyFilter();
        SelectedTabId = group.Id;

        OnNotice($"已撤销：分组「{group.Name}」和它的 {members.Count} 个文件夹都回来了", ToastKind.Success);
    }

    /// <summary>拖拽排序后回写顺序。</summary>
    public async Task ReorderAsync(IReadOnlyList<string> orderedIds)
    {
        if (!await GuardAsync("保存顺序失败", () => _folders.ReorderAsync(orderedIds)))
        {
            return;
        }

        RebuildSource();
        ApplyFilter();
    }

    /// <summary>右键菜单弹出时记住是哪张卡片。</summary>
    public void SetContextCard(FolderCardViewModel card) => _contextCard = card;

    private void RebuildSource()
    {
        _all.Clear();
        _all.AddRange(_folders.GetAll());
    }

    private void RebuildTabs()
    {
        var groups = _folders.GetGroups();

        Tabs.Clear();
        Tabs.Add(new GroupTabViewModel(this, AllTabId, $"全部 {_all.Count}"));

        // 收藏页固定显示（0 项也在）：星星是新功能，Tab 忽多忽少会让人找不到刚收藏的东西去哪了。
        Tabs.Add(new GroupTabViewModel(this, FavoritesTabId, $"★ 收藏 {_folders.GetFavorites().Count}"));

        // 常用类型紧跟「全部」：桌面上散落的图片/文档/压缩包是最想一眼找到的，
        // 且 0 个文件也照样显示 —— 标签忽多忽少会让同一位置的点击落空。
        var counts = CountDesktopFiles();
        foreach (var category in DesktopFileRules.OrderedCategories)
        {
            counts.TryGetValue(category, out var taken);
            Tabs.Add(new GroupTabViewModel(this, 
                DesktopFileRules.TabId(category),
                $"{DesktopFileRules.DisplayName(category)} {taken}"));
        }

        // 分组也带数量，和桌面分类同一套写法：一眼能看出哪个组还是空的。
        Tabs.Add(new GroupTabViewModel(this, UngroupedTabId, $"未分组 {_all.Count(item => item.GroupId is null)}"));
        foreach (var group in groups)
        {
            Tabs.Add(new GroupTabViewModel(this, group.Id, $"{group.Name} {_all.Count(item => item.GroupId == group.Id)}"));
        }

        SyncTabSelection();

        GroupMenu.Clear();
        GroupMenu.Add(new GroupTab(UngroupedTabId, "未分组"));
        foreach (var group in groups)
        {
            GroupMenu.Add(new GroupTab(group.Id, group.Name));
        }

        if (!IsDesktopView && !IsSearchView)
        {
            SetStatus($"{Tabs.Count} 个视图 · {_all.Count} 个文件夹");
        }
    }

    private Dictionary<DesktopFileCategory, int> CountDesktopFiles()
        => _desktopFiles.Files.GroupBy(file => file.Category).ToDictionary(group => group.Key, group => group.Count());

    private void SyncTabSelection()
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab.Id == SelectedTabId;
        }
    }

    private void ApplyFilter()
    {
        var key = Keyword.Trim();

        // 搜索优先于当前 Tab：输入了词就是要找东西，不该先问「你现在停在哪个视图上」。
        if (IsSearchView)
        {
            ApplySearch(key);
            return;
        }

        if (DesktopFileRules.IsCategoryTab(SelectedTabId))
        {
            ApplyDesktopFilter(key);
            return;
        }

        if (SelectedTabId == FavoritesTabId)
        {
            ApplyFavoritesFilter(key);
            return;
        }

        DesktopFiles.Clear();
        IsDesktopEmpty = false;
        FavoriteCards.Clear();
        IsFavoritesEmpty = false;
        SearchHits.Clear();
        IsSearchEmpty = false;

        IEnumerable<FolderItem> query = SelectedTabId switch
        {
            AllTabId => _all,
            UngroupedTabId => _all.Where(item => item.GroupId is null),
            _ => _all.Where(item => item.GroupId == SelectedTabId),
        };

        if (key.Length > 0)
        {
            query = query.Where(item => Matches(item, key));
        }

        Cards.Clear();
        foreach (var item in query)
        {
            Cards.Add(CreateCard(item));
        }

        // 搜完清空时状态栏不能留着「搜索「xxx」：N 个文件夹 · M 个文件」那句 —— 它说的已经不是屏幕上这些了。
        SetStatus($"当前视图 {Cards.Count} 个文件夹");
    }

    /// <summary>桌面文件卡片：建出来就带上收藏状态，星星不能先画成空心再等异步回填。</summary>
    private DesktopFileItemViewModel CreateFileCard(DesktopFile file)
        => new(this, file)
        {
            IsFavorite = _folders.IsFavorite(file.Path),
        };

    private FolderCardViewModel CreateCard(FolderItem item)
    {
        var card = new FolderCardViewModel(this, item.Id, item.DisplayName, item.RealPath, item.GroupId, item.Color, item.Valid);

        // 星星的初值必须一次到位：先画成空心再等异步回填，用户会以为刚收藏的东西没生效。
        card.IsFavorite = _folders.IsFavorite(item.RealPath);
        return card;
    }

    /// <summary>搜索结果里桌面文件最多显示这么多条，超了就说明并指向全局搜索。</summary>
    private const int SearchFileCap = 60;

    /// <summary>一条都没搜到（空面板要有句话）。</summary>
    [ObservableProperty]
    private bool _isSearchEmpty;

    /// <summary>
    /// 面板内搜索：受管文件夹 + 桌面文件一起找。以前这里只筛当前 Tab 的文件夹，
    /// 于是「我记得桌面上有个 xxx.pdf」在面板里搜不出来，像工具坏了。
    /// </summary>
    private void ApplySearch(string key)
    {
        Cards.Clear();
        DesktopFiles.Clear();
        FavoriteCards.Clear();
        IsDesktopEmpty = false;
        IsFavoritesEmpty = false;
        SearchHits.Clear();

        var folders = _all.Where(item => Matches(item, key)).ToList();
        var files = _desktopFiles.Files
            .Where(file => PinyinMatcher.Score(file.Name, key, file.Path) > PinyinMatcher.NoMatch)
            .ToList();

        foreach (var item in folders)
        {
            SearchHits.Add(CreateCard(item));
        }

        foreach (var file in files.Take(SearchFileCap))
        {
            SearchHits.Add(CreateFileCard(file));
        }

        IsSearchEmpty = SearchHits.Count == 0;

        var summary = $"搜索「{key}」：{folders.Count} 个文件夹 · {files.Count} 个桌面文件";
        if (files.Count > SearchFileCap)
        {
            summary += $"（文件太多，只显示前 {SearchFileCap} 个）";
        }

        SetStatus(IsSearchEmpty ? $"{summary} · 没找到" : summary);
    }

    /// <summary>当前分类里一个文件都没有（空面板要有句话，不然看着像坏了）。</summary>
    [ObservableProperty]
    private bool _isDesktopEmpty;

    /// <summary>
    /// 桌面分类视图：条目是从桌面现场读出来的虚拟分类，与文件夹引用（config.json 里的路径）互斥显示，
    /// 所以这边填充时要把卡片列表清空，反过来也一样。
    /// </summary>
    private void ApplyDesktopFilter(string key)
    {
        var category = DesktopFileRules.CategoryOfTab(SelectedTabId);
        var files = _desktopFiles.Files;

        Cards.Clear();
        DesktopFiles.Clear();
        FavoriteCards.Clear();
        SearchHits.Clear();
        IsSearchEmpty = false;
        IsFavoritesEmpty = false;

        if (category is null)
        {
            IsDesktopEmpty = true;
            return;
        }

        foreach (var file in files)
        {
            if (file.Category != category
                || (key.Length > 0 && PinyinMatcher.Score(file.Name, key, file.Path) <= PinyinMatcher.NoMatch))
            {
                continue;
            }

            DesktopFiles.Add(CreateFileCard(file));
        }

        SetStatus($"{DesktopFileRules.DisplayName(category.Value)} {DesktopFiles.Count} 个 · 桌面共 {files.Count} 个文件");
        IsDesktopEmpty = DesktopFiles.Count == 0;
    }

    // 面板、启动台、全局搜索用同一套匹配规则：在启动台能打「jsq」找到计算器，换个窗口就打不开，
    // 用户只会认为这个搜索坏了。
    private static bool Matches(FolderItem item, string key)
        => PinyinMatcher.Score(item.DisplayName, key, item.RealPath) > PinyinMatcher.NoMatch;

    private async Task LoadCountsAsync()
    {
        _countCts?.Cancel();
        _countCts?.Dispose();

        var cts = new CancellationTokenSource();
        _countCts = cts;

        // 搜索视图下卡片混在 SearchHits 里，条目数一样要统计。
        foreach (var card in Cards.Concat(SearchHits.OfType<FolderCardViewModel>()).ToList())
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            await LoadCountForAsync(card);
        }
    }

    private async Task LoadCountForAsync(FolderCardViewModel card)
    {
        try
        {
            card.FileCount = await _folders.CountFilesAsync(card.RealPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "统计文件数失败 {Path}", card.RealPath);
            card.IsCountLoading = false;
            card.IsCountFailed = true;
        }
    }

    // 预览只读列举，且限量 50 条：既满足需求，也避免大目录把 UI 卡住。
    // 多取一条只为了知道「后面还有没有」，好决定要不要挂那行截断提示。
    private IEnumerable<PreviewEntry> EnumeratePreview(string path)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        var probe = PreviewLimit + 1;
        var taken = 0;
        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            if (taken >= probe)
            {
                yield break;
            }

            // 预览行也能收藏，所以建行之时要把当前收藏状态带进来，不然星星一律显示成空心。
            yield return new PreviewEntry(this, new DirectoryInfo(directory).Name, directory, true)
            {
                IsFavorite = _folders.IsFavorite(directory),
                ModifiedText = FileText.Modified(Directory.GetLastWriteTime(directory)),
            };
            taken++;
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            if (taken >= probe)
            {
                yield break;
            }

            // 大小与日期顺手取：列表现在摆在主区，得像资源管理器那样看得出「哪个是刚改过的」。
            // 一次枚举最多 50 条，多一个 stat 不算成本；取不到就留空，不影响这一行列出来。
            var size = string.Empty;
            var modified = string.Empty;

            try
            {
                var info = new FileInfo(file);
                size = FileText.Size(info.Length);
                modified = FileText.Modified(info.LastWriteTime);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取预览条目大小失败 {Path}", file);
            }

            yield return new PreviewEntry(this, Path.GetFileName(file), file, false)
            {
                IsFavorite = _folders.IsFavorite(file),
                SizeText = size,
                ModifiedText = modified,
            };
            taken++;
        }
    }

    // ==================== 收藏 ====================

    /// <summary>收藏页一条都没有（空面板要有句话）。</summary>
    [ObservableProperty]
    private bool _isFavoritesEmpty;

    private CancellationTokenSource? _favoriteCts;

    private void ApplyFavoritesFilter(string key)
    {
        Cards.Clear();
        DesktopFiles.Clear();
        IsDesktopEmpty = false;
        FavoriteCards.Clear();
        SearchHits.Clear();
        IsSearchEmpty = false;

        var paths = _folders.GetFavorites();

        if (key.Length > 0)
        {
            paths = paths
                .Where(path => PinyinMatcher.Score(LeafName(path), key, path) > PinyinMatcher.NoMatch)
                .ToList();
        }

        foreach (var path in paths)
        {
            var managed = _all.FirstOrDefault(item => SamePath(item.RealPath, path));
            FavoriteCards.Add(new FavoriteCardViewModel(this, path, managed?.DisplayName ?? LeafName(path), managed is not null));
        }

        IsFavoritesEmpty = FavoriteCards.Count == 0;
        SetStatus(IsFavoritesEmpty
            ? "还没有收藏 · 点文件夹卡片右下角的 ☆ 收进来"
            : $"收藏 {FavoriteCards.Count} 项");

        _ = LoadFavoriteDetailsAsync();
    }

    private async Task LoadFavoriteDetailsAsync()
    {
        _favoriteCts?.Cancel();
        _favoriteCts?.Dispose();

        var cts = new CancellationTokenSource();
        _favoriteCts = cts;

        foreach (var card in FavoriteCards.ToList())
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            try
            {
                // 收藏的路径可能在外接盘或网络盘上，Exists 和递归统计都不能压在 UI 线程。
                var detail = await Task.Run(() => Describe(card), cts.Token);

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                (card.IsDirectory, card.IsValid, card.Badge, card.MetaText) = detail;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "读取收藏条目详情失败 {Path}", card.FullPath);
                card.MetaText = "读不到";
            }
        }
    }

    // 字段名按接收方 (IsDirectory, IsValid, Badge, MetaText) 的顺序写：
    // 上一版这里叫 (Exists, IsDirectory, …)，解构时按位置串了一位，
    // 于是收藏的文件被当成「目录 + 路径失效」，卡片上的大小日期整行标红。
    private (bool IsDirectory, bool IsValid, string Badge, string Meta) Describe(FavoriteCardViewModel card)
    {
        if (Directory.Exists(card.FullPath))
        {
            var count = FolderService.CountFiles(card.FullPath, _logger);
            var meta = count >= FolderService.FileCountCap
                ? "10000+"
                : count == 0
                    ? "空"
                    : $"{count} 个文件";
            return (true, true, string.Empty, meta);
        }

        if (File.Exists(card.FullPath))
        {
            var info = new FileInfo(card.FullPath);
            var extension = info.Extension.TrimStart('.').ToUpperInvariant();
            var badge = extension.Length > 4 ? extension[..4] : extension;

            return (false, true, badge.Length > 0 ? badge : "文件",
                $"{FileText.Size(info.Length)} · {FileText.Modified(info.LastWriteTime)}");
        }

        return (false, false, string.Empty, "路径不存在");
    }

    /// <summary>点文件夹卡片右下角的星星：收藏 / 取消收藏。</summary>
    public Task ToggleFavoriteAsync(FolderCardViewModel card)
        => ToggleFavoriteAsync(card.RealPath, card.DisplayName);

    /// <summary>
    /// 切换收藏（路径版）。文件夹卡片、桌面文件卡片、预览里的每一行都走这一条 ——
    /// 收藏本来就只是「一条路径引用」，不该要求它必须先是一张受管文件夹卡片。
    /// </summary>
    public async Task ToggleFavoriteAsync(string path, string displayName)
    {
        // 预览列表末尾那行「仅显示前 50 项」是提示不是路径，星星按钮已经藏了，这里再兜一道。
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var added = false;

        try
        {
            added = await _folders.ToggleFavoriteAsync(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切换收藏失败 {Path}", path);
            OnNotice($"收藏没写进去：{ex.Message}");
            return;
        }

        SyncFavoriteStars(path, added);
        RebuildTabs();

        // 收藏页正开着的话列表要立刻跟上，否则点了星星看不出变化。
        if (IsFavoritesView)
        {
            ApplyFilter();
        }

        OnNotice(
            added ? $"已把「{displayName}」加入收藏" : $"已从收藏移出「{displayName}」",
            ToastKind.Success,
            "撤销",
            () => _ = RestoreFavoriteAsync(path, added));
    }

    /// <summary>
    /// 同一个路径可以同时出现在文件夹卡片、桌面文件卡片、搜索结果和预览行上。
    /// 星星绑的是各自的条目，不一起刷新的话，在这里收藏完，另一处的星星还停在旧状态。
    /// </summary>
    private void SyncFavoriteStars(string path, bool state)
    {
        foreach (var card in Cards.Where(item => SamePath(item.RealPath, path)))
        {
            card.IsFavorite = state;
        }

        foreach (var file in DesktopFiles.Where(item => SamePath(item.Path, path)))
        {
            file.IsFavorite = state;
        }

        foreach (var hit in SearchHits)
        {
            switch (hit)
            {
                case FolderCardViewModel folder when SamePath(folder.RealPath, path):
                    folder.IsFavorite = state;
                    break;
                case DesktopFileItemViewModel file when SamePath(file.Path, path):
                    file.IsFavorite = state;
                    break;
            }
        }

        foreach (var entry in PreviewItems.Where(item => item.HasPath && SamePath(item.Path, path)))
        {
            entry.IsFavorite = state;
        }
    }

    private async Task RestoreFavoriteAsync(string path, bool wasAdded)
    {
        // 撤销只把状态拧回去，不再报一条：用户刚点的就是这个按钮，再回一句是重复话。
        if (wasAdded)
        {
            await _folders.RemoveFavoriteAsync(path);
        }
        else
        {
            await _folders.AddFavoritesAsync([path]);
        }

        RebuildTabs();
        ApplyFilter();
    }

    /// <summary>收藏页上点实心星星或右键「取消收藏」。</summary>
    public async Task UnfavoriteAsync(FavoriteCardViewModel card)
    {
        try
        {
            await _folders.RemoveFavoriteAsync(card.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消收藏失败 {Path}", card.FullPath);
            OnNotice($"取消收藏没写进去：{ex.Message}");
            return;
        }

        RebuildTabs();
        ApplyFilter();

        var path = card.FullPath;
        OnNotice($"已从收藏移出「{card.Title}」", ToastKind.Success, "撤销", () => _ = AddFavoriteBackAsync(path));
    }

    private async Task AddFavoriteBackAsync(string path)
    {
        await _folders.AddFavoritesAsync([path]);
        RebuildTabs();
        ApplyFilter();
    }

    /// <summary>收藏页的「＋ 收藏文件 / 压缩包」：让窗口弹挑文件框。</summary>
    [RelayCommand]
    public void RequestPickFile() => PickFileRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>把用户挑中的文件收进收藏（目录用卡片星星，这里补上其余类型）。</summary>
    public async Task AddFavoritePathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        int added;
        try
        {
            added = await _folders.AddFavoritesAsync(paths);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量收藏失败");
            OnNotice($"收藏没写进去：{ex.Message}");
            return;
        }

        RebuildTabs();
        ApplyFilter();

        var skipped = paths.Count - added;
        OnNotice(
            added == 0
                ? "这些已经在收藏里了"
                : $"已收藏 {added} 项" + (skipped > 0 ? $"，{skipped} 项已在收藏里" : string.Empty),
            added == 0 ? ToastKind.Info : ToastKind.Success);
    }

    /// <summary>双击收藏卡片：目录进资源管理器，文件走系统关联程序，与在资源管理器里双击一致。</summary>
    public void OpenFavorite(FavoriteCardViewModel card) => OpenExternally(card.FullPath, card.Title);

    /// <summary>
    /// 单击收藏卡片：先看，不开。目录就列出它里面的内容，文件就直接看它的内容 ——
    /// 和主视图「单击卡片预览」同一个意思，双击才是打开。
    /// </summary>
    public void PreviewFavorite(FavoriteCardViewModel card)
    {
        // 自己判断是不是目录，不用卡片上那个 IsDirectory：详情是后台慢慢填的，
        // 收藏刚点亮就点卡片的话它还是 false，目录会被当成文件、预览到上一层去。
        if (Directory.Exists(card.FullPath))
        {
            _previewBack.Clear();
            _previewCard = null;
            ShowPreview(card.FullPath);
            return;
        }

        if (File.Exists(card.FullPath))
        {
            _previewBack.Clear();
            _previewCard = null;
            PreviewFile(card.FullPath);
            return;
        }

        OnNotice($"所在位置已经打不开：{card.FullPath}", ToastKind.Info);
    }

    /// <summary>右键：在资源管理器中选中它。</summary>
    public void RevealFavorite(FavoriteCardViewModel card)
    {
        try
        {
            _launcher.OpenInExplorer(card.FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开收藏条目所在位置失败 {Path}", card.FullPath);
            OnNotice($"打不开所在位置：{ex.Message}");
        }
    }

    private static bool SamePath(string left, string right)
        => string.Equals(
            left.TrimEnd('\\', '/'),
            right.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    // ==================== 新建文件夹 / 文件 ====================

    /// <summary>新建浮层是否打开。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCreateEntryCommand))]
    [NotifyPropertyChangedFor(nameof(CreateEntryPreview))]
    [NotifyPropertyChangedFor(nameof(IsCreateEntryVisible))]
    private bool _isCreateEntryOpen;

    /// <summary>浮层绑的是这个（与其他三个浮层同一套写法）。名字对不上时绑定是静默失败的，
    /// Visibility 会停在默认值 Visible —— 表现就是「一打开面板就挂着新建框，点取消也收不掉」。</summary>
    public bool IsCreateEntryVisible => IsCreateEntryOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCreateEntryCommand))]
    [NotifyPropertyChangedFor(nameof(CreateEntryPreview))]
    [NotifyPropertyChangedFor(nameof(CreateEntryError))]
    private string _createEntryText = string.Empty;

    /// <summary>
    /// 新建落在哪个目录。三个入口给的答案不一样：卡片上的「＋」= 那个目录，
    /// 工具栏的「＋ 新建」= 桌面，预览面板的「＋」= 正在看的那一层。浮层里也允许临时换地方。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateEntryPreview))]
    [NotifyCanExecuteChangedFor(nameof(CommitCreateEntryCommand))]
    private string _createEntryDirectory = string.Empty;

    private FolderCardViewModel? _createEntryCard;

    /// <summary>浮层里可选的新建类型（文件夹 / Word / Excel / PPT / Markdown / 文本 / 自己写后缀）。</summary>
    public IReadOnlyList<NewEntryOptionViewModel> CreateEntryTypes { get; }

    /// <summary>当前选中的类型，默认「文件夹」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreateEntryPreview))]
    [NotifyCanExecuteChangedFor(nameof(CommitCreateEntryCommand))]
    private NewEntryOption _selectedCreateEntryType = NewEntryCatalog.Options[0];

    partial void OnSelectedCreateEntryTypeChanged(NewEntryOption value)
    {
        // 选中项可以从两边动：点按钮（IsSelected 写回来）或重开浮层（这里重置成「文件夹」），
        // 所以每次都要把各条目的勾选状态对齐一遍，否则会出现两个都亮着。
        foreach (var option in CreateEntryTypes)
        {
            option.IsSelected = ReferenceEquals(option.Option, value);
        }
    }

    /// <summary>点浮层上的某个类型。</summary>
    public void SelectCreateEntryType(NewEntryOption option) => SelectedCreateEntryType = option;

    /// <summary>浮层上点「换个位置…」：让窗口弹目录选择框。</summary>
    public event EventHandler? PickCreateLocationRequested;

    /// <summary>边输边判的原因，空字符串表示这个名字能用。</summary>
    public string CreateEntryError => NewEntryRules.Validate(ResolvedCreateName) ?? string.Empty;

    /// <summary>用户输入的名字 + 选中的类型 = 真正要落盘的名字。</summary>
    private string ResolvedCreateName => NewEntryCatalog.ResolveName(CreateEntryText, SelectedCreateEntryType);

    /// <summary>
    /// 把「建成什么类型、叫什么、落在哪」写在输入框下面。选类型这条口径要让人看见结果，
    /// 尤其「Word 文档 + 报告」会自动补成 <c>报告.docx</c>，不写出来用户不知道自己建的是什么。
    /// </summary>
    public string CreateEntryPreview
    {
        get
        {
            var name = ResolvedCreateName;
            if (CreateEntryDirectory.Length == 0 || name.Length == 0)
            {
                return SelectedCreateEntryType.Shape == NewEntryShape.FreeForm
                    ? "不带后缀建文件夹，带后缀（如 main.c）建文件"
                    : $"建一个{SelectedCreateEntryType.Label}";
            }

            var kind = SelectedCreateEntryType.Shape == NewEntryShape.Folder
                ? "文件夹"
                : SelectedCreateEntryType.Shape == NewEntryShape.Fixed
                    ? SelectedCreateEntryType.Label
                    : NewEntryRules.Classify(name) == NewEntryKind.Folder ? "文件夹" : "文件";

            return $"将新建{kind}：{Path.Combine(CreateEntryDirectory, name)}";
        }
    }

    private void OpenCreateOverlay(string directory, FolderCardViewModel? card)
    {
        if (!Directory.Exists(directory))
        {
            OnNotice($"这个目录已经不在了：{directory}", ToastKind.Info);
            return;
        }

        // 别的浮层还开着就先收掉：两层叠在一起，Esc 该收谁说不清。
        CancelRename();
        CancelCreateGroup();
        CancelRenameGroup();
        ClosePreview();

        _createEntryCard = card;
        CreateEntryDirectory = directory;
        CreateEntryText = string.Empty;
        SelectedCreateEntryType = NewEntryCatalog.Options[0];
        IsCreateEntryOpen = true;
    }

    /// <summary>点卡片上的「＋」：建在这个卡片对应的目录里。</summary>
    public void BeginCreateEntry(FolderCardViewModel card)
    {
        if (!card.IsValid)
        {
            OnNotice("这个目录已经不在了，先右键「重新定位到新位置」再新建。", ToastKind.Info);
            return;
        }

        OpenCreateOverlay(card.RealPath, card);
    }

    /// <summary>工具栏的「＋ 新建」：默认落在桌面上（用户实际的桌面目录，可能被重定向到别处）。</summary>
    [RelayCommand]
    public void BeginCreateOnDesktop() => OpenCreateOverlay(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        card: null);

    /// <summary>预览面板里的「＋」：建在你正在看的那一层目录里，下钻进去也算。</summary>
    [RelayCommand]
    public void BeginCreateInPreview()
    {
        if (_previewPath.Length == 0)
        {
            OnNotice("先单击一张卡片打开目录预览，再在这里新建。", ToastKind.Info);
            return;
        }

        OpenCreateOverlay(_previewPath, _previewCard);
    }

    /// <summary>换个位置：让窗口挑目录，挑完回填到浮层（浮层保持打开）。</summary>
    [RelayCommand]
    public void RequestPickCreateLocation() => PickCreateLocationRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>窗口挑完目录回来。不存在就不改，并说清楚为什么。</summary>
    public void SetCreateEntryDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            OnNotice($"那个位置现在打不开，还是建在「{CreateEntryDirectory}」", ToastKind.Info);
            return;
        }

        // 换地方之后要刷的是新位置对应的卡片（不在当前视图里就没有要刷的）。
        _createEntryCard = Cards.FirstOrDefault(card => SamePath(card.RealPath, directory));

        CreateEntryDirectory = directory;
    }

    [RelayCommand]
    public void CancelCreateEntry()
    {
        IsCreateEntryOpen = false;
        _createEntryCard = null;
    }

    private bool CanCommitCreateEntry() =>
        CreateEntryDirectory.Length > 0 && NewEntryRules.Validate(ResolvedCreateName) is null;

    /// <summary>确认新建。只放用户当场命名的新条目，同名一律拒绝，绝不覆盖已有内容。</summary>
    [RelayCommand(CanExecute = nameof(CanCommitCreateEntry))]
    public async Task CommitCreateEntryAsync()
    {
        var directory = CreateEntryDirectory;
        var option = SelectedCreateEntryType;
        var name = ResolvedCreateName;
        string created;

        try
        {
            created = await _folders.CreateEntryAsync(directory, name, option);
        }
        catch (Exception ex)
        {
            // 这里的消息本身就是给人看的完整一句话（重名、非法名、目录不在了、路径太长），
            // 别再加前缀，加上去反而读不通。
            _logger.LogWarning(ex, "新建失败 {Parent}\\{Name}", directory, name);
            OnNotice(ex.Message);
            return;
        }

        var card = _createEntryCard;
        CancelCreateEntry();

        if (card is not null)
        {
            await LoadCountForAsync(card);
        }

        var label = option.Shape == NewEntryShape.Folder
            ? "文件夹"
            : option.Shape == NewEntryShape.Fixed
                ? option.Label
                : NewEntryRules.Classify(name) == NewEntryKind.Folder ? "文件夹" : "文件";

        OnNotice(
            $"已新建{label}「{name}」",
            ToastKind.Success,
            "打开",
            () =>
            {
                try
                {
                    _launcher.Launch(created);
                }
                catch (Exception ex)
                {
                    OnNotice($"打不开「{name}」：{ex.Message}");
                }
            });
    }

    private void OnNotice(
        string message,
        ToastKind kind = ToastKind.Error,
        string? actionLabel = null,
        Action? action = null)
        => UserNotice?.Invoke(this, new NoticeEventArgs(message, kind, actionLabel, action));
}

/// <summary>请窗口确认的「删掉这个分组」。</summary>
/// <param name="GroupId">分组 Id。</param>
/// <param name="GroupName">分组名，弹窗要说清删的是哪一个。</param>
/// <param name="FolderCount">组内文件夹数，删掉之后它们会退到未分组。</param>
public sealed record GroupDeleteRequest(string GroupId, string GroupName, int FolderCount);
