namespace DeskMate.Core.Models;

/// <summary>
/// 一个文件该用哪种方式画出内容。判定只看扩展名与文件名，不做嗅探 ——
/// 猜错代价是「预览空白」，而按扩展名走最多是「这类我们暂时看不了」，后者对用户是可解释的。
/// </summary>
public enum FilePreviewKind
{
    /// <summary>暂时看不了：只给元信息，并告诉用户双击会用哪个程序打开。</summary>
    Unsupported = 0,

    /// <summary>纯文本 / 代码：等宽、保留缩进。</summary>
    Text = 1,

    /// <summary>Markdown：按块渲染，不是源码。</summary>
    Markdown = 2,

    /// <summary>图片：WPF 自带解码器能解的那些。</summary>
    Image = 3,

    /// <summary>PDF：逐页渲染成位图。</summary>
    Pdf = 4,

    /// <summary>压缩包：列条目，不解压。</summary>
    Archive = 5,

    /// <summary>OOXML 文档（.docx/.xlsx/.pptx）：包里的 XML 就是正文，直接抽文字。</summary>
    Office = 6,
}

/// <summary>
/// 读出来的文本预览。
/// </summary>
/// <param name="Text">已解码的正文（截断时已切到最后一条完整行）。</param>
/// <param name="EncodingName">实际按哪种编码解的，界面上要写出来 —— 中文注释解错编码时用户得知道为什么。</param>
/// <param name="TotalBytes">文件真实大小。</param>
/// <param name="Truncated">是否只读了开头一段。</param>
public readonly record struct TextPreview(string Text, string EncodingName, long TotalBytes, bool Truncated);

/// <summary>压缩包里的一条条目（只列不解压，也不写回任何东西）。</summary>
public readonly record struct ArchiveEntryInfo(string FullName, long SizeBytes, DateTime ModifiedAt, bool IsDirectory);

/// <summary>Markdown 的块类型。渲染样式归界面，这里只负责「这一行是哪种块」。</summary>
public enum MarkdownBlockKind
{
    Heading = 0,
    Bullet = 1,
    Numbered = 2,
    Quote = 3,
    Code = 4,
    Divider = 5,
    Paragraph = 6,
}

/// <summary>
/// 一块 Markdown。
/// </summary>
/// <param name="Level">标题级别（1–6），非标题块为 0。</param>
/// <param name="Text">去掉块标记之后的正文。</param>
public readonly record struct MarkdownBlock(MarkdownBlockKind Kind, int Level, string Text);
