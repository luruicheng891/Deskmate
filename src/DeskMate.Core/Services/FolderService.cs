using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 文件夹管理：只维护「路径引用 + 展示属性」，全部改动落在 config.json。
/// 本类任何方法都不会写、移动、删除用户目录里的内容 —— 这是产品的最高约束。
/// </summary>
public sealed class FolderService : IFolderService
{
    /// <summary>统计上限，超过后 UI 显示「10000+」，避免超大目录卡住界面。</summary>
    public const int FileCountCap = 10000;

    private readonly IConfigService _config;
    private readonly ILogger<FolderService> _logger;

    /// <summary>构造文件夹服务。</summary>
    public FolderService(IConfigService config, ILogger<FolderService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<FolderItem> GetAll()
        => _config.Current.Folders.OrderBy(item => item.Order).ToList();

    /// <inheritdoc />
    public async Task<FolderItem> AddAsync(string realPath)
    {
        var fullPath = Normalize(realPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"文件夹不存在：{fullPath}");
        }

        var folders = _config.Current.Folders;
        var existing = folders.FirstOrDefault(item => string.Equals(item.RealPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // 重复添加是常见误操作，当成幂等处理而不是报错。
            return existing;
        }

        var item = new FolderItem
        {
            DisplayName = new DirectoryInfo(fullPath).Name,
            RealPath = fullPath,
            Order = folders.Count == 0 ? 0 : folders.Max(f => f.Order) + 1,
            Valid = true,
            LastChecked = DateTime.Now,
        };

        folders.Add(item);
        await _config.SaveAsync();
        _logger.LogInformation("已添加文件夹引用 {Path}", fullPath);
        return item;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string id)
    {
        var removed = _config.Current.Folders.RemoveAll(item => item.Id == id);
        if (removed > 0)
        {
            await _config.SaveAsync();
        }
    }

    /// <inheritdoc />
    public async Task RenameDisplayAsync(string id, string newName)
    {
        var item = Find(id);
        if (item is null)
        {
            return;
        }

        var trimmed = newName?.Trim();
        item.DisplayName = string.IsNullOrEmpty(trimmed) ? new DirectoryInfo(item.RealPath).Name : trimmed;
        await _config.SaveAsync();
    }

    /// <inheritdoc />
    public async Task<FolderItem> RelocateAsync(string id, string newRealPath)
    {
        var item = Find(id) ?? throw new DirectoryNotFoundException("这条引用已经不在了。");
        var fullPath = Normalize(newRealPath);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"文件夹不存在：{fullPath}");
        }

