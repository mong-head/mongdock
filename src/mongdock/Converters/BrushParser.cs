using System.Globalization;
using System.Windows.Media;

namespace Mongdock.Converters;

/// <summary>설정의 색 문자열("#AARRGGBB" / "#RRGGBB") → Color/Brush. 비었거나 잘못된 값이면 기본색.</summary>
public static class BrushParser
{
    public static SolidColorBrush Parse(string? text, Color fallback) => Frozen(ParseColor(text, fallback));

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

    /// <summary>"#AARRGGBB" 형식 리터럴 (테마 기본색 정의용).</summary>
    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>상대 휘도(0~1, sRGB 근사). 알파는 무시.</summary>
    public static double Luminance(Color c)
        => (0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B));

    private static double Lin(byte v)
    {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    /// <summary>흰색 쪽으로 amount(0~1)만큼 섞음.</summary>
    public static Color Lighten(Color c, double amount)
    {
        byte L(byte v) => (byte)Math.Round(v + (255 - v) * amount);
        return Color.FromArgb(c.A, L(c.R), L(c.G), L(c.B));
    }

    public static string ToHex(Color c) => c.ToString(CultureInfo.InvariantCulture);
}
