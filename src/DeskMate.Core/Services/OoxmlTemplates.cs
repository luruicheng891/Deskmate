using System.IO.Compression;
using System.Text;

namespace DeskMate.Core.Services;

/// <summary>
/// 最小的合法 Office 文件。
/// <para>
/// 不能图省事写 0 字节的 <c>.docx</c>：Office 会报「文件已损坏，无法打开」，
/// 用户刚点完新建就撞上一个错误框，比没有这个入口更糟。docx/xlsx/pptx 都是 zip 包，
/// 这里按 OOXML 规范放齐必需的那几个部件，让 Word / Excel / PowerPoint 认得出来。
/// </para>
/// </summary>
public static class OoxmlTemplates
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>按扩展名（含点，如 <c>.docx</c>）取模板字节；不是 Office 类型时返回 null。</summary>
    public static byte[]? TryBuild(string extension) => extension.ToLowerInvariant() switch
    {
        ".docx" => Word(),
        ".xlsx" => Excel(),
        ".pptx" => PowerPoint(),
        _ => null,
    };

    private static byte[] Word() => Pack(
        ("[Content_Types].xml", $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <Types xmlns="{ContentTypesNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
         """),
        ("_rels/.rels", PackageRelationships("word/document.xml")),
        ("word/document.xml", """
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr></w:body></w:document>
         """));

    private static byte[] Excel() => Pack(
        ("[Content_Types].xml", $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <Types xmlns="{ContentTypesNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
         """),
        ("_rels/.rels", PackageRelationships("xl/workbook.xml")),
        ("xl/workbook.xml", """
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>
         """),
        ("xl/_rels/workbook.xml.rels", $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="{OfficeRelNs}/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{OfficeRelNs}/styles" Target="styles.xml"/></Relationships>
         """),
        ("xl/worksheets/sheet1.xml", """
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><dimension ref="A1"/><sheetData/></worksheet>
         """),
        ("xl/styles.xml", """
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs></styleSheet>
         """));

    private static byte[] PowerPoint() => Pack(
        ("[Content_Types].xml", $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <Types xmlns="{ContentTypesNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/><Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/><Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/></Types>
         """),
        ("_rels/.rels", PackageRelationships("ppt/presentation.xml")),
        ("ppt/presentation.xml", """
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <p:presentation xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldSz cx="12192000" cy="6858000"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>
         """),
        ("ppt/_rels/presentation.xml.rels", $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="{OfficeRelNs}/slideMaster" Target="slideMasters/slideMaster1.xml"/></Relationships>
         """),
        ("ppt/slideMasters/slideMaster1.xml", MasterOrLayoutXml("""<p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst>""")),
        ("ppt/slideMasters/_rels/slideMaster1.xml.rels", $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="{OfficeRelNs}/slideLayout" Target="../slideLayouts/slideLayout1.xml"/><Relationship Id="rId2" Type="{OfficeRelNs}/theme" Target="../theme/theme1.xml"/></Relationships>
         """),
        ("ppt/slideLayouts/slideLayout1.xml", MasterOrLayoutXml(string.Empty)),
        ("ppt/slideLayouts/_rels/slideLayout1.xml.rels", $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="{OfficeRelNs}/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>
         """),
        ("ppt/theme/theme1.xml", ThemeXml()));

    /// <summary>母版与版式共用一套骨架：一个空的形状树，母版再多一段版式 Id 列表。</summary>
    private static string MasterOrLayoutXml(string extra) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <p:sldMaster xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>{extra}</p:sldMaster>
        """;

    /// <summary>PowerPoint 要求主题里有完整的 colorMap / fontScheme / fmtScheme（各三档样式）。</summary>
    private static string ThemeXml() => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office"><a:themeElements><a:clrScheme name="Office"><a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1><a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="44546A"/></a:dk2><a:lt2><a:srgbClr val="E7E6E6"/></a:lt2><a:accent1><a:srgbClr val="4472C4"/></a:accent1><a:accent2><a:srgbClr val="ED7D31"/></a:accent2><a:accent3><a:srgbClr val="A5A5A5"/></a:accent3><a:accent4><a:srgbClr val="FFC000"/></a:accent4><a:accent5><a:srgbClr val="5B9BD5"/></a:accent5><a:accent6><a:srgbClr val="70AD47"/></a:accent6><a:hlink><a:srgbClr val="0563C1"/></a:hlink><a:folHlink><a:srgbClr val="954F72"/></a:folHlink></a:clrScheme><a:fontScheme name="Office"><a:majorFont><a:latin typeface="Calibri Light"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Calibri"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Office"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w="9525"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="28575"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>
        """;

    private static string PackageRelationships(string target) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="{OfficeRelNs}/officeDocument" Target="{target}"/></Relationships>
        """;

    private static byte[] Pack(params (string Path, string Xml)[] parts)
    {
        using var buffer = new MemoryStream();

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, xml) in parts)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = Utf8NoBom.GetBytes(xml);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        return buffer.ToArray();
    }
}
