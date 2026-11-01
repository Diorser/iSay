using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using iSay.Voice;

namespace iSay.Desktop;

public sealed class HistoryPreviewConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is HistoryEntry entry ? string.IsNullOrWhiteSpace(entry.FormattedText) ? entry.RawText : entry.FormattedText : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not null && value != DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TailPreviewConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || text.Length == 0) return "等待你开口…";
        // 按完整字素截取结尾，浮条始终呈现新词，避免拆开表情或组合字符。
        var starts = StringInfo.ParseCombiningCharacters(text);
        var preview = starts.Length > 27 ? "…" + text[starts[^27]..] : text;
        return preview.Replace('\r', ' ').Replace('\n', ' ');
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
