using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskMate.App.Services;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.ViewModels;

/// <summary>搜索结果里的一行。</summary>
public sealed partial class SearchHitViewModel : ObservableObject
{
    private readonly SearchViewModel _owner;

    /// <summary>构造结果项。</summary>
    public SearchHitViewModel(SearchViewModel owner, SearchResult result)
    {
        _owner = owner;
        Result = result;
        Title = result.Title;
        Subtitle = result.Subtitle;
        TypeLabel = result.Type switch
        {
            "app" => "应用",
            "folder" => "文件夹",
            _ => "文件",
        };
    }

    /// <summary>原始结果。</summary>
    public SearchResult Result { get; }

    /// <summary>主标题。</summary>
    public string Title { get; }

    /// <summary>副标题（路径）。</summary>
    public string Subtitle { get; }

    /// <summary>类型标签。</summary>
    public string TypeLabel { get; }

    /// <summary>图标来源路径。</summary>
    public string? IconPath => Result.IconPath;

    [ObservableProperty]
    private BitmapSource? _icon;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>右键：按回车同样的方式打开。</summary>
    [RelayCommand]
    public void Open() => _owner.Choose(this);

    /// <summary>右键：到资源管理器里选中它。</summary>
    [RelayCommand]
    public void Reveal() => _owner.Reveal(this);

    /// <summary>右键：复制完整路径。</summary>
    [RelayCommand]
    public void CopyPath() => _owner.CopyPathOf(Result.Path);
}

