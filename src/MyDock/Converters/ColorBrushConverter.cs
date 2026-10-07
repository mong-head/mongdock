using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MyDock.Converters;

/// <summary>"#AARRGGBB" / "#RRGGBB" 문자열 → 고정(Frozen) SolidColorBrush. 잘못된 값이면 기본색.</summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class ColorBrushConverter : IValueConverter
{
    /// <summary>변환 실패 시 쓸 색 (XAML 에서 지정 가능).</summary>
    public Color Fallback { get; set; } = Colors.Gray;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Parse(value as string, Fallback);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SolidColorBrush b ? b.Color.ToString(CultureInfo.InvariantCulture) : Binding.DoNothing;

    public static SolidColorBrush Parse(string? text, Color fallback)
    {
        var brush = new SolidColorBrush(ParseColor(text, fallback));
        brush.Freeze();
        return brush;
    }

    public static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try
        {
            return ColorConverter.ConvertFromString(text.Trim()) is Color c ? c : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }
}
