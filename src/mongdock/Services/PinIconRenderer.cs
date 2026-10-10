using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// "아이콘 고르기"(#24, 루틴·독 폴더 공통)로 고른 아이콘을 그림: 판 색 8가지 위 흰 기호/글자, 또는 그림 파일(정사각형으로 잘라 독 아이콘 모양).
/// 같은 값이면 캐시. UI 스레드에서.
/// </summary>
internal static class PinIconRenderer
{
    /// <summary>판 색 이름 (PinIcon.Color) — 고르기 카드의 순서.</summary>
    public static readonly string[] Colors = { "mongdock", "blue", "green", "orange", "red", "pink", "gray", "black" };

    /// <summary>기호 30개 (Segoe Fluent Icons / MDL2 Assets — 윈도우 기본 글꼴).</summary>
    public static readonly string[] Glyphs =
    {
        "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "",
        "", "", "", "", "", "", "", "", "", "",
    };

    public static (Color Top, Color Bottom) PlateColors(string? name) => name switch
    {
        "blue" => (Color.FromRgb(0x86, 0xC1, 0xFF), Color.FromRgb(0x3E, 0x7F, 0xF2)),
        "green" => (Color.FromRgb(0x8C, 0xE0, 0xA4), Color.FromRgb(0x2E, 0xB4, 0x68)),
        "orange" => (Color.FromRgb(0xFF, 0xC8, 0x80), Color.FromRgb(0xFF, 0x8C, 0x2E)),
        "red" => (Color.FromRgb(0xFF, 0x93, 0x8A), Color.FromRgb(0xEE, 0x45, 0x3C)),
        "pink" => (Color.FromRgb(0xFF, 0xAE, 0xD6), Color.FromRgb(0xF0, 0x62, 0xAA)),
        "gray" => (Color.FromRgb(0xCF, 0xD2, 0xD8), Color.FromRgb(0x8D, 0x91, 0x9B)),
        "black" => (Color.FromRgb(0x50, 0x53, 0x5C), Color.FromRgb(0x1C, 0x1D, 0x22)),
        _ => (MacIconRenderer.SkyTop, MacIconRenderer.SkyBottom),
    };

    private static readonly Dictionary<string, ImageSource> Cache = new();

    /// <summary>
    /// 고른 아이콘. auto(또는 null)·그림 파일을 못 읽음 → null (부르는 쪽 기본 그림).
    /// 기호·글자 없이 색만 고르면 plain(색) — 독 폴더는 그 색 판의 폴더(+ 종류 그림), 휴지통은 그 색 판의 휴지통. plainKey 는 캐시 구분용.
    /// </summary>
    public static ImageSource? Render(PinIcon? icon, Func<string?, ImageSource>? plain = null, string plainKey = "", bool missing = false)
    {
        if (icon is null || icon.Mode == PinIconMode.Auto) return null;
        bool plainOnly = icon.Mode == PinIconMode.Glyph && string.IsNullOrEmpty(icon.Glyph) && string.IsNullOrWhiteSpace(icon.Text) && plain is not null;
        string key = $"{icon.Mode}|{icon.Color}|{icon.Glyph}|{icon.Text}|{icon.File}|{(plainOnly ? plainKey : "")}|{missing}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        ImageSource? img = icon.Mode switch
        {
            PinIconMode.File => FromFile(icon.File),
            _ when plainOnly => plain!(icon.Color),
            _ => MacIconRenderer.Symbol(missing ? "gray" : icon.Color, icon.Glyph, icon.Text),
        };
        if (img is not null)
        {
            if (Cache.Count >= 64) Cache.Clear(); // 고르기 카드에서 글자를 칠 때마다 쌓이지 않게
            Cache[key] = img;
        }
        return img;
    }

    /// <summary>그림 파일 → 가운데 정사각형으로 잘라 독 아이콘 모양(맥 스퀴클). 못 읽으면 null.</summary>
    private static ImageSource? FromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 파일을 잠그지 않음
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 512;
            bmp.EndInit();
            bmp.Freeze();
            int side = Math.Min(bmp.PixelWidth, bmp.PixelHeight);
            var square = new CroppedBitmap(bmp, new Int32Rect((bmp.PixelWidth - side) / 2, (bmp.PixelHeight - side) / 2, side, side));
            square.Freeze();
            return MacIconRenderer.Normalize(square) ?? (ImageSource)square;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"아이콘 그림 읽기 실패: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>글자 아이콘의 글꼴 (한글·영문 모두 굵게).</summary>
    internal static readonly Typeface TextFace = new(new FontFamily("Segoe UI Variable Display, Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    internal static readonly Typeface GlyphFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    internal static FormattedText Text(string s, Typeface face, double size, Brush brush) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1.0);
}
