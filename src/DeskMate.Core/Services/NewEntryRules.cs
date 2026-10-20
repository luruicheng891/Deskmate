namespace DeskMate.Core.Services;

/// <summary>「新建」按名字判断出来的类型。</summary>
public enum NewEntryKind
{
    /// <summary>名字里没有后缀 → 建文件夹。</summary>
    Folder,

    /// <summary>名字带后缀（如 <c>main.c</c>）→ 建空文件。</summary>
    File,
}

/// <summary>
/// 卡片上「＋ 新建」的名字规则。用户定的口径就一句话：不带后缀是文件夹，带后缀是文件，
/// 所以这里只照字面判断 —— 不替用户补 <c>.txt</c>，也不把 <c>报告.docx</c> 建成文件夹。
/// </summary>
public static class NewEntryRules
{
    /// <summary>Windows 不允许出现在文件名里的字符（<c>Path.GetInvalidFileNameChars</c> 还带控制字符，这里只列用户看得懂的那几个）。</summary>
    private static readonly char[] IllegalChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>保留设备名：即便写成 CON.txt 也不能用，所以比对的是点号前面那一段。</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>单个名字的长度上限。真正常识范围内够用，再长卡片上也读不完。</summary>
    public const int MaxNameLength = 120;

    /// <summary>这个 <paramref name="name"/> 该建成文件夹还是文件。</summary>
    public static NewEntryKind Classify(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var dot = trimmed.LastIndexOf('.');

        // 没有点，或者点收尾（"报告." 没有可用后缀）→ 文件夹。
        // 点开头（".gitignore"）算文件名，这是大家约定的写法，不按「没后缀」处理。
        return dot < 0 || dot == trimmed.Length - 1 ? NewEntryKind.Folder : NewEntryKind.File;
    }

    /// <summary>
    /// 能不能拿这个名字建。返回 <c>null</c> 表示可以，否则是给用户看的中文原因 ——
    /// 输入框旁边要的就是这句话，别只回一句「创建失败」。
    /// </summary>
    public static string? Validate(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return "名字不能为空。";
        }

        if (trimmed.IndexOfAny(IllegalChars) >= 0)
        {
            return "名字里不能用 \\ / : * ? \" < > | 这些字符。";
        }

        if (trimmed.EndsWith('.') || trimmed.EndsWith(' '))
        {
            return "名字不能以点或空格结尾。";
        }

        if (trimmed.Length > MaxNameLength)
        {
            return $"名字太长了，最多 {MaxNameLength} 个字。";
        }

        var stem = trimmed[..(trimmed.IndexOf('.') < 0 ? trimmed.Length : trimmed.IndexOf('.'))];
        if (Array.Exists(ReservedNames, item => string.Equals(item, stem, StringComparison.OrdinalIgnoreCase)))
        {
            return $"「{stem}」是 Windows 保留的设备名，换一个。";
        }

        return null;
    }
}
