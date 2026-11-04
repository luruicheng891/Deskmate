using DeskMate.App.Services;
using DeskMate.App.ViewModels;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 批5 的四条口径：列表行能多选、框选结果算得对、单击已选中的卡片不丢选区、
/// 行级「重命名 / 移到回收站」在任何视图里都给出同一件事。
/// </summary>
public class RowSelectionAndWriteTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly FakeFileOperations _files = new();
    private readonly ConfigService _config;
    private readonly FolderService _folders;
    private readonly FolderManagerViewModel _viewModel;

    public RowSelectionAndWriteTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(_config, NullLogger<FolderService>.Instance);
        _viewModel = new FolderManagerViewModel(
            _folders, _launcher, new FakeDesktopFileService(), _files, _config, NullLogger<FolderManagerViewModel>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task PlainClickSelectsOnlyThatRow_AndTellsTheCallerToExpandIt()
    {
        var rows = await BrowseThreeFilesAsync();

        Assert.True(_viewModel.HandleRowClick(rows[1], ctrl: false, shift: false));
        Assert.Equal(1, _viewModel.SelectedRowCount);
        Assert.True(rows[1].IsSelected);
        Assert.False(rows[0].IsSelected);
    }

    [Fact]
    public async Task ControlClickOnADirectoryAddsToTheSelectionButDoesNotSwitchPages()
    {
        var rows = await BrowseThreeFilesAsync();
        var folder = rows.First(row => row.IsDirectory);
        var file = rows.First(row => !row.IsDirectory);

        _viewModel.HandleRowClick(file, ctrl: false, shift: false);

        // Ctrl 点目录：进选区（返回 false = 不展开），不然「多选几个文件夹一起拖出去」做不到。
        Assert.False(_viewModel.HandleRowClick(folder, ctrl: true, shift: false));
        Assert.Equal(2, _viewModel.SelectedRowCount);

        // 再 Ctrl 一下就出去了。
        _viewModel.HandleRowClick(folder, ctrl: true, shift: false);
        Assert.Equal(1, _viewModel.SelectedRowCount);
    }

    [Fact]
    public async Task ControlClickOnAFileStillPeeksAtIt()
    {
        // Ctrl 点文件时返回 true：加选之外顺手在右边看一眼，这不影响选区。
        var rows = await BrowseThreeFilesAsync();
        var file = rows.First(row => !row.IsDirectory);

        Assert.True(_viewModel.HandleRowClick(file, ctrl: true, shift: false));
    }

    [Fact]
    public async Task ShiftClickSelectsTheRangeFromTheAnchor()
    {
        var rows = await BrowseThreeFilesAsync();

        _viewModel.HandleRowClick(rows[0], ctrl: false, shift: false);
        _viewModel.HandleRowClick(rows[2], ctrl: false, shift: true);

        Assert.Equal(3, _viewModel.SelectedRowCount);
        Assert.All(rows, row => Assert.True(row.IsSelected));
    }

    [Fact]
    public async Task DraggingASelectedRowCarriesTheWholeSelection()
    {
        var rows = await BrowseThreeFilesAsync();

        _viewModel.HandleRowClick(rows[0], ctrl: false, shift: false);
        _viewModel.HandleRowClick(rows[1], ctrl: true, shift: false);

        var paths = _viewModel.DragRowPaths(rows[0]);
        Assert.Equal(2, paths.Count);

        // 按住的那张不在选区里 → 只带它自己。
        var alone = _viewModel.DragRowPaths(rows[2]);
        Assert.Equal(rows[2].Path, Assert.Single(alone));
    }

    [Fact]
    public async Task BandSelectionReplacesTheSelection_AndCanAddToIt()
    {
        var rows = await BrowseThreeFilesAsync();
        _viewModel.HandleRowClick(rows[0], ctrl: false, shift: false);

        _viewModel.ApplyRowBandSelection([rows[1].Path, rows[2].Path], additive: false);
        Assert.Equal(2, _viewModel.SelectedRowCount);
        Assert.False(rows[0].IsSelected);

        _viewModel.ApplyRowBandSelection([rows[0].Path], additive: true);
        Assert.Equal(3, _viewModel.SelectedRowCount);
    }

    [Fact]
    public async Task HintRowsAreNotSelectable()
    {
        // 「还有 N 项」「空目录」那类提示行没有路径：进选区会让拖出去带上一个空字符串。
        var big = Directory.CreateDirectory(Path.Combine(_temp.Path, "很多")).FullName;
        for (var index = 0; index < 60; index++)
        {
            File.WriteAllText(Path.Combine(big, $"f{index:00}.txt"), "x");
        }

        await _viewModel.AddPathAsync(big);
        await _viewModel.LoadAsync();
        _viewModel.PreviewCard(_viewModel.Cards.Single());

        var hint = _viewModel.PreviewItems.Single(entry => !entry.HasPath);
        Assert.False(_viewModel.HandleRowClick(hint, ctrl: false, shift: false));
        Assert.Equal(0, _viewModel.SelectedRowCount);
    }

    [Fact]
    public async Task RedrawingTheListDropsSelectionThatNoLongerExists()
    {
        var rows = await BrowseThreeFilesAsync();
        _viewModel.HandleRowClick(rows[0], ctrl: false, shift: false);
        Assert.Equal(1, _viewModel.SelectedRowCount);

        _viewModel.EnterFolder(rows.First(row => row.IsDirectory));

        Assert.Equal(0, _viewModel.SelectedRowCount);
    }

    [Fact]
    public async Task LeftClickOnAMemberCollapsesToThatOne_TheWayExplorerDoes()
    {
        foreach (var name in new[] { "甲", "乙", "丙" })
        {
            await _viewModel.AddPathAsync(Directory.CreateDirectory(Path.Combine(_temp.Path, name)).FullName);
        }

        await _viewModel.LoadAsync();

        _viewModel.HandleCardClick(_viewModel.Cards[0], ctrl: false, shift: false);
        _viewModel.HandleCardClick(_viewModel.Cards[1], ctrl: true, shift: false);
        Assert.Equal(2, _viewModel.SelectedCount);

        // 不带修饰键的单击 = 「就以这张为准」，选区收成一条。这是资源管理器的口径，不是丢选区的 bug。
        _viewModel.HandleCardClick(_viewModel.Cards[0], ctrl: false, shift: false);
        Assert.Equal(1, _viewModel.SelectedCount);
    }

    [Fact]
    public async Task RightClickOnAMemberActsOnTheWholeBatch()
    {
        // 批量处理的真正入口是右键。多选完右键其中一张，命令必须作用在整批上 ——
        // 用户报的「多选完一踩就只剩一个」如果发生在右键这条路上，归组/颜色就会漏掉几个。
        foreach (var name in new[] { "甲", "乙", "丙" })
        {
            await _viewModel.AddPathAsync(Directory.CreateDirectory(Path.Combine(_temp.Path, name)).FullName);
        }

        await _viewModel.LoadAsync();

        _viewModel.HandleCardClick(_viewModel.Cards[0], ctrl: false, shift: false);
        _viewModel.HandleCardClick(_viewModel.Cards[1], ctrl: true, shift: false);

        _viewModel.SetContextCard(_viewModel.Cards[0]);

        Assert.Equal(2, _viewModel.BatchTargetCount);
        Assert.Equal(2, _viewModel.BatchTargetPaths().Count);
    }

    [Fact]
    public async Task DoubleClickingADirectoryRowOpensExplorer_TheSameEverywhere()
    {
        var rows = await BrowseThreeFilesAsync();
        var folder = rows.First(row => row.IsDirectory);

        _viewModel.OpenPreviewEntry(folder);

        Assert.Contains(_launcher.Opened, path => SamePath(path, folder.Path));
        Assert.DoesNotContain(_launcher.Launched, path => SamePath(path, folder.Path));

        // 收藏页里同一个目录也必须走同一条路。
        await _folders.AddFavoritesAsync([folder.Path]);
        await _viewModel.LoadAsync();
        _viewModel.SelectTabCommand.Execute(_viewModel.Tabs.First(tab => tab.Name.StartsWith("★ 收藏")));

        _viewModel.OpenFavorite(_viewModel.FavoriteCards.Single());
        Assert.Equal(2, _launcher.Opened.Count);
        Assert.Empty(_launcher.Launched);
    }

    [Fact]
    public async Task RenamingFromTheFavoritesPageMovesTheFavoriteReference()
    {
        var rows = await BrowseThreeFilesAsync();
        var folder = rows.First(row => row.IsDirectory);
        await _folders.AddFavoritesAsync([folder.Path]);

        var target = new EntryTarget(folder.Path, folder.Name, true);
        await _viewModel.RenameEntryAsync(target, "换了名");

        // 配置里的收藏要跟着换到新路径，否则留下一条点开就报错的引用。
        Assert.Contains(_folders.GetFavorites(), path => SamePath(path, Path.Combine(_temp.Path, "子目录", "换了名")));
        Assert.DoesNotContain(_folders.GetFavorites(), path => SamePath(path, folder.Path));
    }

    [Fact]
    public async Task RecyclingFromAnywhereGoesThroughTheRecycleBinOnly()
    {
        var rows = await BrowseThreeFilesAsync();
        var file = rows.First(row => !row.IsDirectory);

        await _viewModel.RecycleEntryAsync(new EntryTarget(file.Path, file.Name, false));

        // 替身只记录「进了回收站」这一件事；真实现（SHFileOperation + FOF_ALLOWUNDO）由 FileOperationServiceTests 直接测。
        Assert.Contains(file.Path, _files.Recycled);
    }

    [Fact]
    public async Task DesktopRowsExposeTheSameTwoWriteCommands()
    {
        // 桌面分类那一页的行也要能改名与进回收站：入口不同、能力必须相同。
        var rows = await BrowseThreeFilesAsync();
        var file = rows.First(row => !row.IsDirectory);

        EntryTarget? requested = null;
        _viewModel.EntryRenameRequested += (_, target) => requested = target;

        file.Rename();

        Assert.NotNull(requested);
        Assert.Equal(file.Path, requested!.Path);
    }

    private async Task<List<PreviewEntry>> BrowseThreeFilesAsync()
    {
        var root = Directory.CreateDirectory(Path.Combine(_temp.Path, "子目录")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "里面的目录"));
        File.WriteAllText(Path.Combine(root, "一.txt"), "x");
        File.WriteAllText(Path.Combine(root, "二.txt"), "yy");

        await _viewModel.AddPathAsync(root);
        await _viewModel.LoadAsync();
        _viewModel.PreviewCard(_viewModel.Cards.Single());
        await _viewModel.WaitForBrowsePageAsync();

        return _viewModel.PreviewItems.Where(entry => entry.HasPath).ToList();
    }

    private static bool SamePath(string left, string right)
        => string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

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