/// <summary>
/// 全局搜索视图模型：防抖搜索、上下键选择、回车启动。
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private const int DebounceMs = 150;
    private const int MaxResults = 20;
    private const int IconPixelSize = 32;

    private readonly ISearchService _search;
    private readonly IAppLauncherService _launcher;
    private readonly IIconService _icons;
    private readonly IUsageService _usage;
    private readonly ILogger<SearchViewModel> _logger;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _queryCts;

    /// <summary>构造搜索视图模型。</summary>
    public SearchViewModel(
        ISearchService search,
        IAppLauncherService launcher,
        IIconService icons,
        IUsageService usage,
        ILogger<SearchViewModel> logger)
    {
        _search = search;
        _launcher = launcher;
        _icons = icons;
        _usage = usage;
        _logger = logger;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMs) };
        _debounce.Tick += OnDebounceTick;
    }

    /// <summary>结果被启动后请求关闭窗口。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>零结果时用户点了「重新扫描应用」。扫描由协调器做，那里能同时刷新启动台的列表。</summary>
    public event EventHandler? RescanRequested;

    /// <summary>需要提示用户的失败信息。</summary>
    public event EventHandler<NoticeEventArgs>? UserNotice;

    /// <summary>当前结果。</summary>
    public ObservableCollection<SearchHitViewModel> Results { get; } = new();

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private string _hint = DefaultHint;

    /// <summary>输入框空着时的提示，也说清了「这里能搜什么」。底部一行还要留给按键说明，两边都得短。</summary>
    private const string DefaultHint = "应用 / 文件夹 / 桌面文件 / 目录内容，支持拼音";

    /// <summary>上一次真正查过的词，零结果时的两个兜底动作要用它。</summary>
    private string _lastQuery = string.Empty;

    /// <summary>查过一次且一条都没有：底部要给出路，不能只留一句「没有结果」。</summary>
    public bool HasNoResults => Results.Count == 0 && Hint.StartsWith("没有匹配", StringComparison.Ordinal);

    private int _selectedIndex;

    /// <summary>开始输入时重置窗口内容。</summary>
    public void PrepareForInput()
    {
        Keyword = string.Empty;
        _lastQuery = string.Empty;
        Results.Clear();
        Hint = DefaultHint;
    }

    partial void OnKeywordChanged(string value)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _ = RunQueryAsync();
    }

    /// <summary>执行一次搜索并异步补齐图标。</summary>
    public async Task RunQueryAsync()
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();

        var cts = new CancellationTokenSource();
        _queryCts = cts;

        var key = Keyword.Trim();
        if (key.Length == 0)
        {
            Results.Clear();
            _lastQuery = string.Empty;
            Hint = DefaultHint;
            IsSearching = false;
            return;
        }

        IsSearching = true;
        IReadOnlyList<SearchResult> found;
        try
        {
            found = await _search.SearchAsync(key, MaxResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搜索失败");
            Results.Clear();
            Hint = "搜索出错，详见日志";
            IsSearching = false;
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        Results.Clear();
        foreach (var result in found)
        {
            Results.Add(new SearchHitViewModel(this, result));
        }

        _lastQuery = key;
        _selectedIndex = 0;
        ApplySelection();
        Hint = found.Count == 0 ? $"没有匹配「{key}」的结果" : $"{found.Count} 个结果";
        IsSearching = false;

        await LoadIconsAsync(found, cts.Token);
    }

    /// <summary>↑↓ 移动选择。</summary>
    [RelayCommand]
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        _selectedIndex = (_selectedIndex + delta + Results.Count) % Results.Count;
        ApplySelection();
    }

    /// <summary>回车启动当前项。</summary>
    [RelayCommand]
    public void ChooseSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= Results.Count)
        {
            return;
        }

        Choose(Results[_selectedIndex]);
    }

    /// <summary>启动某一条结果。</summary>
    [RelayCommand]
    public void Choose(SearchHitViewModel? hit)
    {
        if (hit is null)
        {
            return;
        }

        try
        {
            if (hit.Result.Type == "folder")
            {
                _launcher.OpenInExplorer(hit.Result.Path);
            }
            else
            {
                _launcher.Launch(hit.Result.Path);
            }

            _usage.Record(hit.Result.Id.ToUpperInvariant());
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开搜索结果失败 {Path}", hit.Result.Path);
            UserNotice?.Invoke(this, new NoticeEventArgs($"打不开「{hit.Title}」：{ex.Message}"));
        }
    }

    /// <summary>Ctrl+Enter 或右键：不打开它，只是到资源管理器里选中它。</summary>
    public void Reveal(SearchHitViewModel hit)
    {
        try
        {
            _launcher.OpenInExplorer(hit.Result.Path);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "定位搜索结果失败 {Path}", hit.Result.Path);
            UserNotice?.Invoke(this, new NoticeEventArgs($"定位不到「{hit.Title}」：{ex.Message}"));
        }
    }

    /// <summary>右键：复制完整路径。</summary>
    public void CopyPathOf(string path)
    {
        try
        {
            System.Windows.Clipboard.SetText(path);
            UserNotice?.Invoke(this, new NoticeEventArgs($"路径已复制：{path}", ToastKind.Success));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "复制路径失败 {Path}", path);
            UserNotice?.Invoke(this, new NoticeEventArgs($"复制失败：{ex.Message}"));
        }
    }

    /// <summary>零结果时：让启动台和搜索的应用列表重新对齐现场（刚装的软件在这里补扫一次）。</summary>
    [RelayCommand]
    public void RequestRescan()
    {
        _lastQuery = Keyword.Trim();
        RescanRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 零结果时：把这个词交给系统搜索。DeskMate 只索引自己管得到的地方，
    /// 全盘内容检索本来就该用 Windows 索引，这里做的是接过去而不是自己再造一套。
    /// </summary>
    [RelayCommand]
    public void SearchWithSystem()
    {
        var query = (_lastQuery.Length == 0 ? Keyword : _lastQuery).Trim();
        if (query.Length == 0)
        {
            return;
        }

        try
        {
            _launcher.OpenUri($"search-ms:query={Uri.EscapeDataString(query)}");
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "调用系统搜索失败");
            UserNotice?.Invoke(this, new NoticeEventArgs($"打不开系统搜索：{ex.Message}"));
        }
    }

    /// <summary>Ctrl+Enter：不打开它，只是到资源管理器里选中当前项。</summary>
    [RelayCommand]
    public void RevealSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= Results.Count)
        {
            return;
        }

        Reveal(Results[_selectedIndex]);
    }

    /// <summary>鼠标悬停时把键盘焦点同步到该条结果。</summary>
    public void SelectHit(SearchHitViewModel hit)
    {
        var index = Results.IndexOf(hit);
        if (index >= 0 && index != _selectedIndex)
        {
            _selectedIndex = index;
            ApplySelection();
        }
    }

    private void ApplySelection()
    {
        for (var index = 0; index < Results.Count; index++)
        {
            Results[index].IsSelected = index == _selectedIndex;
        }
    }

    private async Task LoadIconsAsync(IReadOnlyList<SearchResult> found, CancellationToken token)
    {
        for (var index = 0; index < found.Count && index < Results.Count; index++)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var path = found[index].IconPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                Results[index].Icon = await _icons.GetIconAsync(path, IconPixelSize);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "搜索结果图标加载失败 {Path}", path);
            }
        }
    }
}
