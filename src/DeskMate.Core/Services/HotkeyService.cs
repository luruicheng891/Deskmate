using System.Windows.Interop;
using DeskMate.Core.Interfaces;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 全局快捷键服务。真正的 P/Invoke 与 WM_HOTKEY 分发在 <see cref="HotkeyManager"/>，
/// 本类只负责把托盘隐藏窗口的 <see cref="HwndSource"/> 交出去并记录冲突原因。
/// </summary>
public sealed class HotkeyService : IHotkeyService
{
    private readonly ILogger<HotkeyService> _logger;

    /// <summary>构造快捷键服务。</summary>
    public HotkeyService(ILogger<HotkeyService> logger) => _logger = logger;

    /// <inheritdoc />
    public string? LastError { get; private set; }

    /// <summary>
    /// 绑定快捷键宿主窗口。必须由有消息泵的 UI 线程调用，通常在建好托盘隐藏窗口后执行一次。
    /// </summary>
    public void AttachSource(HwndSource source)
    {
        HotkeyManager.Attach(source);
        _logger.LogInformation("快捷键宿主已就绪");
    }

    /// <inheritdoc />
    public bool Register(string hotkey, Action callback)
    {
        var ok = HotkeyManager.Register(hotkey, callback, out var error);
        LastError = error;

        if (ok)
        {
            _logger.LogInformation("全局快捷键 {Hotkey} 已注册", hotkey);
        }
        else
        {
            _logger.LogWarning("全局快捷键 {Hotkey} 注册失败：{Error}", hotkey, error);
        }

        return ok;
    }

    /// <inheritdoc />
    public void Unregister() => HotkeyManager.Unregister();
}
