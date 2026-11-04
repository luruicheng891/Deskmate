using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 键盘导航：条目本身 Focusable=False，焦点由视图模型持「虚拟焦点」。
/// 这里钉的是四件会真坏的事：方向键按列走、回车展开、退一层、同层翻文件。
/// </summary>
public class KeyboardFocusTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;
    private readonly string _root;

    public KeyboardFocusTests()
    {
        _root = MakeDir("根");
        MakeDir(Path.Combine("根", "子甲"));
        MakeDir(Path.Combine("根", "子乙"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "yy");

        var config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(
            _folders, _launcher, new FakeDesktopFileService(), new FakeFileOperations(), config, NullLogger<FolderManagerViewModel>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ArrowKeys_WalkTheCardGridRowByRow()
    {
        await _viewModel.AddPathAsync(Path.Combine(_root, "子甲"));
        await _viewModel.AddPathAsync(Path.Combine(_root, "子乙"));
        await _viewModel.LoadAsync();
        _viewModel.ReportCardAreaWidth(458);
        Assert.Equal(2, _viewModel.CardColumns);

        // 空白起步：按一下 ↓ 落到第一条，而不是「从第二条开始」。
        _viewModel.MoveFocus(0, 1);
        Assert.Same(_viewModel.Cards[0], _viewModel.FocusedItem);

        _viewModel.MoveFocus(1, 0);
        Assert.Same(_viewModel.Cards[1], _viewModel.FocusedItem);

        // 最后一行再往下不循环回头条 —— 那会让人以为按键失灵。
        _viewModel.MoveFocus(0, 1);
        Assert.Same(_viewModel.Cards[1], _viewModel.FocusedItem);
    }

    [Fact]
    public async Task Enter_OnAFocusedCard_WalksIntoThatFolder()
    {
        await _viewModel.AddPathAsync(_root);
        await _viewModel.LoadAsync();

        _viewModel.MoveFocusToFirst();
        Assert.True(_viewModel.ActivateFocused(openExternally: false));
        Assert.True(_viewModel.IsBrowsing);

        // 回车走的是「就地进来」，不该顺手把资源管理器也叫起来。
        Assert.Empty(_launcher.Opened);
    }

    [Fact]
    public async Task ControlEnter_OnAFocusedRow_HandsItToTheSystem()
    {
        await _viewModel.AddPathAsync(_root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards[0]);
        var fileRow = _viewModel.PreviewItems.First(entry => !entry.IsDirectory);
        _viewModel.SetFocus(fileRow);

        Assert.True(_viewModel.ActivateFocused(openExternally: true));
        Assert.Contains(_launcher.Launched, path => path.EndsWith("a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enter_TheHintRowIsFocusableButDoesNothing()
    {
        // 末尾那行「还有 N 项」不是路径：焦点能走到它上面，但回车不该因此报错或打开别的东西。
        var big = MakeDir("很多");
        for (var index = 0; index < 60; index++)
        {
            File.WriteAllText(Path.Combine(big, $"f{index:00}.txt"), "x");
        }

        await _viewModel.AddPathAsync(big);
        await _viewModel.LoadAsync();
        _viewModel.PreviewCard(_viewModel.Cards[0]);

        _viewModel.MoveFocusToLast();
        Assert.False(_viewModel.ActivateFocused(openExternally: false));
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task Backspace_StepsOutOfDrilledFoldersThenLeavesBrowse()
    {
        await _viewModel.AddPathAsync(_root);
        await _viewModel.LoadAsync();
        _viewModel.PreviewCard(_viewModel.Cards[0]);

        // 根 → 子甲（压一层），退回来应该看见根的内容，再退一次才回卡片网格。
        _viewModel.EnterFolder(_viewModel.PreviewItems.First(entry => entry.Name == "子甲"));
        Assert.DoesNotContain(_viewModel.PreviewItems, entry => entry.Name == "a.txt");

        _viewModel.GoUpOneLevel();
        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "a.txt");

        _viewModel.GoUpOneLevel();
        Assert.False(_viewModel.IsBrowsing);
    }

    [Fact]
    public async Task SteppingSidewise_CyclesThroughTheFilesOfThisLevel()
    {
        await _viewModel.AddPathAsync(_root);
        await _viewModel.LoadAsync();
        _viewModel.PreviewCard(_viewModel.Cards[0]);

        _viewModel.PreviewFile(Path.Combine(_root, "a.txt"));
        Assert.Equal("a.txt", _viewModel.FilePreview!.Name);

        // 同层顺序由文化排序决定，所以这里只断言「换到了另一个文件，并且能换回来」。
        _viewModel.StepSiblingFile(1);
        var moved = _viewModel.FilePreview!.Name;
        Assert.NotEqual("a.txt", moved);

        _viewModel.StepSiblingFile(-1);
        Assert.Equal("a.txt", _viewModel.FilePreview!.Name);
    }

    [Fact]
    public async Task ChangingLevel_DoesNotLeaveFocusOnThePreviousPage()
    {
        // 上一层那行的焦点对象已经不在这一页里；留着它，方向键会从「不知道哪里」起步。
        await _viewModel.AddPathAsync(_root);
        await _viewModel.LoadAsync();

        _viewModel.MoveFocusToFirst();
        Assert.NotNull(_viewModel.FocusedItem);

        _viewModel.PreviewCard(_viewModel.Cards[0]);
        Assert.Null(_viewModel.FocusedItem);
    }

    [Fact]
    public void FocusingASecondItem_ClearsThePreviousOne()
    {
        // 焦点标记如果只加不减，屏幕上会出现两条「都亮着」的卡片。
        var first = new FolderCardViewModel(_viewModel, "1", "第一个", _root, null, null, true);
        var second = new FolderCardViewModel(_viewModel, "2", "第二个", _root, null, null, true);

        _viewModel.SetFocus(first);
        _viewModel.SetFocus(second);

        Assert.False(first.IsFocused);
        Assert.True(second.IsFocused);
    }

    [Fact]
    public void CopyingOrOpeningAnEmptyPath_DoesNothing()
    {
        // 占位行没有路径：既不能去碰剪贴板，也不能去拉系统面板。
        var placeholder = new PreviewEntry(_viewModel, "只是句提示", string.Empty, false);

        _viewModel.CopyPath(placeholder);
        _viewModel.OpenWith(placeholder);
        Assert.Empty(_launcher.Launched);
    }

    private string MakeDir(string relative)
    {
        var path = Path.Combine(_temp.Path, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingLauncher : IAppLauncherService
    {
        public List<string> Launched { get; } = new();

        public List<string> Opened { get; } = new();

        public void Launch(string path) => Launched.Add(path);

        public void OpenInExplorer(string path) => Opened.Add(path);

        public void OpenUri(string uri)
        {
        }

        public bool HasDefaultProgram(string path) => true;
    }
}
