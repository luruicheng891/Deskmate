namespace DeskMate.Core.Interfaces;

/// <summary>开机自启，唯一允许写注册表的场景（HKCU Run）。</summary>
public interface IAutoStartService
{
    /// <summary>读取 HKCU\...\Run 判断是否已开启。</summary>
    bool IsEnabled();

    /// <summary>写入或删除自启项。</summary>
    void SetEnabled(bool enabled);
}
