using DeskMate.Core.Models;

namespace DeskMate.Core.Services;

/// <summary>
/// 极简 Markdown 分块：只认「行首能看出来」的那几种块（标题、列表、引用、代码围栏、分隔线），
/// 段落原样保留。刻意不做完整 CommonMark —— 目标是让一份课程笔记点开就能读，
/// 而不是实现一个规范（真要完整渲染就得引 Markdig，那是新依赖）。
/// </summary>
public static class MarkdownBlocks
{
    /// <summary>把整篇文本切成块；超过 <see cref="FilePreviewRules.MaxMarkdownBlocks"/> 块就停，并补一条说明块。</summary>
    public static IReadOnlyList<MarkdownBlock> Split(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var inCode = false;
        var codeFence = string.Empty;
        var code = new List<string>();

        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.TrimEnd('\r').ToString();
            var trimmed = line.TrimStart();

            // 代码围栏内部原样收集：里面的 # 与 - 不是标记。
            if (inCode)
            {
                if (trimmed.StartsWith(codeFence, StringComparison.Ordinal))
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, 0, string.Join('\n', code)));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    code.Add(line);
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCode = true;
                codeFence = trimmed[..3];
                continue;
            }

            if (blocks.Count >= FilePreviewRules.MaxMarkdownBlocks)
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, 0, $"（Markdown 太长，只渲染前 {FilePreviewRules.MaxMarkdownBlocks} 块）"));
                break;
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (IsDivider(trimmed))
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Divider, 0, string.Empty));
                continue;
            }

            var heading = HeadingLevel(trimmed);
            if (heading > 0)
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, heading, trimmed.TrimStart('#').Trim()));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, 0, trimmed.TrimStart('>').Trim()));
                continue;
            }

            if (trimmed.Length > 1 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
            {
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, 0, trimmed[2..].Trim()));
                continue;
            }

            var numbered = OrderedItemText(trimmed);
            if (numbered is not null)
            {
                // 序号本身要留着（Level 里）：列表被打乱顺序时，「第 3 条」这种指代就废了。
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Numbered, numbered.Value.Number, numbered.Value.Text));
                continue;
            }

            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, 0, line.Trim()));
        }

        if (inCode)
        {
            // 文件结尾正好没收尾的围栏：照样画出来，别把内容吞掉。
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, 0, string.Join('\n', code)));
        }

        return blocks;
    }

    /// <summary>
    /// 行内强调（<c>**粗**</c>、<c>`码`</c>）拆成片段交给界面去上色。
    /// 只处理这一对最常见的，其它行内语法（链接、图片、HTML）原样显示。
    /// </summary>
    public static IReadOnlyList<(string Text, MarkdownInlineStyle Style)> Inline(string text)
    {
        var parts = new List<(string, MarkdownInlineStyle)>();
        var index = 0;

        while (index < text.Length)
        {
            var bold = text.IndexOf("**", index, StringComparison.Ordinal);
            var code = text.IndexOf('`', index);
            var next = (bold, code) switch
            {
                (-1, -1) => -1,
                (-1, var c) => c,
                (var b, -1) => b,
                (var b, var c) => Math.Min(b, c),
            };

            if (next < 0)
            {
                parts.Add((text[index..], MarkdownInlineStyle.Plain));
                break;
            }

            if (next > index)
            {
                parts.Add((text[index..next], MarkdownInlineStyle.Plain));
            }

            var isBold = next == bold;
            var closer = isBold ? "**" : "`";
            var end = text.IndexOf(closer, next + closer.Length, StringComparison.Ordinal);

            if (end < 0)
            {
                // 只有开没有闭：当成普通文本，不然半颗星会一路加粗到段尾。
                parts.Add((text[next..], MarkdownInlineStyle.Plain));
                break;
            }

            parts.Add((text[(next + closer.Length)..end], isBold ? MarkdownInlineStyle.Bold : MarkdownInlineStyle.Code));
            index = end + closer.Length;
        }

        return parts;
    }

    private static bool IsDivider(string trimmed)
        => trimmed.Length >= 3 && (trimmed.All(character => character == '-' || character == ' ' || character == '*')
            || trimmed.All(character => character == '_' || character == ' '));

    private static int HeadingLevel(string trimmed)
    {
        if (trimmed[0] != '#')
        {
            return 0;
        }

        var level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
        {
            level++;
        }

        // 「#hashtag」不是标题：级别后面必须跟空格。
        return level is >= 1 and <= 6 && level < trimmed.Length && trimmed[level] == ' ' ? level : 0;
    }

    private static (int Number, string Text)? OrderedItemText(string trimmed)
    {
        var index = 0;
        while (index < trimmed.Length && char.IsDigit(trimmed[index]))
        {
            index++;
        }

        if (index == 0 || index + 1 >= trimmed.Length)
        {
            return null;
        }

        if (trimmed[index] != '.' && trimmed[index] != ')')
        {
            return null;
        }

        // 「1..2」不是列表项；数字读不出来也不当列表（比如「2020. 年」这种写法）。
        return trimmed[index] == '.' && trimmed[index + 1] == '.'
            ? null
            : int.TryParse(trimmed[..index], out var number)
                ? (number, trimmed[(index + 1)..].Trim())
                : null;
    }
}

/// <summary>行内片段的样式。</summary>
public enum MarkdownInlineStyle
{
    Plain = 0,
    Bold = 1,
    Code = 2,
}
