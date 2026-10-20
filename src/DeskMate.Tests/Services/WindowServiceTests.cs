using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 点击 Dock 图标时该操作哪个窗口 —— 这是「不再重复开实例」的全部决策逻辑，单独测清楚。
/// </summary>
public class WindowServiceTests
{
    private static readonly AppWindowInfo Topmost = new((nint)100, 1, "最上面的窗口");
    private static readonly AppWindowInfo Behind = new((nint)200, 1, "后面的窗口");

    private static WindowService Create() => new(NullLogger<WindowService>.Instance);

    [Fact]
    public void PickClickTarget_ReturnsNull_WhenAppHasNoWindow()
        => Assert.Null(Create().PickClickTarget([], (nint)999));

    [Fact]
    public void PickClickTarget_PrefersTheForegroundWindow_SoClickMinimizesIt()
    {
        var picked = Create().PickClickTarget([Topmost, Behind], Behind.Handle);

        Assert.Equal(Behind, picked);
    }

    [Fact]
    public void PickClickTarget_FallsBackToTopmostWindow_WhenNoneIsForeground()
    {
        var picked = Create().PickClickTarget([Topmost, Behind], (nint)777);

        Assert.Equal(Topmost, picked);
    }

    [Fact]
    public async Task SwitchToAsync_ReturnsFalse_ForUnknownExecutable_SoDockLaunchesInstead()
        => Assert.False(await Create().SwitchToAsync(@"C:\does\not\exist\__nope__.exe"));
}
