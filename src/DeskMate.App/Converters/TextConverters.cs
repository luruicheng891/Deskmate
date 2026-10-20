using System.Globalization;
using System.Windows.Data;

namespace DeskMate.App.Converters;

/// <summary>
/// 文件数 → 文案。达到统计上限时显示「10000+」，与需求 M3 的性能约定一致。
/// </summary>
[ValueConversion(typeof(int), typeof(string))]
public sealed class FileCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int c ? c : 0;
        return count switch
        {
            <= 0 => "空",
            >= 10000 => "10000+",
            _ => $"{count} 个文件",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 是否为目录 → Segoe MDL2 字形（目录夹 / 文件）。预览列表用来自不带图片地标识类型。
/// </summary>
[ValueConversion(typeof(bool), typeof(string))]
public sealed class DirectoryGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 名称 → 首字符。图标提取失败时，占位圆块上显示这个字，避免出现一排空白格。
/// </summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class FirstLetterConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        return string.IsNullOrWhiteSpace(text) ? "?" : text.Trim()[0].ToString().ToUpperInvariant();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 路径 → 末级名称。用于卡片上把长路径压短。
/// </summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class PathToLeafConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var separator = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
