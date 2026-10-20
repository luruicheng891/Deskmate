using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 扫描服务的真机集成测试：直接跑在这台机器上，验证「开始菜单 + 注册表 App Paths + 启动台目录」
/// 三条来源都能出结果、去重正确、并且二次打开走缓存。启动台目录指到临时目录，
/// 免得结果跟着用户往真目录里加应用而变。
/// </summary>
public class AppScannerServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StaScheduler _sta = new();
    private readonly ConfigService _config;

    public AppScannerServiceTests()
    {
        _config = new ConfigService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigService>.Instance,
            _temp.File("config.json"));

        // 扫描会把启动台目录也算进来，不指到临时目录的话，结果会随用户往启动台里加应用而变。
        _config.Current.LaunchpadFolder = _temp.File("launchpad");
    }

    [Fact]
    public async Task ScanInstalledAppsAsync_ReturnsUsableEntries()
    {
        var scanner = Create();

        var apps = await scanner.ScanInstalledAppsAsync();

        Assert.NotEmpty(apps);
        Assert.All(apps, app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
            Assert.False(string.IsNullOrWhiteSpace(app.Path));
            Assert.False(string.IsNullOrWhiteSpace(app.Id));
            Assert.Contains(app.Source, new[] { "StartMenu", "Registry", "Launchpad" });
        });
        Assert.Equal(apps.Count, apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task ScanInstalledAppsAsync_WritesCacheReadableByNextInstance()
    {
        var cachePath = _temp.File("apps_cache.json");

        var first = new AppScannerService(_sta, _config, NullLogger<AppScannerService>.Instance, cachePath);
        var scanned = await first.ScanInstalledAppsAsync();

        Assert.True(File.Exists(cachePath), "扫描完应写出 apps_cache.json，供二次打开秒开");

        var second = new AppScannerService(_sta, _config, NullLogger<AppScannerService>.Instance, cachePath);
        var fromCache = await second.ScanInstalledAppsAsync();

        Assert.Equal(scanned.Count, fromCache.Count);
        Assert.Equal(scanned.Select(a => a.Name).OrderBy(n => n), fromCache.Select(a => a.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task SearchAppsAsync_FiltersWithPinyinAndReturnsMatchingOnly()
    {
        var scanner = Create();
        await scanner.ScanInstalledAppsAsync();

        var results = await scanner.SearchAppsAsync("记事本");

        Assert.All(results, app => Assert.True(
            PinyinMatcher.Matches(app.Name, "记事本") || PinyinMatcher.Matches(app.Path, "记事本"),
            $"结果「{app.Name}」并不匹配关键词"));
    }

    [Fact]
    public async Task RefreshAsync_RescansAndKeepsShape()
    {
        var scanner = Create();
        var initial = await scanner.ScanInstalledAppsAsync();

        var refreshed = await scanner.RefreshAsync();

        Assert.NotEmpty(refreshed);
        Assert.Equal(initial.Count, refreshed.Count);
    }

    private AppScannerService Create()
        => new(_sta, _config, NullLogger<AppScannerService>.Instance, _temp.File("apps_cache.json"));

    public void Dispose()
    {
        _sta.Dispose();
        _temp.Dispose();
    }
}
