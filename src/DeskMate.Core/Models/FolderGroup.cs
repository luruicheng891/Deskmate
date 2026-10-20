namespace DeskMate.Core.Models;

/// <summary>文件夹分组。</summary>
public class FolderGroup
{
    /// <summary>稳定标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>分组名。</summary>
    public string Name { get; set; } = "";

    /// <summary>Tab 顺序。</summary>
    public int Order { get; set; }

    /// <summary>Tab 强调色。</summary>
    public string? Color { get; set; }
}
