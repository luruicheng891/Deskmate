using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using DeskMate.Core.Services;

namespace DeskMate.App.Converters;

/// <summary>
/// 把一行 Markdown 正文（或 Word 抽出来的段落）连行内标记一起渲染成 <see cref="TextBlock"/>：
/// <c>**粗**</c> 变半粗、<c>`码`</c> 变等宽带底色。
/// <para>
/// 为什么返回控件而不是字符串：WPF 没有「一段文字里每几个字换一种样式」的绑定写法，
/// 只有 <see cref="InlineCollection"/> 做得到；而块与块之间的差别（标题、列表、代码块）仍然由模板的触发器管，
/// 这里只管块内。
/// </para>
/// <para>
/// 生成的 <see cref="TextBlock"/> 不设字号/颜色 —— 它挂进模板之后会**继承**外层那些可继承属性，
/// 于是触发器改一处字号，行内所有片段跟着变。底色用资源引用而不是取快照，换深浅色才不会留在旧色上。
/// </para>
/// </summary>
[ValueConversion(typeof(string), typeof(TextBlock))]
public sealed class MarkdownInlineConverter : IValueConverter
{
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Microsoft YaHei UI");

    /// <summary>正文 → 带样式的 TextBlock。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };

        foreach (var (text, style) in MarkdownBlocks.Inline(value as string ?? string.Empty))
        {
            var run = new Run(text);

            switch (style)
            {
                case MarkdownInlineStyle.Bold:
                    run.FontWeight = FontWeights.SemiBold;
                    break;

                case MarkdownInlineStyle.Code:
                    run.FontFamily = MonoFont;
                    run.SetResourceReference(Inline.BackgroundProperty, "SurfaceHoverBrush");
                    break;
            }

            block.Inlines.Add(run);
        }

        return block;
    }

    /// <summary>单向：界面上的文字不会写回文件。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
