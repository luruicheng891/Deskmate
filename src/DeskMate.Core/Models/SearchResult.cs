namespace DeskMate.Core.Models;

/// <summary>全局搜索的一条结果。</summary>
public record SearchResult
{
    /// <summary>app / folder / file。</summary>
    public string Type { get; init; } = "";

    /// <summary>对应条目的 Id。</summary>
    public string Id { get; init; } = "";

    /// <summary>主标题。</summary>
    public string Title { get; init; } = "";

    /// <summary>副标题，通常是路径。</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>图标来源路径。</summary>
    public string? IconPath { get; init; }

    /// <summary>回车时要执行的路径。</summary>
    public string Path { get; init; } = "";

    /// <summary>排序分，越大越靠前。</summary>
    public int Score { get; init; }
}
