using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using DeskMate.Core.Models;

namespace DeskMate.Core.Services;

/// <summary>
/// 从 OOXML（.docx / .xlsx / .pptx）里抽出**带结构**的段落。
/// <para>
/// 与「只抽文字」的区别就是这份文件存在的全部理由：简历、试卷、实验报告这类文档，
/// 「哪一行是标题、哪一块是表格」比正文本身更重要 —— 抽平成一片文字等于没渲染。
/// </para>
/// <para>
/// 复用 Markdown 的块模型：Word 的加粗用 <c>**</c> 包起来交给同一套行内渲染，表格整块按等宽画
/// （等宽才能让列对齐），标题级别优先看 <c>w:pStyle</c>，没有样式时按字号兜底。
/// 老式 .doc/.xls/.ppt 是二进制复合文档，不在这里做。
/// </para>
/// </summary>
public static class OfficeDocumentReader
{
    private const int MaxBlocks = 4000;
    private const int MaxSheets = 4;
    private const int MaxRowsPerSheet = 200;

    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Draw = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";

    /// <summary>抽出结构化段落；读不出结构时返回空表（界面会退回「元信息 + 双击打开」那张卡）。</summary>
    public static async Task<IReadOnlyList<MarkdownBlock>> ReadAsync(string path, CancellationToken token = default)
        => await Task.Run(() => Read(path), token);

