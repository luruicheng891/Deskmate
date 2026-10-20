using System.Windows.Threading;
using DeskMate.App.Services;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskMate.Tests.App;

/// <summary>
/// 遮罩取帧泵的契约：壁纸在动就要一帧帧跟上去，不动就退到慢档别白烧 CPU，
/// 全都取不到时才允许退回底色。这三件事在屏幕上只能靠眼睛看，所以在这里用假帧钉死。
/// </summary>
public class BackdropPumpTests : IDisposable
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(200);

    private readonly StaScheduler _sta = new();
    private readonly Dispatcher _dispatcher;

    public BackdropPumpTests()
    {
        // 泵把画面用 Dispatcher 送回「UI 线程」，测试里这条 STA 线程就是那个收件方，
        // 它一直在泵消息，回调才会真的执行。
        _dispatcher = _sta.RunAsync(() => Dispatcher.CurrentDispatcher).Result;
    }

    [Fact]
    public void KeepsRepaintingWhileTheWallpaperMoves()
    {
        var round = 0;
        var painted = 0;
        using var pump = new BackdropPump(
            () => Frame((byte)Interlocked.Increment(ref round)),
            _ => Interlocked.Increment(ref painted),
            new BackdropFramePacer(Fast, Slow),
            _dispatcher,
            NullLogger.Instance);

        pump.Start();
        WaitUntil(() => Volatile.Read(ref painted) >= 6);
        pump.Stop();

        // 动态壁纸下就该一直在最快档上追；退让到慢档的话，屏幕上又变回一张定格。
        Assert.True(Volatile.Read(ref painted) >= 6, $"只重画了 {Volatile.Read(ref painted)} 帧，没跟上");
        Assert.Equal(Fast, pump.CurrentDelay);
    }

    [Fact]
    public void SettlesDownWhenNothingChanges()
    {
        var painted = 0;
        using var pump = new BackdropPump(
            () => Frame(0x80),
            _ => Interlocked.Increment(ref painted),
            new BackdropFramePacer(Fast, Slow),
            _dispatcher,
            NullLogger.Instance);

        pump.Start();
        WaitUntil(() => pump.CurrentDelay >= Slow);
        pump.Stop();

        // 静态壁纸：抓了很多次，但只往屏幕上刷过一次，而且越来越省 —— 和以前写死 30 秒一样不花钱。
        Assert.Equal(1, Volatile.Read(ref painted));
        Assert.Equal(Slow, pump.CurrentDelay);
    }

    [Fact]
    public void FallsBackOnlyUntilItGetsSomething()
    {
        var round = 0;
        var fallbacks = 0;
        var bitmaps = 0;
        using var pump = new BackdropPump(
            () => Interlocked.Increment(ref round) <= 3 ? null : Frame(0x66),
            frame =>
            {
                if (frame is null)
                {
                    Interlocked.Increment(ref fallbacks);
                }
                else
                {
                    Interlocked.Increment(ref bitmaps);
                }
            },
            new BackdropFramePacer(Fast, Slow),
            _dispatcher,
            NullLogger.Instance);

        // 一张都没取到过的时候（锁屏、桌面窗口换了实现）才退回底色；桌面一回来就必须把壁纸刷上，
        // 不然左下角会一直糊着一块主题色。
        pump.Start();
        WaitUntil(() => Volatile.Read(ref bitmaps) >= 1);
        pump.Stop();

        Assert.True(Volatile.Read(ref fallbacks) >= 1, "一直没取到内容时应当退回底色");
        Assert.Equal(1, Volatile.Read(ref bitmaps));
        Assert.Equal(1, pump.PublishedFrames);
    }

    [Fact]
    public void KeepsTheLastFrameWhenTheDesktopBrieflyGoesAway()
    {
        var round = 0;
        var fallbacks = 0;
        using var pump = new BackdropPump(
            () => Interlocked.Increment(ref round) == 1 ? Frame(0x77) : null,
            frame =>
            {
                if (frame is null)
                {
                    Interlocked.Increment(ref fallbacks);
                }
            },
            new BackdropFramePacer(Fast, Slow),
            _dispatcher,
            NullLogger.Instance);

        pump.Start();
        WaitUntil(() => pump.CurrentDelay >= Slow);
        pump.Stop();

        // 已经画着一帧了就别动它：全屏切换、动态壁纸重启会连着几轮取不到，
        // 为此把壁纸换成一块纯色，比慢一帧难看得多。
        Assert.Equal(0, Volatile.Read(ref fallbacks));
        Assert.Equal(1, pump.PublishedFrames);
    }

    public void Dispose() => _sta.Dispose();

    private void WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Thread.Sleep(10);
        }
    }

    /// <summary>一条 8x2 的假带子：整块同一颜色，换个 shade 就是「画面变了」。</summary>
    private static BandPixels Frame(byte shade)
    {
        var pixels = new byte[8 * 4 * 2];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = shade;
            pixels[i + 1] = shade;
            pixels[i + 2] = shade;
        }

        return new BandPixels(pixels, 8, 2, 8 * 4);
    }
}
