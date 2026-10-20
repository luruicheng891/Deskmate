using DeskMate.Core.Models;

namespace DeskMate.Core.Interfaces;

/// <summary>
/// 桌面文件的虚拟分类视图。只读 —— 不移动、不改名、不删除任何桌面文件。
/// </summary>
public interface IDesktopFileService
{
    /// <summary>当前桌面上非文件夹且未被忽略的文件，已按扩展名归类。</summary>
    IReadOnlyList<DesktopFile> Files { get; }

    /// <summary>桌面新增/删除/重命名后触发（已去抖）。回调线程不一定是 UI 线程。</summary>
    event EventHandler? Changed;

    /// <summary>立刻重新扫描并返回结果。</summary>
    IReadOnlyList<DesktopFile> Refresh();
}
