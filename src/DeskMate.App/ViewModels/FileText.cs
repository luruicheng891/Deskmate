namespace DeskMate.App.ViewModels;

/// <summary>卡片上那些「给人看的文件大小 / 时间」写法，桌面分类与收藏共用一份。</summary>
internal static class FileText
{
    /// <summary>字节数换成 B/KB/MB/GB，一位小数。</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    /// <summary>日期到「天」就够，时分在窄卡片上会被裁掉。</summary>
    public static string Modified(DateTime value) => value.ToString("yyyy-MM-dd");
}
