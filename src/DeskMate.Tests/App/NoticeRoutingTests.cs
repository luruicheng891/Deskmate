using System.Windows.Media.Imaging;
using DeskMate.App.Services;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 提示与刷新这条链路的回归测试。
/// <para>
/// 钉住两件以前缺的东西：批量操作只报一条结果（原先每项弹一个模态框），
/// 以及应用列表要能发现「一装一卸、总数没变」的变化（原先只比数量，这种情况永远不采纳新结果）。
/// </para>
/// </summary>
public sealed class NoticeRoutingTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task AddPathsAsync_ReportsOneWarningThatCountsTheSkippedItem()
    {
        var directory = Directory("资料");
        var file = File("随手拍.png");
        var (viewModel, notices, folders) = FolderManager();

        await viewModel.AddPathsAsync([directory, file]);

        var notice = Assert.Single(notices);
        Assert.Equal(ToastKind.Warning, notice.Kind);
        Assert.Contains("1", notice.Message);
        Assert.Contains("没加上", notice.Message);
        Assert.Single(folders.GetAll());
    }

    [Fact]
    public async Task AddPathsAsync_WhenNothingCanBeAdded_ReportsErrorOnce()
    {
        var file = File("notes.txt");
        var (viewModel, notices, _) = FolderManager();

        await viewModel.AddPathsAsync([file]);

        var notice = Assert.Single(notices);
        Assert.Equal(ToastKind.Error, notice.Kind);
        Assert.Contains("一个都没加上", notice.Message);
        Assert.Empty(viewModel.Cards);
    }

    [Fact]
    public async Task AddPathAsync_SingleFolder_ReportsSuccess()
    {
        var directory = Directory("课程");
        var (viewModel, notices, _) = FolderManager();

        await viewModel.AddPathAsync(directory);

        var notice = Assert.Single(notices);
        Assert.Equal(ToastKind.Success, notice.Kind);
        Assert.Contains("已加入管理", notice.Message);
    }

    [Fact]
    public async Task RefreshAsync_AdoptsSwapWithSameCount()
    {
        var scanner = new SwappableScanner
        {
            Current = Apps(("记事本", @"C:\a\notepad.exe"), ("旧版浏览器", @"C:\b\old.exe")),
        };
        var launcher = Launcher(scanner);

        await launcher.LoadAsync();
        Assert.Contains(Tiles(launcher), tile => tile.Name == "旧版浏览器");

        // 一装一卸：总数没变，只有 Id 变了。按数量判断的旧写法在这种情况下永远不采纳新列表。
        scanner.Current = Apps(("记事本", @"C:\a\notepad.exe"), ("新版浏览器", @"C:\b\new.exe"));

        await launcher.RefreshAsync(report: true);

        var tiles = Tiles(launcher).ToList();
        Assert.Contains(tiles, tile => tile.Name == "新版浏览器");
        Assert.DoesNotContain(tiles, tile => tile.Name == "旧版浏览器");
    }

    [Fact]
    public async Task RefreshAsync_WithoutReport_StillAlignsButStaysQuiet()
    {
        var scanner = new SwappableScanner { Current = Apps(("记事本", @"C:\a\notepad.exe")) };
        var launcher = Launcher(scanner);
        var notices = new List<NoticeEventArgs>();
        launcher.UserNotice += (_, args) => notices.Add(args);

        await launcher.LoadAsync();
        scanner.Current = Apps(("计算器", @"C:\a\calc.exe"));

        await launcher.RefreshAsync(report: false);

        Assert.Empty(notices);
        Assert.Contains(Tiles(launcher), tile => tile.Name == "计算器");
    }

    [Fact]
    public void CapturedHotkey_LandsInTheSlotThatWasBeingCaptured()
    {
        var config = Config();
        var settings = Settings(config);

        settings.BeginHotkeyCapture(SettingsViewModel.LauncherSlot);
        Assert.True(settings.IsCapturingLauncher);
        Assert.False(settings.IsCapturingSearch);

        settings.ApplyCapturedHotkey("Ctrl+Alt+L");

        Assert.Equal("Ctrl+Alt+L", config.Current.LauncherHotkey);
        Assert.Equal("Ctrl+Alt+L", settings.LauncherHotkeyDisplay);
        Assert.Equal("Alt+Space", config.Current.Hotkey);
        Assert.False(settings.IsCapturingHotkey);
    }

    [Fact]
    public void ClearHotkeySlot_EmptiesOnlyThatSlotAndReportsIt()
    {
        var config = Config();
        config.Current.FolderHotkey = "Ctrl+Alt+F";
        var settings = Settings(config);
        settings.LoadFromConfig();
        var notices = new List<NoticeEventArgs>();
        settings.UserNotice += (_, args) => notices.Add(args);

        settings.ClearHotkeySlot(SettingsViewModel.FolderSlot);

        Assert.Equal(string.Empty, config.Current.FolderHotkey);
        Assert.Equal("Alt+Space", config.Current.Hotkey);
        Assert.Equal("未绑定 · 点一下再按下组合键", settings.FolderHotkeyDisplay);
        var notice = Assert.Single(notices);
        Assert.Contains("文件夹面板", notice.Message);
    }

    [Fact]
    public async Task ZeroResults_OpensTwoWaysOut_AndRescanGoesThroughTheShell()
    {
        var search = new SearchViewModel(
            new StubSearch([]),
            new NullLauncher(),
            new NullIconService(),
            new EmptyUsage(),
            NullLogger<SearchViewModel>.Instance);

        var rescanRequested = false;
        search.RescanRequested += (_, _) => rescanRequested = true;

        search.Keyword = "没见过的东西";
        await search.RunQueryAsync();

        Assert.Contains("没有匹配", search.Hint);
        Assert.True(search.HasNoResults);

        search.RequestRescanCommand.Execute(null);
        Assert.True(rescanRequested);
    }

    [Fact]
    public async Task WithResults_FallbackActionsStayHidden()
    {
        var hits = new[] { new SearchResult { Type = "app", Id = "A", Title = "记事本", Path = @"C:\a\notepad.exe" } };
        var search = new SearchViewModel(
            new StubSearch(hits),
            new NullLauncher(),
            new NullIconService(),
            new EmptyUsage(),
            NullLogger<SearchViewModel>.Instance);

        search.Keyword = "记事本";
        await search.RunQueryAsync();

        Assert.False(search.HasNoResults);
        Assert.Equal("1 个结果", search.Hint);
    }

    [Fact]
    public async Task DockRelocate_ClearsTheInvalidFlagAndPointsAtTheNewTarget()
    {
        var config = Config();
        var missing = System.IO.Path.Combine(_temp.Path, "gone.exe");
        config.Current.DockItems.Add(new DockItem { Id = "d1", DisplayName = "gone", TargetPath = missing });

        var dock = Dock(config);
        await dock.LoadAsync();

        // 目标不存在时必须标出来：一个「看起来完全正常」的图标每次点都报错，用户只会认为软件坏了。
        Assert.False(dock.Items.Single(item => item.Id == "d1").IsValid);

        await dock.RelocateItemAsync("d1", File("newtool.exe"));

        var relocated = dock.Items.Single(item => item.Id == "d1");
        Assert.True(relocated.IsValid);

        // 名字还是自动取的文件名时跟着换。
        Assert.Equal("newtool", relocated.DisplayName);
        Assert.Equal("newtool", config.Current.DockItems.Single().DisplayName);
    }

    [Fact]
    public async Task DockRename_OnlyChangesTheDisplayName()
    {
        var config = Config();
        var exe = File("tool.exe");
        config.Current.DockItems.Add(new DockItem { Id = "d1", DisplayName = "tool", TargetPath = exe });

        var dock = Dock(config);
        await dock.LoadAsync();

        await dock.RenameItemAsync("d1", "  我的工具  ");

        Assert.Equal("我的工具", config.Current.DockItems.Single().DisplayName);
        Assert.Equal("我的工具", dock.Items.Single(item => item.Id == "d1").DisplayName);

        // 改名不能顺手把启动目标也动了。
        Assert.Equal(exe, config.Current.DockItems.Single().TargetPath);
    }

    [Fact]
    public async Task DockRename_BlankNameIsRejectedAndKeepsTheOldOne()
    {
        var config = Config();
        config.Current.DockItems.Add(new DockItem { Id = "d1", DisplayName = "tool", TargetPath = File("tool.exe") });

        var dock = Dock(config);
        await dock.LoadAsync();

        var notices = new List<NoticeEventArgs>();
        dock.UserNotice += (_, args) => notices.Add(args);

        await dock.RenameItemAsync("d1", "   ");

        var notice = Assert.Single(notices);
        Assert.Equal(ToastKind.Warning, notice.Kind);
        Assert.Equal("tool", config.Current.DockItems.Single().DisplayName);
    }

    [Fact]
    public async Task DockCustomIcon_KeepsTheLaunchTargetAndCanBeRestored()
    {
        var config = Config();
        var exe = File("tool.exe");
        config.Current.DockItems.Add(new DockItem { Id = "d1", DisplayName = "tool", TargetPath = exe });

        var dock = Dock(config);
        await dock.LoadAsync();

        await dock.SetIconAsync("d1", File("other.dll"));

        Assert.True(dock.Items.Single(item => item.Id == "d1").HasIconOverride);
        Assert.EndsWith("other.dll", config.Current.DockItems.Single().IconOverride);
        Assert.Equal(exe, config.Current.DockItems.Single().TargetPath);

        await dock.RestoreIconAsync("d1");

        Assert.Equal(string.Empty, config.Current.DockItems.Single().IconOverride);
        Assert.False(dock.Items.Single(item => item.Id == "d1").HasIconOverride);
    }

    [Fact]
    public async Task DockCustomIcon_RejectsAMissingSource()
    {
        var config = Config();
        config.Current.DockItems.Add(new DockItem { Id = "d1", DisplayName = "tool", TargetPath = File("tool.exe") });

        var dock = Dock(config);
        await dock.LoadAsync();

        var notices = new List<NoticeEventArgs>();
        dock.UserNotice += (_, args) => notices.Add(args);

        await dock.SetIconAsync("d1", System.IO.Path.Combine(_temp.Path, "nope.ico"));

        Assert.Single(notices);
        Assert.Equal(string.Empty, config.Current.DockItems.Single().IconOverride);
    }

    [Fact]
    public async Task LaunchingAnInvalidDockItem_ExplainsAndOffersRelocateInsteadOfThrowing()
    {
        var config = Config();
        config.Current.DockItems.Add(new DockItem
        {
            Id = "d1",
            DisplayName = "gone",
            TargetPath = System.IO.Path.Combine(_temp.Path, "gone.exe"),
        });

        var dock = Dock(config);
        await dock.LoadAsync();

        var notices = new List<NoticeEventArgs>();
        dock.UserNotice += (_, args) => notices.Add(args);

        await dock.LaunchItemAsync("d1");

        var notice = Assert.Single(notices);
        Assert.Equal(ToastKind.Warning, notice.Kind);
        Assert.Contains("重新选择目标", notice.Message);
    }

    [Fact]
    public async Task GroupActionOnAMultiSelection_AppliesToEverySelectedCard()
    {
        var (viewModel, _, folders) = FolderManager();
        await viewModel.AddPathsAsync([Directory("高数"), Directory("线性代数"), Directory("英语")]);
        var group = await folders.AddGroupAsync("课程资料");

        var cards = viewModel.Cards.ToList();
        viewModel.HandleCardClick(cards[0], ctrl: false, shift: false);
        viewModel.HandleCardClick(cards[1], ctrl: true, shift: false);
        Assert.Equal(2, viewModel.SelectedCount);

        // 八个文件夹归一个组要右键八次，是这套面板最劝退的地方：选中后一次就该全进去。
        await cards[0].ApplyGroupAsync(new GroupTab(group.Id, "课程资料"));

        Assert.Equal(2, folders.GetAll().Count(item => item.GroupId == group.Id));
        Assert.Equal(1, folders.GetAll().Count(item => item.GroupId is null));
    }

    [Fact]
    public async Task DeleteSelection_AskedBeforeRemovingMoreThanOne()
    {
        var (viewModel, _, folders) = FolderManager();
        await viewModel.AddPathsAsync([Directory("甲"), Directory("乙")]);

        int? asked = null;
        viewModel.BatchDeleteRequested += (_, count) => asked = count;

        viewModel.SelectAllCardsCommand.Execute(null);
        Assert.Equal(2, viewModel.SelectedCount);

        await viewModel.DeleteSelectionCommand.ExecuteAsync(null);

        // 一次点掉两条引用不该是无声的：先问，问到答案之前一条都不能少。
        Assert.Equal(2, asked);
        Assert.Equal(2, folders.GetAll().Count);

        await viewModel.RemoveSelectedAsync();
        Assert.Empty(folders.GetAll());
        Assert.Equal(0, viewModel.SelectedCount);
    }

    [Fact]
    public async Task DraggingOutCarriesEverySelectedCard()
    {
        var (viewModel, _, _) = FolderManager();
        await viewModel.AddPathsAsync([Directory("甲"), Directory("乙"), Directory("丙")]);

        var cards = viewModel.Cards.ToList();
        viewModel.HandleCardClick(cards[0], ctrl: false, shift: false);
        Assert.Single(viewModel.DragPathsFor(cards[0]));

        viewModel.HandleCardClick(cards[1], ctrl: true, shift: false);
        var dragged = viewModel.DragPathsFor(cards[0]);

        // 多选之后拖出去要一次带走全部选中的，而不是只有按住的那一张。
        Assert.Equal(2, dragged.Count);
        Assert.Contains(dragged, path => path.EndsWith("甲", StringComparison.Ordinal));
        Assert.Contains(dragged, path => path.EndsWith("乙", StringComparison.Ordinal));
    }

    public void Dispose() => _temp.Dispose();

    private string Directory(string name)
    {
        var path = System.IO.Path.Combine(_temp.Path, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private string File(string name)
    {
        var path = System.IO.Path.Combine(_temp.Path, name);
        System.IO.File.WriteAllText(path, "x");
        return path;
    }

    private ConfigService Config() => new(NullLogger<ConfigService>.Instance, _temp.File("config.json"));

    private SettingsViewModel Settings(IConfigService config) => new(
        config,
        new FakeAutoStart(),
        new SwappableScanner(),
        new NullLauncher(),
        new EmptyUsage(),
        NullLogger<SettingsViewModel>.Instance);

    private (FolderManagerViewModel ViewModel, List<NoticeEventArgs> Notices, FolderService Folders) FolderManager()
    {
        var folders = new FolderService(Config(), NullLogger<FolderService>.Instance);
        var viewModel = new FolderManagerViewModel(
            folders, new NullLauncher(), new FakeDesktopFileService(), new FakeFileOperations(), NullLogger<FolderManagerViewModel>.Instance);

        var notices = new List<NoticeEventArgs>();
        viewModel.UserNotice += (_, args) => notices.Add(args);

        return (viewModel, notices, folders);
    }

    private LauncherViewModel Launcher(IAppScannerService scanner)
    {
        // 启动台和 Dock 必须共用同一份配置实例，否则一边写的另一边看不到。
        var config = Config();

        return new LauncherViewModel(
            scanner,
            new NullIconService(),
            new NullLauncher(),
            new EmptyUsage(),
            config,
            Dock(config),
            NullLogger<LauncherViewModel>.Instance);
    }

    private static DockViewModel Dock(IConfigService config) => new(
        config,
        new NullIconService(),
        new NullLauncher(),
        new NoProcessesProbe(),
        new NoWindows(),
        new EmptyUsage(),
        NullLogger<DockViewModel>.Instance);

    private static IEnumerable<AppTileViewModel> Tiles(LauncherViewModel launcher)
        => launcher.Rows.SelectMany(row => row.Tiles);

    private static IReadOnlyList<AppInfo> Apps(params (string Name, string Path)[] entries)
        => entries
            .Select(entry => new AppInfo
            {
                Id = entry.Path.ToUpperInvariant(),
                Name = entry.Name,
                Path = entry.Path,
                IconPath = entry.Path,
                Source = "StartMenu",
            })
            .ToList();

    private sealed class StubSearch(IReadOnlyList<SearchResult> hits) : ISearchService
    {
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string keyword, int maxResults = 20)
            => Task.FromResult(hits);
    }

    private sealed class SwappableScanner : IAppScannerService
    {
        public IReadOnlyList<AppInfo> Current { get; set; } = [];

        public Task<IReadOnlyList<AppInfo>> ScanInstalledAppsAsync(bool forceRefresh = false) => Task.FromResult(Current);

        public Task<IReadOnlyList<AppInfo>> RefreshAsync() => Task.FromResult(Current);

        public Task<IReadOnlyList<AppInfo>> SearchAppsAsync(string keyword) => Task.FromResult(Current);

        public Task<string> AddToLaunchpadAsync(string sourcePath) => Task.FromResult(sourcePath);
    }

    private sealed class FakeAutoStart : IAutoStartService
    {
        public bool IsEnabled() => false;

        public void SetEnabled(bool enabled)
        {
        }
    }

    private sealed class NullLauncher : IAppLauncherService
    {
        public void Launch(string path)
        {
        }

        public void OpenInExplorer(string path)
        {
        }

        public void OpenUri(string uri)
        {
        }

        /// <summary>这些替身不关心关联查询，一律回答「有」，让「双击交给系统」那条主路照旧走通。</summary>
        public bool HasDefaultProgram(string path) => true;
    }

    private sealed class NullIconService : IIconService
    {
        public Task<BitmapSource?> GetIconAsync(string path, int size = 64) => Task.FromResult<BitmapSource?>(null);

        public void ClearCache()
        {
        }
    }

    private sealed class NoProcessesProbe : IProcessProbeService
    {
        public Task<string> ResolveExecutablePathAsync(string path) => Task.FromResult(path);

        public Task<IReadOnlySet<string>> SnapshotRunningNamesAsync()
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<RunningApp>> SnapshotRunningAppsAsync() => Task.FromResult<IReadOnlyList<RunningApp>>([]);

        public bool IsRunning(string executablePath, IReadOnlySet<string> runningNames) => false;
    }

    private sealed class NoWindows : IWindowService
    {
        public Task<IReadOnlyList<AppWindowInfo>> GetWindowsAsync(string executablePath)
            => Task.FromResult<IReadOnlyList<AppWindowInfo>>([]);

        public AppWindowInfo? PickClickTarget(IReadOnlyList<AppWindowInfo> windows, nint foregroundHandle)
            => windows.Count == 0 ? null : windows[0];

        public Task<bool> SwitchToAsync(string executablePath) => Task.FromResult(false);

        public bool Activate(AppWindowInfo window) => false;

        public bool Close(AppWindowInfo window) => false;

        public Task<int> CloseAllAsync(string executablePath) => Task.FromResult(0);
    }

    private sealed class EmptyUsage : IUsageService
    {
        public void Record(string id)
        {
        }

        public int GetCount(string id) => 0;

        public Task LoadAsync() => Task.CompletedTask;

        public int Reset() => 0;
    }
}
