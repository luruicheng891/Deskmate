using DeskMate.Core.Models;

namespace DeskMate.Core.Interfaces;

/// <summary>配置的加载、保存与变更广播。</summary>
public interface IConfigService
{
    /// <summary>当前配置实例，全程单例，各服务直接读写它。</summary>
    AppConfig Current { get; }

    /// <summary>配置文件路径。导入前自动备份要用它，设置页也直接显示给用户。</summary>
    string FilePath { get; }

    /// <summary>从 %APPDATA%\DeskMate\config.json 载入；文件缺失或损坏时回落默认值。</summary>
    Task LoadAsync();

    /// <summary>
    /// 写盘并广播 <see cref="ConfigChanged"/>。
    /// 写失败时内存会退回磁盘上最后一次成功的状态再抛出：界面不能显示一个根本没存进去的改动。
    /// </summary>
    Task SaveAsync();

    /// <summary>恢复为出厂默认配置并落盘（设置页「重置为默认」）。</summary>
    Task ResetAsync();

    /// <summary>配置已变更，UI 侧据此刷新。</summary>
    event EventHandler? ConfigChanged;
}
