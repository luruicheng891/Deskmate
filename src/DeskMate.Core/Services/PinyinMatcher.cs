using System.Collections.Concurrent;

namespace DeskMate.Core.Services;

/// <summary>
/// 中文 / 英文 / 拼音首字母混合匹配。
/// 排序档位严格对应需求 M4：完全匹配 &gt; 前缀匹配 &gt; 包含 &gt; 拼音。
/// </summary>
public static class PinyinMatcher
{
    /// <summary>无匹配。</summary>
    public const int NoMatch = 0;

    /// <summary>标题与关键词完全相同。</summary>
    public const int ScoreExact = 1000;

    /// <summary>标题以关键词开头。</summary>
    public const int ScorePrefix = 700;

    /// <summary>标题包含关键词。</summary>
    public const int ScoreContains = 400;

    /// <summary>拼音或首字母以关键词开头，如 jsq / jisuanqi 命中「计算器」。</summary>
    public const int ScorePinyinPrefix = 350;

    /// <summary>拼音或首字母包含关键词。</summary>
    public const int ScorePinyinContains = 250;

    /// <summary>只有路径命中，优先级最低。</summary>
    public const int ScorePathContains = 150;

    private const char CjkStart = (char)0x4E00;
    private const char CjkEnd = (char)0x9FFF;

    private static readonly ConcurrentDictionary<char, (string Full, string Initial)> CharCache = new();

    /// <summary>
    /// 给单条结果打分。传入 <paramref name="path"/> 时，路径命中会拿到 <see cref="ScorePathContains"/>。
    /// </summary>
    /// <param name="title">显示名。</param>
    /// <param name="keyword">用户输入。</param>
    /// <param name="path">可选的完整路径，用于兜底匹配。</param>
    /// <returns>匹配分，0 表示不匹配。</returns>
    public static int Score(string? title, string? keyword, string? path = null)
    {
        var key = Trim(keyword);
        if (key.Length == 0)
        {
            return NoMatch;
        }

        var best = ScoreText(title, key);

        if (best == NoMatch && !string.IsNullOrWhiteSpace(path))
        {
            // 路径命中只看目录名与文件名，避免把整条 C:\Users\... 都当成可匹配串。
            var fileName = Path.GetFileNameWithoutExtension(path);
            var directoryName = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty).Name;
            best = Math.Max(ScoreText(fileName, key), ScoreText(directoryName, key));
            if (best == NoMatch && Strip(path).Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                best = ScorePathContains;
            }
        }

        return best;
    }

    /// <summary>是否命中。</summary>
    public static bool Matches(string? title, string? keyword) => Score(title, keyword) > NoMatch;

    /// <summary>
    /// 生成一条文本的全拼串与首字母串，例如「计算器J」→ ("jisuanqij", "jsqj")。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>全拼与首字母，均已转小写。</returns>
    public static (string Full, string Initials) GetPinyinKeys(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, string.Empty);
        }

        var full = new System.Text.StringBuilder(text.Length * 4);
        var initials = new System.Text.StringBuilder(text.Length);

        foreach (var c in text)
        {
            if (IsCjk(c))
            {
                var pinyin = ReadCjk(c);
                full.Append(pinyin.Full);
                if (pinyin.Full.Length > 0)
                {
                    initials.Append(pinyin.Full[0]);
                }
            }
            else if (char.IsLetterOrDigit(c))
            {
                var lower = char.ToLowerInvariant(c).ToString();
                full.Append(lower);
                initials.Append(lower);
            }
        }

        return (full.ToString(), initials.ToString());
    }

    private static int ScoreText(string? text, string key)
    {
        var value = Trim(text);
        if (value.Length == 0)
        {
            return NoMatch;
        }

        if (string.Equals(value, key, StringComparison.OrdinalIgnoreCase))
        {
            return ScoreExact;
        }

        if (value.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            return ScorePrefix;
        }

        if (value.Contains(key, StringComparison.OrdinalIgnoreCase))
        {
            return ScoreContains;
        }

        var (full, initials) = GetPinyinKeys(value);
        if (initials.Length > 0 && (initials.StartsWith(key) || full.StartsWith(key)))
        {
            return ScorePinyinPrefix;
        }

        if (initials.Length > 0 && (initials.Contains(key) || full.Contains(key)))
        {
            return ScorePinyinContains;
        }

        return NoMatch;
    }

    // 中文标点与全角空格不参与匹配，去掉首尾空白即可。
    private static string Trim(string? text) => text?.Trim() ?? string.Empty;

    private static string Strip(string text) => text.Trim();

    private static bool IsCjk(char c) => c >= CjkStart && c <= CjkEnd;

    private static (string Full, string Initial) ReadCjk(char c)
        => CharCache.GetOrAdd(c, static ch =>
        {
            // 多音字取第一个读音，与输入法默认行为一致。
            // 非汉字调用 ChineseChar 构造函数会抛 ArgumentException，因此先用码位区间挡住。
            try
            {
                var readings = new Microsoft.International.Converters.PinYinConverter.ChineseChar(ch).Pinyins;
                foreach (var reading in readings)
                {
                    if (string.IsNullOrEmpty(reading))
                    {
                        continue;
                    }

                    var clean = TrimTone(reading.ToLowerInvariant());
                    return clean.Length > 0 ? (clean, clean[..1]) : (string.Empty, string.Empty);
                }
            }
            catch (ArgumentException)
            {
                // 落在 CJK 码位区间内但字典没有收录，按无拼音处理。
            }

            return (string.Empty, string.Empty);
        });

    // "JI4" → "ji"：声调是末尾的 1~5，个别条目末尾无数字。
    private static string TrimTone(string pinyin)
    {
        var end = pinyin.Length;
        while (end > 0 && char.IsAsciiDigit(pinyin[end - 1]))
        {
            end--;
        }

        return pinyin[..end];
    }
}
