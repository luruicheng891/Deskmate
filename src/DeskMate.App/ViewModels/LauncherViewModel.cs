using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
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
/// 启动台视图模型：应用列表 + 搜索过滤 + 行级虚拟化分块。
/// </summary>
public sealed partial class LauncherViewModel : ObservableObject
{
    private const int DebounceMs = 150;
    private const int DefaultColumns = 7;
    private const int FrequentTileCount = 28;
    private const int IconPixelSize = 64;

    private readonly IAppScannerService _scanner;
    private readonly IIconService _icons;
    private readonly IAppLauncherService _launcher;
    private readonly IUsageService _usage;
    private readonly IConfigService _config;
    private readonly DockViewModel _dock;
    private readonly ILogger<LauncherViewModel> _logger;
    private readonly DispatcherTimer _debounce;
    private readonly List<AppTileViewModel> _visibleTiles = new();

    private IReadOnlyList<AppInfo> _apps = Array.Empty<AppInfo>();
    private CancellationTokenSource? _iconLoadCts;

    // 已经解析过的图标（null 表示「试过，确实取不到」）。磁贴每轮过滤都重新生成，
    // 没有这张表的话每敲一个字整屏就退回字母占位，等异步加载追上为止是成片空白。
    private readonly Dictionary<string, BitmapSource?> _iconByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造启动台视图模型。</summary>
    public LauncherViewModel(
        IAppScannerService scanner,
        IIconService icons,
        IAppLauncherService launcher,
        IUsageService usage,
        IConfigService config,
        DockViewModel dock,
        ILogger<LauncherViewModel> logger)
    {
        _scanner = scanner;
        _icons = icons;
        _launcher = launcher;
        _usage = usage;
        _config = config;
        _dock = dock;
        _logger = logger;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += OnDebounceTick;

        _lastLaunchpadFolder = _config.Current.LaunchpadFolder;
        _config.ConfigChanged += OnConfigChanged;
    }

    private string _lastLaunchpadFolder;

