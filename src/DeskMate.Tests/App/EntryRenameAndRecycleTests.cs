using DeskMate.App.ViewModels;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 浏览列表某一行的「重命名」与「移到回收站」。
/// 这两条是整个面板唯一会动到磁盘的操作，所以除了成功路径，
/// 更要钉住：改坏了什么都不动、用户取消就什么都不动、以及指向它的收藏不能留在原地变失效引用。
/// </summary>
public class EntryRenameAndRecycleTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly FakeDesktopFileService _desktop = new();
    private readonly FakeFileOperations _files = new();
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;
    private readonly List<string> _notices = new();

    public EntryRenameAndRecycleTests()
    {
        var config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(_folders, _launcher, _desktop, _files, NullLogger<FolderManagerViewModel>.Instance);
        _viewModel.UserNotice += (_, args) => _notices.Add(args.Message);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>建一层里有文件的目录、收进面板、走进去，返回那一行。</summary>
    private async Task<PreviewEntry> BrowseAFile(string name = "笔记.md")
    {
        var root = Path.Combine(_temp.Path, "作业");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, name), "第一行\n");
        await _folders.AddAsync(root);
        await _viewModel.LoadAsync();

        _viewModel.PreviewCard(_viewModel.Cards.Single());
        return _viewModel.PreviewItems.Single(entry => entry.HasPath && !entry.IsDirectory);
    }

    [Fact]
    public async Task RenamingARowMovesTheFavoriteThatPointedAtIt()
    {
        var row = await BrowseAFile();
        var old = row.Path;
        await _folders.AddFavoritesAsync([old]);

        await _viewModel.RenameEntryAsync(row, "改过的.md");

        var target = Path.Combine(Path.GetDirectoryName(old)!, "改过的.md");
        Assert.Equal(target, Assert.Single(_files.Renamed).To);

        // 收藏只存路径。磁盘上改了名而不换这条，「★ 收藏」页就留下一条点开就报错的失效引用。
        Assert.Contains(target, _folders.GetFavorites());
        Assert.DoesNotContain(old, _folders.GetFavorites());

        // 列表重数一遍，用户立刻看到新名字。
        Assert.Contains(_viewModel.PreviewItems, entry => entry.Name == "改过的.md");
        Assert.Contains("改名为", Assert.Single(_notices));
    }

    [Fact]
    public async Task RenamingTheFileBeingViewedClosesThePaneButKeepsTheList()
    {
        var row = await BrowseAFile();
        _viewModel.PreviewFile(row.Path);
        Assert.True(_viewModel.IsFilePreviewOpen);

        await _viewModel.RenameEntryAsync(row, "换名");

        // 右边那份预览指向的已经是旧路径了，留着就是让人继续看一个不存在的名字。
        Assert.False(_viewModel.IsFilePreviewOpen);
        Assert.True(_viewModel.IsBrowsing);
        Assert.NotEmpty(_viewModel.PreviewItems);
    }

    [Fact]
    public async Task ARejectedRenameLeavesEverythingWhereItWas()
    {
        var row = await BrowseAFile();
        var old = row.Path;
        _files.RenameThrows = new IOException("同一目录里已经有「笔记.md」了。");

        await _viewModel.RenameEntryAsync(row, "撞名了");

        Assert.Empty(_files.Renamed);
        var stillThere = Assert.Single(_viewModel.PreviewItems, entry => entry.HasPath && !entry.IsDirectory);
        Assert.Equal(old, stillThere.Path);
        Assert.Contains("没改成", Assert.Single(_notices));
    }

    [Fact]
    public async Task RecyclingARowDropsItsFavoriteAndRefreshesTheList()
    {
        var row = await BrowseAFile("要删的.txt");
        var path = row.Path;
        await _folders.AddFavoritesAsync([path]);

        await _viewModel.RecycleEntryAsync(row);

        Assert.Equal(path, Assert.Single(_files.Recycled));
        Assert.Empty(_folders.GetFavorites());
        Assert.DoesNotContain(_viewModel.PreviewItems, entry => entry.Path == path);
        Assert.Contains("回收站", Assert.Single(_notices));
    }

    [Fact]
    public async Task ACancelledRecycleChangesNothing()
    {
        var row = await BrowseAFile("留着吧.txt");
        var path = row.Path;
        await _folders.AddFavoritesAsync([path]);
        _files.RecycleResult = false;

        await _viewModel.RecycleEntryAsync(row);

        // 用户在确认框里点了「否」：收藏、列表、提示都不该有任何动静。
        Assert.Equal(path, Assert.Single(_files.Recycled));
        Assert.Contains(path, _folders.GetFavorites());
        Assert.Contains(_viewModel.PreviewItems, entry => entry.Path == path);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task AFailedRecycleSaysWhyInsteadOfPretending()
    {
        var row = await BrowseAFile("占用中.txt");
        var path = row.Path;
        await _folders.AddFavoritesAsync([path]);
        _files.RecycleThrows = new IOException("文件可能正被别的程序占用。");
        _files.DeleteOnRecycle = false;

        await _viewModel.RecycleEntryAsync(row);

        Assert.Contains(path, _folders.GetFavorites());
        Assert.Contains(_viewModel.PreviewItems, entry => entry.Path == path);
        Assert.Contains("没删成", Assert.Single(_notices));
    }

    [Fact]
    public async Task PlaceholderRowsIgnoreRenameAndRecycle()
    {
        await BrowseAFile();

        // 「仅显示前 50 项」那类提示行没有路径，菜单项虽然按 HasPath 收着，命令本身也要挡住。
        var placeholder = new PreviewEntry(_viewModel, "（空目录或无读取权限）", string.Empty, false);

        var renamed = 0;
        _viewModel.EntryRenameRequested += (_, _) => renamed++;
        _viewModel.EntryDeleteConfirmRequested += (_, _) => renamed++;

        placeholder.RenameCommand.Execute(null);
        placeholder.RecycleCommand.Execute(null);

        Assert.Equal(0, renamed);
    }

    private sealed class RecordingLauncher : Core.Interfaces.IAppLauncherService
    {
        public List<string> Launched { get; } = new();

        public void Launch(string path) => Launched.Add(path);

        public void OpenInExplorer(string path)
        {
        }

        public void OpenUri(string uri)
        {
        }

        public bool HasDefaultProgram(string path) => true;
    }
}
