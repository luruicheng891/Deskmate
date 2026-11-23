using DeskMate.App.Services;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 路径框那一格：行为对齐资源管理器的地址栏 —— 输入或粘贴一个路径，回车就到得去，
/// 框里的字能整段拿走。这一格是「路径」这条信息唯一的进出通道，静默失败等于没有。
/// </summary>
public sealed class PathBoxTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly FolderService _folders;
    private readonly RecordingLauncher _launcher = new();
    private readonly List<NoticeEventArgs> _notices = new();
    private readonly FolderManagerViewModel _viewModel;

    public PathBoxTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(_config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(
            _folders,
            _launcher,
            new FakeDesktopFileService(),
            new FakeFileOperations(),
            new FakeIconService(),
            _config,
            NullLogger<FolderManagerViewModel>.Instance);

        _viewModel.UserNotice += (_, args) => _notices.Add(args);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TypedFolder_GoesIntoThatFolder()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "目标层")).FullName;
        File.WriteAllText(Path.Combine(path, "a.txt"), "x");

        _viewModel.PathBoxText = path;
        _viewModel.GoToPathBoxCommand.Execute(null);

        Assert.True(_viewModel.IsBrowsing);
        Assert.Contains(_viewModel.PreviewItems, item => item.Name == "a.txt");

        // 跳完之后框里就是这一层：接着按 Ctrl+A / Ctrl+C 拿到的就是它。
        Assert.Equal(path, _viewModel.PathBoxText);
    }

    [Fact]
    public void TypedFile_HandsItToTheSystem()
    {
        var path = Path.Combine(_temp.Path, "报告.md");
        File.WriteAllText(path, "内容");

        _viewModel.PathBoxText = path;
        _viewModel.GoToPathBoxCommand.Execute(null);

        Assert.Equal(path, Assert.Single(_launcher.Launched));
        Assert.False(_viewModel.IsBrowsing);
    }

    [Fact]
    public void TypedMissingPath_SaysWhichPathAndStaysPut()
    {
        var path = Path.Combine(_temp.Path, "没这个东西.txt");

        _viewModel.PathBoxText = path;
        _viewModel.GoToPathBoxCommand.Execute(null);

        Assert.Contains("不存在", _notices[^1].Message);
        Assert.Contains(path, _notices[^1].Message);
        Assert.Empty(_launcher.Launched);
        Assert.False(_viewModel.IsBrowsing);
    }

    [Fact]
    public void EmptyBox_IsCalledOutInsteadOfDoingNothing()
    {
        _viewModel.PathBoxText = "   ";
        _viewModel.GoToPathBoxCommand.Execute(null);

        Assert.Contains("路径框是空的", _notices[^1].Message);
    }

    [Fact]
    public void EnvironmentVariablePath_ResolvesLikeTheAddressBarDoes()
    {
        var target = Directory.CreateDirectory(Path.Combine(_temp.Path, "环境变量层")).FullName;

        // 把临时目录本身当成「可展开的路径」来喂：地址栏认 %VAR%，这一格也得认。
        var raw = target.Replace(_temp.Path, "%DM_TEMP%");
        Environment.SetEnvironmentVariable("DM_TEMP", _temp.Path);
        try
        {
            _viewModel.PathBoxText = raw;
            _viewModel.GoToPathBoxCommand.Execute(null);

            Assert.True(_viewModel.IsBrowsing);
            Assert.Equal(target, _viewModel.PathBoxText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DM_TEMP", null);
        }
    }

    [Fact]
    public async Task BoxTracksTheFolderBeingBrowsed()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "跟着走")).FullName;
        await _viewModel.AddPathAsync(path);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());

        Assert.Equal(path, _viewModel.PathBoxText);

        // 退出浏览之后回到卡片：框里跟着换成那张卡片的路径，而不是留着上一层。
        _viewModel.ClosePreview();
        Assert.Equal(path, _viewModel.PathBoxText);
    }

    [Fact]
    public async Task BoxDoesNotClobberWhatTheUserIsTyping()
    {
        var path = Directory.CreateDirectory(Path.Combine(_temp.Path, "别盖我")).FullName;
        await _viewModel.AddPathAsync(path);
        await _viewModel.LoadAsync();

        _viewModel.IsPathBoxEditing = true;
        _viewModel.PathBoxText = @"E:\Desktop\打了一半的";

        _viewModel.PreviewCard(_viewModel.Cards.Single());

        Assert.Equal(@"E:\Desktop\打了一半的", _viewModel.PathBoxText);

        // 失焦（窗口告知）之后立刻补回当前语境。
        _viewModel.IsPathBoxEditing = false;
        Assert.Equal(path, _viewModel.PathBoxText);
    }

    private sealed class RecordingLauncher : IAppLauncherService
    {
        public List<string> Launched { get; } = new();

        public void Launch(string path) => Launched.Add(path);

        public void OpenInExplorer(string path) => Launched.Add(path);

        public void OpenUri(string uri) => Launched.Add(uri);

        public bool HasDefaultProgram(string path) => true;
    }
}
