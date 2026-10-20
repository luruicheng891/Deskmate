using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskMate.Core.Services;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.App.Services;

/// <summary>
/// 壁纸那条带的取帧泵：自己一条后台线程，抓一帧 → 和上一帧比像素 → 只有真的变了才建位图送回 UI 线程，
/// 抓下一帧要等多久由 <see cref="BackdropFramePacer"/> 按「壁纸有没有在动」决定。
/// <para>
/// 为什么非要一条自己的线程：一次整屏 PrintWindow 实测 13~29 ms。放回 UI 线程上，「跟上动态壁纸」
/// 就等于每秒把界面按住三次，Dock 的动画和点击判定先抖起来 —— 那比遮罩慢半拍严重多了。
/// 顺带 GDI 那些句柄也只在这同一条线程上建和拆，不会出现「A 线程建的画布给 B 线程用」。
/// </para>
/// <para>
/// 泵不碰任何 WPF 属性：要看遮罩在哪、多大，由窗口把几何算成一条不可变记录交进来（<paramref name="grab"/>
/// 里自己去读）；取到的画面用 <see cref="Dispatcher"/> 送回 UI 线程去画。
/// </para>
/// </summary>
public sealed class BackdropPump : IDisposable
{
    private readonly Func<BandPixels?> _grab;
    private readonly Action<BitmapSource?> _publish;
    private readonly BackdropFramePacer _pacer;
    private readonly object _pacerGate = new();
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly ManualResetEventSlim _wake = new(false);
    private int _running;
    private int _invalidateRequested;

    /// <summary>每开一条线程发一个号：老线程发现自己被取代了就自己收，不会有两个泵同时抢那个 pacer。</summary>
    private int _generation;

    /// <param name="grab">在后台线程执行：去桌面窗口裁这一条带子，取不到返回 null。</param>
    /// <param name="publish">在 UI 线程执行：收到新画面；null = 从来没取到过内容，该退回底色。</param>
    public BackdropPump(
        Func<BandPixels?> grab,
        Action<BitmapSource?> publish,
        BackdropFramePacer pacer,
        Dispatcher dispatcher,
        ILogger logger)
    {
        _grab = grab;
        _publish = publish;
        _pacer = pacer;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>现在多久抓一帧（诊断用）。</summary>
    public TimeSpan CurrentDelay => _pacer.CurrentDelay;

    /// <summary>最近一次抓帧花了多久（诊断用）。</summary>
    public TimeSpan LastGrabDelay { get; private set; }

    /// <summary>一共送回过几张新画面（诊断用；0 说明一次都没变过，或压根没取到内容）。</summary>
    public int PublishedFrames { get; private set; }

    /// <summary>开泵。已经在跑就是空操作。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        _wake.Reset();

        new Thread(() => Run(generation))
        {
            IsBackground = true,
            Name = "DeskMate 壁纸取帧",
            Priority = ThreadPriority.BelowNormal,
        }.Start();
    }

    /// <summary>停泵：正在等的那一轮立刻醒、退出。没在跑就是空操作。</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
        {
            return;
        }

        _wake.Set();
    }

    /// <summary>上一帧不算数（遮罩换了尺寸、重新回到屏幕上）：马上重抓一张，并回到最快档。</summary>
    public void Invalidate()
    {
        Interlocked.Exchange(ref _invalidateRequested, 1);
        _wake.Set();
    }

    /// <summary>
    /// 退出时调。故意不拆 <see cref="_wake"/>：它可能正被那条还没走完本轮抓帧的线程等着，
    /// 拆了就是把 <c>ObjectDisposedException</c> 塞进别人的收尾；这条线程是后台线程，进程一退它就没了。
    /// </summary>
    public void Dispose() => Stop();

    private void Run(int generation)
    {
        while (IsMine(generation))
        {
            if (Interlocked.Exchange(ref _invalidateRequested, 0) == 1)
            {
                lock (_pacerGate)
                {
                    _pacer.Reset();
                }
            }

            var started = Stopwatch.GetTimestamp();
            BandPixels? band;
            try
            {
                band = _grab();
            }
            catch (Exception ex)
            {
                // 桌面窗口在取的那一瞬间被换掉（退出动态壁纸、切用户）是真会发生的，
                // 这一轮当没取到就行，不该让一条后台线程带着异常走掉。
                _logger.LogDebug(ex, "任务栏遮罩：取帧抛异常");
                band = null;
            }

            LastGrabDelay = Stopwatch.GetElapsedTime(started);

            // 纯黑 = 什么都没画进来，既不能当成「壁纸就是黑的」刷上去，也不能当成「壁纸动了」。
            var usable = band is { IsBlank: false } ? band : null;
            bool changed;
            bool hasFrame;
            lock (_pacerGate)
            {
                changed = _pacer.Observe(usable?.Pixels);
                hasFrame = _pacer.HasPreviousFrame;
            }

            if (changed && usable is { } frame)
            {
                PublishedFrames++;
                Publish(frame.ToBitmapSource());
                _logger.LogDebug(
                    "任务栏遮罩：帧差有变，单帧 {Grab:F1} ms，下一帧 {Next:F0} ms 后",
                    LastGrabDelay.TotalMilliseconds,
                    _pacer.CurrentDelay.TotalMilliseconds);
            }
            else if (usable is null && !hasFrame)
            {
                // 只有「从来没取到过内容」才退回主题底色。已经画着一帧就让它留着：
                // 桌面窗口短暂取不到（全屏切换、动态壁纸重启）是常事，为此把壁纸换成一块纯色比慢一帧难看多了。
                Publish(null);
            }

            _wake.Wait(_pacer.CurrentDelay);
            _wake.Reset();
        }
    }

    private bool IsMine(int generation) =>
        Volatile.Read(ref _running) == 1 && Volatile.Read(ref _generation) == generation;

    private void Publish(BitmapSource? frame)
    {
        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Render, () => _publish(frame));
        }
        catch (InvalidOperationException)
        {
            // 正在退出，UI 线程已经不收件了。
        }
    }
}
