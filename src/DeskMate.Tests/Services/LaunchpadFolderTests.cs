using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 启动台快捷方式文件夹：手动添加、自动同步、以及「移除后不再加回」。
/// </summary>
public sealed class LaunchpadFolderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StaScheduler _sta = new();
    private readonly ConfigService _config;

    public LaunchpadFolderTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
    }

    [Fact]
    public async Task Create_Then_Resolve_RoundTripsTargetAndArguments()
    {
        var link = _temp.File("记事本.lnk");
        var target = @"C:\Windows\System32\notepad.exe";

        await _sta.RunAsync(() =>
        {
            ShellLink.Create(link, target, "记事本", Path.GetDirectoryName(target), "/hello");
            return true;
        });

        Assert.True(File.Exists(link));

        var resolved = await _sta.RunAsync(() => ShellLink.Resolve(link));

        Assert.Equal(target, resolved.Target, ignoreCase: true);
        Assert.Equal("/hello", resolved.Arguments);
    }

    // 用一个独一无二的假 exe：真程序可能被开始菜单的快捷方式抢先去重，搜索也会命中同名应用，
    // 那样断言就不是在验证我们要验的东西了。
    private string MakeFakeTool(string tag)
    {
        var path = _temp.File($"{tag}-unique-tool.exe");
        File.WriteAllText(path, "MZ");
        return path;
    }

    [Fact]
    public async Task AddToLaunchpadAsync_MakesTheAppAppearInScan()
    {
        var tool = MakeFakeTool("scan");
        var folder = _temp.File("launchpad");
        _config.Current.LaunchpadFolder = folder;
        var scanner = CreateScanner();

        var link = await scanner.AddToLaunchpadAsync(tool);

        var apps = await scanner.ScanInstalledAppsAsync(forceRefresh: true);
        var added = apps.Single(app => string.Equals(app.Path, link, StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Launchpad", added.Source);
        Assert.Contains("unique-tool", added.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(link), "添加后应在快捷方式文件夹里生成一个 .lnk");
    }

    [Fact]
    public async Task AddToLaunchpadAsync_AutoRenames_WhenNameAlreadyTaken()
    {
        var tool = MakeFakeTool("rename");
        var folder = _temp.File("launchpad2");
        _config.Current.LaunchpadFolder = folder;
        var scanner = CreateScanner();

        var first = await scanner.AddToLaunchpadAsync(tool);
        var second = await scanner.AddToLaunchpadAsync(tool);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task AddToLaunchpadAsync_Throws_ForMissingSource()
    {
        var scanner = CreateScanner();

        await Assert.ThrowsAsync<System.IO.FileNotFoundException>(() =>
            scanner.AddToLaunchpadAsync(Path.Combine(_temp.Path, "不存在.exe")));
    }

    [Fact]
    public async Task HiddenApps_StayOutOfSearch_AfterRescan()
    {
        var tool = MakeFakeTool("hidden");
        var folder = _temp.File("launchpad3");
        _config.Current.LaunchpadFolder = folder;
        var scanner = CreateScanner();
        var link = await scanner.AddToLaunchpadAsync(tool);

        var usage = new UsageService(NullLogger<UsageService>.Instance, _temp.File("usage.json"));
        var search = new SearchService(
            scanner,
            _config,
            usage,
            new DeskMate.Tests.App.FakeDesktopFileService(),
            NullLogger<SearchService>.Instance);
        var keyword = Path.GetFileNameWithoutExtension(link);

        // 先确认它本来是能搜到的，否则后面的「搜不到」说明不了任何问题。
        Assert.NotEmpty(await search.SearchAsync(keyword));

        var apps = await scanner.ScanInstalledAppsAsync();
        var target = apps.Single(app => string.Equals(app.Path, link, StringComparison.OrdinalIgnoreCase));

        // 用户手动移除 = 记进 hiddenApps，之后无论重扫多少次都不会自己跑回来。
        _config.Current.HiddenApps.Add(target.Id);
        await _config.SaveAsync();

        Assert.Empty(await search.SearchAsync(keyword));

        await scanner.RefreshAsync();
        Assert.Contains(target.Id, (await scanner.ScanInstalledAppsAsync()).Select(app => app.Id));
        Assert.Empty(await search.SearchAsync(keyword));
    }

    /// <summary>
    /// 重新添加一个「之前被隐藏过」的程序，必须顺手解除隐藏。
    /// <para>
    /// 难点在去重：同一个目标只在扫描结果里留一条，而那条的 Path 往往是先到的那个链接。
    /// 于是「按链接路径去结果里找条目、再解隐藏」的写法在重复添加时永远找不到自己，
    /// 用户看到的就是「加了但没出现」。这里用同一个工具连加三次，把这条路径钉死。
    /// </para>
    /// </summary>
    [Fact]
    public async Task AddToLaunchpadAsync_UnhidesTheTargetTheUserHadHiddenEarlier()
    {
        var tool = MakeFakeTool("unhide");
        var folder = _temp.File("launchpad4");
        _config.Current.LaunchpadFolder = folder;
        var scanner = CreateScanner();

        await scanner.AddToLaunchpadAsync(tool);
        await scanner.AddToLaunchpadAsync(tool);

        _config.Current.HiddenApps.Add(tool.ToUpperInvariant());
        await _config.SaveAsync();

        // 第三次添加生成的是新链接，扫描结果里那条记录的 Path 并不是它。
        var third = await scanner.AddToLaunchpadAsync(tool);

        Assert.True(File.Exists(third));
        Assert.DoesNotContain(tool.ToUpperInvariant(), _config.Current.HiddenApps, StringComparer.OrdinalIgnoreCase);
    }

    private AppScannerService CreateScanner()
        => new(_sta, _config, NullLogger<AppScannerService>.Instance, _temp.File("apps_cache.json"));

    public void Dispose()
    {
        _sta.Dispose();
        _temp.Dispose();
    }
}
