namespace DeskMate.Core.Models;

/// <summary>启动台/搜索用的应用条目。</summary>
public record AppInfo
{
    /// <summary>去重键，取解析后的真实目标路径（小写）。</summary>
    public string Id { get; init; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; init; } = "";

    /// <summary>实际要 ShellExecute 的路径，通常是 .lnk。</summary>
    public string Path { get; init; } = "";

    /// <summary>图标来源路径，为空时回落到 <see cref="Path"/>。</summary>
    public string? IconPath { get; init; }

    /// <summary>StartMenu / Registry / UWP。</summary>
    public string Source { get; init; } = "";
}
