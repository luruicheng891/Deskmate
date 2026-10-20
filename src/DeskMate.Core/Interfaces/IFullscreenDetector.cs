namespace DeskMate.Core.Interfaces;

/// <summary>全屏状态探测，Dock 自动让路依赖它。</summary>
public interface IFullscreenDetector
{
    /// <summary>当前前台窗口是否全屏。</summary>
    bool IsForegroundFullscreen();

    /// <summary>前台窗口全屏状态发生变化（参数为新的全屏状态）。</summary>
    event EventHandler<bool>? FullscreenChanged;
}
