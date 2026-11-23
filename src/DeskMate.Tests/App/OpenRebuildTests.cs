using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 「打开面板」这一拍不许把屏幕上那批条目推倒重建。
/// <para>
/// 重建在视图模型上只花几毫秒，贵的是 WPF 那一侧重新生成容器：本机实测 130~300ms，
/// 正好压在 320ms 的开场动画中间 —— 动画走的是真实时间，被吃掉的那段不会补回来，
/// 用户看到的就是一次「啪」（报上来的原话是「打开很僵硬、卡卡的」）。
/// 所以这里钉住「现场没变就沿用同一批对象」。
/// </para>
/// </summary>
public sealed class OpenRebuildTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;

    public OpenRebuildTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(_config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(
            _folders,
            new NullLauncher(),
            new FakeDesktopFileService(),
            new FakeFileOperations(),
            new FakeIconService(), _config,
            NullLogger<FolderManagerViewModel>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ReloadingWithNothingChangedKeepsTheSameCardObjects()
    {
        foreach (var name in new[] { "甲", "乙" })
        {
            await _folders.AddAsync(Directory.CreateDirectory(Path.Combine(_temp.Path, name)).FullName);
        }

        await _viewModel.LoadAsync();
        var before = _viewModel.Cards.ToList();

        // 每次呼出面板都会走这条重载：数据一字不变时容器一个都不该重建。
        await _viewModel.LoadAsync();

        Assert.Equal(before, _viewModel.Cards);
    }

    [Fact]
    public async Task AFolderThatDisappearedGreysTheSameCardInPlace()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "会消失")).FullName;
        await _folders.AddAsync(path);
        await _viewModel.LoadAsync();

        var card = Assert.Single(_viewModel.Cards);
        Assert.True(card.IsValid);

        Directory.Delete(path);
        await _viewModel.LoadAsync();

        Assert.Same(card, Assert.Single(_viewModel.Cards));
        Assert.False(card.IsValid);
    }

    [Fact]
    public async Task TabsKeepTheirObjectsWhenOnlyTheCountsMove()
    {
        await _folders.AddAsync(Directory.CreateDirectory(Path.Combine(_temp.Path, "一组")).FullName);
        await _viewModel.LoadAsync();
        var before = _viewModel.Tabs.ToList();

        await _folders.AddAsync(Directory.CreateDirectory(Path.Combine(_temp.Path, "二组")).FullName);
        await _viewModel.LoadAsync();

        Assert.Equal(before, _viewModel.Tabs);
        Assert.Contains(_viewModel.Tabs, tab => tab.Name.StartsWith("全部") && tab.Name.EndsWith("2"));
    }

    [Fact]
    public async Task SameCountButDifferentMembersStillRebuildsTheCards()
    {
        // 「数量没变」不等于「屏幕上还是那几行」：一张移出、一张移入时这一页换了人，
        // 必须重建 —— 沿用旧对象的话，屏幕上留着的是已经不属于这个分组的那几个。
        var group = await _folders.AddGroupAsync("课程");
        var moved = await _folders.AddAsync(Directory.CreateDirectory(Path.Combine(_temp.Path, "移出")).FullName);
        var staying = await _folders.AddAsync(Directory.CreateDirectory(Path.Combine(_temp.Path, "留下")).FullName);
        var entering = await _folders.AddAsync(Directory.CreateDirectory(Path.Combine(_temp.Path, "进组")).FullName);

        await _folders.UpdateGroupAsync(moved.Id, group.Id);
        await _folders.UpdateGroupAsync(staying.Id, group.Id);

        await _viewModel.LoadAsync();
        _viewModel.SelectTabCommand.Execute(_viewModel.Tabs.Single(tab => tab.Id == group.Id));
        var before = _viewModel.Cards.ToList();
        Assert.Equal(2, before.Count);

        await _folders.UpdateGroupAsync(moved.Id, null);
        await _folders.UpdateGroupAsync(entering.Id, group.Id);
        await _viewModel.LoadAsync();

        Assert.Equal(new[] { "留下", "进组" }, _viewModel.Cards.Select(card => card.DisplayName));
        Assert.All(before, card => Assert.DoesNotContain(card, _viewModel.Cards));
    }

    [Fact]
    public async Task PreWarmBuildsTheListWithoutReadingAnyDirectory()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "预热")).FullName;
        await _folders.AddAsync(path);

        _viewModel.PreWarm();

        Assert.Single(_viewModel.Cards);
        Assert.Equal(string.Empty, _viewModel.Cards[0].ModifiedText);

        // 预热只铺界面：读目录时间是磁盘活（虽然一次 stat 很便宜），留到人真的打开面板时再开始。
        Assert.Equal(0, _viewModel.TimeProbes);
    }

    [Fact]
    public async Task CardsAndBrowseRowsCarryTheWindowsTypeIcon()
    {
        // 面板里的行与卡片要画 Windows 对这一类的默认图标（文件夹 / Word / PDF / …），
        // 不能整页一个字形。替身回一张 1×1 位图，这里锁的是「建条目时真的去问了图标」。
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "带图标")).FullName;
        File.WriteAllText(Path.Combine(path, "说明.txt"), "x");
        await _folders.AddAsync(path);

        await _viewModel.LoadAsync();
        Assert.NotNull(Assert.Single(_viewModel.Cards).Icon);

        _viewModel.PreviewCard(_viewModel.Cards[0]);
        var row = _viewModel.PreviewItems.OfType<PreviewEntry>().Single(entry => entry.HasPath);
        Assert.NotNull(row.Icon);
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
