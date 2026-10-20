using DeskMate.Core.Services;
using Xunit;

namespace DeskMate.Tests.Services;

/// <summary>
/// 分组名校验规则：空、超长、重名（去首尾空格 + 不区分大小写）。
/// 这几条规则同时驱动浮层的实时提示与「确认」按钮的可用性，所以单独钉住。
/// </summary>
public class GroupNameRulesTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("  课程资料  ", "课程资料")]
    [InlineData("a\tb", "a\tb")]
    public void Normalize_TrimsBothEnds(string? input, string expected)
        => Assert.Equal(expected, GroupNameRules.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsEmpty(string input)
        => Assert.Equal("请输入分组名称", GroupNameRules.Validate(GroupNameRules.Normalize(input), []));

    [Fact]
    public void Validate_RejectsTooLong()
    {
        var error = GroupNameRules.Validate(new string('长', GroupNameRules.MaxLength + 1), []);

        Assert.Contains("20", error);
    }

    [Fact]
    public void Validate_AllowsExactlyTheLimit()
        => Assert.Null(GroupNameRules.Validate(new string('长', GroupNameRules.MaxLength), []));

    [Theory]
    [InlineData("课程资料", new[] { "课程资料" })]
    [InlineData("课程资料", new[] { " 课程资料 " })]
    [InlineData("Course", new[] { "course" })]
    [InlineData("COURSE", new[] { "course" })]
    public void Validate_RejectsDuplicatesIgnoringCaseAndSpaces(string name, string[] taken)
        => Assert.Equal("该分组名已存在，请换一个", GroupNameRules.Validate(name, taken));

    [Fact]
    public void Validate_AcceptsAFreshName()
        => Assert.Null(GroupNameRules.Validate("毕业设计", ["课程资料", "course"]));
}
