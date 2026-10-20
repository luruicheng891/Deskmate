namespace DeskMate.Core.Models;

/// <summary>
/// 桌面上一个非文件夹的文件，已经按扩展名归好类。
/// 只是读出来的视图数据 —— DeskMate 不移动、不改名、不删除任何桌面文件。
/// </summary>
/// <param name="Name">文件名（含扩展名）。</param>
/// <param name="Path">完整路径。</param>
/// <param name="Extension">用于归类的小写扩展名，不含点（复合扩展取最后一段）。</param>
/// <param name="Category">归到的分类。</param>
/// <param name="SizeBytes">文件大小。</param>
/// <param name="ModifiedAt">最后修改时间。</param>
public readonly record struct DesktopFile(
    string Name,
    string Path,
    string Extension,
    DesktopFileCategory Category,
    long SizeBytes,
    DateTime ModifiedAt);

/// <summary>桌面文件的虚拟分类。顺序即标签顺序，「其他」永远最后。</summary>
public enum DesktopFileCategory
{
    Archive = 0,
    Image = 1,
    Document = 2,
    Other = 3,
}
