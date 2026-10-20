namespace DeskMate.Core.Services;

/// <summary>
/// 用户数据只写 <c>%APPDATA%\DeskMate\</c>（核心原则二：数据隔离），
/// 卸载时删掉这一个目录即可清干净。
/// </summary>
public static class WellKnownPaths
{
    /// <summary>数据根目录。</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskMate");

    /// <summary>主配置。</summary>
    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>应用扫描缓存，二次打开启动台直接读它。</summary>
    public static string AppsCacheFile => Path.Combine(Root, "apps_cache.json");

    /// <summary>启动次数统计。</summary>
    public static string UsageFile => Path.Combine(Root, "usage.json");

    /// <summary>日志目录。</summary>
    public static string LogsFolder => Path.Combine(Root, "logs");

    /// <summary>
    /// 启动台自定义快捷方式文件夹的默认位置。放在自己的数据目录里，
    /// 用户可以在设置里改成任意目录，但只有一个。
    /// </summary>
    public static string DefaultLaunchpadFolder => Path.Combine(Root, "Launchpad");

    /// <summary>生效的启动台文件夹：配置为空时用默认目录。</summary>
    public static string ResolveLaunchpadFolder(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultLaunchpadFolder : configured.Trim().Trim('"');

    /// <summary>当前用户的桌面目录。</summary>
    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>所有用户的公共桌面目录。</summary>
    public static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>创建数据目录与日志目录，启动时调用一次。</summary>
    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsFolder);

        // 启动台快捷方式文件夹默认就存在，用户直接往里放快捷方式即可。
        Directory.CreateDirectory(DefaultLaunchpadFolder);
    }
}
