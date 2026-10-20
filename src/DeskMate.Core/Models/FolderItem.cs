namespace DeskMate.Core.Models;

/// <summary>
/// 受管文件夹。<see cref="RealPath"/> 只是引用，DeskMate 不会对该目录做任何写操作。
/// </summary>
public class FolderItem
{
    /// <summary>稳定标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>卡片上显示的名字，可与真实目录名不同。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>真实目录绝对路径。</summary>
    public string RealPath { get; set; } = "";

    /// <summary>所属分组，null 表示未分组。</summary>
    public string? GroupId { get; set; }

    /// <summary>颜色标签，ARGB 十六进制字符串。</summary>
    public string? Color { get; set; }

    /// <summary>卡片排序序号。</summary>
    public int Order { get; set; }

    /// <summary>上次校验时路径是否存在。</summary>
    public bool Valid { get; set; } = true;

    /// <summary>上次校验时间。</summary>
    public DateTime LastChecked { get; set; }
}
