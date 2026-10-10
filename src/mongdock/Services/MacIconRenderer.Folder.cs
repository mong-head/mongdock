using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mongdock.Services;

/// <summary>독 폴더 아이콘의 종류 그림 — 무슨 폴더인지 보이게 (알려진 폴더만, 그 밖은 None).</summary>
internal enum FolderGlyph { None, Downloads, Documents, Pictures, Desktop, Music, Videos }

/// <summary>
/// 독 폴더 아이콘 (#24-B): 앱 모음(#d26)과 같은 하늘색~연보라 판 위에 흰 폴더, 알려진 폴더면 앞판에 종류 그림
/// (다운로드 ↓ · 문서 줄 · 사진 산 · 바탕 화면 모니터 · 음악 음표 · 동영상 ▶). 없어진 폴더는 회색 판.
/// </summary>
internal static partial class MacIconRenderer
{
    private static readonly Color FolderInk = Color.FromRgb(0x7A, 0x8A, 0xEE); // 흰 폴더 위 그림 색 (판의 중간 톤)
    private static readonly Color MissingTop = Color.FromRgb(0xD1, 0xD1, 0xD6), MissingBottom = Color.FromRgb(0xAE, 0xAE, 0xB2);

    public static BitmapSource Folder(FolderGlyph glyph = FolderGlyph.None, bool missing = false) => Render(dc =>
    {
        dc.DrawGeometry(missing ? new LinearGradientBrush(MissingTop, MissingBottom, 75) : new LinearGradientBrush(SkyTop, SkyBottom, 75),
            new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 0.5), Squircle);
        DrawFolder(dc, FolderBox(BodyRect, 0.68, down: 0.02), glyph, missing);
    });

    /// <summary>판 r 가운데 폭 = r 의 width 비율, 높이 = 폭의 0.8.</summary>
    private static Rect FolderBox(Rect r, double width, double down = 0)
    {
        double w = r.Width * width, h = w * 0.80;
        return new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2 + r.Height * down, w, h);
    }

    /// <summary>뒤판(왼쪽 위 탭) → 앞판(+ 그림). 아래로 옅은 그림자 (앱 모음 칸과 같은 톤).</summary>
    private static void DrawFolder(DrawingContext dc, Rect box, FolderGlyph glyph, bool missing)
    {
        double w = box.Width, h = box.Height, rad = w * 0.075;
        var shade = new SolidColorBrush(missing ? Color.FromArgb(0x30, 0x40, 0x40, 0x48) : Color.FromArgb(0x38, 0x6A, 0x5C, 0xC8));

        var back = new CombinedGeometry(GeometryCombineMode.Union,
            new RectangleGeometry(new Rect(box.X, box.Y, w * 0.42, h * 0.30), rad, rad),
            new RectangleGeometry(new Rect(box.X, box.Y + h * 0.12, w, h * 0.88), rad, rad));
        dc.PushTransform(new TranslateTransform(0, h * 0.05));
        dc.DrawGeometry(shade, null, back);
        dc.Pop();
        dc.DrawGeometry(new SolidColorBrush(missing ? Color.FromRgb(0xEA, 0xEA, 0xEE) : Color.FromRgb(0xE6, 0xE4, 0xFB)), null, back);

        var front = new Rect(box.X, box.Y + h * 0.27, w, h * 0.73);
        dc.DrawRoundedRectangle(shade, null, new Rect(front.X, front.Y - h * 0.015, front.Width, front.Height), rad, rad);
        dc.DrawRoundedRectangle(new LinearGradientBrush(Colors.White, missing ? Color.FromRgb(0xF2, 0xF2, 0xF4) : Color.FromRgb(0xF3, 0xF1, 0xFF), 90), null, front, rad, rad);

        if (glyph != FolderGlyph.None) DrawFolderGlyph(dc, front, glyph, missing ? Color.FromRgb(0xA0, 0xA0, 0xA8) : FolderInk);
    }

    /// <summary>앞판 가운데의 종류 그림 (한 색, 굵은 선 — 52px 에서도 보이게).</summary>
    private static void DrawFolderGlyph(DrawingContext dc, Rect front, FolderGlyph glyph, Color color)
    {
        var ink = new SolidColorBrush(color);
        double s = front.Height * 0.64, cx = front.X + front.Width / 2, cy = front.Y + front.Height * 0.53;
        Pen P(double t) => new(ink, s * t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (glyph)
        {
            case FolderGlyph.Downloads:
                dc.DrawLine(P(0.18), new Point(cx, cy - s * 0.42), new Point(cx, cy + s * 0.30));
                dc.DrawGeometry(null, P(0.18), Poly(false, new Point(cx - s * 0.30, cy), new Point(cx, cy + s * 0.32), new Point(cx + s * 0.30, cy)));
                break;
            case FolderGlyph.Documents:
                for (int i = 0; i < 3; i++)
                {
                    double y = cy - s * 0.28 + i * s * 0.28, len = i == 2 ? s * 0.44 : s * 0.76;
                    dc.DrawLine(P(0.15), new Point(cx - s * 0.38, y), new Point(cx - s * 0.38 + len, y));
                }
                break;
            case FolderGlyph.Pictures:
                dc.DrawEllipse(ink, null, new Point(cx + s * 0.24, cy - s * 0.22), s * 0.11, s * 0.11);
                dc.DrawGeometry(ink, P(0.06), Poly(true, new Point(cx - s * 0.45, cy + s * 0.32), new Point(cx - s * 0.12, cy - s * 0.10),
                    new Point(cx + s * 0.08, cy + s * 0.12), new Point(cx + s * 0.22, cy), new Point(cx + s * 0.45, cy + s * 0.32)));
                break;
            case FolderGlyph.Desktop:
                // 모니터: 화면 테두리 + 받침
                dc.DrawRoundedRectangle(null, P(0.13), new Rect(cx - s * 0.42, cy - s * 0.36, s * 0.84, s * 0.54), s * 0.07, s * 0.07);
                dc.DrawLine(P(0.13), new Point(cx, cy + s * 0.18), new Point(cx, cy + s * 0.32));
                dc.DrawLine(P(0.13), new Point(cx - s * 0.20, cy + s * 0.34), new Point(cx + s * 0.20, cy + s * 0.34));
                break;
            case FolderGlyph.Music:
                // 음표 두 개: 기둥 + 위 막대 + 둥근 머리
                dc.DrawLine(P(0.12), new Point(cx - s * 0.16, cy + s * 0.24), new Point(cx - s * 0.16, cy - s * 0.32));
                dc.DrawLine(P(0.12), new Point(cx + s * 0.30, cy + s * 0.14), new Point(cx + s * 0.30, cy - s * 0.42));
                dc.DrawLine(P(0.16), new Point(cx - s * 0.16, cy - s * 0.32), new Point(cx + s * 0.30, cy - s * 0.42));
                dc.DrawEllipse(ink, null, new Point(cx - s * 0.28, cy + s * 0.26), s * 0.14, s * 0.11);
                dc.DrawEllipse(ink, null, new Point(cx + s * 0.18, cy + s * 0.16), s * 0.14, s * 0.11);
                break;
            case FolderGlyph.Videos:
                dc.DrawGeometry(ink, P(0.10), Poly(true, new Point(cx - s * 0.24, cy - s * 0.36), new Point(cx + s * 0.36, cy), new Point(cx - s * 0.24, cy + s * 0.36)));
                break;
        }
    }

    private static Geometry Poly(bool filled, params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], filled, filled);
            for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, true);
        }
        g.Freeze();
        return g;
    }
}
