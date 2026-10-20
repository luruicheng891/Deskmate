using System.IO.Compression;
using System.Text;
using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 文件内容预览的读取层。三类都要覆盖：判定（该用哪种方式画）、解码（编码与截断）、
/// 以及「不许把用户的文件锁住」—— 这条是产品底线，只有真开着另一个句柄才测得出来。
/// </summary>
public class FilePreviewTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Write(string name, string text) => Write(name, Encoding.UTF8.GetBytes(text));

    // ==================== 判定 ====================

    [Theory]
    [InlineData(@"C:\x\笔记.md", FilePreviewKind.Markdown)]
    [InlineData(@"C:\x\main.c", FilePreviewKind.Text)]
    [InlineData(@"C:\x\stm32f1xx.h", FilePreviewKind.Text)]
    [InlineData(@"C:\x\project.uvprojx", FilePreviewKind.Text)]
    [InlineData(@"C:\x\Makefile", FilePreviewKind.Text)]
    [InlineData(@"C:\x\LICENSE", FilePreviewKind.Text)]
    [InlineData(@"C:\x\图.png", FilePreviewKind.Image)]
    [InlineData(@"C:\x\报告.pdf", FilePreviewKind.Pdf)]
    [InlineData(@"C:\x\备份.zip", FilePreviewKind.Archive)]
    [InlineData(@"C:\x\实验报告.docx", FilePreviewKind.Office)]
    [InlineData(@"C:\x\数据.xlsx", FilePreviewKind.Office)]
    [InlineData(@"C:\x\老报告.doc", FilePreviewKind.Unsupported)]
    [InlineData(@"C:\x\固件.bin", FilePreviewKind.Unsupported)]
    [InlineData(@"C:\x\头像.webp", FilePreviewKind.Unsupported)]
    [InlineData(@"C:\x\无扩展名", FilePreviewKind.Unsupported)]
    public void Classify_PicksTheRendererByExtension(string path, FilePreviewKind expected)
        => Assert.Equal(expected, FilePreviewRules.Classify(path));

    [Fact]
    public void UnsupportedHint_NamesTheFormatAndTheNextClick()
    {
        // 「不支持」三个字不算回答：得说清为什么，以及下一步点哪。
        var hint = FilePreviewRules.UnsupportedHint(@"C:\x\压缩包.rar");
        Assert.Contains("WinRAR", hint);
        Assert.Contains("双击", hint);

        Assert.Contains(".docx", FilePreviewRules.UnsupportedHint(@"C:\x\报告.doc"));
    }

    // ==================== 文本 ====================

    [Fact]
    public async Task TextPreview_ReadsUtf8AndReportsTheEncoding()
    {
        var path = Write("说明.txt", "// 中文注释\nint main(void) { return 0; }\n");

        var preview = await TextPreviewReader.ReadAsync(path);

        Assert.Equal("UTF-8", preview.EncodingName);
        Assert.Contains("中文注释", preview.Text);
        Assert.False(preview.Truncated);
    }

    [Fact]
    public async Task TextPreview_StripsTheBomItUsedForDetection()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("第一条\n")).ToArray();

        var preview = await TextPreviewReader.ReadAsync(Write("bom.txt", withBom));

        Assert.Equal("UTF-8", preview.EncodingName);

        // 只能这样断言：xunit 的字符串比较走不区分文化的排序规则，U+FEFF 这种零宽字符会被当成「哪儿都匹配」，
        // 所以 StartsWith / DoesNotContain 都测不出 BOM 到底去没去掉。
        Assert.Equal("第一条", preview.Text);
    }

    [Fact]
    public async Task TextPreview_FallsBackToGb18030ForLegacyChineseSources()
    {
        // Keil / 老 VC 工程里的中文注释绝大多数是 GBK/GB18030，按 UTF-8 解就是乱码。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding("GB18030").GetBytes("波特率 9600，中断优先级\n");

        var preview = await TextPreviewReader.ReadAsync(Write("uart.c", gbk));

        Assert.Equal("GB18030", preview.EncodingName);
        Assert.Contains("波特率 9600", preview.Text);
    }

    [Fact]
    public async Task TextPreview_CutsAtALineBoundaryAndSaysItTruncated()
    {
        var line = "0123456789";
        var many = string.Concat(Enumerable.Repeat(line + "\n", 100_000));
        var path = Write("big.txt", many);

        var preview = await TextPreviewReader.ReadAsync(path);

        Assert.True(preview.Truncated);
        Assert.True(preview.TotalBytes > FilePreviewRules.MaxTextBytes);
        Assert.True(preview.Text.Length < preview.TotalBytes);
        Assert.EndsWith(line, preview.Text);
        Assert.DoesNotContain("\n", preview.Text[^line.Length..]);
    }

    [Fact]
    public async Task TextPreview_DoesNotLockAFileSomebodyElseHasOpen()
    {
        var path = Write("busy.log", "正在被写的日志\n");

        // 模拟日志程序正开着这个文件写：预览必须照样读得出来，也不能反过来挡住对方。
        await using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        var preview = await TextPreviewReader.ReadAsync(path);
        Assert.Contains("正在被写", preview.Text);

        held.Seek(0, SeekOrigin.End);
        held.Write(Encoding.UTF8.GetBytes("第二行\n"));
        held.Flush();
        Assert.True(new FileInfo(path).Length > preview.TotalBytes);
    }

    // ==================== Markdown ====================

    [Fact]
    public void MarkdownBlocks_SplitsTheLineStartMarkersAndKeepsCodeVerbatim()
    {
        const string text = """
            # 第一章

            正文一段，带 **重点** 和 `code`。

            - 第一条
            1. 编号条

            > 引用一句

            ---

            ```c
            #notAHeading
            -notABullet
            ```
            """;

        var blocks = MarkdownBlocks.Split(text);

        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal("第一章", blocks[0].Text);

        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Paragraph && block.Text.StartsWith("正文一段"));
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Bullet && block.Text == "第一条");
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Numbered && block.Text == "编号条");
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Quote && block.Text == "引用一句");
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Divider);

        var code = blocks.Single(block => block.Kind == MarkdownBlockKind.Code);
        Assert.Contains("#notAHeading", code.Text);
        Assert.Contains("-notABullet", code.Text);
    }

    [Fact]
    public void MarkdownBlocks_InlineSplitsBoldAndCodeButLeavesStrayMarkersAlone()
    {
        var parts = MarkdownBlocks.Inline("这是 **粗** 和 `码`，还有半颗 * 号");

        Assert.Contains(parts, part => part.Style == MarkdownInlineStyle.Bold && part.Text == "粗");
        Assert.Contains(parts, part => part.Style == MarkdownInlineStyle.Code && part.Text == "码");
        Assert.Contains(parts, part => part.Style == MarkdownInlineStyle.Plain && part.Text.Contains("半颗"));
    }

    [Fact]
    public void MarkdownBlocks_HeadingNeedsASpaceSoHashtagsStayText()
        => Assert.Equal(MarkdownBlockKind.Paragraph, Assert.Single(MarkdownBlocks.Split("#hashtag 不是标题")).Kind);

    // ==================== 压缩包 ====================

    [Fact]
    public async Task ArchiveLister_ListsEntriesWithoutExtractingAnything()
    {
        // 源文件放在子目录里，这样「包外面有没有被落下 a.txt」才是个有意义的判断。
        var source = Path.Combine(_temp.Path, "原料");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "内容");

        var path = Path.Combine(_temp.Path, "包.zip");
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(source, "a.txt"), "a.txt");
            archive.CreateEntry("子目录/");
        }

        var entries = await ArchiveLister.ListAsync(path);

        Assert.Contains(entries, entry => entry.FullName == "a.txt" && !entry.IsDirectory && entry.SizeBytes == 6);
        Assert.Contains(entries, entry => entry.IsDirectory);

        // 只列不解：不许在包外面落下任何东西。
        Assert.False(File.Exists(Path.Combine(_temp.Path, "a.txt")));
        Assert.Equal(
            ["包.zip", "原料"],
            Directory.GetFileSystemEntries(_temp.Path).Select(name => Path.GetFileName(name)!).ToArray());
    }

    // ==================== OOXML ====================

    [Fact]
    public async Task OfficeReader_KeepsHeadingsBoldRunsAndTables()
    {
        var path = Path.Combine(_temp.Path, "报告.docx");
        WriteZip(path, ("word/document.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>实验目的</w:t></w:r></w:p>
                <w:p><w:r><w:t>测量</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>UART 波特率</w:t></w:r><w:r><w:tab/><w:t>9600</w:t></w:r></w:p>
                <w:p><w:r><w:t>   </w:t></w:r></w:p>
                <w:tbl>
                  <w:tr><w:tc><w:p><w:r><w:t>项目</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>分数</w:t></w:r></w:p></w:tc></w:tr>
                  <w:tr><w:tc><w:p><w:r><w:t>平时</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>50</w:t></w:r></w:p></w:tc></w:tr>
                </w:tbl>
              </w:body>
            </w:document>
            """));

        var blocks = await OfficeDocumentReader.ReadAsync(path);

        var heading = Assert.Single(blocks, block => block.Kind == MarkdownBlockKind.Heading);
        Assert.Equal(1, heading.Level);
        Assert.Equal("实验目的", heading.Text);

        // 加粗的 run 用 ** 包起来，交给和 Markdown 同一套行内渲染。
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Paragraph
            && block.Text == "测量**UART 波特率**\t9600");

        // 表格合成一个等宽块：一行一个灰条会碎成斑马纹，读不出「这是一张表」。
        var table = Assert.Single(blocks, block => block.Kind == MarkdownBlockKind.Code);
        Assert.Equal("项目 | 分数\n平时 | 50", table.Text);
    }

    [Fact]
    public async Task OfficeReader_TreatsBigBoldShortLinesAsHeadings()
    {
        var path = Path.Combine(_temp.Path, "简历.docx");
        WriteZip(path, ("word/document.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:rPr><w:b/><w:sz w:val="32"/></w:rPr><w:t>项目经验</w:t></w:r></w:p>
                <w:p><w:r><w:rPr><w:sz w:val="32"/></w:rPr><w:t>这是一大段正文，虽然字号也不小，但不该被当成标题处理。</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """));

        var blocks = await OfficeDocumentReader.ReadAsync(path);

        // 简历几乎不用「标题 1」样式，全靠手动加大字号 + 加粗：不兜这一层就整页都是正文。
        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("项目经验", blocks[0].Text);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[1].Kind);
    }

    [Fact]
    public async Task OfficeReader_DumpsSheetRowsWithTheirSharedStrings()
    {
        var path = Path.Combine(_temp.Path, "数据.xlsx");
        WriteZip(path,
            ("xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheets><sheet name="测温记录" sheetId="1" r:id="rId1"
                     xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" /></sheets>
                </workbook>
                """),
            ("xl/sharedStrings.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <si><t>通道</t></si><si><t>温度</t></si>
                </sst>
                """),
            ("xl/worksheets/sheet1.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
                    <row r="2"><c r="A2"><v>1</v></c><c r="B2"><v>36.5</v></c></row>
                  </sheetData>
                </worksheet>
                """));

        var blocks = await OfficeDocumentReader.ReadAsync(path);

        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal("测温记录", blocks[0].Text);

        var grid = Assert.Single(blocks, block => block.Kind == MarkdownBlockKind.Code);
        Assert.Contains("通道 | 温度", grid.Text);
        Assert.Contains("1 | 36.5", grid.Text);
    }

    [Fact]
    public async Task OfficeReader_ReadsSlideTextInPageOrder()
    {
        var path = Path.Combine(_temp.Path, "答辩.pptx");
        WriteZip(path,
            ("ppt/slides/slide2.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:sp><p:nvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                       <p:txBody><a:p><a:r><a:t>第二页结论</a:t></a:r></a:p></p:txBody></p:sp>
                </p:sld>
                """),
            ("ppt/slides/slide10.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:sp><p:txBody><a:p><a:r><a:t>第十页致谢</a:t></a:r></a:p></p:txBody></p:sp>
                </p:sld>
                """),
            ("ppt/slides/slide1.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:sp><p:txBody><a:p><a:r><a:t>第一页标题</a:t></a:r></a:p></p:txBody></p:sp>
                </p:sld>
                """));

        var blocks = await OfficeDocumentReader.ReadAsync(path);

        // 页号按数字排，不按字符串：否则 slide10 会插到 slide2 前面。
        Assert.Equal(["第 1 页", "第 2 页", "第 3 页"], blocks.Where(block => block.Kind == MarkdownBlockKind.Heading && block.Level == 1).Select(block => block.Text));

        var order = blocks.Select(block => block.Text).ToList();
        Assert.True(order.IndexOf("第一页标题") < order.IndexOf("第二页结论"));
        Assert.True(order.IndexOf("第二页结论") < order.IndexOf("第十页致谢"));

        // 标题占位符单独成标题，正文占位符不会。
        Assert.Contains(blocks, block => block.Kind == MarkdownBlockKind.Heading && block.Level == 2 && block.Text == "第二页结论");
    }

    [Fact]
    public async Task OfficeReader_GivesUpQuietlyOnAPackageWithNothingInside()
    {
        var path = Path.Combine(_temp.Path, "空壳.docx");
        WriteZip(path, ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types />"));

        Assert.Empty(await OfficeDocumentReader.ReadAsync(path));
    }

    private void WriteZip(string path, params (string Entry, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