    private static IReadOnlyList<MarkdownBlock> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" or ".docm" => ReadWord(archive),
            ".pptx" or ".pptm" => ReadSlides(archive),
            ".xlsx" or ".xlsm" => ReadWorkbook(archive),
            _ => [],
        };
    }

    // ==================== Word ====================

    private static IReadOnlyList<MarkdownBlock> ReadWord(ZipArchive archive)
    {
        var blocks = new List<MarkdownBlock>();
        var body = Load(archive, "word/document.xml")?.Root?.Element(Word + "body");

        if (body is null)
        {
            return blocks;
        }

        // 只走正文的直接子元素：段落与表格交替出现，表格里面的段落由 AppendTable 自己处理，
        // 用 Descendants 会把它们再数一遍。
        foreach (var element in body.Elements())
        {
            if (!TryKeep(blocks))
            {
                break;
            }

            if (element.Name == Word + "tbl")
            {
                AppendTable(blocks, element);
            }
            else if (element.Name == Word + "p")
            {
                AppendParagraph(blocks, element);
            }
        }

        return blocks;
    }

    private static void AppendParagraph(List<MarkdownBlock> blocks, XElement paragraph)
    {
        var level = HeadingLevel(paragraph);

        // 标题不再包 **：块本身就会渲染成半粗，再包一层等于在粗体上又印一对星号。
        var text = ParagraphText(paragraph, Word + "t", level <= 0, (Word + "tab", "\t"), (Word + "br", "\n")).Trim();

        if (text.Length == 0)
        {
            return;
        }

        blocks.Add(level > 0
            ? new MarkdownBlock(MarkdownBlockKind.Heading, level, text)
            : new MarkdownBlock(MarkdownBlockKind.Paragraph, 0, text));
    }

    /// <summary>
    /// 标题级别。先看样式（Word 的「标题 1 / Heading 1」），没样式时按字号兜底 ——
    /// 简历和课件里大量「手动加大字号」的标题根本不带 pStyle，不兜这一层就全成正文。
    /// </summary>
    private static int HeadingLevel(XElement paragraph)
    {
        var style = (string?)paragraph.Element(Word + "pPr")?.Element(Word + "pStyle")?.Attribute(Word + "val") ?? string.Empty;

        if (style.Contains("标题", StringComparison.Ordinal) || style.Contains("heading", StringComparison.OrdinalIgnoreCase))
        {
            var digit = DigitIn(style);
            return digit is >= 1 and <= 6 ? digit.Value : 1;
        }

        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase) || style.Equals("标题", StringComparison.Ordinal))
        {
            return 1;
        }

        // 字号（半点）→ 磅：Word 存的是半点数，28 = 14pt。
        var size = paragraph.Descendants(Word + "sz").FirstOrDefault() is { } first
            ? TryParseHalfPoint((string?)first.Attribute(Word + "val"))
            : 0d;

        var text = paragraph.Descendants(Word + "t").Aggregate(new StringBuilder(), (builder, node) => builder.Append((string?)node ?? string.Empty));

        if (text.Length == 0 || text.Length > 40)
        {
            return 0;
        }

        // 字号（磅）：Word 的 w:sz 存的是半点数，TryParseHalfPoint 已经换算过。
        // 整段加粗 + 字号偏大，才敢当标题：只按字号会把所有大号字正文都提成标题。
        var allBold = paragraph.Descendants(Word + "r").All(run => IsBold(run)
            || !run.Descendants(Word + "t").Any(node => !string.IsNullOrEmpty((string?)node)));

        return size switch
        {
            >= 16 when allBold => 1,
            >= 13 when allBold => 2,
            _ => 0,
        };
    }

    private static string RunTextWithBold(XElement paragraph)
    {
        var builder = new StringBuilder();

        foreach (var run in paragraph.Descendants(Word + "r"))
        {
            var text = string.Concat(run.Descendants(Word + "t").Select(node => (string?)node ?? string.Empty));

            foreach (var extra in new[] { (Word + "tab", "\t"), (Word + "br", "\n") })
            {
                if (run.Elements(extra.Item1).Any())
                {
                    text += extra.Item2;
                }
            }

            if (text.Length == 0)
            {
                continue;
            }

            // 已经有星号就不包了：不然 **a*b** 这种会被行内解析器切歪。
            builder.Append(IsBold(run) && !text.Contains('*') ? $"**{text}**" : text);
        }

        return builder.ToString();
    }

    private static bool IsBold(XElement run)
    {
        var bold = run.Element(Word + "rPr")?.Element(Word + "b");

        return bold is not null
            && (string?)bold.Attribute(Word + "val") is not ("0" or "false" or "none");
    }

    private static void AppendTable(List<MarkdownBlock> blocks, XElement table)
    {
        var rows = new List<string>();

        foreach (var row in table.Elements(Word + "tr"))
        {
            var cells = row.Elements(Word + "tc")
                .Select(cell => string.Join(" ", cell.Elements(Word + "p").Select(PlainText)))
                .ToArray();

            if (cells.Any(cell => cell.Length > 0))
            {
                rows.Add(string.Join(" | ", cells));
            }
        }

        if (rows.Count > 0)
        {
            // 整张表合成一个等宽块：一行一个灰条会碎成斑马纹，反而读不出「这是一张表」。
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, 0, string.Join('\n', rows)));
        }
    }

    private static string PlainText(XElement paragraph)
        => RunTextWithBold(paragraph).Trim();

    // ==================== PPT ====================

    private static IReadOnlyList<MarkdownBlock> ReadSlides(ZipArchive archive)
    {
        var blocks = new List<MarkdownBlock>();

        var slides = archive.Entries
            .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => NumberOf(entry.FullName))
            .ToList();

        foreach (var (entry, index) in slides.Select((slide, position) => (slide, position)))
        {
            if (!TryKeep(blocks))
            {
                break;
            }

            var document = Load(archive, entry.FullName);

            if (document is null)
            {
                continue;
            }

            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, 1, $"第 {index + 1} 页"));

            foreach (var shape in document.Descendants(Presentation + "sp"))
            {
                var texts = shape.Descendants(Draw + "p")
                    .Select(paragraph => ParagraphText(paragraph, Draw + "t", false, (Draw + "br", "\n")))
                    .Where(text => text.Length > 0)
                    .ToList();

                if (texts.Count == 0)
                {
                    continue;
                }

                // 占位符类型是 title/ctrTitle 的那一块当标题，其余按正文。
                var isTitle = shape.Descendants(Presentation + "ph")
                    .Select(place => (string?)place.Attribute("type"))
                    .Any(type => type is "title" or "ctrTitle");

                blocks.Add(new MarkdownBlock(
                    isTitle ? MarkdownBlockKind.Heading : MarkdownBlockKind.Paragraph,
                    isTitle ? 2 : 0,
                    string.Join('\n', texts)));
            }
        }

        return blocks;
    }

    // ==================== Excel ====================

    private static IReadOnlyList<MarkdownBlock> ReadWorkbook(ZipArchive archive)
    {
        var shared = ReadSharedStrings(archive);
        var workbook = Load(archive, "xl/workbook.xml");
        var blocks = new List<MarkdownBlock>();

        var sheets = archive.Entries
            .Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => NumberOf(entry.FullName))
            .ToList();

        foreach (var (entry, index) in sheets.Take(MaxSheets).Select((sheet, position) => (sheet, position)))
        {
            if (!TryKeep(blocks))
            {
                break;
            }

            var document = Load(archive, entry.FullName);

            if (document is null)
            {
                continue;
            }

            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, 2, SheetName(workbook, index)));

            var rows = document.Descendants(Sheet + "row")
                .Take(MaxRowsPerSheet)
                .Select(row => string.Join(" | ", row.Elements(Sheet + "c").Select(cell => CellText(cell, shared))))
                .Where(line => line.Replace("|", string.Empty).Trim().Length > 0)
                .ToList();

            if (rows.Count > 0)
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, 0, string.Join('\n', rows)));
            }

            if (sheets.Count > MaxSheets && index == MaxSheets - 1)
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, 0, $"（还有 {sheets.Count - MaxSheets} 个工作表没列）"));
            }
        }

        return blocks;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var document = Load(archive, "xl/sharedStrings.xml");

        if (document is null)
        {
            return [];
        }

        return document.Descendants(Sheet + "si")
            .Select(item => string.Concat(item.Descendants(Sheet + "t").Select(text => (string?)text ?? string.Empty)))
            .ToList();
    }

    private static string CellText(XElement cell, IReadOnlyList<string> shared)
    {
        var type = (string?)cell.Attribute("t");

        if (type == "s")
        {
            var raw = (string?)cell.Element(Sheet + "v");
            return int.TryParse(raw, out var index) && index >= 0 && index < shared.Count ? shared[index] : string.Empty;
        }

        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(Sheet + "t").Select(text => (string?)text ?? string.Empty));
        }

        return (string?)cell.Element(Sheet + "v") ?? string.Empty;
    }

    private static string SheetName(XContainer? workbook, int index)
    {
        var names = workbook?.Descendants(Sheet + "sheet").Select(element => (string?)element.Attribute("name")).ToList();

        return names is not null && index < names.Count && !string.IsNullOrWhiteSpace(names[index])
            ? names[index]!
            : $"工作表 {index + 1}";
    }

    // ==================== 公用 ====================

    /// <summary>
    /// 把一个段落里的文字按顺序拼起来。
    /// </summary>
    /// <param name="markBold">加粗的 run 要不要用 <c>**</c> 包起来 —— 标题块自己就是粗的，再包一层只会变成噪声。</param>
    private static string ParagraphText(XElement paragraph, XName textName, bool markBold, params (XName Name, string Text)[] extras)
    {
        var builder = new StringBuilder();
        var runName = textName.Namespace + "r";

        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == textName)
            {
                var text = node.Value;
                var parent = node.Parent;
                var bold = markBold && parent is not null && parent.Name == runName && IsBold(parent);

                builder.Append(bold && !text.Contains('*') ? $"**{text}**" : text);
                continue;
            }

            foreach (var (name, text) in extras)
            {
                if (node.Name == name)
                {
                    builder.Append(text);
                    break;
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static bool TryKeep(List<MarkdownBlock> blocks)
    {
        if (blocks.Count < MaxBlocks)
        {
            return true;
        }

        blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, 0, $"（内容太长，只渲染前 {MaxBlocks} 块）"));
        return false;
    }

    private static XDocument? Load(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);

        if (entry is null)
        {
            return null;
        }

        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            // 包坏了一半（下载没下完、被同步盘截断）：这一份读不出来就整体放弃，别抛到界面。
            return null;
        }
    }

    private static int NumberOf(string entryName)
    {
        var digits = new string(entryName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : int.MaxValue;
    }

    private static int? DigitIn(string value)
    {
        foreach (var character in value)
        {
            if (char.IsDigit(character))
            {
                return character - '0';
            }
        }

        return null;
    }

    private static double TryParseHalfPoint(string? value)
        => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result / 2d
            : 0d;
}
