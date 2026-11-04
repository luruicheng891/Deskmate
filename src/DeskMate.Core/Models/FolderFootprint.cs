namespace DeskMate.Core.Models;

/// <summary>
/// 一个目录的「足迹」：一次遍历同时数清文件、称重总大小，顺带取最后修改时间。
/// 分成三个方法各扫一遍盘是没必要的 —— 网络盘上一轮能挂几秒。
/// </summary>
/// <param name="FileCount">递归到的文件数，超过 <see cref="FileCountCap"/> 就是那个上限值。</param>
/// <param name="SizeBytes">文件字节总数；<paramref name="SizeMeasured"/> 为 false 时它没数完，别拿来显示。</param>
/// <param name="SizeMeasured">总大小是否数完了（超大目录或中途没权限时会放弃，宁可不说也不说个错的）。</param>
/// <param name="Modified">目录自身的最后写入时间；取不到时为 <see cref="DateTime.MinValue"/>。</param>
public sealed record FolderFootprint(
    int FileCount,
    long SizeBytes,
    bool SizeMeasured,
    DateTime Modified)
{
    /// <summary>文件数显示上限：再多也只说「10000+」，继续走盘的收益已经没了。</summary>
    public const int FileCountCap = 10000;

    /// <summary>
    /// 总大小的字节上限。超过它就不再称了 —— 一个装了几十万个文件的仓库目录，
    /// 「总计 41.3 GB」这个数字本身不值得让界面等第二轮遍历。
    /// </summary>
    public const long SizeCap = 512L * 1024 * 1024 * 1024;

    /// <summary>取不到修改时间时干脆不显示，而不是显示 0001-01-01 那种怪日期。</summary>
    public bool HasModified => Modified != DateTime.MinValue;
}
