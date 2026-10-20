using DeskMate.Core.Models;
using DeskMate.Core.Services;

namespace DeskMate.Core.Interfaces;

/// <summary>
/// 文件夹管理。所有方法只操作配置中的路径引用，
/// 不会移动、删除、重命名用户真实目录 —— 这是产品的最高约束。
/// </summary>
public interface IFolderService
{
    /// <summary>全部受管文件夹，按 Order 升序。</summary>
    IReadOnlyList<FolderItem> GetAll();

    /// <summary>新增引用；同一路径重复添加时返回已存在的条目。</summary>
    Task<FolderItem> AddAsync(string realPath);

    /// <summary>移除引用（不删除磁盘目录）。</summary>
    Task RemoveAsync(string id);

    /// <summary>只改显示名，真实目录名不变。</summary>
    Task RenameDisplayAsync(string id, string newName);

    /// <summary>
    /// 把一条引用改指到新的位置（失效引用唯一的自救办法）。只改配置里的路径，磁盘上什么都不动。
    /// 新路径不存在、或已经有另一条引用指向它时抛异常，由调用方提示用户。
    /// </summary>
    Task<FolderItem> RelocateAsync(string id, string newRealPath);

    /// <summary>改分组，null 表示移出到未分组。</summary>
    Task UpdateGroupAsync(string id, string? groupId);

    /// <summary>改颜色标签，null 表示清除。</summary>
    Task UpdateColorAsync(string id, string? color);

    /// <summary>按给定 Id 顺序重排。</summary>
    Task ReorderAsync(IEnumerable<string> orderedIds);

    /// <summary>异步校验所有路径存在性，更新 Valid。</summary>
    Task ValidateAllAsync();

    /// <summary>扫描桌面（含公共桌面）下的一级文件夹绝对路径。</summary>
    Task<IReadOnlyList<string>> ScanDesktopFoldersAsync();

    /// <summary>统计目录内文件总数，超过 10000 时返回 10000 并由 UI 显示「10000+」。</summary>
    Task<int> CountFilesAsync(string path);

    /// <summary>全部分组，按 Order 升序。</summary>
    IReadOnlyList<FolderGroup> GetGroups();

    /// <summary>新建分组。</summary>
    Task<FolderGroup> AddGroupAsync(string name);

    /// <summary>改分组显示名。组内文件夹不受影响，它们记的是分组 Id。</summary>
    Task RenameGroupAsync(string groupId, string newName);

    /// <summary>删除分组，组内文件夹退化为未分组。</summary>
    Task RemoveGroupAsync(string groupId);

    /// <summary>收藏的路径，按加入顺序。</summary>
    IReadOnlyList<string> GetFavorites();

    /// <summary>这条路径是否已收藏（同一目录的结尾分隔符差异算同一条）。</summary>
    bool IsFavorite(string path);

    /// <summary>切换收藏状态，返回切换后的结果（true = 现在已收藏）。</summary>
    Task<bool> ToggleFavoriteAsync(string path);

    /// <summary>批量收藏，已收藏过的自动跳过，返回真正加进去几个。</summary>
    Task<int> AddFavoritesAsync(IReadOnlyList<string> paths);

    /// <summary>取消收藏。只动配置里的这一行，磁盘上什么都不动。</summary>
    Task RemoveFavoriteAsync(string path);

    /// <summary>
    /// 把收藏里指向 <paramref name="from"/> 的那条换成 <paramref name="to"/>。
    /// 磁盘上改了名，收藏里那条旧路径就指向不存在的东西了 —— 不换的话「★ 收藏」页会凭空多一条失效项。
    /// </summary>
    Task<bool> ReplaceFavoriteAsync(string from, string to);

    /// <summary>
    /// 在 <paramref name="directory"/> 里新建一个文件夹或空文件，返回新建出来的完整路径。
    /// 这是 DeskMate 唯一会往用户目录里「放东西」的操作，且只放用户当场命名的新条目：
    /// 同名的一律拒绝，绝不覆盖、移动、改名或删除已有的内容。
    /// 名字不合法、父目录不在了、路径超长时抛异常，消息就是给用户看的原因。
    /// </summary>
    Task<string> CreateEntryAsync(string directory, string name);

    /// <summary>
    /// 按给定类型新建（Word / Excel / PPT 会写出合法的最小 OOXML 包，而不是打不开的 0 字节文件）。
    /// <paramref name="option"/> 为「文件夹」时名字里有点也照样建目录；为固定类型时自动补后缀。
    /// </summary>
    Task<string> CreateEntryAsync(string directory, string name, NewEntryOption option);
}
