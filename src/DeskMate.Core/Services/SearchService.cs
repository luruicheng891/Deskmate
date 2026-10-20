using System.Collections.Concurrent;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 统一搜索：应用 + 受管文件夹 + Dock 项 + 桌面文件 + 受管文件夹的一级内容。
/// <para>
/// 排序严格按需求 M4 的档位，使用频率只做同分决胜，权重刻意小于最小档位间隔（50），
/// 否则一个被点过很多次的「包含匹配」会盖掉更精准的前缀匹配。
/// </para>
/// </summary>
public sealed class SearchService : ISearchService
{
    private const int UsageBonusCap = 40;

    /// <summary>
    /// 文件类结果最多占几个名额。输一个「a」能命中成百上千个文件，
    /// 不设上限的话应用和文件夹会被挤出结果列表，而这里最主要的动作仍然是启动应用。
    /// </summary>
    private const int MaxFileResults = 8;

    /// <summary>每个受管文件夹只索引一级内容，并且限量：全量递归会在每次敲键时把磁盘读爆。</summary>
    private const int FolderEntryCap = 300;

    /// <summary>索引停留时间。搜索是边打字边触发的，磁盘列举必须摊薄。</summary>
    private static readonly TimeSpan IndexTtl = TimeSpan.FromSeconds(30);

