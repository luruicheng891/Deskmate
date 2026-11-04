using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 卡片与收藏页的「目录足迹」：文件数、最后修改时间、按需称重的总大小。
/// 钉两件事 —— 该显示的要显示出来，以及切标签页不许把每张卡重新扫一遍盘。
/// </summary>
public class FootprintTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;

    public FootprintTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(_config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(
            _folders, new NullLauncher(), new FakeDesktopFileService(), new FakeFileOperations(), _config, NullLogger<FolderManagerViewModel>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Cards_ShowFileCountAndLastModified()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "课题")).FullName;
        File.WriteAllText(Path.Combine(path, "a.txt"), "内容");
        await _viewModel.AddPathAsync(path);
        await _viewModel.LoadAsync();

        var card = _viewModel.Cards.Single();
        Assert.Equal(1, card.FileCount);
        Assert.Matches(@"\d{4}-\d{2}-\d{2}", card.ModifiedText);

        // 总大小不默认算：默认算就是首屏并发扫盘，「一打开面板就卡」正是这么来的。
        Assert.False(card.HasSize);
    }

    [Fact]
    public async Task SwitchingTabs_DoesNotRecountEveryCard()
    {
        foreach (var name in new[] { "甲", "乙", "丙" })
        {
            var path = Directory.CreateDirectory(Path.Combine(_temp.Path, name)).FullName;
            File.WriteAllText(Path.Combine(path, "a.txt"), "x");
            await _viewModel.AddPathAsync(path);
        }

        await _viewModel.LoadAsync();
        var probesAfterFirstLoad = _viewModel.FootprintProbes;

        // 切到「未分组」再切回来：卡片重画了，但盘不用再扫一遍。
        var group = await _folders.AddGroupAsync("课程");
        _viewModel.RefreshView(); // 外面直接加的分组，Tab 条要重算才看得见
        _viewModel.SelectTabCommand.Execute(_viewModel.Tabs.First(tab => tab.Name.StartsWith("未分组")));
        _viewModel.SelectTabCommand.Execute(_viewModel.Tabs.First(tab => tab.Id == group.Id));
        await _viewModel.LoadAsync();

        Assert.Equal(probesAfterFirstLoad, _viewModel.FootprintProbes);
    }

    [Fact]
    public async Task CreatingSomethingInsideACardInvalidatesItsCount()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "作业")).FullName;
        await _viewModel.AddPathAsync(path);
        await _viewModel.LoadAsync();

        var card = _viewModel.Cards.Single();
        Assert.Equal(0, card.FileCount);

        // 走新建那条真入口（浮层 → 提交），因为「作废缓存」就挂在那一步上。
        _viewModel.CreateEntryDirectory = path;
        _viewModel.SelectedCreateEntryType = NewEntryCatalog.Options.Single(option => option.Label == "文本文件");
        _viewModel.CreateEntryText = "新文件";
        await _viewModel.CommitCreateEntryAsync();
        await _viewModel.LoadAsync();

        Assert.Equal(1, _viewModel.Cards.Single().FileCount);
        Assert.True(_viewModel.FootprintProbes > 1);
    }

    [Fact]
    public async Task MeasureSize_FillsTheCardAndIsRemembered()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "素材")).FullName;
        File.WriteAllBytes(Path.Combine(path, "a.bin"), new byte[2048]);
        await _viewModel.AddPathAsync(path);
        await _viewModel.LoadAsync();

        var probesBefore = _viewModel.FootprintProbes;
        var card = _viewModel.Cards.Single();

        await card.MeasureSizeAsync();

        Assert.Equal("2 KB", card.SizeText);
        Assert.Equal("共 2 KB", card.SizeLine);
        Assert.True(card.HasSize);

        // 第二张卡问同一个目录：命中缓存，不该再扫一遍。
        await card.MeasureSizeAsync();
        Assert.Equal(probesBefore + 1, _viewModel.FootprintProbes);
    }

    [Fact]
    public async Task Favorites_DoNotWalkTheTreeOnOpen()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "大目录")).FullName;
        for (var index = 0; index < 30; index++)
        {
            File.WriteAllText(Path.Combine(path, $"f{index}.txt"), "x");
        }

        await _folders.AddFavoritesAsync([path]);
        await _viewModel.LoadAsync();

        _viewModel.SelectTabCommand.Execute(_viewModel.Tabs.First(tab => tab.Name.StartsWith("★ 收藏")));

        // 收藏页一打开就把每个目录整棵树数一遍，就是「打开这一页先卡一下」。
        var card = _viewModel.FavoriteCards.Single();
        await WaitForSettledAsync(card);

        Assert.DoesNotContain("个文件", card.MetaText);
        Assert.Contains("右键算大小", card.MetaText);

        await card.MeasureSizeAsync();
        Assert.Contains("30 个文件", card.MetaText);
    }

    [Fact]
    public void MeasureReportsCountSizeAndModifiedInOneWalk()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "样本")).FullName;
        File.WriteAllBytes(Path.Combine(path, "a.bin"), new byte[1024]);
        Directory.CreateDirectory(Path.Combine(path, "子"));
        File.WriteAllBytes(Path.Combine(path, "子", "b.bin"), new byte[1024]);

        var footprint = FolderService.Measure(path, wantSize: true);

        Assert.Equal(2, footprint.FileCount);
        Assert.Equal(2048, footprint.SizeBytes);
        Assert.True(footprint.SizeMeasured);
        Assert.True(footprint.HasModified);

        // 不算大小时也照样给得出个数与日期，且明确说「大小没数」。
        var light = FolderService.Measure(path, wantSize: false);
        Assert.Equal(2, light.FileCount);
        Assert.False(light.SizeMeasured);
    }

    /// <summary>收藏详情在后台线程填，断言前给它一点时间（最多 2 秒，超时就让断言去报错）。</summary>
    private static async Task WaitForSettledAsync(FavoriteCardViewModel card)
    {
        for (var tick = 0; tick < 40 && card.MetaText == "读取中…"; tick++)
        {
            await Task.Delay(50);
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

        public bool HasDefaultProgram(string path) => true;
    }
}
