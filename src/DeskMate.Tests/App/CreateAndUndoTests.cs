using DeskMate.App.Services;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 批6：新建之后要能顺手改名，批量移除引用要能撤销。
/// 两条都是「做错了没法回头 / 下一步最常用的动作没给路径」这类会真劝退人的缺口。
/// </summary>
public class CreateAndUndoTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;
    private readonly List<NoticeEventArgs> _notices = new();

    public CreateAndUndoTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(_config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(
            _folders, new NullLauncher(), new FakeDesktopFileService(), new FakeFileOperations(), _config, NullLogger<FolderManagerViewModel>.Instance);
        _viewModel.UserNotice += (_, args) => _notices.Add(args);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task CreatingAnEntryLandsOnItSoF2RenamesImmediately()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp.Path, "新建测试")).FullName;
        await _viewModel.AddPathAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.CreateEntryDirectory = root;
        _viewModel.SelectedCreateEntryType = NewEntryCatalog.Options.Single(option => option.Label == "文本文件");
        _viewModel.CreateEntryText = "临时";
        await _viewModel.CommitCreateEntryAsync();

        Assert.True(_viewModel.IsBrowsing);
        Assert.Equal("临时.txt", _viewModel.FocusedItem?.FocusName);
        Assert.Contains("按 F2 可改名", _notices[^1].Message);
    }

    [Fact]
    public async Task RemovingSeveralReferencesCanBeUndoneInPlace()
    {
        var created = new List<FolderItem>();

        foreach (var name in new[] { "甲", "乙", "丙" })
        {
            var path = Directory.CreateDirectory(Path.Combine(_temp.Path, name)).FullName;
            created.Add(await _folders.AddAsync(path));
        }

        await _viewModel.LoadAsync();

        _viewModel.HandleCardClick(_viewModel.Cards[0], ctrl: false, shift: false);
        _viewModel.HandleCardClick(_viewModel.Cards[1], ctrl: true, shift: false);
        await _viewModel.RemoveSelectedAsync();

        Assert.Single(_viewModel.Cards);

        var undo = _notices.Last(candidate => candidate.ActionLabel == "撤销");
        undo.Action!.Invoke();
        await WaitUntilAsync(() => _viewModel.Cards.Count == 3);

        // 放回的是原来那几条：同一个 Id、同一个位置，不是「重新添加一遍」。
        Assert.Equal(3, _viewModel.Cards.Count);
        Assert.Contains(_viewModel.Cards, card => card.Id == created[0].Id);
        Assert.Contains(_viewModel.Cards, card => card.Id == created[1].Id);
        Assert.Equal(
            created.Select(item => item.Id).ToList(),
            _folders.GetAll().OrderBy(item => item.Order).Select(item => item.Id).ToList());
    }

    [Fact]
    public async Task UndoAfterBatchRemovalKeepsGroupAndColor()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "带标签")).FullName;
        var item = await _folders.AddAsync(path);
        var group = await _folders.AddGroupAsync("课程");
        await _folders.UpdateGroupAsync(item.Id, group.Id);
        await _folders.UpdateColorAsync(item.Id, "#EF4444");
        await _viewModel.LoadAsync();

        _viewModel.HandleCardClick(_viewModel.Cards.Single(), ctrl: false, shift: false);
        await _viewModel.RemoveSelectedAsync();
        _notices.Last(candidate => candidate.ActionLabel == "撤销").Action!.Invoke();
        await WaitUntilAsync(() => _viewModel.Cards.Count == 1);

        var restored = _viewModel.Cards.Single();
        Assert.Equal(group.Id, restored.GroupId);
        Assert.Equal("#EF4444", restored.Color);
    }

    [Fact]
    public async Task UndoIsOneShotAndSurvivesNothingAfterIt()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "一份")).FullName;
        await _folders.AddAsync(path);
        await _viewModel.LoadAsync();

        _viewModel.HandleCardClick(_viewModel.Cards.Single(), ctrl: false, shift: false);
        await _viewModel.RemoveSelectedAsync();
        var undo = _notices.Last(candidate => candidate.ActionLabel == "撤销");

        undo.Action!.Invoke();
        await WaitUntilAsync(() => _viewModel.Cards.Count == 1);
        undo.Action.Invoke();
        await Task.Delay(50);

        // 第二次点撤销不该把同一条再放回去一份（重复引用会在列表里出现两张一模一样的卡片）。
        Assert.Single(_viewModel.Cards);
    }

    /// <summary>撤销那一路是「点了就走、后台把引用放回去」，所以断言前等它到位（等不到就让下一条断言报错）。</summary>
    private async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var tick = 0; tick < 60 && !ready(); tick++)
        {
            await Task.Delay(25);
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
