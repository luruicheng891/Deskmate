using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 桌面文件视图：只收文件、忽略系统与隐藏项，并且目录一变就能收到去抖后的通知。
/// 服务指向临时目录 —— 让测试去写用户真实桌面是不该发生的。
/// </summary>
public class DesktopFileServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly DesktopFileService _service;

    public DesktopFileServiceTests()
    {
        _service = new DesktopFileService(NullLogger<DesktopFileService>.Instance, [_temp.Path]);
    }

    [Fact]
    public void Refresh_TakesFilesOnlyAndSkipsFoldersAndSystemEntries()
    {
        File.WriteAllText(_temp.File("截图.png"), "x");
        File.WriteAllText(_temp.File("desktop.ini"), "x");
        File.WriteAllText(_temp.File("私密.docx"), "x");
        new FileInfo(_temp.File("私密.docx")).Attributes = FileAttributes.Hidden;
        Directory.CreateDirectory(_temp.File("子文件夹"));

        // 核心原则：读桌面不等于改桌面，这里只是列举。
        Assert.Equal(new[] { "截图.png" }, Names(_service.Refresh()));
    }

    [Fact]
    public void Refresh_CategorizesByExtensionAndSortsByName()
    {
        File.WriteAllText(_temp.File("b.pdf"), "x");
        File.WriteAllText(_temp.File("a.zip"), "x");
        File.WriteAllText(_temp.File("c.png"), "x");
        File.WriteAllText(_temp.File("d.exe"), "x");

        var files = _service.Refresh();

        Assert.Equal(new[] { "a.zip", "b.pdf", "c.png", "d.exe" }, Names(files));
        Assert.Equal(
            new[] { DesktopFileCategory.Archive, DesktopFileCategory.Document, DesktopFileCategory.Image, DesktopFileCategory.Other },
            files.Select(file => file.Category));
    }

    [Fact]
    public async Task CreatingAFile_RaisesOneDebouncedChangeAndShowsIt()
    {
        await AfterChangeAsync(() => File.WriteAllText(_temp.File("新文档.txt"), "内容"));

        Assert.Contains(_service.Files, file => file.Name == "新文档.txt" && file.Category == DesktopFileCategory.Document);
    }

    [Fact]
    public async Task DeletingAFile_DropsItFromTheSnapshot()
    {
        var path = _temp.File("待删.png");
        await AfterChangeAsync(() => File.WriteAllText(path, "x"));
        await AfterChangeAsync(() => File.Delete(path));

        Assert.DoesNotContain(_service.Files, file => file.Name == "待删.png");
    }

    public void Dispose() => _service.Dispose();

    private static string[] Names(IReadOnlyList<DesktopFile> files)
        => files.Select(file => file.Name).ToArray();

    /// <summary>
    /// 做一个改动桌面的动作，并等去抖后的 <see cref="IDesktopFileService.Changed"/>。
    /// 监听是异步的，用 Sleep 猜时间只会让测试偶发红，所以直接等事件、超时即判定监听没工作。
    /// </summary>
    private async Task AfterChangeAsync(Action action, int timeoutMs = 8000)
    {
        var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => signaled.TrySetResult();

        _service.Changed += handler;
        try
        {
            action();
            await signaled.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        finally
        {
            _service.Changed -= handler;
        }
    }
}
