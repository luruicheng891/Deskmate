using System.Windows.Media.Imaging;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 启动台磁贴的命令链路的回归测试。
/// 用户报的「点击添加到 Dock 不成功」根因是右键菜单绑定够不到 VM，
/// 这里从磁贴自身命令出发验证整条链路真的会写配置。
/// </summary>
public sealed class LauncherTileCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly RecordingLauncherService _launcherService = new();
    private readonly RecordingUsage _usage = new();
    private readonly FakeScanner _scanner = new();
    private readonly LauncherViewModel _launcher;

    public LauncherTileCommandTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));

        var dock = new DockViewModel(
            _config,
            new NullIconService(),
            _launcherService,
            new FakeProbe(),
            new FakeWindows(),
            _usage,
            NullLogger<DockViewModel>.Instance);

        _launcher = new LauncherViewModel(
            _scanner,
            new NullIconService(),
            _launcherService,
            _usage,
            _config,
            dock,
            NullLogger<LauncherViewModel>.Instance);
    }

    [Fact]
    public async Task AddToDockCommand_WritesLaunchableItemIntoConfig()
    {
        var tile = await FirstTileAsync();

        await tile.AddToDockCommand.ExecuteAsync(null);

        var dockItem = Assert.Single(_config.Current.DockItems);
        Assert.Equal(tile.Path, dockItem.TargetPath);
        Assert.True(dockItem.IsPinned);
    }

    [Fact]
    public async Task HideCommand_Then_RestoreCommand_RoundTripsHiddenList()
    {
        var tile = await FirstTileAsync();

        await tile.HideCommand.ExecuteAsync(null);
        Assert.Contains(tile.Id, _config.Current.HiddenApps);
        Assert.DoesNotContain(tile, VisibleTiles());

        await tile.RestoreCommand.ExecuteAsync(null);
        Assert.Empty(_config.Current.HiddenApps);
    }

    [Fact]
    public async Task OpenCommand_LaunchesTilePath()
    {
        var tile = await FirstTileAsync();

        tile.OpenCommand.Execute(null);

        Assert.Equal(tile.Path, Assert.Single(_launcherService.Launched));
    }

    [Fact]
    public async Task NoiseFilteredApps_NeverReachTheLauncher()
    {
        // 扫描器已经过滤过一层；这里确认 VM 不会自己塞回噪声条目。
        var tiles = await Task.Run(async () =>
        {
            await _launcher.LoadAsync();
            return VisibleTiles().ToList();
        });

        Assert.All(tiles, tile => Assert.DoesNotContain("语言首选项", tile.Name));
    }

    [Fact]
    public async Task HideThenReload_StaysHidden_AndShowsUpOnHiddenTab()
    {
        var tile = await FirstTileAsync();

        await tile.HideCommand.ExecuteAsync(null);
        await _launcher.LoadAsync();

        Assert.DoesNotContain(tile.Id, VisibleTiles().Select(t => t.Id));

        await _launcher.ShowHiddenTabCommand.ExecuteAsync(null);
        Assert.Contains(tile.Id, VisibleTiles().Select(t => t.Id));
    }

    private async Task<AppTileViewModel> FirstTileAsync()
    {
        await _launcher.LoadAsync();
        return VisibleTiles().First();
    }

    /// <summary>
    /// 「添加应用」之后必须当场看得见结果：还停在「常用」页（新应用使用次数为 0 排不进来），
    /// 或者搜索框里留着刚才找应用时输入的词，都会让刚加的磁贴不出现，用户只能判定成「添加失败」。
    /// </summary>
    [Fact]
    public async Task AddAppFromPathAsync_ResetsSearchAndTab_SoTheNewTileIsVisible()
    {
        _launcher.Keyword = "trae";
        _launcher.IsAllTab = false;
        _launcher.IsFrequentTab = true;

        await _launcher.AddAppFromPathAsync(@"E:\Trae-work\TRAE SOLO CN\TRAE SOLO CN.exe");

        Assert.Equal(string.Empty, _launcher.Keyword);
        Assert.True(_launcher.IsAllTab);
        Assert.False(_launcher.IsFrequentTab);
        Assert.Contains(VisibleTiles(), tile => tile.Name == "TRAE SOLO CN");
        Assert.Contains("已添加", _launcher.StatusText);
    }

    private IEnumerable<AppTileViewModel> VisibleTiles() => _launcher.Rows.SelectMany(row => row.Tiles);

    public void Dispose() => _temp.Dispose();

    private sealed class FakeScanner : IAppScannerService
    {
        private static readonly AppInfo[] Apps =
        [
            new() { Id = @"C:\WINDOWS\SYSTEM32\NOTEPAD.EXE", Name = "记事本", Path = @"C:\Windows\System32\notepad.exe", IconPath = @"C:\Windows\System32\notepad.exe", Source = "StartMenu" },
            new() { Id = @"C:\CHROME\CHROME.EXE", Name = "Google Chrome", Path = @"C:\chrome\chrome.exe", IconPath = @"C:\chrome\chrome.exe", Source = "StartMenu" },
        ];

        /// <summary>模拟「添加应用」成功后：链接生成 + 下一次扫描能看见它。</summary>
        public string LastLink { get; private set; } = string.Empty;

        private readonly List<AppInfo> _added = new();

        private IReadOnlyList<AppInfo> Current => _added.Count == 0 ? Apps : [.. Apps, .. _added];

        public Task<IReadOnlyList<AppInfo>> ScanInstalledAppsAsync(bool forceRefresh = false) => Task.FromResult(Current);

        public Task<IReadOnlyList<AppInfo>> RefreshAsync() => Task.FromResult(Current);

        public Task<IReadOnlyList<AppInfo>> SearchAppsAsync(string keyword) => Task.FromResult(Current);

        public Task<string> AddToLaunchpadAsync(string sourcePath)
        {
            LastLink = @"C:\Users\me\AppData\Roaming\DeskMate\Launchpad\TRAE SOLO CN.lnk";
            _added.Add(new AppInfo
            {
                Id = sourcePath.ToUpperInvariant(),
                Name = "TRAE SOLO CN",
                Path = LastLink,
                IconPath = LastLink,
                Source = "Launchpad",
            });

            return Task.FromResult(LastLink);
        }
    }

    private sealed class NullIconService : IIconService
    {
        public Task<BitmapSource?> GetIconAsync(string path, int size = 64) => Task.FromResult<BitmapSource?>(null);

        public void ClearCache()
        {
        }
    }

    private sealed class FakeProbe : IProcessProbeService
    {
        public Task<string> ResolveExecutablePathAsync(string path) => Task.FromResult(path);

        public Task<IReadOnlySet<string>> SnapshotRunningNamesAsync() => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task<IReadOnlyList<RunningApp>> SnapshotRunningAppsAsync() => Task.FromResult<IReadOnlyList<RunningApp>>([]);

        public bool IsRunning(string executablePath, IReadOnlySet<string> runningNames) => false;
    }

    private sealed class RecordingLauncherService : IAppLauncherService
    {
        public List<string> Launched { get; } = new();

        public void Launch(string path) => Launched.Add(path);

        public void OpenInExplorer(string path)
        {
        }

        public void OpenUri(string uri)
        {
        }

        /// <summary>这些替身不关心关联查询，一律回答「有」，让「双击交给系统」那条主路照旧走通。</summary>
        public bool HasDefaultProgram(string path) => true;
    }

    private sealed class RecordingUsage : IUsageService
    {
        public void Record(string id)
        {
        }

        public int GetCount(string id) => 0;

        public Task LoadAsync() => Task.CompletedTask;

        public int Reset() => 0;
    }

    // 一律报告「没有窗口」，于是 Dock 的点击语义就落到「启动新实例」这条分支上。
    private sealed class FakeWindows : IWindowService
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
}
