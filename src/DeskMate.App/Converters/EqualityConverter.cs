using System.Globalization;
using System.Windows.Data;

namespace DeskMate.App.Converters;

/// <summary>
/// 值与参数（忽略大小写）相等 → true。用于设置页「当前主题 / Dock 位置」的单选高亮。
/// </summary>
[ValueConversion(typeof(object), typeof(bool))]
public sealed class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value?.ToString() ?? string.Empty, parameter?.ToString() ?? string.Empty,
                         StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
