namespace DeskMate.Core.Services;

/// <summary>
/// 任务栏遮罩的「帧差自适应」：把抓回来的那条壁纸带和上一帧逐字节比，
/// 变了才重画，并且用「这一下有没有变」决定下一帧什么时候再抓。
/// <para>
/// 为什么不能写死一个频率：动态壁纸下那条带子每一帧都不一样，半分钟抓一次等于屏幕上盖着一张定格；
/// 但一次整屏 PrintWindow 实测 13~29 ms，按屏幕帧率追既追不起也犯不着。所以检测到变化就收紧到
/// <see cref="Fastest"/>，连续 <see cref="StillFramesBeforeBackoff"/> 帧一模一样就一路减半退让到
/// <see cref="Slowest"/> —— 静态壁纸于是几乎不花 CPU（和以前 30 秒一次一样），壁纸真在动的时侯
/// 最多落后 <see cref="Fastest"/> 就跟上。换壁纸、解锁屏幕这些「变了」的事件照样会被下一帧抓到。
/// </para>
/// <para>只给取帧那一条线程用，本身不做线程安全。</para>
/// </summary>
public sealed class BackdropFramePacer
{
    /// <summary>
    /// @param fastest 检测到变化时的最快间隔。
    /// @param slowest 一直不变时退让到的上限（也就是老行为里那个固定频率）。
    /// @param stillFramesBeforeBackoff 连续几帧相同才允许退让一档。设 1 的话，一个「两帧一循环」的
    ///        动画会隔一次就被判成静止而一路退光，所以默认要连续两帧。
    /// </summary>
    public BackdropFramePacer(TimeSpan fastest, TimeSpan slowest, int stillFramesBeforeBackoff = 2)
    {
        if (fastest <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fastest), "最快间隔必须大于 0");
        }

        if (slowest < fastest)
        {
            throw new ArgumentOutOfRangeException(nameof(slowest), "退让上限不能比最快间隔还短");
        }

        if (stillFramesBeforeBackoff < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stillFramesBeforeBackoff), "至少连续一帧相同才允许退让");
        }

        Fastest = fastest;
        Slowest = slowest;
        StillFramesBeforeBackoff = stillFramesBeforeBackoff;
        CurrentDelay = fastest;
    }

    /// <summary>检测到画面在动时的下限。</summary>
    public TimeSpan Fastest { get; }

    /// <summary>画面不动时退让的上限。</summary>
    public TimeSpan Slowest { get; }

    /// <summary>连续几帧相同后退让一档。</summary>
    public int StillFramesBeforeBackoff { get; }

    /// <summary>下一帧该等多久。调用方每次取帧后读它。</summary>
    public TimeSpan CurrentDelay { get; private set; }

    /// <summary>已经存着的上一帧（自检与日志用，不涉及画面内容）。</summary>
    public bool HasPreviousFrame => _previous is not null;

    /// <summary>
    /// 交回这一帧的原始像素：非 null 表示抓到了（BGRA 逐行），null 表示这一轮什么都没取到
    /// （锁屏、安全桌面、桌面窗口换了实现）。返回 true 就代表画面变了，调用方要重画。
    /// <para>
    /// 没抓到的那一轮不清上一帧：屏幕上还画着它呢，等桌面回来时再比一次才知道到底变没变。
    /// 调用方要用 <see cref="HasPreviousFrame"/> 决定「什么都没取到」时是退回底色还是先留着旧帧。
    /// </para>
    /// </summary>
    public bool Observe(byte[]? pixels)
    {
        // 逐字节比：420x48 那条带也就 80 KB，SequenceEqual 是向量化的，微秒级，
        // 比「采样签名」又准又省事 —— 采样会漏掉只动一小块的动画。
        var changed = pixels is not null && (_previous is null || !pixels.AsSpan().SequenceEqual(_previous));
        if (changed)
        {
            _previous = pixels;
        }

        if (changed)
        {
            _stillFrames = 0;
            CurrentDelay = Max(Fastest, CurrentDelay / 2);
        }
        else if (++_stillFrames >= StillFramesBeforeBackoff)
        {
            _stillFrames = 0;
            CurrentDelay = Min(Slowest, CurrentDelay * 2);
        }

        return changed;
    }

    /// <summary>
    /// 上一帧不算数：遮罩换了尺寸或重新出现在屏幕上。下一帧一定重画，节奏也回到最快档，
    /// 免得退让到 30 秒之后，用户要先盯着定格等半天才看到画面跟上。
    /// </summary>
    public void Reset()
    {
        _previous = null;
        _stillFrames = 0;
        CurrentDelay = Fastest;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private byte[]? _previous;
    private int _stillFrames;
}
