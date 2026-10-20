using DeskMate.Core.Services;
using DeskMate.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Interop;

/// <summary>
/// 快捷方式里的目标经常是 8.3 短路径（真机上就遇到过 <c>QODERC~1.EXE</c>）。
/// 不还原成长路径，进程名就对不上，运行指示、悬停预览、点图标切回窗口会一起失效，
/// 所以这条链路要单独钉住。
/// </summary>
public class ProcessProbePathTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly StaScheduler _sta = new();

    [Fact]
    public void GetLongPath_RestoresTheShortForm()
    {
        var longPath = Path.Combine(_temp.Path, "Qoder CN 应用目录");
        Directory.CreateDirectory(longPath);

        var shortPath = PathInterop.GetShortPath(longPath);

        // 卷上没启用短文件名时 GetShortPathName 会原样返回，这种情况下断言依然成立。
        Assert.Equal(longPath, PathInterop.GetLongPath(shortPath));
    }

    [Fact]
    public void GetLongPath_LeavesUnknownPathsAlone()
    {
        const string missing = @"Z:\不存在的东西\SOME~1.EXE";

        Assert.Equal(missing, PathInterop.GetLongPath(missing));
    }

    [Fact]
    public async Task ResolveExecutablePathAsync_UnwrapsAShortcutWhoseTargetIsAnEightThreePath()
    {
        var appDirectory = Path.Combine(_temp.Path, "Qoder CN");
        Directory.CreateDirectory(appDirectory);
        var exe = Path.Combine(appDirectory, "Qoder CN.exe");
        File.WriteAllText(exe, string.Empty);

        var shortTarget = PathInterop.GetShortPath(exe);
        var link = Path.Combine(_temp.Path, "Qoder CN.lnk");
        await _sta.RunAsync(() => ShellLink.Create(link, shortTarget));

        var probe = new ProcessProbeService(_sta, NullLogger<ProcessProbeService>.Instance);

        var resolved = await probe.ResolveExecutablePathAsync(link);

        Assert.Equal(exe, resolved);
        Assert.Equal("Qoder CN", Path.GetFileNameWithoutExtension(resolved));
    }

    [Fact]
    public async Task ResolveExecutablePathAsync_KeepsPlainExePathsAsIs()
    {
        var exe = Path.Combine(_temp.Path, "Code.exe");
        File.WriteAllText(exe, string.Empty);

        var probe = new ProcessProbeService(_sta, NullLogger<ProcessProbeService>.Instance);

        Assert.Equal(exe, await probe.ResolveExecutablePathAsync(exe));
    }

    public void Dispose()
    {
        _sta.Dispose();
        _temp.Dispose();
    }
}
