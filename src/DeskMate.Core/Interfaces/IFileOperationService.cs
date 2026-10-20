namespace DeskMate.Core.Interfaces;

/// <summary>
/// 真的会动到磁盘上那个文件的两件事：改名、移到回收站。
/// <para>
/// 这是这个应用里唯一一处碰用户文件的地方，其余全部功能都只存路径引用。
/// 加它是因为用户在 2026-10-17 明确要求「浏览列表里的文件要能重命名和删除」，
/// 并且当场定了口径：删除一律进回收站（可恢复），改名只改名字、不挪位置。
/// </para>
/// </summary>
public interface IFileOperationService
{
    /// <summary>
    /// 在原目录里改名（不移动位置）。返回改完之后的完整路径。
    /// 名字为空、含非法字符、或同目录下已经有同名项时抛异常 —— 绝不覆盖已有文件。
    /// </summary>
    string Rename(string path, string newName);

    /// <summary>移到回收站（用户可以从回收站恢复）。返回 false 表示被取消；真失败抛异常。</summary>
    bool Recycle(string path);
}
