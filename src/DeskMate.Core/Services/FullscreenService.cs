using System.Windows.Threading;
using DeskMate.Core.Interfaces;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 全屏检测服务：把「前台窗口是不是全屏」翻译成一个布尔状态事件，状态没变则不发。
/// Dock 与任务栏遮罩靠它实现「全屏应用运行时自动让路」。
/// </summary>
public sealed class FullscreenService : IFullscreenDetector, IDisposable
{
    /// <summary>
    /// 重新问一遍前台窗口的间隔。
    /// <para>
    /// 为什么光靠 WinEvent 不够：<strong>进入全屏不换前台窗口</strong> —— 还是那个浏览器/游戏窗口，
    /// 只是改了样式和矩形，<c>EVENT_SYSTEM_FOREGROUND</c> 一声不响。于是按了 F11、点了视频全屏之后，
    /// Dock 一直盖在画面上（用户报的就是这个）。前台事件只覆盖「切到一个已经在全屏的窗口」那一半。
    /// </para>
    /// <para>
    /// 400 ms 是「看不出来它才收」和「别一直占 CPU」之间的折中：一次判定就是几个 user32 调用。
    /// </para>
    /// </summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromMilliseconds(400);

    private readonly ILogger<FullscreenService> _logger;
    private DispatcherTimer? _recheck;
    private string _lastNearMiss = string.Empty;
    private bool _isFullscreen;
    private bool _watching;

    /// <summary>构造全屏检测服务。</summary>
    public FullscreenService(ILogger<FullscreenService> logger) => _logger = logger;

    /// <inheritdoc />
    public event EventHandler<bool>? FullscreenChanged;

    /// <inheritdoc />
    public bool IsForegroundFullscreen() => FullscreenDetector.IsForegroundFullscreen();

    /// <summary>
    /// 开始监听前台窗口切换。必须在有消息泵的 UI 线程上调用：
    /// WINEVENT_OUTOFCONTEXT 的回调是在安装钩子的那个线程上派发的，
    /// 从没有消息泵的线程安装会导致回调永远不来。轮询用的 <see cref="DispatcherTimer"/> 同理。
    /// </summary>
    public void StartWatching()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        _isFullscreen = IsForegroundFullscreen();
        FullscreenDetector.StartWatching(_ => Recheck());

        _recheck = new DispatcherTimer { Interval = RecheckInterval };
        _recheck.Tick += (_, _) => Recheck();
        _recheck.Start();

        _logger.LogInformation("全屏检测已启动，初始状态 {Fullscreen}", _isFullscreen);
    }

    /// <summary>停止监听并释放钩子。</summary>
    public void Dispose()
    {
        if (!_watching)
        {
            return;
        }

        _watching = false;
        _recheck?.Stop();
        _recheck = null;
        FullscreenDetector.StopWatching();
    }

    private void Recheck()
    {
        var current = IsForegroundFullscreen();

        if (!current)
        {
            // 「明明铺满了却被某条排除挡下」是排障最需要的信息。只在原因变化时记一句，不刷屏。
            var reason = FullscreenDetector.Explain(WindowInterop.GetForeground());
            if (reason.StartsWith("铺满了", StringComparison.Ordinal) && reason != _lastNearMiss)
            {
                _lastNearMiss = reason;
                _logger.LogDebug("全屏判据挡下了这一层：{Reason}", reason);
            }
        }

        if (current == _isFullscreen)
        {
            return;
        }

        _isFullscreen = current;
        FullscreenChanged?.Invoke(this, current);
    }
}