    private readonly IAppScannerService _apps;
    private readonly IConfigService _config;
    private readonly IUsageService _usage;
    private readonly IDesktopFileService _desktopFiles;
    private readonly ILogger<SearchService> _logger;
    private readonly ConcurrentDictionary<string, FolderSnapshot> _index = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造搜索服务。</summary>
    public SearchService(
        IAppScannerService apps,
        IConfigService config,
        IUsageService usage,
        IDesktopFileService desktopFiles,
        ILogger<SearchService> logger)
    {
        _apps = apps;
        _config = config;
        _usage = usage;
        _desktopFiles = desktopFiles;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string keyword, int maxResults = 20)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return [];
        }

        var key = keyword.Trim();

        // 应用列表本身可能有缓存未命中的情况，先在异步路径上取好，
        // 剩下的（磁盘列举 + 几千次打分）整段丢到线程池：用户正在一个字一个字地敲，UI 线程不能占。
        IReadOnlyList<AppInfo> apps;
        try
        {
            apps = await _apps.ScanInstalledAppsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "搜索时读取应用列表失败，本次只返回其余来源的结果");
            apps = [];
        }

        return await Task.Run<IReadOnlyList<SearchResult>>(() => SearchCore(key, maxResults, apps));
    }

    private IReadOnlyList<SearchResult> SearchCore(string key, int maxResults, IReadOnlyList<AppInfo> apps)
    {
        var results = new List<SearchResult>(64);

        AddApps(results, key, apps);
        AddManagedFolders(results, key);
        AddDockItems(results, key);
        AddDesktopFiles(results, key);
        AddFolderContents(results, key);

        var ranked = results
            .GroupBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(result => result.Score).First())
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TakeWithFileQuota(ranked, maxResults);
    }

    /// <summary>先满足应用与文件夹，文件类结果限量，最后一起截断。</summary>
    private static IReadOnlyList<SearchResult> TakeWithFileQuota(IReadOnlyList<SearchResult> ranked, int maxResults)
    {
        var taken = new List<SearchResult>(maxResults);
        var files = 0;

        foreach (var result in ranked)
        {
            if (taken.Count >= maxResults)
            {
                break;
            }

            if (result.Type == "file")
            {
                if (files >= MaxFileResults)
                {
                    continue;
                }

                files++;
            }

            taken.Add(result);
        }

        return taken;
    }

    private void AddApps(List<SearchResult> results, string key, IReadOnlyList<AppInfo> apps)
    {
        // 用户在启动台里隐藏过的条目，搜索结果里也不该再冒出来。
        var hidden = _config.Current.HiddenApps;

        foreach (var app in apps)
        {
            if (hidden.Contains(app.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = PinyinMatcher.Score(app.Name, key, app.Path);
            if (score <= PinyinMatcher.NoMatch)
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Type = "app",
                Id = app.Id,
                Title = app.Name,
                Subtitle = Shorten(app.Path),
                IconPath = app.IconPath ?? app.Path,
                Path = app.Path,
                Score = score + UsageBonus(app.Id),
            });
        }
    }

    private void AddManagedFolders(List<SearchResult> results, string key)
    {
        foreach (var folder in _config.Current.Folders)
        {
            var score = PinyinMatcher.Score(folder.DisplayName, key, folder.RealPath);
            if (score <= PinyinMatcher.NoMatch)
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Type = "folder",
                Id = folder.Id,
                Title = folder.DisplayName,
                Subtitle = folder.RealPath,
                IconPath = folder.RealPath,
                Path = folder.RealPath,
                Score = score + UsageBonus(folder.Id),
            });
        }
    }

    private void AddDockItems(List<SearchResult> results, string key)
    {
        foreach (var dockItem in _config.Current.DockItems)
        {
            var score = PinyinMatcher.Score(dockItem.DisplayName, key, dockItem.TargetPath);
            if (score <= PinyinMatcher.NoMatch)
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Type = "app",
                Id = dockItem.Id,
                Title = dockItem.DisplayName,
                Subtitle = Shorten(dockItem.TargetPath),
                IconPath = dockItem.TargetPath,
                Path = dockItem.TargetPath,
                Score = score + UsageBonus(dockItem.TargetPath),
            });
        }
    }

    private void AddDesktopFiles(List<SearchResult> results, string key)
    {
        foreach (var file in _desktopFiles.Files)
        {
            var score = PinyinMatcher.Score(file.Name, key, file.Path);
            if (score <= PinyinMatcher.NoMatch)
            {
                continue;
            }

            AddFileResult(results, file.Name, file.Path, score, isDirectory: false);
        }
    }

    /// <summary>受管文件夹的一级内容：桌面上的文件夹大多就是「最近要用的东西」的所在。</summary>
    private void AddFolderContents(List<SearchResult> results, string key)
    {
        foreach (var folder in _config.Current.Folders)
        {
            if (!folder.Valid)
            {
                continue;
            }

            foreach (var entry in EntriesOf(folder.RealPath))
            {
                var score = PinyinMatcher.Score(entry.Name, key, entry.Path);
                if (score <= PinyinMatcher.NoMatch)
                {
                    continue;
                }

                AddFileResult(results, entry.Name, entry.Path, score, entry.IsDirectory);
            }
        }
    }

    private void AddFileResult(List<SearchResult> results, string name, string path, int score, bool isDirectory)
    {
        var id = path.ToUpperInvariant();

        results.Add(new SearchResult
        {
            Type = isDirectory ? "folder" : "file",
            Id = id,
            Title = name,
            Subtitle = Shorten(path),
            IconPath = path,
            Path = path,
            Score = score + UsageBonus(id),
        });
    }

    private IReadOnlyList<FileEntry> EntriesOf(string path)
    {
        if (_index.TryGetValue(path, out var cached) && DateTime.UtcNow - cached.TakenAt < IndexTtl)
        {
            return cached.Entries;
        }

        var entries = new List<FileEntry>(FolderEntryCap);

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                if (entries.Count >= FolderEntryCap)
                {
                    break;
                }

                var name = new DirectoryInfo(directory).Name;
                if (IsHidden(name))
                {
                    continue;
                }

                entries.Add(new FileEntry(name, directory, true));
            }

            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (entries.Count >= FolderEntryCap)
                {
                    break;
                }

                var name = Path.GetFileName(file);
                if (IsHidden(name))
                {
                    continue;
                }

                entries.Add(new FileEntry(name, file, false));
            }
        }
        catch (Exception ex)
        {
            // 网盘暂时断开、别人占着目录这类情况：本轮跳过这个文件夹，不能整次搜索失败。
            _logger.LogDebug(ex, "索引受管文件夹失败，本轮跳过 {Path}", path);
        }

        _index[path] = new FolderSnapshot(DateTime.UtcNow, entries);
        return entries;
    }

    /// <summary>
    /// 点号开头的（.git、.vscode）和 shell 自己留下的（desktop.ini、Thumbs.db）不进索引。
    /// 判据直接复用桌面分类那条规则，两处一个标准；属性位不去读 —— 那要给每个条目多一次 stat，
    /// 而隐藏属性对搜索结果的影响远不如名字前缀大。
    /// </summary>
    private static bool IsHidden(string name) => DesktopFileRules.ShouldIgnore(name, FileAttributes.Normal);

    private int UsageBonus(string id) => Math.Min(_usage.GetCount(id), UsageBonusCap);

    private static string Shorten(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) ? path : directory;
    }

    /// <summary>一个受管文件夹的一级内容快照。</summary>
    /// <param name="TakenAt">取样时刻。</param>
    /// <param name="Entries">条目。</param>
    private sealed record FolderSnapshot(DateTime TakenAt, IReadOnlyList<FileEntry> Entries);

    /// <summary>索引到的一条目录内容。</summary>
    /// <param name="Name">名字。</param>
    /// <param name="Path">完整路径。</param>
    /// <param name="IsDirectory">是不是目录。</param>
    private sealed record FileEntry(string Name, string Path, bool IsDirectory);
}
