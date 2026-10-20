using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DeskMate.Core.Interfaces;
using DeskMate.Core.Models;
using DeskMate.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeskMate.Core.Services;

/// <summary>
/// 已安装应用扫描：开始菜单目录 + 注册表 App Paths。
/// 扫描全程只读，且任何单个坏快捷方式都不会中断整轮扫描。
/// </summary>
public sealed class AppScannerService : IAppScannerService
{
    private const string AppPathsKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private readonly StaScheduler _sta;
    private readonly IConfigService _config;
    private readonly ILogger<AppScannerService> _logger;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _seenTargets = new(StringComparer.OrdinalIgnoreCase);
    private volatile List<AppInfo> _apps = [];

    /// <summary>构造扫描服务。</summary>
    /// <param name="sta">执行 COM 解析的 STA 线程。</param>
    /// <param name="config">配置，用于读取自定义快捷方式文件夹。</param>
    /// <param name="logger">日志。</param>
    /// <param name="cachePath">默认 %APPDATA%\DeskMate\apps_cache.json。</param>
    public AppScannerService(
        StaScheduler sta,
        IConfigService config,
        ILogger<AppScannerService> logger,
        string? cachePath = null)
    {
        _sta = sta;
        _config = config;
        _logger = logger;
        _cachePath = cachePath ?? WellKnownPaths.AppsCacheFile;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppInfo>> ScanInstalledAppsAsync(bool forceRefresh = false)
    {
        if (_apps.Count > 0 && !forceRefresh)
        {
            return _apps;
        }

        // 二次打开启动台的路径：磁盘缓存命中就直接返回，扫描留给后台。
        if (!forceRefresh && await TryLoadCacheAsync())
        {
            return _apps;
        }

        return await ScanCoreAsync();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AppInfo>> RefreshAsync() => ScanCoreAsync();

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppInfo>> SearchAppsAsync(string keyword)
    {
        var apps = await ScanInstalledAppsAsync();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return apps;
        }

        return apps
            .Select(app => (App: app, Score: PinyinMatcher.Score(app.Name, keyword, app.Path)))
            .Where(pair => pair.Score > PinyinMatcher.NoMatch)
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.App.Name, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.App)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<string> AddToLaunchpadAsync(string sourcePath)
    {
        var source = Environment.ExpandEnvironmentVariables(sourcePath).Trim().Trim('"');
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("找不到要添加的程序或快捷方式。", source);
        }

        var folder = WellKnownPaths.ResolveLaunchpadFolder(_config.Current.LaunchpadFolder);

        // 建快捷方式是 COM 调用，排到 STA 线程上。
        var linkPath = await _sta.RunAsync(() => CreateLaunchpadLink(folder, source));

        // 显式添加代表用户此刻就是想要它，之前被隐藏的同一目标要放行。
        // 这里必须按「目标路径」算 Id 去解隐藏，不能在 _apps 里按链接路径找：
        // 同一个程序常在开始菜单还有另一个快捷方式，去重后 _apps 里那条的 Path 是开始菜单的 .lnk，
        // 拿我们刚建的链接路径永远匹配不上，于是「重新添加一个被隐藏过的应用」会静默无效。
        var targetId = await _sta.RunAsync(() => TargetIdOf(linkPath));
        if (_config.Current.HiddenApps.RemoveAll(
                id => string.Equals(id, targetId, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            await _config.SaveAsync();
            _logger.LogInformation("已解除隐藏：{Id}", targetId);
        }

        await ScanCoreAsync();

        _logger.LogInformation("已加入启动台：{Link}", linkPath);
        return linkPath;
    }

    /// <summary>按扫描时的同一套规则算出这个链接对应条目的 Id（真实目标的大写路径）。</summary>
    private static string TargetIdOf(string linkPath)
    {
        if (!ShellLink.IsShortcut(linkPath))
        {
            return Canonical(linkPath).ToUpperInvariant();
        }

        var target = ShellLink.Resolve(linkPath).Target;

        return Canonical(string.IsNullOrWhiteSpace(target) ? linkPath : target).ToUpperInvariant();
    }

    /// <summary>
    /// 目标路径的规范形式：展开环境变量、还原 8.3 短路径、去掉多余的引号。
    /// 去重键、条目 Id 与隐藏列表全都建立在它之上，两种写法混用会让「重新添加一个被隐藏过的应用」失效。
    /// </summary>
    private static string Canonical(string path)
        => PathInterop.GetLongPath(Environment.ExpandEnvironmentVariables(path).Trim().Trim('"'));

    private static string CreateLaunchpadLink(string folder, string source)
    {
        Directory.CreateDirectory(folder);

        if (!ShellLink.IsShortcut(source))
        {
            var exeName = Path.GetFileNameWithoutExtension(source);
            var exeLink = UniqueLinkPath(folder, exeName);
            ShellLink.Create(exeLink, source, exeName, Path.GetDirectoryName(source));
            return exeLink;
        }

        var resolved = ShellLink.Resolve(source);
        var target = string.IsNullOrWhiteSpace(resolved.Target)
            ? source
            : Environment.ExpandEnvironmentVariables(resolved.Target).Trim().Trim('"');

        // 转写成新快捷方式而不是复制原文件：原 .lnk 里的相对路径与图标引用换目录后容易失效。
        var link = UniqueLinkPath(folder, Path.GetFileNameWithoutExtension(source));
        ShellLink.Create(
            link,
            target,
            Path.GetFileNameWithoutExtension(source),
            Path.GetDirectoryName(target),
            string.IsNullOrWhiteSpace(resolved.Arguments) ? null : resolved.Arguments,
            string.IsNullOrWhiteSpace(resolved.IconPath) ? null : resolved.IconPath,
            resolved.IconIndex);

        return link;
    }

    private static string UniqueLinkPath(string folder, string baseName)
    {
        var safe = string.Join('_', baseName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (safe.Length == 0)
        {
            safe = "app";
        }

        var candidate = Path.Combine(folder, safe + ".lnk");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{safe} ({suffix++}).lnk");
        }

        return candidate;
    }

    private async Task<IReadOnlyList<AppInfo>> ScanCoreAsync()
    {
        await _scanGate.WaitAsync();
        try
        {
            // ShellLink 是 COM 组件，必须在 STA 线程上创建，线程池线程上做会随机失败。
            var scanned = await _sta.RunAsync(Scan);
            _apps = scanned;
            await PersistCacheAsync(scanned);
            _logger.LogInformation("应用扫描完成，共 {Count} 个", scanned.Count);
            return scanned;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private List<AppInfo> Scan()
    {
        _seenTargets.Clear();
        var results = new List<AppInfo>(256);

        foreach (var root in StartMenuRoots())
        {
            foreach (var lnk in EnumerateFilesSafe(root, "*.lnk"))
            {
                var app = FromShortcut(lnk, "StartMenu");
                if (app is not null)
                {
                    results.Add(app);
                }
            }
        }

        // 用户自己的快捷方式文件夹：放进去就等于出现在启动台，删掉快捷方式或右键隐藏则移除。
        foreach (var entry in EnumerateLaunchpadEntries())
        {
            var app = ShellLink.IsShortcut(entry)
                ? FromShortcut(entry, "Launchpad")
                : FromExecutable(entry, "Launchpad");

            if (app is not null)
            {
                results.Add(app);
            }
        }

        foreach (var app in EnumerateAppPaths())
        {
            results.Add(app);
        }

        // 系统后台组件与管理单元件不该出现在启动台里（Office 语言首选项、组件服务、各类 helper）。
        return results
            .Where(app => !AppFilterRules.IsNoise(app))
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> EnumerateLaunchpadEntries()
    {
        var folder = WellKnownPaths.ResolveLaunchpadFolder(_config.Current.LaunchpadFolder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return EnumerateFilesSafe(folder, "*.lnk").Concat(EnumerateFilesSafe(folder, "*.exe"));
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
    }

    private AppInfo? FromShortcut(string lnkPath, string source)
    {
        try
        {
            var resolved = ShellLink.Resolve(lnkPath);

            // 目标可能是 %windir%\regedit.exe 这类未展开写法，也可能是 8.3 短路径，
            // 统一规范成长路径后再去重；否则同一个程序会因为写法不同而重复出现在启动台里，
            // 隐藏列表也会因为两种写法对不上而解不开。
            var rawTarget = string.IsNullOrWhiteSpace(resolved.Target)
                ? Canonical(lnkPath)
                : Canonical(resolved.Target);

            // 去重键用真实目标：同一个程序常在开始菜单出现多个链接。
            if (!_seenTargets.TryAdd(rawTarget, 0))
            {
                return null;
            }

            var name = Path.GetFileNameWithoutExtension(lnkPath);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return new AppInfo
            {
                Id = rawTarget.ToUpperInvariant(),
                Name = name,
                // 启动时仍然执行 .lnk 本身，这样快捷方式里配置的参数和工作目录不会丢。
                Path = lnkPath,
                // 图标也交给 .lnk：IconService 会解析出自定义图标位置并处理 ,-N 索引与环境变量。
                // 直接把 ShellLink 给的图标位置字符串存下来是错的 —— 那是 "%windir%\x,-50" 这种未展开、
                // 且索引已经丢失的形式，取不到图标（启动台部分格子空白就是这个原因）。
                IconPath = lnkPath,
                Source = source,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "跳过无法解析的快捷方式 {Path}", lnkPath);
            return null;
        }
    }

    /// <summary>快捷方式文件夹里直接放 .exe 的情况：目标就是它自己。</summary>
    private AppInfo? FromExecutable(string exePath, string source)
    {
        var target = Canonical(exePath);
        var key = target.ToUpperInvariant();

        if (!_seenTargets.TryAdd(target, 0))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(target);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new AppInfo
        {
            Id = key,
            Name = name,
            Path = target,
            IconPath = target,
            Source = source,
        };
    }

    private IEnumerable<AppInfo> EnumerateAppPaths()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var appPaths = baseKey.OpenSubKey(AppPathsKeyPath);
            if (appPaths is null)
            {
                continue;
            }

            foreach (var subKeyName in appPaths.GetSubKeyNames())
            {
                // 32 位程序注册在 WOW6432Node 下，不额外展开就会漏掉一批软件。
                if (string.Equals(subKeyName, "WOW6432Node", StringComparison.OrdinalIgnoreCase))
                {
                    using var wow = appPaths.OpenSubKey(subKeyName);
                    if (wow is not null)
                    {
                        foreach (var nested in wow.GetSubKeyNames())
                        {
                            var fromWow = ReadAppPathEntry(wow, nested);
                            if (fromWow is not null)
                            {
                                yield return fromWow;
                            }
                        }
                    }

                    continue;
                }

                var app = ReadAppPathEntry(appPaths, subKeyName);
                if (app is not null)
                {
                    yield return app;
                }
            }
        }
    }

    private AppInfo? ReadAppPathEntry(RegistryKey parent, string subKeyName)
    {
        try
        {
            using var key = parent.OpenSubKey(subKeyName);
            var raw = key?.GetValue(string.Empty) as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var exe = Environment.ExpandEnvironmentVariables(raw).Trim().Trim('"');
            if (!File.Exists(exe) || !_seenTargets.TryAdd(exe, 0))
            {
                return null;
            }

            return new AppInfo
            {
                Id = exe.ToUpperInvariant(),
                Name = Path.GetFileNameWithoutExtension(subKeyName),
                Path = exe,
                IconPath = exe,
                Source = "Registry",
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "跳过异常 App Paths 项 {Key}", subKeyName);
            return null;
        }
    }

    private async Task<bool> TryLoadCacheAsync()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return false;
            }

            var json = await File.ReadAllTextAsync(_cachePath, Encoding.UTF8);
            var cache = JsonSerializer.Deserialize<AppsCache>(json);
            if (cache?.Apps is null || cache.Apps.Count == 0)
            {
                return false;
            }

            _apps = cache.Apps;
            foreach (var app in cache.Apps)
            {
                _seenTargets.TryAdd(app.Id, 0);
            }

            _logger.LogInformation("应用缓存命中 {Count} 条（扫描于 {Time}）", cache.Apps.Count, cache.ScannedAt);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "应用缓存读取失败，改为重新扫描");
            return false;
        }
    }

    private async Task PersistCacheAsync(List<AppInfo> apps)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath) ?? string.Empty);
            var payload = new AppsCache { ScannedAt = DateTime.Now, Apps = apps };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_cachePath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // 缓存只是加速手段，写失败不影响本次使用，故降级为告警而非向上抛。
            _logger.LogWarning(ex, "应用缓存写入失败");
        }
    }

    /// <summary>
    /// 递归枚举匹配文件，遇到没有权限的目录跳过而不是抛异常中断。
    /// <see cref="Directory.EnumerateFiles(string,string,SearchOption)"/> 的 AllDirectories 模式
    /// 会在碰到受限目录时直接抛 UnauthorizedAccessException，开始菜单里这种目录很常见。
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current, pattern);
                directories = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var directory in directories)
            {
                pending.Push(directory);
            }
        }
    }

    private sealed class AppsCache
    {
        public DateTime ScannedAt { get; set; }

        public List<AppInfo> Apps { get; set; } = [];
    }
}
