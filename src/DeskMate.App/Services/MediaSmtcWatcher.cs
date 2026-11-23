using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsMediaController;
using Microsoft.Extensions.Logging;
using static WindowsMediaController.MediaManager;

namespace DeskMate.App.Services;

/// <summary>当前「正在播放」摘要：封面字节、标题、歌手、是否在放。</summary>
public sealed record MusicNowPlaying(byte[]? Cover, string Title, string Artist, bool IsPlaying);
/// <summary>
/// 系统媒体会话（SMTC）监听的最小包装：读封面/标题/播放状态，发播放暂停与切歌命令。
/// 核心思路取自 AF-Media-Bar（MIT）：任何接入了 Windows 系统媒体控制的应用
/// （网易云、QQ音乐、酷狗、Edge/Chrome 的视频页……）都会在这里留下会话，
/// 不需要跟某个播放器单独对接。
/// </summary>
public sealed class MediaSmtcWatcher : IDisposable
{
    /// <summary>「正在播放」有变化（含从有到无）。事件在 WinRT 线程上回调，消费方自行调度。</summary>
    public event Action<MusicNowPlaying?>? NowPlayingChanged;

    private readonly ILogger<MediaSmtcWatcher> _logger;
    private MediaManager? _manager;
    private MusicNowPlaying? _current;
    private int _started;
    private bool _disposed;

    /// <summary>构造 SMTC 监听器。</summary>
    public MediaSmtcWatcher(ILogger<MediaSmtcWatcher> logger) => _logger = logger;

    /// <summary>当前摘要（可能为空：没有任何应用在放媒体）。</summary>
    public MusicNowPlaying? Current => _current;

