using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 文件夹面板的预览下钻：点进子目录要在面板里继续深入，而不是每次都把外部资源管理器叫起来。
/// 用真实的 FolderService（配置写到临时目录），所以路径、列举、返回栈都是现场行为。
/// </summary>
public class FolderPreviewNavigationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;
    private readonly string _root;

    public FolderPreviewNavigationTests()
    {
        _root = MakeDir("入口");
        MakeDir(Path.Combine("入口", "子目录A"));
        MakeDir(Path.Combine("入口", "子目录A", "更深一层"));
        MakeDir(Path.Combine("入口", "子目录B"));
        File.WriteAllText(Path.Combine(_temp.Path, "入口", "说明.txt"), "内容");

        var config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(_folders, _launcher, new FakeDesktopFileService(), new FakeFileOperations(), NullLogger<FolderManagerViewModel>.Instance);
    }

    [Fact]
    public async Task PreviewCard_ListsChildrenAndLeavesRealFoldersAlone()
    {
        await OpenPreviewAsync();

        Assert.True(_viewModel.IsPreviewOpen);
        Assert.Contains(_viewModel.PreviewItems, entry => entry is { IsDirectory: true, Name: "子目录A" });

        // 核心原则：浏览只是读，磁盘上的目录与文件一个都不能动。
        Assert.True(Directory.Exists(Path.Combine(_root, "子目录A", "更深一层")));
        Assert.Equal("内容", File.ReadAllText(Path.Combine(_root, "说明.txt")));
    }

    [Fact]
    public async Task PreviewCard_TellsTheUserTheListIsTruncated()
    {
        var big = MakeDir("很多文件");
        for (var index = 0; index < 55; index++)
        {
            File.WriteAllText(Path.Combine(big, $"f{index}.txt"), "x");
        }

        var item = await _folders.AddAsync(big);
        await _viewModel.LoadAsync();

        _viewModel.Cards.Single(candidate => candidate.Id == item.Id).Preview();

        // 只画 50 条而不说后面还有，用户就会认为这个目录只有 50 个文件。
        Assert.Equal(51, _viewModel.PreviewItems.Count);
        Assert.Contains("仅显示前 50 项", _viewModel.PreviewItems[50].Name);
    }

    [Fact]
    public async Task EnterFolder_GoesIntoTheSubfolderInsteadOfOpeningExplorer()
    {
        await OpenPreviewAsync();

        _viewModel.EnterFolder(Entry("子目录A"));

        Assert.Contains(_viewModel.PreviewItems, entry => entry is { IsDirectory: true, Name: "更深一层" });
        Assert.DoesNotContain(_viewModel.PreviewItems, entry => entry.Name == "子目录B");
        Assert.Empty(_launcher.Opened);
    }

    [Fact]
    public async Task GoBackPreview_StepsOutOneLevelAtATimeThenCloses()
    {
        await OpenPreviewAsync();
        _viewModel.EnterFolder(Entry("子目录A"));
        _viewModel.EnterFolder(Entry("更深一层"));

        _viewModel.GoBackPreviewCommand.Execute(null);
        // 退回上一层 = 站在「子目录A」里看它的内容，所以列出来的是更深一层。
        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "更深一层");

        _viewModel.GoBackPreviewCommand.Execute(null);
        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "子目录B");

        // 已经退回入口，再按一次就是关掉面板，而不是停在原地毫无反应。
        _viewModel.GoBackPreviewCommand.Execute(null);
        Assert.False(_viewModel.IsPreviewOpen);
    }

    [Fact]
    public async Task EnteringTheSameFolderTwice_DoesNotStackUpIdenticalHistory()
    {
        await OpenPreviewAsync();
        var sub = Entry("子目录A");

        _viewModel.EnterFolder(sub);
        _viewModel.EnterFolder(sub);
        _viewModel.GoBackPreviewCommand.Execute(null);

        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "子目录B");
    }

    [Fact]
    public async Task EnteringAFile_NeitherNavigatesNorLaunchesAnything()
    {
        await OpenPreviewAsync();

        _viewModel.EnterFolder(Entry("说明.txt"));

        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "子目录B");
        Assert.Empty(_launcher.Opened);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task DoubleClickingAFile_OpensTheFileItselfNotItsFolder()
    {
        await OpenPreviewAsync();
        var file = Entry("说明.txt");

        _viewModel.OpenPreviewEntry(file);

        // Launch = 交给关联程序直接打开；OpenInExplorer 只会跳到它所在目录选中它，还要再点一次。
        Assert.Equal(new[] { file.Path }, _launcher.Launched);
        Assert.Empty(_launcher.Opened);
    }

    public void Dispose() => _temp.Dispose();

    private async Task OpenPreviewAsync()
    {
        await _folders.AddAsync(_root);
        await _viewModel.LoadAsync();
        _viewModel.Cards.Single().Preview();
    }

    private PreviewEntry Entry(string name)
        => _viewModel.PreviewItems.Single(entry => entry.Name == name);

    private string MakeDir(string relative)
    {
        var path = Path.Combine(_temp.Path, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingLauncher : IAppLauncherService
    {
        public List<string> Opened { get; } = new();

        public List<string> Launched { get; } = new();

        public void Launch(string path) => Launched.Add(path);

        public void OpenInExplorer(string path) => Opened.Add(path);

        public void OpenUri(string uri)
        {
        }

        /// <summary>这些替身不关心关联查询，一律回答「有」，让「双击交给系统」那条主路照旧走通。</summary>
        public bool HasDefaultProgram(string path) => true;
    }
}
