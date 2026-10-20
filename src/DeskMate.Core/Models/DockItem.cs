namespace DeskMate.Core.Models;

/// <summary>Dock 上的一个启动项。</summary>
public class DockItem
{
    /// <summary>稳定标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>显示名称，可自定义，不影响真实文件名。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>exe 或 lnk 路径。</summary>
    public string TargetPath { get; set; } = "";

    /// <summary>图标缓存键，路径不变时避免重复提取。</summary>
    public string IconCacheKey { get; set; } = "";

    /// <summary>
    /// 自定义图标来源（.ico / .exe / .dll）。留空表示用目标自己的图标。
    /// 只影响显示，启动的还是 <see cref="TargetPath"/>。
    /// </summary>
    public string IconOverride { get; set; } = "";

    /// <summary>排序序号，从 0 开始连续。</summary>
    public int Order { get; set; }

    /// <summary>是否固定（固定项排在最前，不受运行状态影响）。</summary>
    public bool IsPinned { get; set; }
}
