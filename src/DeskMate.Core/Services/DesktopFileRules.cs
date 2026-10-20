using DeskMate.Core.Models;

namespace DeskMate.Core.Services;

/// <summary>
/// 桌面文件的归类规则：按扩展名映射到虚拟分类。
/// <para>
/// 一个扩展名只属于一个分类 —— 映射表按 <see cref="Priority"/> 的顺序构建并只接受第一次出现，
/// 所以不存在「.svg 同时算图片和矢量图」这种需要现场裁决的情况。规则集中在这里是为了能单测。
/// </para>
/// </summary>
public static class DesktopFileRules
{
    /// <summary>虚拟分类标签的 Id 前缀，用来和用户的真实分组 Id 区分开。</summary>
    public const string CategoryPrefix = "desktop:";

    /// <summary>
    /// 归类冲突时的优先级，同时也是顶部标签的排列顺序：一个扩展名只认第一个命中的分类，
    /// 所以不会出现「.svg 算图片还是矢量图」这种要现场裁决的情况，两条规则也只需维护一份。
    /// </summary>
    private static readonly DesktopFileCategory[] Priority =
    [
        DesktopFileCategory.Image,
        DesktopFileCategory.Document,
        DesktopFileCategory.Archive,
    ];

    private static readonly string[] ArchiveExtensions =
    [
        "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "iso",
    ];

    private static readonly string[] ImageExtensions =
    [
        "png", "jpg", "jpeg", "gif", "webp", "svg", "bmp", "ico", "tif", "tiff", "heic", "avif", "raw",
    ];

    private static readonly string[] DocumentExtensions =
    [
        "doc", "docx", "pdf", "txt", "md", "markdown", "xls", "xlsx", "ppt", "pptx",
        "csv", "rtf", "odt", "ods", "odp", "docm", "xlsm", "pptm", "epub",
    ];

    private static readonly Dictionary<string, DesktopFileCategory> Extensions = BuildMap();

    /// <summary>顶部标签的排列顺序：图片、文档、压缩包，兜底的「其他」永远在最后。</summary>
    public static IReadOnlyList<DesktopFileCategory> OrderedCategories { get; } =
        [.. Priority, DesktopFileCategory.Other];

    /// <summary>标签 Id，供顶部 Tab 与筛选复用。</summary>
    public static string TabId(DesktopFileCategory category) => CategoryPrefix + category.ToString().ToLowerInvariant();

    /// <summary>标签显示名。</summary>
    public static string DisplayName(DesktopFileCategory category) => category switch
    {
        DesktopFileCategory.Archive => "压缩包",
        DesktopFileCategory.Image => "图片",
        DesktopFileCategory.Document => "文档",
        _ => "其他",
    };

    /// <summary>是否是桌面分类的标签 Id。</summary>
    public static bool IsCategoryTab(string? tabId)
        => !string.IsNullOrEmpty(tabId) && tabId.StartsWith(CategoryPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>标签 Id 反查分类；不是桌面分类时返回 null。与 <see cref="TabId"/> 互为逆运算。</summary>
    public static DesktopFileCategory? CategoryOfTab(string? tabId)
    {
        if (!IsCategoryTab(tabId))
        {
            return null;
        }

        var name = tabId![CategoryPrefix.Length..];
        return Enum.TryParse<DesktopFileCategory>(name, ignoreCase: true, out var category)
            ? category
            : null;
    }

    /// <summary>
    /// 按扩展名归类。复合扩展（<c>a.tar.gz</c>）取最后一段，所以归进压缩包而不是按 <c>a.tar</c> 的前缀判断。
    /// 没扩展名或认不出来的都进「其他」。
    /// </summary>
    public static DesktopFileCategory Categorize(string extension)
        => Extensions.TryGetValue(Normalize(extension), out var category) ? category : DesktopFileCategory.Other;

    /// <summary>取用于归类的扩展名：小写、不带点、只留最后一段。</summary>
    public static string ExtensionOf(string fileName)
    {
        var index = fileName.LastIndexOf('.');
        if (index < 0 || index == fileName.Length - 1)
        {
            return string.Empty;
        }

        // 点号后面还有点（如 archive.tar.gz）时只认最后一段。
        var tail = fileName[(index + 1)..];
        return Normalize(tail);
    }

    /// <summary>
    /// 是否应当忽略这个文件：隐藏/系统属性、以点开头的（.DS_Store 这类）、
    /// 以及 Windows 自己维护桌面的两个文件（desktop.ini、Thumbs.db）。
    /// </summary>
    public static bool ShouldIgnore(string fileName, System.IO.FileAttributes attributes)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.StartsWith('.'))
        {
            return true;
        }

        if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const System.IO.FileAttributes hiddenish = System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System;
        return (attributes & hiddenish) != 0;
    }

    private static string Normalize(string extension) => extension.Trim().TrimStart('.').ToLowerInvariant();

    private static Dictionary<string, DesktopFileCategory> BuildMap()
    {
        var map = new Dictionary<string, DesktopFileCategory>(StringComparer.OrdinalIgnoreCase);

        // 按优先级顺序填，同一个扩展名只认第一次出现的分类。
        foreach (var category in Priority)
        {
            foreach (var extension in ExtensionsOf(category))
            {
                map.TryAdd(extension, category);
            }
        }

        return map;
    }

    private static IEnumerable<string> ExtensionsOf(DesktopFileCategory category) => category switch
    {
        DesktopFileCategory.Image => ImageExtensions,
        DesktopFileCategory.Archive => ArchiveExtensions,
        DesktopFileCategory.Document => DocumentExtensions,
        _ => [],
    };
}
