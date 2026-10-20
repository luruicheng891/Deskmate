using System.Windows.Media.Imaging;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// Dock 运行区的规则：开着窗口的应用自动排在固定项之前，关掉就消失，已固定的不重复，
/// 并且这些临时项一个都不许写进 config.json。
/// </summary>
public sealed class DockRunningSectionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly FakeProbe _probe = new();
    private readonly FakeWindows _windows = new();
    private readonly DockViewModel _dock;

    public DockRunningSectionTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _config.Current.DockItems.Add(new DockItem { DisplayName = "记事本", TargetPath = @"C:\Windows\notepad.exe", Order = 0, IsPinned = true });
        _config.Current.DockItems.Add(new DockItem { DisplayName = "代码", TargetPath = @"C:\VS\Code.exe", Order = 1, IsPinned = true });

        _dock = new DockViewModel(
            _config,
            new FakeIcons(),
            new FakeLauncher(),
            _probe,
            _windows,
            new FakeUsage(),
            NullLogger<DockViewModel>.Instance);
    }

    [Fact]
    public async Task RunningApps_SitBeforePinnedOnes_AndNeverReachConfig()
    {
        _probe.Apps.Add(new RunningApp(@"C:\Tools\Alpha\Alpha.exe", "Alpha", new DateTime(2026, 1, 1)));
        _probe.Apps.Add(new RunningApp(@"C:\Tools\Beta\Beta.exe", "Beta", new DateTime(2026, 1, 2)));

        await _dock.LoadAsync();

        Assert.Equal(
            new[] { "Alpha", "Beta", "记事本", "代码" },
            _dock.Items.Select(item => item.DisplayName).ToArray());
        Assert.Equal(2, _config.Current.DockItems.Count);
    }

    [Fact]
    public async Task AlreadyPinnedApp_IsNotDuplicated()
    {
        _probe.Apps.Add(new RunningApp(@"C:\Windows\notepad.exe", "notepad", new DateTime(2026, 1, 1)));

        await _dock.LoadAsync();

        Assert.Equal(2, _dock.Items.Count);
        Assert.Single(_dock.Items, item => item.DisplayName == "记事本");
        Assert.True(_dock.Items[0].IsRunning);
    }

    [Fact]
    public async Task CloseCommand_TellsTheUserHowManyWindowsItClosed()
    {
        _probe.Apps.Add(new RunningApp(@"C:\Tools\Alpha\Alpha.exe", "Alpha", new DateTime(2026, 1, 1)));
        await _dock.LoadAsync();

        var item = _dock.Items.Single(entry => entry.DisplayName == "Alpha");
        Assert.Equal(System.Windows.Visibility.Visible, item.CloseVisibility);

        string? notice = null;
        _dock.UserNotice += (_, args) => notice = args.Message;

        // 只回一句「已关闭」等于没说：几个窗口被关掉用户是不知道的，误判成"没反应"会连点。
        _windows.WindowsPerClose = 2;
        await item.CloseCommand.ExecuteAsync(null);

        Assert.Equal([@"C:\Tools\Alpha\Alpha.exe"], _windows.ClosedPaths);
        Assert.Contains("2 个窗口", notice);
    }

    [Fact]
    public async Task CloseCommand_ExplainsWhyNothingHappened_ForTrayOnlyApps()
    {
        _probe.Apps.Add(new RunningApp(@"C:\Tools\Alpha\Alpha.exe", "Alpha", new DateTime(2026, 1, 1)));
        await _dock.LoadAsync();

        var item = _dock.Items.Single(entry => entry.DisplayName == "Alpha");
        string? notice = null;
        _dock.UserNotice += (_, args) => notice = args.Message;

        // 一个窗口都关不掉（缩在托盘里）时不能沉默，否则用户只会认为右键菜单坏了。
        _windows.WindowsPerClose = 0;
        await item.CloseCommand.ExecuteAsync(null);

        Assert.Contains("没有可关闭的窗口", notice);
    }

    [Fact]
    public async Task ClosedApp_DisappearsOnTheNextRefresh()
    {
        _probe.Apps.Add(new RunningApp(@"C:\Tools\Alpha\Alpha.exe", "Alpha", new DateTime(2026, 1, 1)));
        await _dock.LoadAsync();
        Assert.Contains(_dock.Items, item => item.DisplayName == "Alpha");

        _probe.Apps.Clear();
        await _dock.RefreshRunningStateAsync();

        Assert.Equal(new[] { "记事本", "代码" }, _dock.Items.Select(item => item.DisplayName).ToArray());
    }

    [Fact]
    public async Task Separator_LandsOnTheFirstPinnedItem()
    {
        _probe.Apps.Add(new RunningApp(@"C:\Tools\Alpha\Alpha.exe", "Alpha", new DateTime(2026, 1, 1)));

        await _dock.LoadAsync();

        Assert.False(_dock.Items[0].ShowSeparator);
        Assert.True(_dock.Items[1].ShowSeparator);
        Assert.Equal("记事本", _dock.Items[1].DisplayName);
    }

    [Fact]
    public async Task NoRunningApps_LeavesNoSeparator()
    {
        await _dock.LoadAsync();

        Assert.All(_dock.Items, item => Assert.False(item.ShowSeparator));
    }

    [Fact]
    public async Task PinItem_MovesItIntoThePinnedSectionWithoutDuplicating()
    {
        var app = new RunningApp(@"C:\Tools\Alpha\Alpha.exe", "Alpha", new DateTime(2026, 1, 1));
        _probe.Apps.Add(app);
        await _dock.LoadAsync();

        await _dock.PinItemAsync($"running:{app.ExecutablePath}");

        Assert.Contains(_config.Current.DockItems, item => item.TargetPath == app.ExecutablePath);
        Assert.Single(_dock.Items, item => item.DisplayName == "Alpha");
        Assert.True(_dock.Items[^1].IsPinned);
    }

    [Fact]
    public async Task Reorder_IgnoresRunningIdsAndKeepsThemInTheFront()
    {
        _probe.Apps.Add(new RunningApp(@"C:\Tools\Alpha\Alpha.exe", "Alpha", new DateTime(2026, 1, 1)));
        await _dock.LoadAsync();

        // 用户把「代码」拖到「记事本」前面；列表里还夹着运行区的 Alpha。
        await _dock.ReorderAsync(["running:C:\\Tools\\Alpha\\Alpha.exe", _config.Current.DockItems[1].Id, _config.Current.DockItems[0].Id]);

        Assert.Equal(
            new[] { "Alpha", "代码", "记事本" },
            _dock.Items.Select(item => item.DisplayName).ToArray());
        Assert.Equal(
            new[] { @"C:\VS\Code.exe", @"C:\Windows\notepad.exe" },
            _config.Current.DockItems.OrderBy(item => item.Order).Select(item => item.TargetPath).ToArray());
    }

    public void Dispose() => _temp.Dispose();

    private sealed class FakeProbe : IProcessProbeService
    {
        public List<RunningApp> Apps { get; } = new();

        public Task<string> ResolveExecutablePathAsync(string path) => Task.FromResult(path);

        public Task<IReadOnlySet<string>> SnapshotRunningNamesAsync() => Task.FromResult<IReadOnlySet<string>>(
            Apps.Select(app => Path.GetFileNameWithoutExtension(app.ExecutablePath).ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase));

        public bool IsRunning(string executablePath, IReadOnlySet<string> runningNames)
            => runningNames.Contains(Path.GetFileNameWithoutExtension(executablePath));

        public Task<IReadOnlyList<RunningApp>> SnapshotRunningAppsAsync() => Task.FromResult<IReadOnlyList<RunningApp>>(Apps.ToList());
    }

    private sealed class FakeIcons : IIconService
    {
        public Task<BitmapSource?> GetIconAsync(string path, int size = 64) => Task.FromResult<BitmapSource?>(null);

        public void ClearCache()
        {
        }
    }

    private sealed class FakeLauncher : IAppLauncherService
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

    private sealed class FakeWindows : IWindowService
    {
        public Task<IReadOnlyList<AppWindowInfo>> GetWindowsAsync(string executablePath)
            => Task.FromResult<IReadOnlyList<AppWindowInfo>>([]);

        public AppWindowInfo? PickClickTarget(IReadOnlyList<AppWindowInfo> windows, nint foregroundHandle) => null;

        public Task<bool> SwitchToAsync(string executablePath) => Task.FromResult(false);

        public bool Activate(AppWindowInfo window) => false;

        public bool Close(AppWindowInfo window) => true;

        /// <summary>记录被「关闭窗口」问过哪个应用，并返回预设的窗口数。</summary>
        public List<string> ClosedPaths { get; } = [];

        public int WindowsPerClose { get; set; }

        public Task<int> CloseAllAsync(string executablePath)
        {
            ClosedPaths.Add(executablePath);
            return Task.FromResult(WindowsPerClose);
        }
    }

    private sealed class FakeUsage : IUsageService
    {
        public void Record(string id)
        {
        }

        public int GetCount(string id) => 0;

        public Task LoadAsync() => Task.CompletedTask;

        public Task SaveAsync() => Task.CompletedTask;

        public int Reset() => 0;
    }
}
