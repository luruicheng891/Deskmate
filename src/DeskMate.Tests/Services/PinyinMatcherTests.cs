using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Services;

public class PinyinMatcherTests
{
    [Theory]
    [InlineData("计算器", "计算器", PinyinMatcher.ScoreExact)]
    [InlineData("记事本", "记事", PinyinMatcher.ScorePrefix)]
    [InlineData("Visual Studio", "studio", PinyinMatcher.ScoreContains)]
    [InlineData("计算器", "jsq", PinyinMatcher.ScorePinyinPrefix)]
    [InlineData("计算器", "jisuan", PinyinMatcher.ScorePinyinPrefix)]
    [InlineData("Windows Terminal", "windows", PinyinMatcher.ScorePrefix)]
    [InlineData("Windows Terminal", "term", PinyinMatcher.ScoreContains)]
    [InlineData("Windows Terminal", "tml", PinyinMatcher.NoMatch)]
    public void Score_ReturnsExpectedTier(string title, string keyword, int expected)
        => Assert.Equal(expected, PinyinMatcher.Score(title, keyword));

    [Fact]
    public void Score_TiersFollowSpecOrder()
    {
        Assert.True(PinyinMatcher.ScoreExact > PinyinMatcher.ScorePrefix);
        Assert.True(PinyinMatcher.ScorePrefix > PinyinMatcher.ScoreContains);
        Assert.True(PinyinMatcher.ScoreContains > PinyinMatcher.ScorePinyinPrefix);
        Assert.True(PinyinMatcher.ScorePinyinPrefix > PinyinMatcher.ScorePinyinContains);
    }

    [Theory]
    [InlineData("计算器", "jsq")]
    [InlineData("计算器", "jisuanqi")]
    [InlineData("通讯工具", "tx")]
    public void Matches_ChineseByPinyin(string title, string keyword)
        => Assert.True(PinyinMatcher.Matches(title, keyword));

    [Theory]
    [InlineData("计算器", "jsq", "jisuanqi")]
    [InlineData("微信", "wx", "weixin")]
    [InlineData("Edge", "edge", "edge")]
    public void GetPinyinKeys_BuildsBothForms(string text, string expectedInitials, string expectedFull)
    {
        var (full, initials) = PinyinMatcher.GetPinyinKeys(text);

        Assert.Equal(expectedInitials, initials);
        Assert.Equal(expectedFull, full);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Score_NoMatch_ForEmptyKeyword(string? keyword)
        => Assert.Equal(PinyinMatcher.NoMatch, PinyinMatcher.Score("计算器", keyword));

    [Fact]
    public void Score_FallsBackToPathName_WhenTitleMisses()
    {
        var score = PinyinMatcher.Score("未命名", "notepad", @"C:\Tools\notepad.exe");

        Assert.True(score > PinyinMatcher.NoMatch);
    }

    [Fact]
    public void Score_MixedNameWithSymbols_DoesNotThrow()
    {
        // 落在 CJK 区间但字典未收录的字，以及 emoji / 全角符号，都必须安全通过。
        Assert.True(PinyinMatcher.Score("测𠀀试😀", "ce") >= PinyinMatcher.NoMatch);
    }
}
