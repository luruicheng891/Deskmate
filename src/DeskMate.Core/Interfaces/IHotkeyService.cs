namespace DeskMate.Core.Interfaces;

/// <summary>全局快捷键注册。</summary>
public interface IHotkeyService
{
    /// <summary>注册热键，冲突时返回 false 并给出原因。</summary>
    bool Register(string hotkey, Action callback);

    /// <summary>注册失败的原因，供设置页提示「快捷键冲突」。</summary>
    string? LastError { get; }

    /// <summary>注销全部热键。</summary>
    void Unregister();
}
