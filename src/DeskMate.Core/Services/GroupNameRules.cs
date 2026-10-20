namespace DeskMate.Core.Services;

/// <summary>
/// 分组名校验规则。单独成一个类，是因为同一套规则要在两个地方生效：
/// 输入时实时提示，以及提交前再判一次 —— 只靠按钮置灰挡不住回车和脚本调用。
/// </summary>
public static class GroupNameRules
{
    /// <summary>分组名长度上限。</summary>
    public const int MaxLength = 20;

    /// <summary>去掉首尾空格后的最终名字。</summary>
    public static string Normalize(string? input) => (input ?? string.Empty).Trim();

    /// <summary>
    /// 校验一个已经 <see cref="Normalize"/> 过的名字。
    /// </summary>
    /// <param name="name">去过首尾空格的名字。</param>
    /// <param name="takenNames">已占用的名字（含内置视图名）。比对不区分大小写。</param>
    /// <returns>返回 <c>null</c> 表示通过；否则是给用户看的提示文案。</returns>
    public static string? Validate(string name, IEnumerable<string> takenNames)
    {
        if (name.Length == 0)
        {
            return "请输入分组名称";
        }

        if (name.Length > MaxLength)
        {
            return $"分组名不能超过 {MaxLength} 个字符（当前 {name.Length} 个）";
        }

        foreach (var taken in takenNames)
        {
            if (string.Equals(taken.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return "该分组名已存在，请换一个";
            }
        }

        return null;
    }
}
