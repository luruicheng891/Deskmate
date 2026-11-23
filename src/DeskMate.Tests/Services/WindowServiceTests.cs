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

    /// <summary>
    /// 壳进程兜底的核心：沿父进程号往下收后代。有些应用的图标对应启动器壳，
    /// 真窗口开在子进程里 —— 不收的话用户每点一次就多开一个新实例。
    /// </summary>
    public class CollectDescendantsTests
    {
        [Fact]
        public void FindsChildrenAndGrandchildren_ButNotTheThirdLevel()
        {
            var parents = new Dictionary<int, int>
            {
                [10] = 1, // 根 1 的孩子
                [11] = 1, // 另一个孩子
                [20] = 10, // 孙辈（启动器 → 引导器 → 主程序）
                [30] = 20, // 曾孙 —— 超出两代，不收
            };

            var found = WindowService.CollectDescendants(parents, [1], depth: 2);

            Assert.Contains(10, found);
            Assert.Contains(11, found);
            Assert.Contains(20, found);
            Assert.DoesNotContain(30, found);
        }

        [Fact]
        public void OnlyFollowsTheRequestedRoots()
        {
            var parents = new Dictionary<int, int>
            {
                [10] = 1,
                [99] = 42, // 别人家的进程树，不碰
            };

            var found = WindowService.CollectDescendants(parents, [1], depth: 2);

            Assert.Equal(new[] { 10 }, found);
        }

        [Fact]
        public void CyclicParentLinks_DoNotLoopOrDuplicate()
        {
            var parents = new Dictionary<int, int>
            {
                [7] = 8,
                [8] = 7,
                [10] = 1,
            };

            var found = WindowService.CollectDescendants(parents, [1], depth: 5);

            Assert.Equal(new[] { 10 }, found);
        }
    }
}
