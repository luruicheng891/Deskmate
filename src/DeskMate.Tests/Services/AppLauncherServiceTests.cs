using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 「该由谁来接这个文件」的判定。启动本身要真拉起程序，单测里不碰；
/// 这里锁的是那些一错就让用户读到「点了没反应」的判断。
/// </summary>
public sealed class AppLauncherServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppLauncherService _launcher = new(NullLogger<AppLauncherService>.Instance, new NoWindows());

    /// <summary>
    /// 快捷方式与可执行文件本身就是 shell 能直接跑起来的东西，不该去问关联表：
    /// 实测拿 <c>.lnk</c> 问得到 null、拿 <c>.exe</c> 问得到 <c>%1</c> 这种占位串。
    /// 判成「没有能打开它的程序」就等于把 Dock 与启动台的图标自己拦在门外。
    /// </summary>
    [Theory]
    [InlineData("微信.lnk")]
    [InlineData("Code.exe")]
    [InlineData("setup.msi")]
    [InlineData("start.bat")]
    public void ShortcutAndExecutable_AreTheirOwnProgram(string fileName)
    {
        var path = _temp.File(fileName);
        File.WriteAllText(path, string.Empty);

        Assert.True(_launcher.HasDefaultProgram(path));
    }

    [Fact]
    public void UnregisteredExtension_StillSaysNoProgramCanOpenIt()
    {
        var path = _temp.File("notes.qqzunknown");
        File.WriteAllText(path, string.Empty);

        Assert.False(_launcher.HasDefaultProgram(path));
    }

    [Fact]
    public void Directory_IsOpenedByTheShellItself()
    {
        Assert.True(_launcher.HasDefaultProgram(AppContext.BaseDirectory));
    }

    [Fact]
    public void MissingTarget_FailsLoudlyInsteadOfPretending()
    {
        Assert.Throws<FileNotFoundException>(() => _launcher.Launch(_temp.File("does-not-exist.lnk")));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>这些判定都不看窗口，给一个「永远没有窗口」的窗口服务就够了。</summary>
    private sealed class NoWindows : Core.Interfaces.IWindowService
    {
        public Task<IReadOnlyList<Core.Models.AppWindowInfo>> GetWindowsAsync(string executablePath)
            => Task.FromResult<IReadOnlyList<Core.Models.AppWindowInfo>>([]);

        public Task<IReadOnlyList<Core.Models.AppWindowInfo>> GetWindowsByProcessNameAsync(string processName)
            => Task.FromResult<IReadOnlyList<Core.Models.AppWindowInfo>>([]);

        public Core.Models.AppWindowInfo? PickClickTarget(IReadOnlyList<Core.Models.AppWindowInfo> windows, nint foregroundHandle)
            => null;

        public Task<bool> SwitchToAsync(string executablePath) => Task.FromResult(false);

        public bool Activate(Core.Models.AppWindowInfo window) => false;

        public bool Close(Core.Models.AppWindowInfo window) => false;

        public Task<int> CloseAllAsync(string executablePath) => Task.FromResult(0);
    }
}
