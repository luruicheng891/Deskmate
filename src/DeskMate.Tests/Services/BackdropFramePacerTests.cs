using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 任务栏遮罩的帧差自适应：动态壁纸要跟上、静态壁纸别白烧 CPU，这两件事全靠这一个类，
/// 所以把节奏和「什么算变了」都钉死在这里，而不是到屏幕上靠眼睛看。
/// </summary>
public class BackdropFramePacerTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(30);

    [Fact]
    public void StartsAtTheFastestCadenceAndBacksOffOnlyAfterEnoughStillFrames()
    {
        var pacer = new BackdropFramePacer(Fast, Slow);
        Assert.Equal(Fast, pacer.CurrentDelay);

        var pixels = Frame(0x10);

        Assert.True(pacer.Observe(pixels));
        Assert.Equal(Fast, pacer.CurrentDelay);

        // 只有一帧相同还不算「静止」：两帧一循环的动画会隔一次就取到一模一样的画面，
        // 连续两帧相同才允许退一档。
        Assert.False(pacer.Observe(pixels));
        Assert.Equal(Fast, pacer.CurrentDelay);

        Assert.False(pacer.Observe(pixels));
        Assert.Equal(TimeSpan.FromMilliseconds(600), pacer.CurrentDelay);
    }

    [Fact]
    public void EveryStillRoundMakesItCheaperUntilItSettlesAtTheCeiling()
    {
        var pacer = new BackdropFramePacer(Fast, Slow, stillFramesBeforeBackoff: 1);
        var pixels = Frame(0x20);

        Assert.True(pacer.Observe(pixels));
        for (var round = 0; round < 20; round++)
        {
            Assert.False(pacer.Observe(pixels));
        }

        // 静态壁纸最终就该退到以前那个写死的 30 秒 —— 这条是「别越退越贵」的上界。
        Assert.Equal(Slow, pacer.CurrentDelay);
    }

    [Fact]
    public void DetectingMotionTightensBackTowardTheFloor()
    {
        var pacer = new BackdropFramePacer(Fast, Slow, stillFramesBeforeBackoff: 1);
        Assert.True(pacer.Observe(Frame(0x01)));
        Assert.False(pacer.Observe(Frame(0x01)));
        Assert.Equal(TimeSpan.FromMilliseconds(600), pacer.CurrentDelay);
        Assert.False(pacer.Observe(Frame(0x01)));
        Assert.Equal(TimeSpan.FromMilliseconds(1200), pacer.CurrentDelay);

        // 壁纸又开始动了：一路减半回去，但不会低于那一档下限（一次 PrintWindow 要 13~29 ms）。
        for (var round = 0; round < 10; round++)
        {
            Assert.True(pacer.Observe(Frame((byte)(0x10 + round))));
        }

        Assert.Equal(Fast, pacer.CurrentDelay);
    }

    [Fact]
    public void AOnePixelChangeIsAnewFrame()
    {
        var pacer = new BackdropFramePacer(Fast, Slow);
        var first = Frame(0x80);
        Assert.True(pacer.Observe(first));
        Assert.True(pacer.HasPreviousFrame);

        // 只动一小块的动画（粒子、水面反光）也必须认出来。采样签名会漏掉这种，逐字节比不会。
        var second = (byte[])first.Clone();
        second[(40 * 4) + (7 * 4) + 2] = 0xFF; // 第二行第 8 个像素的绿通道
        Assert.True(pacer.Observe(second));

        // 一模一样的一帧再来一次不算「变了」—— 动态壁纸也总有连续两帧同色的时候。
        Assert.False(pacer.Observe((byte[])second.Clone()));
    }

    [Fact]
    public void ABlankRoundDoesntForgetTheFrameAlreadyOnScreen()
    {
        var pacer = new BackdropFramePacer(Fast, Slow);
        var pixels = Frame(0x44);
        Assert.True(pacer.Observe(pixels));

        // 全屏切换、动态壁纸重启这些时候会连续几轮取不到。取不到 ≠ 静止：
        // 上一帧要留着（屏幕上还画着它），节奏倒是可以退，省得跟一个锁着的屏幕较劲。
        Assert.False(pacer.Observe(null));
        Assert.True(pacer.HasPreviousFrame);
        Assert.False(pacer.Observe(null));
        Assert.Equal(TimeSpan.FromMilliseconds(600), pacer.CurrentDelay);

        Assert.False(pacer.Observe(pixels));
        Assert.True(pacer.Observe(Frame(0x45)));
    }

    [Fact]
    public void ResettingMakesTheNextFrameRepaintAndReturnsToTheFastLane()
    {
        var pacer = new BackdropFramePacer(Fast, Slow, stillFramesBeforeBackoff: 1);
        var pixels = Frame(0x77);
        Assert.True(pacer.Observe(pixels));
        Assert.False(pacer.Observe(pixels));

        // 遮罩换了尺寸、或者从全屏里回到屏幕上：旧的那一帧已经不对应新的那条带子了。
        pacer.Reset();

        Assert.False(pacer.HasPreviousFrame);
        Assert.Equal(Fast, pacer.CurrentDelay);
        Assert.True(pacer.Observe(pixels));
    }

    [Fact]
    public void RejectsACadenceThatCantWork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackdropFramePacer(TimeSpan.Zero, Slow));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackdropFramePacer(Slow, Fast));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackdropFramePacer(Fast, Slow, 0));
    }

    /// <summary>造一条 40x2 的假壁纸带子：整块同一颜色，改一个字节就是「画面变了」。</summary>
    private static byte[] Frame(byte shade)
    {
        var pixels = new byte[40 * 4 * 2];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = shade;
            pixels[i + 1] = shade;
            pixels[i + 2] = shade;
        }

        return pixels;
    }
}
