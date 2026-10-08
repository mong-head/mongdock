using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mongdock.Views;

/// <summary>트레이 아이콘 한 장의 색 분석 결과.</summary>
/// <param name="Monochrome">거의 한 가지 색(채도 낮음 + 밝기 분산 작음) — 맥 "템플릿 이미지" 처럼 글자색으로 다시 칠할 대상.</param>
/// <param name="Luminance">불투명도 가중 평균 상대 휘도 (0~1, WCAG).</param>
internal readonly record struct TrayIconLook(bool Monochrome, double Luminance);

/// <summary>
/// 트레이 아이콘이 상단바/⌃ 카드 색과 같아 안 보이는 문제 해결용 판정 (TrayIconButton 이 사용).
/// - 거의 단색 아이콘(흰·검은 글리프 등) → 바 글자색으로 다시 칠함 (알파 유지, OpacityMask).
/// - 컬러 아이콘 → 그대로. 단, 평균 휘도와 바 배경의 대비비가 <see cref="MinContrast"/> 미만이면 뒤에 옅은 둥근 판.
/// 분석은 비트맵(HICON 이 바뀌면 새 BitmapSource)마다 한 번 — ConditionalWeakTable 캐시.
/// </summary>
internal static class TrayIconTint
{
    /// <summary>컬러 아이콘이 이보다 대비가 낮으면 판을 깐다.</summary>
    public const double MinContrast = 1.5;
    /// <summary>판 색 = 바 글자색 × 이 불투명도.</summary>
    public const byte PlateAlpha = 0x24; // ≈ 14%

    private const byte MinAlpha = 48;          // 이보다 투명한 픽셀은 가장자리로 보고 제외
    private const double ChromaLimit = 0.12;   // (max-min)/255 가 이보다 크면 "색 있는" 픽셀
    private const double ColoredShare = 0.06;  // 색 있는 픽셀이 이 비율을 넘으면 컬러 아이콘
    private const double LumSpread = 0.17;     // 밝기(감마 공간) 표준편차가 이보다 크면 여러 색(예: 흰 글리프 + 검은 테두리)

    private static readonly ConditionalWeakTable<BitmapSource, Box> Cache = new();

    private sealed class Box
    {
        public TrayIconLook Look;
    }

    public static TrayIconLook Analyze(ImageSource? source)
    {
        if (source is not BitmapSource bmp) return new TrayIconLook(false, 0.5);
        if (Cache.TryGetValue(bmp, out var box)) return box.Look;
        TrayIconLook look;
        try { look = Compute(bmp); }
        catch { look = new TrayIconLook(false, 0.5); }
        try { Cache.AddOrUpdate(bmp, new Box { Look = look }); } catch { }
        return look;
    }

    private static TrayIconLook Compute(BitmapSource bmp)
    {
        BitmapSource src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        if (w <= 0 || h <= 0 || w * h > 512 * 512) return new TrayIconLook(false, 0.5);
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);

        double wSum = 0, linSum = 0, gSum = 0, gSq = 0, colored = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = px[i + 3];
            if (a < MinAlpha) continue;
            byte b = px[i], g = px[i + 1], r = px[i + 2];
            double wt = a / 255.0;
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if ((max - min) / 255.0 > ChromaLimit) colored += wt;
            double gamma = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            gSum += wt * gamma;
            gSq += wt * gamma * gamma;
            linSum += wt * RelativeLuminance(r, g, b);
            wSum += wt;
        }
        if (wSum < 3) return new TrayIconLook(false, 0.5); // 거의 빈 아이콘
        double mean = gSum / wSum;
        double sd = Math.Sqrt(Math.Max(0, gSq / wSum - mean * mean));
        bool mono = colored / wSum <= ColoredShare && sd <= LumSpread;
        return new TrayIconLook(mono, linSum / wSum);
    }

    private static double Lin(byte c)
    {
        double v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    public static double RelativeLuminance(byte r, byte g, byte b) => 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);

    public static double RelativeLuminance(Color c) => RelativeLuminance(c.R, c.G, c.B);

    /// <summary>WCAG 대비비 (1~21).</summary>
    public static double Contrast(double l1, double l2) => (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);

    /// <summary>
    /// 바 배경 휘도: 불투명한 배경색을 알면 그것, 모르면(투명 바·블러) 글자색의 반대로 추정 (밝은 글자 = 어두운 바).
    /// </summary>
    public static double BackgroundLuminance(Color? background, Color foreground)
    {
        if (background is Color bg && bg.A >= 0x80) return RelativeLuminance(bg);
        return RelativeLuminance(foreground) > 0.4 ? 0.02 : 0.9;
    }
}
