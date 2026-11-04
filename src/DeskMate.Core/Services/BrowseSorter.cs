namespace DeskMate.Core.Services;

/// <summary>浏览列表的排序列。</summary>
public enum BrowseSortKey
{
    /// <summary>名称（自然序，"第1章" 排在 "第10章" 前）。</summary>
    Name,

    /// <summary>类型（扩展名归类，同扩展名再按名称）。</summary>
    Type,

    /// <summary>大小（字节数；目录没有大小，按 0 算，只影响目录组内部的相对次序）。</summary>
    Size,

    /// <summary>修改时间。</summary>
    Date,
}

/// <summary>
/// 浏览列表的排序比较器。
/// <para>
/// 目录恒排在前 —— 资源管理器的第一直觉是「文件夹在上面」，任何列排序都不该把它打散；
/// 名称走自然序（数字段按数值比），"第1章" 才能排在 "第10章" 前面。
/// </para>
/// <para>
/// 为什么不用 <c>shell32!StrCmpLogicalW</c>：这台机器上按名字解析报 EntryPointNotFoundException，
/// 按序数（#186）调用直接把测试主机打成访问违例。自然序的规则本身很简单，自己实现比依赖一个未公开导出可靠。
/// </para>
/// </summary>
public static class BrowseSorter
{
    /// <summary>
    /// 一行的可排序素材。
    /// </summary>
    /// <param name="Name">条目名（不含路径）。</param>
    /// <param name="Extension">扩展名（含点，小写；目录给空串）。</param>
    /// <param name="IsDirectory">是不是目录。</param>
    /// <param name="SizeBytes">字节数；目录与取不到的给 0。</param>
    /// <param name="Modified">最后写入时间。</param>
    public readonly record struct Item(
        string Name,
        string Extension,
        bool IsDirectory,
        long SizeBytes,
        DateTime Modified);

    /// <summary>
    /// 把配置里的字符串解析成排序列；坏值一律回落 Name，旧配置或缺字段都不该炸面板。
    /// </summary>
    public static BrowseSortKey ParseKey(string? raw)
        => raw?.Trim().ToLowerInvariant() switch
        {
            "type" => BrowseSortKey.Type,
            "size" => BrowseSortKey.Size,
            "date" => BrowseSortKey.Date,
            _ => BrowseSortKey.Name,
        };

    /// <summary>按列与方向构造比较器；<paramref name="project"/> 从调用方的行对象里取排序素材。</summary>
    public static IComparer<T> ComparerFor<T>(BrowseSortKey key, bool descending, Func<T, Item> project)
        => new SortComparer<T>(key, descending, project);

    /// <summary>
    /// 自然序比较：数字段按数值比，其余文字按当前文化的排序规则比。
    /// 「1.png &lt; 2.png &lt; 10.png」靠数值段，「甲/乙」这类中文按拼音而不是按码位。
    /// 两边都没有数字时直接整串比 —— 那是绝大多数名字，逐段比会白切很多子串。
    /// </summary>
    public static int CompareNatural(string? left, string? right)
    {
        left ??= string.Empty;
        right ??= string.Empty;

        if (left.Length == 0 || right.Length == 0)
        {
            return left.Length.CompareTo(right.Length);
        }

        if (!left.Any(char.IsAsciiDigit) && !right.Any(char.IsAsciiDigit))
        {
            return string.Compare(left, right, StringComparison.CurrentCulture);
        }

        var i = 0;
        var j = 0;

        while (i < left.Length && j < right.Length)
        {
            var leftDigit = char.IsAsciiDigit(left[i]);
            var rightDigit = char.IsAsciiDigit(right[j]);

            if (leftDigit && rightDigit)
            {
                // 两段数字整段取出来比数值；前导零不计（"007" 与 "7" 视为同一数）。
                var leftRun = ReadDigitRun(left, ref i, out var leftValue);
                var rightRun = ReadDigitRun(right, ref j, out var rightValue);

                if (leftValue != rightValue)
                {
                    return leftValue.CompareTo(rightValue);
                }

                // 数值相同（如 "01" 与 "1"）时，位数多的排后面，保证有唯一次序。
                if (leftRun != rightRun)
                {
                    return leftRun.CompareTo(rightRun);
                }

                continue;
            }

            if (leftDigit != rightDigit)
            {
                // 一边是数字、一边是文字：数字段在前（"1a" 排在 "a1" 前）。
                return leftDigit ? -1 : 1;
            }

            // 两段连续文字，一次比完，别一个字符一个字符地来回调用文化比较。
            var leftText = ReadTextRun(left, ref i);
            var rightText = ReadTextRun(right, ref j);

            var comparison = string.Compare(leftText, rightText, StringComparison.CurrentCulture);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return (left.Length - i).CompareTo(right.Length - j);
    }

    /// <summary>读出一段连续的非数字文字。</summary>
    private static string ReadTextRun(string text, ref int index)
    {
        var start = index;
        while (index < text.Length && !char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        return text[start..index];
    }

    /// <summary>读出一段连续数字的值与长度（值溢出时截断在 long 上限，只影响超长数字串的相对次序）。</summary>
    private static int ReadDigitRun(string text, ref int index, out long value)
    {
        var start = index;
        value = 0;
        var overflow = false;

        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            if (value > (long.MaxValue - 9) / 10)
            {
                overflow = true;
            }
            else
            {
                value = (value * 10) + (text[index] - '0');
            }

            index++;
        }

        if (overflow)
        {
            value = long.MaxValue;
        }

        return index - start;
    }

    private sealed class SortComparer<T> : IComparer<T>
    {
        private readonly BrowseSortKey _key;
        private readonly int _direction;
        private readonly Func<T, Item> _project;

        public SortComparer(BrowseSortKey key, bool descending, Func<T, Item> project)
        {
            _key = key;
            _direction = descending ? -1 : 1;
            _project = project;
        }

        public int Compare(T? source, T? target)
        {
            // List.Sort 不会喂 null，防御性当成相等处理，别在这里抛。
            if (source is null || target is null)
            {
                return 0;
            }

            var left = _project(source);
            var right = _project(target);

            // 目录优先不参与翻转：升降序只翻转「组内」。
            if (left.IsDirectory != right.IsDirectory)
            {
                return left.IsDirectory ? -1 : 1;
            }

            var primary = _key switch
            {
                BrowseSortKey.Name => CompareNatural(left.Name, right.Name),
                BrowseSortKey.Type => CompareTypes(left, right),
                BrowseSortKey.Size => left.SizeBytes.CompareTo(right.SizeBytes),
                BrowseSortKey.Date => left.Modified.CompareTo(right.Modified),
                _ => 0,
            };

            if (primary != 0)
            {
                return _direction * primary;
            }

            // 任何列上平手都退到名称自然序（恒升序，与资源管理器一致），保证整表稳定且可预期。
            var tie = CompareNatural(left.Name, right.Name);

            // 同名（不同扩展）再按扩展分，「a.txt / a.md」这类平手有唯一去处。
            return tie == 0 ? string.CompareOrdinal(left.Extension, right.Extension) : tie;
        }
    }

    private static int CompareTypes(Item left, Item right)
    {
        // 目录在「类型」列叫「文件夹」，与文件的扩展名分组天然不同类。
        var leftType = left.IsDirectory ? "文件夹" : left.Extension.TrimStart('.');
        var rightType = right.IsDirectory ? "文件夹" : right.Extension.TrimStart('.');

        return string.Compare(leftType, rightType, StringComparison.OrdinalIgnoreCase);
    }
}
