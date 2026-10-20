using System.Windows;
using System.Windows.Interop;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Xunit;

namespace DeskMate.Tests.Interop;

/// <summary>
/// 「这个窗口算不算全屏」的两条核心规则。判错的代价是 Dock 和遮罩盖在用户的画面上，
/// 所以几何那一条和「自己的窗口不算」这一条都要钉住。
/// </summary>
public class FullscreenDetectorTests : IDisposable
{
    private static readonly StaScheduler Sta = new();
    private readonly List<Window> _windows = new();

    [Theory]
    [InlineData(0, 0, 1920, 1080)]    // 正好铺满
    [InlineData(-7, -7, 1927, 1087)]  // 带 DWM 不可见边框，比显示器还大
    [InlineData(0, 0, 1921, 1081)]    // DPI 取整多出一像素
    public void CoveringTheMonitorCountsAsFullscreen(int l, int t, int r, int b)
        => Assert.True(FullscreenDetector.CoversMonitor(Rect(l, t, r, b), Monitor));

    [Theory]
    [InlineData(0, 0, 1920, 1032)]    // 最大化但任务栏还占着底下一条：不该让路
    [InlineData(0, 0, 1919, 1080)]    // 差一像素没盖住
    [InlineData(10, 10, 1930, 1090)]  // 往右下挪了，左上没盖住
    public void NotCoveringTheMonitorDoesNot(int l, int t, int r, int b)
        => Assert.False(FullscreenDetector.CoversMonitor(Rect(l, t, r, b), Monitor));

    [Fact]
    public async Task OurOwnWindowNeverCountsAsFullscreen()
    {
        // 自己的窗口铺满屏幕也不算全屏 —— 否则 Dock 一动就把自己判成「有全屏应用」，
        // 让路逻辑会把自己收掉，表现为「Dock 不知道为何消失了」。
        var handle = await Sta.RunAsync(() =>
        {
            var window = new Window
            {
                Title = "DeskMate 全屏判定测试窗",
                Width = 320,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 40,
                Top = 40,
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            // Show 之后才有真实、可见的 HWND —— 否则第一个检查（窗口不可见）就把后面几条挡在前面了。
            window.Show();
            _windows.Add(window);

            return new WindowInteropHelper(window).Handle;
        });

        Assert.True(handle != IntPtr.Zero);
        Assert.False(FullscreenDetector.IsFullscreen(handle));
        Assert.Equal("是我们自己的窗口", FullscreenDetector.Explain(handle));
    }

    public void Dispose()
    {
        foreach (var window in _windows)
        {
            _ = Sta.RunAsync(() =>
            {
                window.Close();
                return true;
            });
        }
    }

    private static WindowInterop.WinRect Monitor => Rect(0, 0, 1920, 1080);

    private static WindowInterop.WinRect Rect(int left, int top, int right, int bottom) => new()
    {
        Left = left,
        Top = top,
        Right = right,
        Bottom = bottom,
    };
}