        var clash = _config.Current.Folders.FirstOrDefault(
            other => other.Id != id && string.Equals(other.RealPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
        {
            throw new InvalidOperationException("这个文件夹已经在管理列表里了。");
        }

        // 显示名还是目录自己的名字时跟着换；用户改过的名字保留，那是他起的。
        if (string.Equals(item.DisplayName, new DirectoryInfo(item.RealPath).Name, StringComparison.Ordinal))
        {
            item.DisplayName = new DirectoryInfo(fullPath).Name;
        }

        item.RealPath = fullPath;
        item.Valid = true;
        item.LastChecked = DateTime.Now;

        await _config.SaveAsync();
        _logger.LogInformation("引用已重新定位 {From} → {To}", id, fullPath);
        return item;
    }

    /// <inheritdoc />
    public async Task UpdateGroupAsync(string id, string? groupId)
    {
        var item = Find(id);
        if (item is null)
        {
            return;
        }

        item.GroupId = groupId;
        await _config.SaveAsync();
    }

    /// <inheritdoc />
    public async Task UpdateColorAsync(string id, string? color)
    {
        var item = Find(id);
        if (item is null)
        {
            return;
        }

        item.Color = color;
        await _config.SaveAsync();
    }

    /// <inheritdoc />
    public async Task ReorderAsync(IEnumerable<string> orderedIds)
    {
        var folders = _config.Current.Folders;
        var ordered = orderedIds.ToList();
        var position = 0;

        foreach (var id in ordered)
        {
            var item = folders.FirstOrDefault(f => f.Id == id);
            if (item is not null)
            {
                item.Order = position++;
            }
        }

        // 没出现在新顺序里的条目顺延到末尾，避免留下重复 Order。
        foreach (var item in folders.Where(f => !ordered.Contains(f.Id)))
        {
            item.Order = position++;
        }

        await _config.SaveAsync();
    }

    /// <inheritdoc />
    public async Task ValidateAllAsync()
    {
        var changed = await Task.Run(() =>
        {
            var any = false;
            foreach (var item in _config.Current.Folders)
            {
                var exists = DirectoryExists(item.RealPath);
                if (item.Valid != exists)
                {
                    item.Valid = exists;
                    any = true;
                }

                item.LastChecked = DateTime.Now;
            }

            return any;
        });

        if (changed)
        {
            await _config.SaveAsync();
            _logger.LogInformation("路径校验发现失效项，已更新配置");
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ScanDesktopFoldersAsync()
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            var results = new List<string>();
            foreach (var desktop in new[] { WellKnownPaths.UserDesktop, WellKnownPaths.PublicDesktop })
            {
                if (!Directory.Exists(desktop))
                {
                    continue;
                }

                try
                {
                    // 只取一级目录：桌面文件本身不归 DeskMate 管，避免误伤用户散落的重要文件。
                    results.AddRange(Directory.GetDirectories(desktop)
                        .Where(dir => !IsReparsePoint(dir))
                        .Select(dir => Path.GetFullPath(dir)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "扫描桌面失败：{Desktop}", desktop);
                }
            }

            return results
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(dir => new DirectoryInfo(dir).Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    /// <inheritdoc />
    public Task<int> CountFilesAsync(string path) => Task.Run(() => CountFiles(path, _logger));

    /// <inheritdoc />
    public IReadOnlyList<FolderGroup> GetGroups()
        => _config.Current.Groups.OrderBy(group => group.Order).ToList();

    /// <inheritdoc />
    public async Task<FolderGroup> AddGroupAsync(string name)
    {
        var groups = _config.Current.Groups;
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("分组名不能为空。", nameof(name));
        }

        var existing = groups.FirstOrDefault(g => string.Equals(g.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var group = new FolderGroup
        {
            Name = trimmed,
            Order = groups.Count == 0 ? 0 : groups.Max(g => g.Order) + 1,
        };

        groups.Add(group);
        await _config.SaveAsync();
        return group;
    }

    /// <inheritdoc />
    public async Task RenameGroupAsync(string groupId, string newName)
    {
        var group = _config.Current.Groups.FirstOrDefault(item => item.Id == groupId);
        if (group is null)
        {
            return;
        }

        var trimmed = newName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("分组名不能为空。", nameof(newName));
        }

        // 组内卡片记的是分组 Id，所以改名不需要动任何文件夹。
        group.Name = trimmed;
        await _config.SaveAsync();
    }

    /// <inheritdoc />
    public async Task RemoveGroupAsync(string groupId)
    {
        var removed = _config.Current.Groups.RemoveAll(g => g.Id == groupId);
        if (removed == 0)
        {
            return;
        }

        // 组没了，组内的引用退化为未分组，而不是连带消失。
        foreach (var item in _config.Current.Folders.Where(f => f.GroupId == groupId))
        {
            item.GroupId = null;
        }

        await _config.SaveAsync();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetFavorites() => _config.Current.Favorites.ToList();

    /// <inheritdoc />
    public bool IsFavorite(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return _config.Current.Favorites.Any(item => SamePath(item, path));
    }

    /// <inheritdoc />
    public async Task<bool> ToggleFavoriteAsync(string path)
    {
        var full = Normalize(path);
        var favorites = _config.Current.Favorites;
        var index = favorites.FindIndex(item => SamePath(item, full));

        if (index >= 0)
        {
            favorites.RemoveAt(index);
            await _config.SaveAsync();
            return false;
        }

        favorites.Add(full);
        await _config.SaveAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<int> AddFavoritesAsync(IReadOnlyList<string> paths)
    {
        var favorites = _config.Current.Favorites;
        var added = 0;

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || favorites.Any(item => SamePath(item, path)))
            {
                continue;
            }

            favorites.Add(Normalize(path));
            added++;
        }

        if (added > 0)
        {
            await _config.SaveAsync();
        }

        return added;
    }

    /// <inheritdoc />
    public async Task RemoveFavoriteAsync(string path)
    {
        var removed = _config.Current.Favorites.RemoveAll(item => SamePath(item, path));
        if (removed > 0)
        {
            await _config.SaveAsync();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceFavoriteAsync(string from, string to)
    {
        var favorites = _config.Current.Favorites;
        var index = favorites.FindIndex(item => SamePath(item, from));
        if (index < 0)
        {
            return false;
        }

        favorites[index] = Normalize(to);
        await _config.SaveAsync();
        return true;
    }

    /// <inheritdoc />
    public Task<string> CreateEntryAsync(string directory, string name) => CreateEntryAsync(directory, name, NewEntryCatalog.FreeForm);

    /// <inheritdoc />
    public async Task<string> CreateEntryAsync(string directory, string name, NewEntryOption option)
    {
        var parent = Normalize(directory);
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("这个目录已经不在了，先重新定位再新建。");
        }

        var clean = NewEntryCatalog.ResolveName(name, option);
        var reason = NewEntryRules.Validate(clean);
        if (reason is not null)
        {
            throw new ArgumentException(reason);
        }

        var target = Path.Combine(parent, clean);

        // 同名一律拒绝：覆盖掉用户已有的文件是这个工具最不能犯的错误。
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new InvalidOperationException($"这里已经有一个叫「{clean}」的了，换个名字。");
        }

        // 传统路径上限 260。中文目录名很容易就顶到，撞上了 Windows 报的是含糊的「路径太长」，先自己说清楚。
        if (target.Length > 259)
        {
            throw new InvalidOperationException($"建出来路径有 {target.Length} 个字符，超过 260 的上限，换个短点的名字。");
        }

        // 「文件夹」那一项一定是目录；「其他（自己写后缀）」回到老口径 —— 不带后缀就是文件夹。
        var isFolder = option.Shape == NewEntryShape.Folder
            || (option.Shape == NewEntryShape.FreeForm && NewEntryRules.Classify(clean) == NewEntryKind.Folder);

        if (isFolder)
        {
            Directory.CreateDirectory(target);
        }
        else
        {
            // Office 三种要有合法的最小包；md / txt / .c 这类空文件就是大家预期的样子。
            await File.WriteAllBytesAsync(target, OoxmlTemplates.TryBuild(Path.GetExtension(target)) ?? []);
        }

        _logger.LogInformation("新建{Kind}：{Path}", isFolder ? "文件夹" : "文件", target);
        return target;
    }

    /// <summary>比较两条路径是不是同一个位置。存进去的都已规范化，但配置可能被手改过，结尾分隔符要容错。</summary>
    private static bool SamePath(string left, string right)
        => string.Equals(
            left.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private FolderItem? Find(string id) => _config.Current.Folders.FirstOrDefault(item => item.Id == id);

    private static string Normalize(string realPath)
    {
        if (string.IsNullOrWhiteSpace(realPath))
        {
            throw new ArgumentException("文件夹路径不能为空。", nameof(realPath));
        }

        // 去掉包裹引号与结尾分隔符，否则 "C:\a" 与 "C:\a\" 会被当成两个不同条目。
        var cleaned = realPath.Trim().Trim('"').TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (cleaned.Length == 0)
        {
            throw new ArgumentException("文件夹路径不能为空。", nameof(realPath));
        }

        var full = Path.GetFullPath(cleaned);

        // 盘根/UNC 根原样返回：TrimEnd 会把 "C:\" 变成语义不同的 "C:"（当前目录）。
        return Path.GetPathRoot(full) == full
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool DirectoryExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch (ArgumentException)
        {
            // 配置里被手工写坏的非法字符路径，视为不存在。
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            // 读不到属性时按普通目录处理：宁可多算几个文件，也不该因一个坏链接让整张卡片的统计失败。
            return false;
        }
    }

    /// <summary>
    /// 递归统计文件数，最多数到 <see cref="FileCountCap"/>。
    /// 跳过再解析点（junction / 符号链接），否则会顺着循环引用无限计数。
    /// </summary>
    public static int CountFiles(string path, ILogger? logger = null)
    {
        if (!DirectoryExists(path))
        {
            return 0;
        }

        var count = 0;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    count++;
                    if (count >= FileCountCap)
                    {
                        return FileCountCap;
                    }
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    if (!IsReparsePoint(directory))
                    {
                        pending.Push(directory);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                logger?.LogDebug("统计文件数时跳过无权限目录 {Path}", current);
            }
            catch (IOException)
            {
                logger?.LogDebug("统计文件数时跳过损坏目录 {Path}", current);
            }
        }

        return count;
    }
}
