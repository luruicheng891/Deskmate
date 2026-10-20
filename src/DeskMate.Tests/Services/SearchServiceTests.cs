using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

public class SearchServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly FakeUsage _usage = new();
    private readonly DeskMate.Tests.App.FakeDesktopFileService _desktop = new();

    public SearchServiceTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
    }

    [Fact]
    public async Task SearchAsync_OrdersByMatchTier()
    {
        var service = CreateService(
            App("Word", @"C:\Office\WINWORD.EXE"),
            App("WordPad", @"C:\Windows\System32\write.exe"),
            App("My Word Notes", @"C:\Notes\word.docx"));

        var results = await service.SearchAsync("word");

        Assert.Equal(new[] { "Word", "WordPad", "My Word Notes" }, results.Select(r => r.Title).ToArray());
    }

    [Fact]
    public async Task SearchAsync_MatchesChineseByPinyinInitials()
    {
        var service = CreateService(App("计算器", @"C:\Windows\System32\calc.exe"));

        var results = await service.SearchAsync("jsq");

        Assert.Single(results);
        Assert.Equal("app", results[0].Type);
    }

    [Fact]
    public async Task SearchAsync_UsageBonusBreaksTiesButNeverCrossesTiers()
    {
        // 「包含匹配」即使被点了一千次，也不能盖过「前缀匹配」。
        var prefix = App("WordPad", @"C:\Windows\System32\write.exe");
        var contains = App("My Word Notes", @"C:\Notes\word.docx");
        _usage.Counts[contains.Id] = 1000;

        var service = CreateService(prefix, contains);
        var results = await service.SearchAsync("word");

        Assert.Equal("WordPad", results[0].Title);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public async Task SearchAsync_PutsMostUsedFirst_WithinSameTier()
    {
        var cold = App("Word", @"C:\Office\WINWORD.EXE");
        var hot = App("Word", @"C:\Other\WORD.EXE");
        _usage.Counts[hot.Id] = 30;

        var service = CreateService(cold, hot);
        var results = await service.SearchAsync("word");

        Assert.Equal(@"C:\Other\WORD.EXE", results[0].Path);
    }

    [Fact]
    public async Task SearchAsync_IncludesFoldersAndDockItems()
    {
        _config.Current.Folders.Add(new FolderItem { DisplayName = "毕业设计", RealPath = @"D:\资料\毕业设计", Id = "f1" });
        _config.Current.DockItems.Add(new DockItem { DisplayName = "浏览器", TargetPath = @"C:\Browser\browser.exe", Id = "d1" });
        var service = CreateService();

        var results = await service.SearchAsync("毕业");

        var folder = Assert.Single(results);
        Assert.Equal("folder", folder.Type);
        Assert.Equal(@"D:\资料\毕业设计", folder.Path);

        var dockResults = await service.SearchAsync("浏览");
        Assert.Equal("app", Assert.Single(dockResults).Type);
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesSamePath_KeepingBestScore()
    {
        var app = App("记事本", @"C:\Windows\System32\notepad.exe");
        _config.Current.DockItems.Add(new DockItem { DisplayName = "记事本", TargetPath = @"C:\Windows\System32\notepad.exe", Id = "d1" });

        var results = await CreateService(app).SearchAsync("记事本");

        Assert.Single(results);
    }

    [Fact]
    public async Task SearchAsync_RespectsMaxResults()
    {
        var apps = Enumerable.Range(1, 40).Select(i => App($"notepad{i}", $@"C:\Tools\notepad{i}.exe")).ToArray();

        var results = await CreateService(apps).SearchAsync("notepad", maxResults: 5);

        Assert.Equal(5, results.Count);
    }

    [Fact]
    public async Task SearchAsync_SkipsAppsTheUserHidInTheLauncher()
    {
        var app = App("记事本", @"C:\Windows\System32\notepad.exe");
        _config.Current.HiddenApps.Add(app.Id);

        var results = await CreateService(app).SearchAsync("记事本");

        Assert.Empty(results);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchAsync_ReturnsNothing_ForBlankKeyword(string keyword)
        => Assert.Empty(await CreateService(App("Word", @"C:\Office\WINWORD.EXE")).SearchAsync(keyword));

    private SearchService CreateService(params AppInfo[] apps)
        => new(new FakeAppScanner(apps), _config, _usage, _desktop, NullLogger<SearchService>.Instance);

    [Fact]
    public async Task SearchAsync_FindsFilesLooseOnTheDesktop()
    {
        _desktop.Add(@"C:\Users\me\Desktop\季度报告.docx");
        var service = CreateService();

        var results = await service.SearchAsync("报告");

        var hit = Assert.Single(results);
        Assert.Equal("file", hit.Type);
        Assert.Equal("季度报告.docx", hit.Title);
    }

    [Fact]
    public async Task SearchAsync_FindsFirstLevelContentsOfManagedFolders()
    {
        var root = System.IO.Path.Combine(_temp.Path, "毕业设计");
        Directory.CreateDirectory(root);
        File.WriteAllText(System.IO.Path.Combine(root, "开题报告.pdf"), "x");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "参考资料"));
        _config.Current.Folders.Add(new FolderItem { DisplayName = "毕业设计", RealPath = root, Id = "f1" });
        var service = CreateService();

        var byName = await service.SearchAsync("开题");
        Assert.Equal("file", Assert.Single(byName).Type);

        // 子目录也算一条文件夹结果：搜到目录时回车应该打开它，而不是报错。
        var byFolder = await service.SearchAsync("参考资料");
        Assert.Equal("folder", Assert.Single(byFolder).Type);
    }

    [Fact]
    public async Task SearchAsync_IndexesNoContentsForFoldersMarkedInvalid()
    {
        var root = System.IO.Path.Combine(_temp.Path, "已经搬走的目录");
        Directory.CreateDirectory(root);
        File.WriteAllText(System.IO.Path.Combine(root, "遗留文件.txt"), "x");
        _config.Current.Folders.Add(new FolderItem
        {
            DisplayName = "旧位置",
            RealPath = root,
            Id = "f1",
            Valid = false,
        });
        var service = CreateService();

        // 卡片上标了失效就不再敲它的盘：内容不出现，一次搜索也不该因此抛异常。
        Assert.Empty(await service.SearchAsync("遗留文件"));
    }

    [Fact]
    public async Task SearchAsync_SkipsDotPrefixedEntriesOfManagedFolders()
    {
        var root = System.IO.Path.Combine(_temp.Path, "工程");
        Directory.CreateDirectory(System.IO.Path.Combine(root, ".git"));
        File.WriteAllText(System.IO.Path.Combine(root, ".env"), "x");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "资料"));
        _config.Current.Folders.Add(new FolderItem { DisplayName = "工程", RealPath = root, Id = "f1" });
        var service = CreateService();

        // .git / .env 这种工具目录每天都在变，搜到它们只会挤掉真正要找的东西；正常目录照常能搜到。
        Assert.Empty(await service.SearchAsync("git"));
        Assert.Empty(await service.SearchAsync("env"));
        Assert.NotEmpty(await service.SearchAsync("资料"));
    }

    [Fact]
    public async Task SearchAsync_FileHitsNeverCrowdOutApps()
    {
        // 输一个「a」能命中成百上千个文件；不设上限的话应用会被挤出结果，而这里最常用的动作是启动应用。
        for (var index = 0; index < 40; index++)
        {
            _desktop.Add($@"C:\Users\me\Desktop\article{index}.txt");
        }

        var service = CreateService(App("Adobe Acrobat", @"C:\Adobe\Acrobat.exe"));

        var results = await service.SearchAsync("article");
        Assert.Contains(results, result => result.Type == "file");

        var mixed = await service.SearchAsync("ac");
        Assert.Contains("Adobe Acrobat", mixed.Select(result => result.Title).ToArray());
    }

    private static AppInfo App(string name, string path)
        => new() { Id = path.ToUpperInvariant(), Name = name, Path = path, IconPath = path, Source = "StartMenu" };

    public void Dispose() => _temp.Dispose();

    private sealed class FakeAppScanner : IAppScannerService
    {
        private readonly List<AppInfo> _apps;

        public FakeAppScanner(params AppInfo[] apps) => _apps = apps.ToList();

        public Task<IReadOnlyList<AppInfo>> ScanInstalledAppsAsync(bool forceRefresh = false)
            => Task.FromResult<IReadOnlyList<AppInfo>>(_apps);

        public Task<IReadOnlyList<AppInfo>> RefreshAsync() => Task.FromResult<IReadOnlyList<AppInfo>>(_apps);

        public Task<IReadOnlyList<AppInfo>> SearchAppsAsync(string keyword)
            => Task.FromResult<IReadOnlyList<AppInfo>>(_apps.Where(a => PinyinMatcher.Matches(a.Name, keyword)).ToList());

        public Task<string> AddToLaunchpadAsync(string sourcePath) => Task.FromResult(string.Empty);
    }

    private sealed class FakeUsage : IUsageService
    {
        public Dictionary<string, int> Counts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Record(string id) => Counts[id] = Counts.TryGetValue(id, out var c) ? c + 1 : 1;

        public int GetCount(string id) => Counts.TryGetValue(id, out var c) ? c : 0;

        public Task LoadAsync() => Task.CompletedTask;

        public int Reset() => Counts.Count;
    }
}
