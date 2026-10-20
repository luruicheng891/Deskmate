using System.IO.Compression;
using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 「新建」的名字口径：不带后缀 = 文件夹，带后缀 = 文件。
/// 判错方向等于擅自改了用户要的东西，所以每个边界都钉住。
/// </summary>
public class NewEntryRulesTests
{
    [Theory]
    [InlineData("第三章", NewEntryKind.Folder)]
    [InlineData("notes", NewEntryKind.Folder)]
    [InlineData("main.c", NewEntryKind.File)]
    [InlineData("报告.docx", NewEntryKind.File)]
    [InlineData("archive.tar.gz", NewEntryKind.File)]
    [InlineData(".gitignore", NewEntryKind.File)]
    [InlineData("报告.", NewEntryKind.Folder)]
    [InlineData("", NewEntryKind.Folder)]
    public void Classify_FollowsTheExtension(string name, NewEntryKind expected)
        => Assert.Equal(expected, NewEntryRules.Classify(name));

    [Theory]
    [InlineData("第三章")]
    [InlineData("main.c")]
    [InlineData("a1_备份 (2)")]
    [InlineData("#pragma.txt")]
    public void Validate_AcceptsOrdinaryNames(string name)
        => Assert.Null(NewEntryRules.Validate(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:报告")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("报告.")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT1.log")]
    public void Validate_Rejects_AndGivesAReason(string? name)
        => Assert.False(string.IsNullOrWhiteSpace(NewEntryRules.Validate(name)));

    [Fact]
    public void Validate_ToleratesSurroundingSpaces()
    {
        // 输入框里多敲了一个空格是常事，这种不该被当成非法名字。
        Assert.Null(NewEntryRules.Validate("  第三章 "));
    }

    [Fact]
    public void Validate_RejectsOverLongName()
        => Assert.NotNull(NewEntryRules.Validate(new string('字', NewEntryRules.MaxNameLength + 1)));

    [Fact]
    public void Catalog_CoversTheSixTypesPlusFreeForm()
        => Assert.Equal(
            ["folder", "docx", "xlsx", "pptx", "md", "txt", "custom"],
            NewEntryCatalog.Options.Select(option => option.Key));

    [Fact]
    public void ResolveName_AppendsTheChosenSuffix_OnlyWhenItIsMissing()
    {
        var word = NewEntryCatalog.Options.First(option => option.Key == "docx");
        var folder = NewEntryCatalog.Options.First(option => option.Key == "folder");

        Assert.Equal("报告.docx", NewEntryCatalog.ResolveName("报告", word));

        // 名字里已经写了后缀就别再补一遍，"报告.docx.docx" 是最蠢的结果。
        Assert.Equal("报告.docx", NewEntryCatalog.ResolveName(" 报告.docx ", word));
        Assert.Equal("报告", NewEntryCatalog.ResolveName("报告", folder));

        // 文件夹模式下名字里那点不算后缀：建出来的就是叫 "v1.2" 的目录。
        Assert.Equal("v1.2", NewEntryCatalog.ResolveName("v1.2", folder));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".pptx")]
    public void OoxmlTemplates_AreRealZipPackages(string extension)
    {
        var bytes = OoxmlTemplates.TryBuild(extension);

        Assert.NotNull(bytes);
        Assert.True(bytes!.Length > 200, $"{extension} 只有 {bytes.Length} 字节，不可能是合法包");
        Assert.Equal('P', (char)bytes[0]);
        Assert.Equal('K', (char)bytes[1]);

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "[Content_Types].xml");
        Assert.Contains(archive.Entries, entry => entry.FullName == "_rels/.rels");
    }

    [Theory]
    [InlineData(".md")]
    [InlineData(".txt")]
    [InlineData(".c")]
    public void OoxmlTemplates_DeclineNonOfficeTypes(string extension)
        => Assert.Null(OoxmlTemplates.TryBuild(extension));
}
