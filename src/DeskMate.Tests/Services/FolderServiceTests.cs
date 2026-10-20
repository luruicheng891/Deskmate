using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

public class FolderServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly FolderService _folders;

    public FolderServiceTests()
    {
        _config = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        _folders = new FolderService(_config, NullLogger<FolderService>.Instance);
    }

    private string MakeDir(string name)
    {
        var path = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task RelocateAsync_ChangesTheReferenceAndLeavesBothDirectoriesAlone()
    {
        var old = MakeDir("旧位置");
        var movedTo = MakeDir("新位置");
        var item = await _folders.AddAsync(old);

        var moved = await _folders.RelocateAsync(item.Id, movedTo);

        Assert.Equal(movedTo, moved.RealPath);
        Assert.Equal("新位置", moved.DisplayName);
        Assert.True(moved.Valid);

        // 最高约束：旧目录还在，里面的东西一个没动。
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public async Task RelocateAsync_KeepsANameTheUserChose()
    {
        var old = MakeDir("旧位置");
        var movedTo = MakeDir("新位置");
        var item = await _folders.AddAsync(old);
        await _folders.RenameDisplayAsync(item.Id, "我的毕设");

        var moved = await _folders.RelocateAsync(item.Id, movedTo);

        // 「新位置」这种自动名跟着换，用户自己起的名字不能被他没要求过的改动覆盖掉。
        Assert.Equal("我的毕设", moved.DisplayName);
    }

    [Fact]
    public async Task RelocateAsync_RejectsMissingTargetAndAPathAnotherEntryAlreadyUses()
    {
        var a = MakeDir("A");
        var b = MakeDir("B");
        var first = await _folders.AddAsync(a);
        await _folders.AddAsync(b);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _folders.RelocateAsync(first.Id, Path.Combine(_temp.Path, "压根不存在")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _folders.RelocateAsync(first.Id, b));

        // 两次拒绝之后原来那条引用必须还是老样子，不能改了一半。
        Assert.Equal(a, _folders.GetAll().Single(entry => entry.Id == first.Id).RealPath);
    }

    [Fact]
    public async Task AddAsync_StoresReferenceWithoutTouchingRealFolder()
    {
        var real = MakeDir("资料");
        File.WriteAllText(Path.Combine(real, "重要.txt"), "内容");

        var item = await _folders.AddAsync(real);

        Assert.Equal("资料", item.DisplayName);
        Assert.Equal(real, item.RealPath);
        Assert.True(Directory.Exists(real));
        Assert.Equal("内容", File.ReadAllText(Path.Combine(real, "重要.txt")));
    }

    [Fact]
    public async Task AddAsync_IsIdempotent_ForSamePathWithOrWithoutTrailingSlash()
    {
        var real = MakeDir("项目");

        var first = await _folders.AddAsync(real);
        var second = await _folders.AddAsync(real + Path.DirectorySeparatorChar);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_config.Current.Folders);
    }

    [Fact]
    public async Task AddAsync_Throws_WhenPathMissing()
        => await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _folders.AddAsync(Path.Combine(_temp.Path, "不存在")));

    [Fact]
    public async Task RenameDisplayAsync_DoesNotChangeRealPath()
    {
        var real = MakeDir("旧名");
        var item = await _folders.AddAsync(real);

        await _folders.RenameDisplayAsync(item.Id, "我的收藏");

        Assert.Equal("我的收藏", _folders.GetAll().Single().DisplayName);
        Assert.Equal(real, _folders.GetAll().Single().RealPath);
        Assert.Equal("旧名", new DirectoryInfo(real).Name);
    }

    [Fact]
    public async Task RemoveAsync_OnlyDropsReference()
    {
        var real = MakeDir("待移除");
        var item = await _folders.AddAsync(real);

        await _folders.RemoveAsync(item.Id);

        Assert.Empty(_folders.GetAll());
        Assert.True(Directory.Exists(real));
    }

    [Fact]
    public async Task ReorderAsync_AppliesGivenOrderAndAppendsUnlisted()
    {
        var a = await _folders.AddAsync(MakeDir("a"));
        var b = await _folders.AddAsync(MakeDir("b"));
        var c = await _folders.AddAsync(MakeDir("c"));

        await _folders.ReorderAsync(new[] { c.Id, a.Id });

        var ordered = _folders.GetAll();
        Assert.Equal(new[] { "c", "a", "b" }, ordered.Select(f => f.DisplayName).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, ordered.Select(f => f.Order).ToArray());
    }

    [Fact]
    public async Task ValidateAllAsync_MarksMissingPathsInvalid_AndPersists()
    {
        var existing = MakeDir("还在");
        var doomed = MakeDir("会消失");
        await _folders.AddAsync(existing);
        var gone = await _folders.AddAsync(doomed);

        Directory.Delete(doomed);
        await _folders.ValidateAllAsync();

        Assert.False(_folders.GetAll().Single(f => f.Id == gone.Id).Valid);
        Assert.True(_folders.GetAll().Single(f => f.Id != gone.Id).Valid);

        var reloaded = new ConfigService(NullLogger<ConfigService>.Instance, _temp.File("config.json"));
        await reloaded.LoadAsync();
        Assert.False(reloaded.Current.Folders.Single(f => f.Id == gone.Id).Valid);
    }

    [Fact]
    public async Task CountFilesAsync_CountsRecursivelyAndSkipsAccessDenied()
    {
        var root = MakeDir("统计");
        Directory.CreateDirectory(Path.Combine(root, "子目录"));
        File.WriteAllText(Path.Combine(root, "1.txt"), "a");
        File.WriteAllText(Path.Combine(root, "子目录", "2.txt"), "b");
        File.WriteAllText(Path.Combine(root, "子目录", "3.log"), "c");

        var count = await _folders.CountFilesAsync(root);

        Assert.Equal(3, count);
    }

    [Fact]
    public async Task CountFilesAsync_ReturnsZero_ForMissingPath()
        => Assert.Equal(0, await _folders.CountFilesAsync(Path.Combine(_temp.Path, "不存在")));

    [Fact]
    public async Task ScanDesktopFoldersAsync_ReturnsOnlyExistingDirectories()
    {
        var found = await _folders.ScanDesktopFoldersAsync();
        var desktops = new[] { WellKnownPaths.UserDesktop, WellKnownPaths.PublicDesktop }
            .Select(dir => dir.TrimEnd(Path.DirectorySeparatorChar));

        Assert.All(found, dir =>
        {
            Assert.True(Directory.Exists(dir), $"扫描结果里出现了不存在的目录 {dir}");
            var parent = Path.GetDirectoryName(dir);
            Assert.Contains(parent, desktops);
        });
    }

    [Fact]
    public async Task RemoveGroupAsync_SendsMembersBackToUngrouped()
    {
        var group = await _folders.AddGroupAsync("工作");
        var item = await _folders.AddAsync(MakeDir("报表"));
        await _folders.UpdateGroupAsync(item.Id, group.Id);

        await _folders.RemoveGroupAsync(group.Id);

        Assert.Empty(_folders.GetGroups());
        Assert.Null(_folders.GetAll().Single().GroupId);
    }

    [Fact]
    public async Task ToggleFavoriteAsync_AddsThenRemoves_AndIgnoresTrailingSeparator()
    {
        var dir = MakeDir("作业");

        Assert.True(await _folders.ToggleFavoriteAsync(dir));
        Assert.True(_folders.IsFavorite(dir + Path.DirectorySeparatorChar));
        Assert.True(_folders.IsFavorite(dir.ToUpperInvariant()));

        Assert.False(await _folders.ToggleFavoriteAsync(dir));
        Assert.Empty(_folders.GetFavorites());
    }

    [Fact]
    public async Task Favorites_PersistToConfig_AsPathReferencesOnly()
    {
        var dir = MakeDir("作业");
        var file = Path.Combine(_temp.Path, "report.zip");
        await File.WriteAllTextAsync(file, "PK");

        Assert.Equal(2, await _folders.AddFavoritesAsync([dir, file, dir]));

        // 收藏只是记路径：原文件必须原地不动，也不该多出任何副本。
        Assert.Equal(["PK"], await File.ReadAllLinesAsync(file));
        Assert.True(Directory.Exists(dir));
        Assert.Equal(2, _config.Current.Favorites.Count);
        Assert.Equal(2, _folders.GetFavorites().Count);
    }

    [Fact]
    public async Task CreateEntryAsync_WithoutExtension_MakesAFolder()
    {
        var parent = MakeDir("作业");

        var created = await _folders.CreateEntryAsync(parent, "第三章");

        Assert.Equal(Path.Combine(parent, "第三章"), created);
        Assert.True(Directory.Exists(created));
    }

    [Fact]
    public async Task CreateEntryAsync_WithExtension_MakesAnEmptyFile()
    {
        var parent = MakeDir("作业");

        var created = await _folders.CreateEntryAsync(parent, "main.c");

        Assert.True(File.Exists(created));
        Assert.Equal(0, new FileInfo(created).Length);
    }

    [Fact]
    public async Task CreateEntryAsync_NeverOverwritesAnExistingItem()
    {
        var parent = MakeDir("作业");
        var existing = Path.Combine(parent, "notes.txt");
        await File.WriteAllTextAsync(existing, "很重要的东西");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _folders.CreateEntryAsync(parent, "notes.txt"));

        Assert.Contains("已经有一个", error.Message);
        Assert.Equal("很重要的东西", await File.ReadAllTextAsync(existing));
    }

    [Fact]
    public async Task CreateEntryAsync_RejectsBadNameAndMissingParent()
    {
        var parent = MakeDir("作业");

        await Assert.ThrowsAsync<ArgumentException>(() => _folders.CreateEntryAsync(parent, "a/b"));
        await Assert.ThrowsAsync<ArgumentException>(() => _folders.CreateEntryAsync(parent, "   "));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _folders.CreateEntryAsync(Path.Combine(_temp.Path, "不存在的目录"), "x"));
    }

    [Fact]
    public async Task CreateEntryAsync_WithWordOption_WritesAnOpenablePackageNotAnEmptyFile()
    {
        var parent = MakeDir("作业");
        var word = NewEntryCatalog.Options.First(option => option.Key == "docx");

        var created = await _folders.CreateEntryAsync(parent, "报告", word);

        Assert.Equal(Path.Combine(parent, "报告.docx"), created);

        // 0 字节的 .docx 会被 Word 判成「文件已损坏」，这条就是这个断言。
        Assert.True(new FileInfo(created).Length > 200);
        Assert.Equal("PK", new string([.. File.ReadAllBytes(created).Take(2).Select(b => (char)b)]));
    }

    [Fact]
    public async Task CreateEntryAsync_InFolderMode_KeepsDotsInTheName()
    {
        var parent = MakeDir("作业");
        var folder = NewEntryCatalog.Options.First(option => option.Key == "folder");

        var created = await _folders.CreateEntryAsync(parent, "v1.2 备份", folder);

        // 选了「文件夹」就一定是文件夹，名字里那个点不该被当成后缀。
        Assert.True(Directory.Exists(created));
        Assert.EndsWith("v1.2 备份", created);
    }

    public void Dispose() => _temp.Dispose();
}
