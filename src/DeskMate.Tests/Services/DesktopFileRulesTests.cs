using DeskMate.Core.Models;
using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 桌面文件归类规则：一个扩展名只归一个类、复合扩展取最后一段、隐藏文件与系统桌面文件不进分类。
/// 这些判定都只依赖文件名，所以能完全脱离真实桌面来测。
/// </summary>
public class DesktopFileRulesTests
{
    [Theory]
    [InlineData("report.pdf", DesktopFileCategory.Document)]
    [InlineData("notes.txt", DesktopFileCategory.Document)]
    [InlineData("photo.JPG", DesktopFileCategory.Image)]
    [InlineData("icon.ico", DesktopFileCategory.Image)]
    [InlineData("bundle.zip", DesktopFileCategory.Archive)]
    [InlineData("game.iso", DesktopFileCategory.Archive)]
    [InlineData("setup.exe", DesktopFileCategory.Other)]
    [InlineData("README", DesktopFileCategory.Other)]
    [InlineData("archive.tar.gz", DesktopFileCategory.Archive)]
    [InlineData("logo.svg", DesktopFileCategory.Image)]
    public void Categorize_ByLastExtensionSegment(string fileName, DesktopFileCategory expected)
        => Assert.Equal(expected, DesktopFileRules.Categorize(DesktopFileRules.ExtensionOf(fileName)));

    [Fact]
    public void TabOrderIsAlsoTheConflictPriority()
    {
        // 顶部标签的顺序就是归类时的优先级，同一份规则两用；
        // 分成两份维护的话，改着改着就会出现「优先归图片却排在压缩包后面」。
        Assert.Equal(
            new[] { DesktopFileCategory.Image, DesktopFileCategory.Document, DesktopFileCategory.Archive, DesktopFileCategory.Other },
            DesktopFileRules.OrderedCategories);
        Assert.Equal(DesktopFileCategory.Image, DesktopFileRules.Categorize("svg"));
    }

    [Theory]
    [InlineData("a.png", "png")]
    [InlineData("a.PNG", "png")]
    [InlineData("a.tar.gz", "gz")]
    [InlineData("无扩展名", "")]
    [InlineData("结尾有个点.", "")]
    [InlineData(".隐藏", "隐藏")]
    public void ExtensionOf_IsLowercaseAndLastSegmentOnly(string fileName, string expected)
        => Assert.Equal(expected, DesktopFileRules.ExtensionOf(fileName));

    [Theory]
    [InlineData("desktop.ini")]
    [InlineData("Thumbs.db")]
    [InlineData(".DS_Store")]
    public void ShouldIgnore_WindowsAndDotFiles(string fileName)
        => Assert.True(DesktopFileRules.ShouldIgnore(fileName, FileAttributes.Normal));

    [Fact]
    public void ShouldIgnore_HiddenAndSystemFiles()
    {
        Assert.True(DesktopFileRules.ShouldIgnore("机密资料.pdf", FileAttributes.Hidden));
        Assert.True(DesktopFileRules.ShouldIgnore("引导文件.sys", FileAttributes.System));
        Assert.False(DesktopFileRules.ShouldIgnore("课程表.xlsx", FileAttributes.Normal));
    }

    [Fact]
    public void TabIdRoundTripsBackToTheCategory()
    {
        foreach (var category in DesktopFileRules.OrderedCategories)
        {
            var tabId = DesktopFileRules.TabId(category);
            Assert.True(DesktopFileRules.IsCategoryTab(tabId));
            Assert.Equal(category, DesktopFileRules.CategoryOfTab(tabId));
        }

        Assert.False(DesktopFileRules.IsCategoryTab("all"));
        Assert.Null(DesktopFileRules.CategoryOfTab("all"));
        Assert.Null(DesktopFileRules.CategoryOfTab(null));
    }

    [Fact]
    public void OtherAlwaysSitsLastSoTheFallbackTabIsPredictable()
        => Assert.Equal(DesktopFileCategory.Other, DesktopFileRules.OrderedCategories[^1]);
}
