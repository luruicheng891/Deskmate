using DeskMate.App.Services;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 拖卡片到分组 Tab 上归组。真拖拽在这台机器上没法合成（WPF 不进 DoDragDrop），
/// 所以钉的是「落点收到 id 之后必须发生的事」：落点判定、整批一起走、内置视图挡下来、落完切过去。
/// </summary>
public class GroupDropTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;
    private readonly List<NoticeEventArgs> _notices = new();

    public GroupDropTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(_config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(
            _folders, new NullLauncher(), new FakeDesktopFileService(), new FakeFileOperations(), _config, NullLogger<FolderManagerViewModel>.Instance);
        _viewModel.UserNotice += (_, args) => _notices.Add(args);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task DropOnAGroup_MovesTheCardPersistsItAndSelectsThatGroup()
    {
        var card = await AddAsync("作业");
        var group = await _folders.AddGroupAsync("课程");

        await _viewModel.MoveToGroupAsync(group.Id, [card.Id]);

        Assert.Equal(group.Id, CardOf(card.Id).GroupId);
        Assert.Equal(group.Id, _viewModel.SelectedTabId);

        // 落点必须真的写进配置：切过去只是界面，重启还在才算归成功。
        await _config.LoadAsync();
        Assert.Equal(group.Id, _config.Current.Folders.Single(item => item.Id == card.Id).GroupId);

        // 归组之后 Tab 上的数量要跟着变（顶部那行数字是用户判断「进去了没」的另一半证据）。
        await _viewModel.LoadAsync();
        Assert.Contains(_viewModel.Tabs, tab => tab.Name == $"{group.Name} 1");
    }

    [Fact]
    public async Task DropCarriesTheWholeSelection_NotJustTheCardUnderTheCursor()
    {
        var first = await AddAsync("第一个");
        var second = await AddAsync("第二个");
        var group = await _folders.AddGroupAsync("课程");

        _viewModel.HandleCardClick(first, ctrl: true, shift: false);
        _viewModel.HandleCardClick(second, ctrl: true, shift: false);

        // 拖其中一张 → 整批 id 都要带走。
        var ids = _viewModel.DragIdsFor(first);
        Assert.Equal(2, ids.Length);

        await _viewModel.MoveToGroupAsync(group.Id, ids);

        // 归组会重画卡片视图，Cards 里已经是新对象：按 Id 重取，别拿旧引用断言。
        Assert.Equal(group.Id, CardOf(first.Id).GroupId);
        Assert.Equal(group.Id, CardOf(second.Id).GroupId);
        Assert.Contains(_notices, notice => notice.Message.Contains("2 个文件夹"));
    }

    [Fact]
    public async Task DropOnBuiltInView_IsRefusedAndSaysWhy()
    {
        var card = await AddAsync("作业");
        var group = await _folders.AddGroupAsync("课程");
        await _viewModel.MoveToGroupAsync(group.Id, [card.Id]);
        _notices.Clear();

        foreach (var tabId in new[] { "all", "fav", "desktop:image" })
        {
            await _viewModel.MoveToGroupAsync(tabId, [card.Id]);

            // 内置视图不是组：落上去必须原地不动，而不是「看着成功了其实没动」。
            Assert.Equal(group.Id, CardOf(card.Id).GroupId);
        }

        Assert.Equal(3, _notices.Count(notice => notice.Message.Contains("不是一个组")));
    }

    [Fact]
    public async Task DropOnUngrouped_TakesTheCardOutOfItsGroup()
    {
        var card = await AddAsync("作业");
        var group = await _folders.AddGroupAsync("课程");
        await _viewModel.MoveToGroupAsync(group.Id, [card.Id]);

        await _viewModel.MoveToGroupAsync("none", [card.Id]);

        Assert.Null(CardOf(card.Id).GroupId);
        Assert.Null(_config.Current.Folders.Single(item => item.Id == card.Id).GroupId);
    }

    [Fact]
    public async Task HighlightingTheDropTab_LightsOnlyThatOne()
    {
        await _folders.AddGroupAsync("课程");
        await _viewModel.LoadAsync();

        _viewModel.HighlightDropTab(_viewModel.Tabs.Last().Id);
        Assert.Equal(1, _viewModel.Tabs.Count(tab => tab.IsDropTarget));

        _viewModel.HighlightDropTab(null);
        Assert.All(_viewModel.Tabs, tab => Assert.False(tab.IsDropTarget));
    }

    /// <summary>按 Id 重取当前屏幕上的那张卡片（归组会重画，旧引用不代表屏幕上那张）。</summary>
    private FolderCardViewModel CardOf(string id) => _viewModel.Cards.Single(card => card.Id == id);

    private async Task<FolderCardViewModel> AddAsync(string name)
    {
        var path = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(path);
        await _viewModel.AddPathAsync(path);
        await _viewModel.LoadAsync();
        return _viewModel.Cards.Single(card => card.DisplayName == name);
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
