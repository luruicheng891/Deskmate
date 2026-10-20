using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DeskMate.App.Converters;

/// <summary>
/// "#RRGGBB" / "#AARRGGBB" 字符串 → SolidColorBrush。
/// 文件夹颜色标签在配置里存的是字符串，UI 需要即时拿到画刷。
/// </summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly Brush Fallback = CreateFallback();

    private static Brush CreateFallback()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return parameter as Brush ?? Fallback;
        }

        try
        {
            var brush = (Brush)new BrushConverter().ConvertFromString(text.Trim())!;
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            // 配置里被写坏的颜色值不该让整张卡片崩掉。
            return parameter as Brush ?? Fallback;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Brush brush ? brush.ToString() : string.Empty;
}
