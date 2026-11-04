using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Logic;

/// <summary>
/// 浏览列表排序的纯逻辑：目录优先、按列、自然序。
/// 这些规则一旦靠现场截图去验就说不清是哪一条坏了，所以拿假数据钉死。
/// </summary>
public class BrowseSorterTests
{
    private static readonly DateTime Old = new(2020, 1, 1);
    private static readonly DateTime New = new(2026, 1, 1);

    private static List<BrowseSorter.Item> Sort(BrowseSortKey key, bool descending, params BrowseSorter.Item[] items)
    {
        var list = items.ToList();
        list.Sort(BrowseSorter.ComparerFor<BrowseSorter.Item>(key, descending, item => item));
        return list;
    }

    private static BrowseSorter.Item Dir(string name, DateTime? modified = null)
        => new(name, string.Empty, true, 0, modified ?? Old);

    private static BrowseSorter.Item File(string name, long size = 1, DateTime? modified = null)
        => new(name, System.IO.Path.GetExtension(name), false, size, modified ?? Old);

    [Fact]
    public void DirectoriesStayFirstWhicheverColumnAndDirection()
    {
        foreach (var key in new[] { BrowseSortKey.Name, BrowseSortKey.Type, BrowseSortKey.Size, BrowseSortKey.Date })
        {
            foreach (var descending in new[] { false, true })
            {
                var sorted = Sort(key, descending, File("a.txt", 999, New), Dir("z盘"), File("b.md"));
                Assert.True(sorted[0].IsDirectory, $"{key}/{descending} 把目录排到了文件后面");
            }
        }
    }

    [Fact]
    public void NameSort_IsNaturalNotCodePoint()
    {
        var sorted = Sort(BrowseSortKey.Name, false, File("第10章.md"), File("第2章.md"), File("第1章.md"));

        Assert.Equal(["第1章.md", "第2章.md", "第10章.md"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void NameSort_DigitRunBeforeLetter()
    {
        var sorted = Sort(BrowseSortKey.Name, false, File("a1.txt"), File("1a.txt"));

        Assert.Equal("1a.txt", sorted[0].Name);
    }

    [Fact]
    public void NameSort_CaseInsensitive()
        => Assert.Equal(["Apple.md", "banana.md"], Sort(BrowseSortKey.Name, false, File("banana.md"), File("Apple.md")).Select(item => item.Name));

    [Fact]
    public void Descending_ReversesWithinTheDirectoryGroupOnly()
    {
        var sorted = Sort(BrowseSortKey.Size, true, Dir("乙"), Dir("甲"), File("小.txt", 10), File("大.txt", 1000));

        Assert.Equal(["甲", "乙", "大.txt", "小.txt"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void DateSort_PutsTheMostRecentlyWrittenFirstWhenDescending()
    {
        var sorted = Sort(BrowseSortKey.Date, true, File("旧.md", 1, Old), File("新.md", 1, New));

        Assert.Equal("新.md", sorted[0].Name);
    }

    [Fact]
    public void TypeSort_GroupsTheSameExtensionTogether()
    {
        var sorted = Sort(BrowseSortKey.Type, false, File("b.md"), File("a.txt"), File("c.md"));

        Assert.Equal(["b.md", "c.md", "a.txt"], sorted.Select(item => item.Name));
    }

    [Fact]
    public void EqualNamesOnTheChosenColumn_FallBackToExtensionSoTheOrderIsStable()
    {
        // 两个同名不同扩展的文件在「名称」列上平手，退到扩展名分先后 —— 反复排必须得到同一个次序。
        var items = new[] { File("a.txt"), File("a.md") };

        var first = Sort(BrowseSortKey.Name, false, items).Select(item => item.Name).ToArray();
        var second = Sort(BrowseSortKey.Name, false, items.Reverse().ToArray()).Select(item => item.Name).ToArray();

        Assert.Equal(["a.md", "a.txt"], first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(null, BrowseSortKey.Name)]
    [InlineData("", BrowseSortKey.Name)]
    [InlineData("乱写的", BrowseSortKey.Name)]
    [InlineData(" size ", BrowseSortKey.Size)]
    [InlineData("DATE", BrowseSortKey.Date)]
    [InlineData("type", BrowseSortKey.Type)]
    public void ParseKey_FallsBackToNameOnAnythingUnexpected(string? raw, BrowseSortKey expected)
        => Assert.Equal(expected, BrowseSorter.ParseKey(raw));
}
