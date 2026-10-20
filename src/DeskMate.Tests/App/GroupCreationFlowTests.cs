using DeskMate.App.Services;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 「新建分组」浮层的完整交互：点按钮才弹框、边输入边校验、确认时去首尾空格后落盘并切到新分组。
/// 用真实 FolderService（配置写在临时目录），所以重名判定走的是真实的已存分组数据。
/// </summary>
public class GroupCreationFlowTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;

    public GroupCreationFlowTests()
    {
        var config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(_folders, new NullLauncher(), new FakeDesktopFileService(), new FakeFileOperations(), NullLogger<FolderManagerViewModel>.Instance);
    }

    [Fact]
    public void BeginCreateGroup_OpensTheModalAndClearsThePreviousInput()
    {
        _viewModel.NewGroupName = "上次没提交的名字";

        _viewModel.BeginCreateGroupCommand.Execute(null);

        Assert.True(_viewModel.IsNewGroupOpen);
        Assert.Equal(string.Empty, _viewModel.NewGroupName);
    }

    [Fact]
    public async Task Commit_TrimsTheNamePersistsItAndSelectsTheNewGroup()
    {
        _viewModel.BeginCreateGroupCommand.Execute(null);
        _viewModel.NewGroupName = "  课程资料  ";

        Assert.True(_viewModel.CommitGroupCommand.CanExecute(null));
        await _viewModel.CommitGroupCommand.ExecuteAsync(null);

        var group = Assert.Single(_folders.GetGroups());
        Assert.Equal("课程资料", group.Name);
        Assert.Equal(group.Id, _viewModel.SelectedTabId);
        Assert.False(_viewModel.IsNewGroupOpen);
    }

    [Fact]
    public async Task DuplicateName_DisablesConfirmAndHints()
    {
        await _folders.AddGroupAsync("课程资料");
        _viewModel.BeginCreateGroupCommand.Execute(null);
        _viewModel.NewGroupName = "课程资料";

        Assert.False(_viewModel.CommitGroupCommand.CanExecute(null));
        Assert.Equal("该分组名已存在，请换一个", _viewModel.GroupNameError);

        // 大小写不同也算重名。
        _viewModel.NewGroupName = "Course";
        await _folders.AddGroupAsync("course");
        Assert.Equal("该分组名已存在，请换一个", _viewModel.GroupNameError);
    }

    [Fact]
    public void EmptyName_DisablesConfirmWithoutShoutingAnError()
    {
        _viewModel.BeginCreateGroupCommand.Execute(null);
        _viewModel.NewGroupName = "   ";

        Assert.False(_viewModel.CommitGroupCommand.CanExecute(null));
        // 刚弹出来一个字没输入时不该立刻报错，置灰的按钮已经表达了意思。
        Assert.Equal(string.Empty, _viewModel.GroupNameError);
    }

    [Fact]
    public void TooLongName_HintsTheLimit()
    {
        _viewModel.BeginCreateGroupCommand.Execute(null);
        _viewModel.NewGroupName = new string('长', GroupNameRules.MaxLength + 1);

        Assert.False(_viewModel.CommitGroupCommand.CanExecute(null));
        Assert.Contains("20", _viewModel.GroupNameError);
    }

    [Fact]
    public void ReservedViewNames_CountAsTaken()
    {
        _viewModel.BeginCreateGroupCommand.Execute(null);
        _viewModel.NewGroupName = "全部";

        Assert.False(_viewModel.CommitGroupCommand.CanExecute(null));
        Assert.Equal("该分组名已存在，请换一个", _viewModel.GroupNameError);
    }

    [Fact]
    public void Cancel_ClosesTheModalWithoutWritingAnything()
    {
        _viewModel.BeginCreateGroupCommand.Execute(null);
        _viewModel.NewGroupName = "半成品";

        _viewModel.CancelCreateGroupCommand.Execute(null);

        Assert.False(_viewModel.IsNewGroupOpen);
        Assert.Empty(_folders.GetGroups());
    }

    [Fact]
    public async Task RenameGroup_KeepsMembersAndOnlyChangesTheLabel()
    {
        var group = await _folders.AddGroupAsync("课程资料");
        var folder = await _folders.AddAsync(NewDirectory("高数"));
        await _folders.UpdateGroupAsync(folder.Id, group.Id);
        await _viewModel.LoadAsync();
        SelectTab(group.Id);

        _viewModel.BeginRenameGroupCommand.Execute(null);

        Assert.True(_viewModel.IsRenameGroupOpen);
        Assert.Equal("课程资料", _viewModel.RenameGroupText);

        _viewModel.RenameGroupText = "  课程资料 2026  ";
        await _viewModel.CommitGroupRenameCommand.ExecuteAsync(null);

        Assert.Equal("课程资料 2026", Assert.Single(_folders.GetGroups()).Name);
        Assert.Equal(group.Id, Assert.Single(_folders.GetAll()).GroupId);
        Assert.False(_viewModel.IsRenameGroupOpen);
    }

    [Fact]
    public async Task RenameGroup_DuplicateIsRejectedButItsOwnNameIsNot()
    {
        var first = await _folders.AddGroupAsync("研一");
        await _folders.AddGroupAsync("研二");
        await _viewModel.LoadAsync();
        SelectTab(first.Id);

        _viewModel.BeginRenameGroupCommand.Execute(null);

        _viewModel.RenameGroupText = "研二";
        Assert.Equal("该分组名已存在，请换一个", _viewModel.RenameGroupError);
        Assert.False(_viewModel.CommitGroupRenameCommand.CanExecute(null));

        // 一个字没改直接点保存，不能被自己的旧名判成重名。
        _viewModel.RenameGroupText = "研一";
        Assert.Equal(string.Empty, _viewModel.RenameGroupError);
        Assert.True(_viewModel.CommitGroupRenameCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteGroup_IsAskedFirstAndUndoBringsTheMembersBack()
    {
        var group = await _folders.AddGroupAsync("课程资料");
        var folder = await _folders.AddAsync(NewDirectory("高数"));
        await _folders.UpdateGroupAsync(folder.Id, group.Id);
        await _viewModel.LoadAsync();
        SelectTab(group.Id);

        GroupDeleteRequest? request = null;
        _viewModel.GroupDeleteConfirmRequested += (_, args) => request = args;
        var notices = new List<NoticeEventArgs>();
        _viewModel.UserNotice += (_, args) => notices.Add(args);

        _viewModel.RequestDeleteGroupCommand.Execute(null);

        // 视图模型不自己删：先问用户，并且把会退到未分组的数量报出来。
        Assert.NotNull(request);
        Assert.Equal("课程资料", request!.GroupName);
        Assert.Equal(1, request.FolderCount);

        await _viewModel.DeleteGroupAsync(group.Id);

        Assert.Empty(_folders.GetGroups());
        Assert.Null(Assert.Single(_folders.GetAll()).GroupId);

        var undo = notices.Last().Action;
        Assert.NotNull(undo);
        undo!.Invoke();

        // 撤销是「先建组、再把成员一个个换回去」，只等分组出现会读到成员还没接上的中间态。
        await WaitUntil(() => _folders.GetGroups().Count > 0 && _folders.GetAll().Single().GroupId is not null);

        Assert.Equal("课程资料", Assert.Single(_folders.GetGroups()).Name);
        Assert.NotNull(Assert.Single(_folders.GetAll()).GroupId);
    }

    public void Dispose() => _temp.Dispose();

    private void SelectTab(string groupId)
        => _viewModel.SelectTabCommand.Execute(_viewModel.Tabs.Single(tab => tab.Id == groupId));

    private string NewDirectory(string name)
    {
        var path = System.IO.Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        if (!condition())
        {
            throw new TimeoutException("等到超时条件仍未成立。");
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
}