    /// <summary>启动监听（幂等）。失败只记日志：音乐控件退化为默认图标，不拖垮外壳。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        try
        {
            var manager = new MediaManager();
            manager.OnAnyMediaPropertyChanged += OnAnythingChanged;
            manager.OnAnyPlaybackStateChanged += OnAnythingChanged;
            manager.OnAnySessionOpened += OnSessionListChanged;
            manager.OnAnySessionClosed += OnSessionListChanged;
            manager.Start();
            _manager = manager;
            _logger.LogInformation("SMTC 媒体监听已启动");

            // 事件只报「变化」：播放器在监听启动前就开着会话的话，不会再有事件，
            // 所以启动后主动拉一次当前状态（会话扫描是异步的，重试几次直到看见会话）。
            _ = RefreshAfterStartAsync();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _started, 0);
            _logger.LogWarning(ex, "SMTC 媒体监听启动失败，音乐控件将只显示默认图标");
        }
    }

    /// <summary>单击：暂停/播放切换。</summary>
    public async Task ToggleAsync()
    {
        var session = await PickSessionAsync();
        try
        {
            if (session?.ControlSession is not null)
            {
                await session.ControlSession.TryTogglePlayPauseAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "切换播放状态失败");
        }
    }

    /// <summary>滚轮向上：上一首。</summary>
    public async Task PreviousAsync() => await SkipAsync(forward: false);

    /// <summary>滚轮向下：下一首。</summary>
    public async Task NextAsync() => await SkipAsync(forward: true);

    private async Task SkipAsync(bool forward)
    {
        var session = await PickSessionAsync();
        try
        {
            if (session?.ControlSession is null)
            {
                return;
            }

            if (forward)
            {
                await session.ControlSession.TrySkipNextAsync();
            }
            else
            {
                await session.ControlSession.TrySkipPreviousAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "切歌失败");
        }
    }

    /// <summary>挑要控制的会话：优先正在播的，否则第一个有控制能力的。</summary>
    private Task<MediaSession?> PickSessionAsync()
    {
        var manager = _manager;
        if (manager is null)
        {
            return null;
        }

        MediaSession? fallback = null;
        foreach (var session in manager.CurrentMediaSessions.Values.ToList())
        {
            if (session?.ControlSession is null)
            {
                continue;
            }

            try
            {
                var playback = session.ControlSession.GetPlaybackInfo();
                if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    return Task.FromResult(session);
                }

                fallback ??= session;
            }
            catch
            {
                // 播放信息读不到的会话照样能当兜底。
                fallback ??= session;
            }
        }

        return Task.FromResult(fallback);
    }

    private void OnSessionListChanged(MediaSession session) => RefreshWithDelay(150);

    private void OnAnythingChanged(MediaSession session, GlobalSystemMediaTransportControlsSessionPlaybackInfo info) => RefreshWithDelay(150);

    private void OnAnythingChanged(MediaSession session, GlobalSystemMediaTransportControlsSessionMediaProperties properties) => RefreshWithDelay(150);

    /// <summary>启动后的主动拉取：SMTC 会话注册是异步的，轮询几次直到看到会话或超时。</summary>
    private async Task RefreshAfterStartAsync()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(500);
            var manager = _manager;
            if (manager is null || _disposed)
            {
                return;
            }

            var hasSession = manager.CurrentMediaSessions.Values.Any(item => item?.ControlSession is not null);
            if (hasSession)
            {
                await RefreshCoreAsync();
                return;
            }
        }
    }

    /// <summary>事件回调入口：延迟一拍再刷新，避开会话集合的中间态。所有异常都不外抛。</summary>
    private async Task RefreshWithDelay(int delayMs)
    {
        try
        {
            await Task.Delay(delayMs);
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "刷新媒体摘要失败");
        }
    }

    /// <summary>刷新当前摘要：重选会话、读媒体属性与封面。</summary>
    private async Task RefreshCoreAsync()
    {
        var manager = _manager;
        if (manager is null || _disposed)
        {
            return;
        }

        var sessions = manager.CurrentMediaSessions.Values
            .Where(item => item?.ControlSession is not null)
            .ToList();
        if (sessions.Count == 0)
        {
            Publish(null);
            return;
        }

        // 跟随的会话：优先正在播的那个。
        GlobalSystemMediaTransportControlsSession? target = null;
        foreach (var item in sessions)
        {
            var playback = item.ControlSession.GetPlaybackInfo();
            if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                target = item.ControlSession;
                break;
            }
        }

        target ??= sessions[0].ControlSession;

        var media = await target.TryGetMediaPropertiesAsync();
        var playing = target.GetPlaybackInfo().PlaybackStatus
                      == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var title = string.IsNullOrWhiteSpace(media.Title) ? "未知曲目" : media.Title;
        var artist = media.Artist ?? string.Empty;

        Publish(new MusicNowPlaying(await ReadThumbnailAsync(media), title, artist, playing));
    }

    /// <summary>读专辑封面字节；读不到就空，由上层退化为音符图标。</summary>
    private static async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties media)
    {
        try
        {
            if (media.Thumbnail is null)
            {
                return null;
            }

            var stream = await media.Thumbnail.OpenReadAsync();
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var size = (uint)stream.Size;
            if (size == 0)
            {
                return null;
            }

            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private void Publish(MusicNowPlaying? value)
    {
        // 标题、歌手、播放状态、封面字节任一变化才广播，避免滚动歌词类应用的高频变更刷屏。
        var previous = _current;
        if (!ReferenceEquals(previous, value)
            && (previous is null) == (value is null)
            && previous is not null && value is not null
            && previous.Title == value.Title
            && previous.Artist == value.Artist
            && previous.IsPlaying == value.IsPlaying
            && previous.Cover is not null && value.Cover is not null
            && previous.Cover.AsSpan().SequenceEqual(value.Cover))
        {
            return;
        }

        _current = value;
        _logger.LogInformation(
            "媒体摘要：{Title} | {Artist} | playing={IsPlaying} | cover={Cover}B",
            value?.Title ?? "(无会话)", value?.Artist ?? "-", value?.IsPlaying ?? false, value?.Cover?.Length ?? 0);
        try
        {
            NowPlayingChanged?.Invoke(value);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "音乐摘要事件回调异常");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var manager = Interlocked.Exchange(ref _manager, null);
        if (manager is null)
        {
            return;
        }

        manager.OnAnyMediaPropertyChanged -= OnAnythingChanged;
        manager.OnAnyPlaybackStateChanged -= OnAnythingChanged;
        manager.OnAnySessionOpened -= OnSessionListChanged;
        manager.OnAnySessionClosed -= OnSessionListChanged;
        try
        {
            manager.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SMTC 监听器释放失败");
        }
    }
}
