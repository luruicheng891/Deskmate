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
        _viewModel = new FolderManagerViewModel(_folders, _launcher, new FakeDesktopFileService(), new FakeFileOperations(), config, NullLogger<FolderManagerViewModel>.Instance);
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
    public async Task PreviewCard_ListsTheFirstPageAndSaysHowManyAreLeft()
    {
        var big = MakeDir("很多文件");
        for (var index = 0; index < 55; index++)
        {
            File.WriteAllText(Path.Combine(big, $"f{index}.txt"), "x");
        }

        var item = await _folders.AddAsync(big);
        await _viewModel.LoadAsync();

        _viewModel.Cards.Single(candidate => candidate.Id == item.Id).Preview();

        // 第一屏只画一批，但末尾必须说清后面还有几条：不吭声就等于告诉用户「这个目录只有 50 个文件」。
        Assert.Equal(51, _viewModel.PreviewItems.Count);
        Assert.Contains("还有 5 项", _viewModel.PreviewItems[50].Name);

        // 滚到底补下一批：剩下的 5 条要真能出现，而且补完后提示行该撤掉。
        _viewModel.LoadMoreIfNeeded();
        Assert.Equal(55, _viewModel.PreviewItems.Count);
        Assert.DoesNotContain(_viewModel.PreviewItems, entry => entry.Name.StartsWith("还有", StringComparison.Ordinal));

        // 再滚不该无限膨胀。
        _viewModel.LoadMoreIfNeeded();
        Assert.Equal(55, _viewModel.PreviewItems.Count);
    }

    [Fact]
    public async Task ToggleSort_SizeDescending_PutsTheBiggestFileFirstAmongFiles()
    {
        // 造一个明显更大的文件（说明.txt 只有 2 字节），要在打开预览之前：
        // 排序只重排已经数到的快照，先打开再写文件等于排一个不含它的表。
        File.WriteAllText(Path.Combine(_root, "大图.png"), new string('x', 4096));
        await OpenPreviewAsync();

        await _viewModel.ToggleSortAsync("Size");

        // 目录恒排在文件前面（资源管理器的第一直觉），换列的默认方向 = 大到小。
        Assert.True(_viewModel.SortDescending);
        var rows = _viewModel.PreviewItems.Where(entry => entry.HasPath).ToList();
        Assert.True(rows[0].IsDirectory);
        Assert.True(rows[1].IsDirectory);
        Assert.Equal("大图.png", rows[2].Name);

        // 再点一次同一列 = 翻转方向，最小的文件排到最前。
        await _viewModel.ToggleSortAsync("Size");
        Assert.False(_viewModel.SortDescending);
        Assert.Equal("说明.txt", _viewModel.PreviewItems.Where(entry => !entry.IsDirectory && entry.HasPath).First().Name);
    }

    [Fact]
    public async Task ToggleSort_Name_UsesNaturalOrderAndIsPersisted()
    {
        var sorted = MakeDir("章节");
        foreach (var name in new[] { "第10章.md", "第2章.md", "第1章.md" })
        {
            File.WriteAllText(Path.Combine(sorted, name), "x");
        }

        var item = await _folders.AddAsync(sorted);
        await _viewModel.LoadAsync();
        _viewModel.Cards.Single(candidate => candidate.Id == item.Id).Preview();

        var names = _viewModel.PreviewItems.Select(entry => entry.Name).ToList();
        Assert.Equal(["第1章.md", "第2章.md", "第10章.md"], names);

        // 排序偏好是全局的：切换一次就要写回 config，下次打开面板还是它。
        await _viewModel.ToggleSortAsync("Date");
        Assert.Equal("Date", await ReadSavedSortAsync());
    }

    private async Task<string> ReadSavedSortAsync()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(_temp.Path, "config.json"));
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("browseSortBy", out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    [Fact]
    public async Task RefreshBrowse_PicksUpFilesOtherProgramsAdded()
    {
        await OpenPreviewAsync();

        // 面板开着的期间，别的程序往这一层写了一个文件。
        File.WriteAllText(Path.Combine(_root, "别人写的.txt"), "x");

        _viewModel.RefreshBrowseCommand.Execute(null);

        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "别人写的.txt");
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