    // 只有快捷方式文件夹真的变了才重扫：配置在每次 Dock 改动、滑条拖动时都会广播，
    // 不加这个判断的话拖一次滑条就触发一轮全量扫描。
    private void OnConfigChanged(object? sender, EventArgs e)
    {
        var folder = _config.Current.LaunchpadFolder;
        if (string.Equals(folder, _lastLaunchpadFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastLaunchpadFolder = folder;
        _ = LoadAsync();
    }

    /// <summary>需要提示用户的失败信息。</summary>
    public event EventHandler<NoticeEventArgs>? UserNotice;

    /// <summary>按行切分后的磁贴，外层用它做虚拟化。</summary>
    public ObservableCollection<AppRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private bool _isAllTab = true;

    [ObservableProperty]
    private bool _isFrequentTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden))]
    [NotifyPropertyChangedFor(nameof(HiddenTabLabel))]
    private int _hiddenCount;

    [ObservableProperty]
    private bool _isHiddenTab;

    /// <summary>只有真的隐藏过东西才显示「已隐藏」页，否则对使用者是噪音。</summary>
    public bool HasHidden => HiddenCount > 0;

    /// <summary>「已隐藏」页的标题，带条目数。</summary>
    public string HiddenTabLabel => $"已隐藏 ({HiddenCount})";

    [ObservableProperty]
    private int _columns = DefaultColumns;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "正在准备应用列表…";

    /// <summary>首次打开扫描（缓存命中时很快），随后后台静默刷新。</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _apps = await _scanner.ScanInstalledAppsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "应用扫描失败");
            StatusText = "应用列表读取失败，详见日志";
            return;
        }
        finally
        {
            IsLoading = false;
        }

        await ApplyFilterAsync();
        await RefreshAsync(report: false);
    }

    /// <summary>
    /// 重新扫描应用列表。装完或卸掉软件之后不必重启 DeskMate，这里就能把列表对齐现场。
    /// </summary>
    /// <param name="report">要不要播报结果：手动点「刷新」要播，后台静默对齐不播。</param>
    public async Task RefreshAsync(bool report = true)
    {
        if (_refreshing)
        {
            // 已经有人在扫了。连点 Dock 的启动台按钮会一次排进去好几轮全量扫描，
            // 后面的那几轮看到的现场和第一轮一样，纯粹白读盘（日志里能看到连着四行「应用扫描完成」）。
            return;
        }

        _refreshing = true;
        try
        {
            await RefreshCoreAsync(report);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private bool _refreshing;

    private async Task RefreshCoreAsync(bool report)
    {
        IsLoading = true;
        IReadOnlyList<AppInfo> fresh;
        try
        {
            fresh = await _scanner.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新扫描应用列表失败");
            IsLoading = false;
            StatusText = "应用列表读取失败，详见日志";
            if (report)
            {
                OnNotice($"重新扫描失败：{ex.Message}");
            }

            return;
        }

        IsLoading = false;

        // 按 Id 集合比差异，不能只比数量：一装一卸时总数不变，只比数量就永远不采纳新结果。
        var before = _apps.Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = fresh.Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = after.Except(before).Count();
        var removed = before.Except(after).Count();

        _apps = fresh;
        await ApplyFilterAsync();

        if (!report)
        {
            return;
        }

        if (added == 0 && removed == 0)
        {
            OnNotice($"应用列表没有变化，共 {fresh.Count} 个", ToastKind.Info);
        }
        else
        {
            OnNotice($"应用列表已更新：新增 {added} 个，移除 {removed} 个，现在共 {fresh.Count} 个", ToastKind.Success);
        }
    }

    /// <summary>工具栏「刷新」与 F5：手动重扫并播报。</summary>
    [RelayCommand]
    public Task RefreshAppsAsync() => RefreshAsync(report: true);

    /// <summary>按可用宽度重算每行磁贴数（需求：每行 7 个，窗口窄时自适应）。</summary>
    public void SetColumns(int columns)
    {
        var clamped = Math.Clamp(columns, 3, 20);
        if (clamped == Columns)
        {
            return;
        }

        Columns = clamped;
        RebuildRows();
    }

    /// <summary>需要用户挑一个程序/快捷方式加入启动台（文件对话框由窗口提供）。</summary>
    public event EventHandler? AddAppRequested;

    /// <summary>
    /// 把用户选中的程序或快捷方式加入启动台文件夹，然后刷新列表。
    /// 只往启动台文件夹写一个新快捷方式，不复制程序本体、也不动用户原来的文件。
    /// </summary>
    public Task AddAppFromPathAsync(string sourcePath) => AddAppsFromPathsAsync([sourcePath]);

    /// <summary>
    /// 批量添加应用：一次重扫、一条结果。逐个弹提示的话，选十个程序就是十次打断；
    /// 某一项失败（选中的文件读不到）也不该让其余项跟着不动。
    /// </summary>
    public async Task AddAppsFromPathsAsync(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0)
        {
            return;
        }

        var added = new List<string>();
        string? firstError = null;

        foreach (var sourcePath in sourcePaths)
        {
            try
            {
                var linkPath = await _scanner.AddToLaunchpadAsync(sourcePath);
                added.Add(Path.GetFileNameWithoutExtension(linkPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "添加应用到启动台失败 {Path}", sourcePath);
                firstError ??= ex.Message;
            }
        }

        if (added.Count == 0)
        {
            OnNotice($"添加失败：{firstError ?? "选中的项目不可用"}");
            return;
        }

        try
        {
            _apps = await _scanner.ScanInstalledAppsAsync(forceRefresh: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加应用后刷新列表失败");
        }

        // 加完必须让用户立刻看得见：停在「常用」页（新应用使用次数为 0 排不进来），
        // 或者搜索框里还留着刚才找应用时输入的词，都会让刚加的应用「好像没加上」。
        Keyword = string.Empty;
        IsHiddenTab = false;
        IsFrequentTab = false;
        IsAllTab = true;

        await ApplyFilterAsync();

        var skipped = sourcePaths.Count - added.Count;
        StatusText = $"已添加 {added.Count} 个应用";
        var summary = added.Count switch
        {
            1 => $"已添加到启动台：{added[0]}",
            <= 3 => $"已添加到启动台：{string.Join("、", added)}",
            _ => $"已添加到启动台 {added.Count} 个应用",
        };

        if (skipped > 0)
        {
            summary += $"，另有 {skipped} 个没加上：{firstError}";
        }

        OnNotice(summary, skipped > 0 ? ToastKind.Warning : ToastKind.Success);
    }

    /// <summary>点「添加应用」：让窗口弹出文件选择框。</summary>
    [RelayCommand]
    public void RequestAddApp() => AddAppRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>切到「全部」页。</summary>
    [RelayCommand]
    public Task ShowAllTabAsync() => SwitchTab(all: true, frequent: false, hidden: false);

    /// <summary>切到「常用」页。</summary>
    [RelayCommand]
    public Task ShowFrequentTabAsync() => SwitchTab(all: false, frequent: true, hidden: false);

    /// <summary>切到「已隐藏」页，在这里可以把误隐藏的条目放回来。</summary>
    [RelayCommand]
    public Task ShowHiddenTabAsync() => SwitchTab(all: false, frequent: false, hidden: true);

    private Task SwitchTab(bool all, bool frequent, bool hidden)
    {
        IsAllTab = all;
        IsFrequentTab = frequent;
        IsHiddenTab = hidden;
        return ApplyFilterInternalAsync();
    }

    /// <summary>单击磁贴启动应用。</summary>
    [RelayCommand]
    public void OpenTile(AppTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        try
        {
            _launcher.Launch(tile.Path);
            _usage.Record(tile.Id.ToUpperInvariant());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动应用失败 {Path}", tile.Path);
            OnNotice($"无法启动「{tile.Name}」：{ex.Message}");
        }
    }

    /// <summary>右键：添加到 Dock。</summary>
    [RelayCommand]
    public async Task AddTileToDockAsync(AppTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        try
        {
            var added = await _dock.AddItemAsync(tile.Path);

            // 已经固定过的再报一次「已固定」就是假回执，用户会以为多加了一个图标。
            OnNotice(added ? $"已把「{tile.Name}」固定到 Dock" : $"「{tile.Name}」已经在 Dock 上了",
                added ? ToastKind.Success : ToastKind.Info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加到 Dock 失败");
            OnNotice($"添加失败：{ex.Message}");
        }
    }

    /// <summary>右键：打开文件位置。</summary>
    [RelayCommand]
    public void OpenTileLocation(AppTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        try
        {
            _launcher.OpenInExplorer(tile.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开文件位置失败 {Path}", tile.Path);
            OnNotice($"打不开该位置：{ex.Message}");
        }
    }

    /// <summary>清空搜索词。</summary>
    [RelayCommand]
    public void ClearKeyword() => Keyword = string.Empty;

    partial void OnKeywordChanged(string value)
    {
        // 输入防抖 150ms：边打字边全量重排会明显卡顿。
        _debounce.Stop();
        _debounce.Start();
    }

    private async void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        try
        {
            await ApplyFilterAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "过滤应用列表失败");
        }
    }

    private async Task ApplyFilterInternalAsync()
    {
        try
        {
            await ApplyFilterAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刷新应用列表失败");
        }
    }

    private async Task ApplyFilterAsync()
    {
        var key = Keyword.Trim();
        var hiddenIds = new HashSet<string>(_config.Current.HiddenApps, StringComparer.OrdinalIgnoreCase);

        HiddenCount = _apps.Count(app => hiddenIds.Contains(app.Id));

        // 「已隐藏」页只列被藏起来的，其余页正好相反。
        var pool = _apps.Where(app => IsHiddenTab == hiddenIds.Contains(app.Id)).ToList();

        List<AppTileViewModel> tiles;

        if (IsFrequentTab && !IsHiddenTab)
        {
            // v1.0 常用页只按启动次数排序，时间衰减留给 v1.1（需求 M2 已注明）。
            tiles = pool
                .Select(app => CreateTile(app, hiddenIds))
                .OrderByDescending(tile => tile.UsageCount)
                .ThenBy(tile => tile.Name, StringComparer.OrdinalIgnoreCase)
                .Take(FrequentTileCount)
                .ToList();
        }
        else if (key.Length == 0)
        {
            tiles = pool
                .Select(app => CreateTile(app, hiddenIds))
                .OrderBy(tile => tile.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            tiles = pool
                .Select(app => (App: app, Score: PinyinMatcher.Score(app.Name, key, app.Path)))
                .Where(pair => pair.Score > PinyinMatcher.NoMatch)
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.App.Name, StringComparer.OrdinalIgnoreCase)
                .Select(pair => CreateTile(pair.App, hiddenIds))
                .ToList();
        }

        _visibleTiles.Clear();
        _visibleTiles.AddRange(tiles);
        RebuildRows();

        StatusText = IsHiddenTab
            ? $"已隐藏 {tiles.Count} 个 · 右键可恢复显示"
            : key.Length == 0
                ? $"{tiles.Count} 个应用"
                : $"{tiles.Count} 个匹配结果";

        await LoadIconsAsync(tiles);
    }

    private void RebuildRows()
    {
        Rows.Clear();

        var columns = Math.Max(1, Columns);
        for (var start = 0; start < _visibleTiles.Count; start += columns)
        {
            Rows.Add(new AppRowViewModel(_visibleTiles.GetRange(start, Math.Min(columns, _visibleTiles.Count - start))));
        }
    }

    private AppTileViewModel CreateTile(AppInfo app, HashSet<string> hiddenIds)
    {
        var tile = new AppTileViewModel(
            this,
            app.Id,
            app.Name,
            app.Path,
            app.IconPath ?? app.Path,
            _usage.GetCount(app.Id),
            hiddenIds.Contains(app.Id));

        if (_iconByPath.TryGetValue(app.IconPath ?? app.Path, out var cached))
        {
            tile.Icon = cached;
        }

        return tile;
    }

    /// <summary>
    /// 右键：把条目从启动台隐藏。只在配置里记一条 Id，不卸载、不动快捷方式，
    /// 随时可以在「已隐藏」页恢复。
    /// </summary>
    [RelayCommand]
    public async Task HideTileAsync(AppTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        var hidden = _config.Current.HiddenApps;
        if (!hidden.Contains(tile.Id, StringComparer.OrdinalIgnoreCase))
        {
            hidden.Add(tile.Id);
            await _config.SaveAsync();
        }

        await ApplyFilterAsync();
    }

    /// <summary>右键：把隐藏过的条目放回启动台。</summary>
    [RelayCommand]
    public async Task RestoreTileAsync(AppTileViewModel? tile)
    {
        if (tile is null)
        {
            return;
        }

        var hidden = _config.Current.HiddenApps;
        var removed = hidden.RemoveAll(id => string.Equals(id, tile.Id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            await _config.SaveAsync();
        }

        await ApplyFilterAsync();
    }

    // 图标逐个异步取：磁贴先出现、图标随后填上，避免首屏被 COM 解析堵住。
    private async Task LoadIconsAsync(IReadOnlyList<AppTileViewModel> tiles)
    {
        _iconLoadCts?.Cancel();
        _iconLoadCts?.Dispose();

        var cts = new CancellationTokenSource();
        _iconLoadCts = cts;

        foreach (var tile in tiles)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var key = tile.IconPath ?? tile.Path;
            if (_iconByPath.TryGetValue(key, out var known))
            {
                tile.Icon = known;
                continue;
            }

            try
            {
                var icon = await _icons.GetIconAsync(key, IconPixelSize);
                _iconByPath[key] = icon;
                tile.Icon = icon;
            }
            catch (Exception ex)
            {
                // 取不到也记一笔，否则每轮过滤都重跑一遍注定失败的 COM 解析。
                _iconByPath[key] = null;
                _logger.LogDebug(ex, "图标加载失败 {Path}", key);
            }
        }

        // 「有些图标显示不出来」只靠肉眼分不清是没取到还是没轮到取，这里把两种情况分开记下来。
        var missing = tiles.Where(tile => tile.Icon is null).ToList();
        if (missing.Count > 0)
        {
            _logger.LogDebug(
                "启动台图标：{Total} 个磁贴里 {Missing} 个没取到 → {Paths}",
                tiles.Count,
                missing.Count,
                string.Join("、", missing.Take(6).Select(tile => tile.IconPath ?? tile.Path)));
        }
    }

    private void OnNotice(string message, ToastKind kind = ToastKind.Error)
        => UserNotice?.Invoke(this, new NoticeEventArgs(message, kind));
}
