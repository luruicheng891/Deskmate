namespace DeskMate.Core.Services;

/// <summary>新建出来的东西长什么样。</summary>
public enum NewEntryShape
{
    /// <summary>永远是文件夹，名字里有点也不当成后缀。</summary>
    Folder,

    /// <summary>永远是这个类型的文件，名字后面自动补后缀。</summary>
    Fixed,

    /// <summary>照用户写的字面判断：不带后缀=文件夹，带后缀=文件（<c>main.c</c> 就靠它）。</summary>
    FreeForm,
}

/// <summary>
/// 新建浮层里的一个类型选项。<param name="Suffix">要补的后缀（含点），文件夹与「自己写后缀」时为空。</param>
/// </summary>
public sealed record NewEntryOption(string Key, string Label, string Suffix, NewEntryShape Shape);

/// <summary>
/// 可选的新建类型。Word / Excel / PPT 不是「建个空文件」就完事 —— 0 字节的 .docx 打开会报损坏，
/// 那三种由 <see cref="OoxmlTemplates"/> 生成合法的最小 OOXML 包，并已在装有 Office 的机器上真打开验证过。
/// </summary>
public static class NewEntryCatalog
{
    /// <summary>「自己写后缀」那一项，也是旧行为的入口。</summary>
    public static readonly NewEntryOption FreeForm = new("custom", "其他（自己写后缀）", string.Empty, NewEntryShape.FreeForm);

    /// <summary>浮层里从上到下排的选项。</summary>
    public static readonly IReadOnlyList<NewEntryOption> Options =
    [
        new("folder", "文件夹", string.Empty, NewEntryShape.Folder),
        new("docx", "Word 文档", ".docx", NewEntryShape.Fixed),
        new("xlsx", "Excel 工作簿", ".xlsx", NewEntryShape.Fixed),
        new("pptx", "PPT 演示文稿", ".pptx", NewEntryShape.Fixed),
        new("md", "Markdown", ".md", NewEntryShape.Fixed),
        new("txt", "文本文件", ".txt", NewEntryShape.Fixed),
        FreeForm,
    ];

    /// <summary>把用户输入的名字和选中的类型拼成真正落到磁盘上的名字。</summary>
    public static string ResolveName(string? name, NewEntryOption option)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (option.Shape != NewEntryShape.Fixed || option.Suffix.Length == 0)
        {
            return trimmed;
        }

        // 已经写了后缀就别再补一遍（"报告.docx" + .docx ≠ "报告.docx.docx"）。
        return trimmed.EndsWith(option.Suffix, StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + option.Suffix;
    }
}
