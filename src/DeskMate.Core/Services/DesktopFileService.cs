using System.Timers;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using Microsoft.Extensions.Logging;

namespace DeskMate.Core.Services;

/// <summary>
/// 桌面文件视图：把桌面上「非文件夹」的文件读出来并按扩展名归类，
/// 再用 <see cref="FileSystemWatcher"/> 在新增/删除/重命名后去抖刷新。
/// <para>
/// 全程只读：不移动、不改名、不删除任何东西，也不写注册表。分类是虚拟的，
/// 桌面文件的真实位置始终不变 —— 这是产品最高约束。
/// </para>
/// </summary>
public sealed class DesktopFileService : IDesktopFileService, IDisposable
{
    /// <summary>连续事件合并成一次刷新：解压一个 zip 会连着抛十几个事件。</summary>
    private const int DebounceMs = 350;

    private readonly ILogger<DesktopFileService> _logger;
    private readonly IReadOnlyList<string> _directories;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly System.Timers.Timer _debounce;
    private readonly List<DesktopFile> _files = new();
    private readonly object _gate = new();

    /// <summary>
    /// 构造桌面文件服务并开始监听。
    /// <paramref name="directories"/> 只为单测留出可写的目录 —— 生产代码一律不传，
    /// 让测试去动用户真实桌面是不该发生的。
    /// </summary>
    public DesktopFileService(ILogger<DesktopFileService> logger, IReadOnlyList<string>? directories = null)
    {
        _logger = logger;
        // 公共桌面也会显示在桌面上，只读用户桌面会漏掉一批快捷方式和文件。
        _directories = directories ?? [WellKnownPaths.UserDesktop, WellKnownPaths.PublicDesktop];

        _debounce = new System.Timers.Timer { Interval = DebounceMs, AutoReset = false };
        _debounce.Elapsed += OnDebounceElapsed;

        Refresh();
        StartWatching();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<DesktopFile> Files
    {
        get
        {
            lock (_gate)
            {
                return _files.ToList();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DesktopFile> Refresh()
    {
        var scanned = new List<DesktopFile>();

        foreach (var directory in _directories)
        {
            scanned.AddRange(ScanDirectory(directory));
        }

        // 用户桌面与公共桌面理论上可能给出同名文件，按完整路径去重后按名字排。
        var ordered = scanned
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_gate)
        {
            _files.Clear();
            _files.AddRange(ordered);
        }

        return ordered;
    }

    /// <summary>释放监听器与定时器。</summary>
    public void Dispose()
    {
        _debounce.Dispose();

        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private static IEnumerable<DesktopFile> ScanDirectory(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(directory))
        {
            DesktopFile? file = Describe(path);
            if (file is not null)
            {
                yield return file.Value;
            }
        }
    }

    /// <summary>读单个文件并归类；忽略或读不到（权限、刚被删）时返回 null。</summary>
    private static DesktopFile? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var name = info.Name;

            if (DesktopFileRules.ShouldIgnore(name, info.Attributes))
            {
                return null;
            }

            var extension = DesktopFileRules.ExtensionOf(name);
            return new DesktopFile(
                name,
                info.FullName,
                extension,
                DesktopFileRules.Categorize(extension),
                info.Length,
                info.LastWriteTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void StartWatching()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
            };

            watcher.Created += (_, _) => ScheduleRefresh();
            watcher.Deleted += (_, _) => ScheduleRefresh();
            watcher.Renamed += (_, _) => ScheduleRefresh();
            watcher.Changed += (_, _) => ScheduleRefresh();

            // 内部缓冲区溢出后 watcher 会自己失效，必须重扫一次并把监听重新打开。
            watcher.Error += (_, e) =>
            {
                _logger.LogWarning(e.GetException(), "桌面目录监听缓冲区溢出，改为全量重扫");
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
                ScheduleRefresh();
            };

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        _logger.LogInformation("桌面文件监听已启动：{Directories}", string.Join("、", _directories));
    }

    private void ScheduleRefresh()
    {
        // Timer 没有 Reset()，停一下再起就是同样的效果。
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceElapsed(object? sender, ElapsedEventArgs e)
    {
        try
        {
            Refresh();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 定时器回调里抛出会拖垮线程池线程，必须就地记录。
            _logger.LogError(ex, "桌面文件刷新失败");
        }
    }
}
