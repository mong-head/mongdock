using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mongdock.Services;

/// <summary>독 폴더 아이콘 종류 (C 안: 폴더마다 다른 그림).</summary>
internal enum FolderGlyph { None, Downloads, Documents, Pictures }

/// <summary>
/// 독 폴더 아이콘 (#24-B 시안): 앱 모음(#d26)과 같은 하늘색~연보라 판 위에 흰 폴더.
/// A = 흰 폴더만, B = 폴더 위로 최근 파일 2~3장이 비어져 나옴, C = 폴더 종류 그림(다운로드 ↓·문서·사진).
/// </summary>
internal static partial class MacIconRenderer
{
    private static readonly Color FolderInk = Color.FromRgb(0x7A, 0x8A, 0xEE); // 흰 폴더 위 그림 색 (판의 중간 톤)

    /// <summary>A·C: 판 + 흰 폴더 (+ 종류 그림).</summary>
    public static BitmapSource Folder(FolderGlyph glyph = FolderGlyph.None) => Render(dc =>
    {
        DrawSkyPlate(dc);
        var box = FolderBox(BodyRect, 0.68, down: 0.02);
        DrawFolder(dc, box, null, glyph);
    });

    /// <summary>B: 판 + 흰 폴더, 그 사이로 최근 파일(썸네일, 없으면 견본 종이) 최대 3장이 비어져 나옴. 앞 = 최근.</summary>
    public static BitmapSource FolderStack(IReadOnlyList<ImageSource?> recent) => Render(dc =>
    {
        DrawSkyPlate(dc);
        var box = FolderBox(BodyRect, 0.68, down: 0.09);
        DrawFolder(dc, box, (d, b) => DrawPeekingCards(d, b, recent), FolderGlyph.None);
    });

    private static void DrawSkyPlate(DrawingContext dc) =>
        dc.DrawGeometry(new LinearGradientBrush(SkyTop, SkyBottom, 75), new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 0.5), Squircle);

    /// <summary>판 r 가운데 폭 = r 의 width 비율, 높이 = 폭의 0.8.</summary>
    private static Rect FolderBox(Rect r, double width, double down = 0)
    {
        double w = r.Width * width, h = w * 0.80;
        return new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2 + r.Height * down, w, h);
    }

    /// <summary>뒤판(왼쪽 위 탭) → between(비어져 나온 종이) → 앞판(+ 그림). 앞판 아래로 옅은 보라 그림자 (앱 모음 칸과 같은 톤).</summary>
    private static void DrawFolder(DrawingContext dc, Rect box, Action<DrawingContext, Rect>? between, FolderGlyph glyph)
    {
        double w = box.Width, h = box.Height, rad = w * 0.075;
        var shade = new SolidColorBrush(Color.FromArgb(0x38, 0x6A, 0x5C, 0xC8));

        // 뒤판: 탭 + 몸통 (조금 더 진한 흰색)
        var back = new CombinedGeometry(GeometryCombineMode.Union,
            new RectangleGeometry(new Rect(box.X, box.Y, w * 0.42, h * 0.30), rad, rad),
            new RectangleGeometry(new Rect(box.X, box.Y + h * 0.12, w, h * 0.88), rad, rad));
        dc.PushTransform(new TranslateTransform(0, h * 0.05));
        dc.DrawGeometry(shade, null, back);
        dc.Pop();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xE6, 0xE4, 0xFB)), null, back);

        between?.Invoke(dc, box);

        // 앞판: 살짝 낮게 시작, 위가 더 밝은 흰색
        var front = new Rect(box.X, box.Y + h * 0.27, w, h * 0.73);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0x6A, 0x5C, 0xC8)), null, new Rect(front.X, front.Y - h * 0.015, front.Width, front.Height), rad, rad);
        dc.DrawRoundedRectangle(new LinearGradientBrush(Colors.White, Color.FromRgb(0xF3, 0xF1, 0xFF), 90), null, front, rad, rad);

        if (glyph != FolderGlyph.None) DrawFolderGlyph(dc, front, glyph);
    }

    /// <summary>앞판 가운데의 종류 그림 (판 중간 톤 한 색, 굵은 선 — 52px 에서도 보이게).</summary>
    private static void DrawFolderGlyph(DrawingContext dc, Rect front, FolderGlyph glyph)
    {
        var ink = new SolidColorBrush(FolderInk);
        double s = front.Height * 0.64, cx = front.X + front.Width / 2, cy = front.Y + front.Height * 0.53;
        var pen = new Pen(ink, s * 0.18) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (glyph)
        {
            case FolderGlyph.Downloads:
                dc.DrawLine(pen, new Point(cx, cy - s * 0.42), new Point(cx, cy + s * 0.30));
                dc.DrawGeometry(null, pen, Poly(new Point(cx - s * 0.30, cy), new Point(cx, cy + s * 0.32), new Point(cx + s * 0.30, cy)));
                break;
            case FolderGlyph.Documents:
                var thin = new Pen(ink, s * 0.15) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 0; i < 3; i++)
                {
                    double y = cy - s * 0.28 + i * s * 0.28, half = i == 2 ? s * 0.22 : s * 0.38;
                    dc.DrawLine(thin, new Point(cx - s * 0.38, y), new Point(cx - s * 0.38 + half * 2 - (i == 2 ? 0 : 0), y));
                }
                break;
            case FolderGlyph.Pictures:
                dc.DrawEllipse(ink, null, new Point(cx + s * 0.24, cy - s * 0.22), s * 0.11, s * 0.11);
                var mountain = new StreamGeometry();
                using (var g = mountain.Open())
                {
                    g.BeginFigure(new Point(cx - s * 0.45, cy + s * 0.32), true, true);
                    g.LineTo(new Point(cx - s * 0.12, cy - s * 0.10), true, false);
                    g.LineTo(new Point(cx + s * 0.08, cy + s * 0.12), true, false);
                    g.LineTo(new Point(cx + s * 0.22, cy + s * 0.00), true, false);
                    g.LineTo(new Point(cx + s * 0.45, cy + s * 0.32), true, false);
                }
                mountain.Freeze();
                dc.DrawGeometry(ink, new Pen(ink, s * 0.06) { LineJoin = PenLineJoin.Round }, mountain);
                break;
        }
    }

    private static Geometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], false, false);
            for (int i = 1; i < pts.Length; i++) c.LineTo(pts[i], true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// 뒤판과 앞판 사이에서 위로 비어져 나온 종이 3장 (뒤 → 앞, 살짝 부채꼴). 썸네일이 있으면 둥근 종이 안에 채워 넣고,
    /// 없으면 견본(사진·PDF·문서 모양).
    /// </summary>
    private static void DrawPeekingCards(DrawingContext dc, Rect box, IReadOnlyList<ImageSource?> recent)
    {
        double cw = box.Width * 0.42, ch = cw * 1.25, rad = cw * 0.08;
        var angles = new[] { -11.0, 9.0, -1.0 };       // 뒤 2장 → 맨 앞(최근)
        var offsets = new[] { -0.24, 0.24, 0.0 };
        var lifts = new[] { 0.18, 0.20, 0.30 };
        int n = Math.Max(2, Math.Min(3, recent.Count == 0 ? 3 : recent.Count));
        for (int k = 3 - n; k < 3; k++)
        {
            int idx = 2 - k; // 맨 앞(k=2) = recent[0]
            var img = idx < recent.Count ? recent[idx] : null;
            double cx = box.X + box.Width / 2 + box.Width * offsets[k];
            double top = box.Y + box.Height * 0.27 - box.Height * lifts[k] - ch * 0.35;
            var rect = new Rect(cx - cw / 2, top, cw, ch);
            dc.PushTransform(new RotateTransform(angles[k], cx, top + ch));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x2A, 0x50, 0x48, 0xA0)), null, new Rect(rect.X, rect.Y + ch * 0.02, cw, ch), rad, rad);
            dc.DrawRoundedRectangle(Brushes.White, null, rect, rad, rad);
            var inner = new Rect(rect.X + cw * 0.08, rect.Y + cw * 0.08, cw * 0.84, ch - cw * 0.16);
            if (img is not null)
            {
                dc.PushClip(new RectangleGeometry(inner, rad * 0.6, rad * 0.6));
                double sc = Math.Max(inner.Width / img.Width, inner.Height / img.Height);
                double iw = img.Width * sc, ih = img.Height * sc;
                dc.DrawImage(img, new Rect(inner.X + (inner.Width - iw) / 2, inner.Y + (inner.Height - ih) / 2, iw, ih));
                dc.Pop();
            }
            else DrawSampleCard(dc, inner, k);
            dc.Pop();
        }
    }

    /// <summary>견본 종이: 0 = PDF(빨간 띠 + 줄), 1 = 문서(파란 줄), 2 = 사진(하늘·산).</summary>
    private static void DrawSampleCard(DrawingContext dc, Rect r, int kind)
    {
        double line = r.Height * 0.06;
        switch (kind)
        {
            case 0:
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xF2, 0x5C, 0x54)), null, new Rect(r.X, r.Y, r.Width, r.Height * 0.22), line * 0.5, line * 0.5);
                for (int i = 0; i < 4; i++)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xD9, 0xDB, 0xE3)), null, new Rect(r.X, r.Y + r.Height * (0.34 + i * 0.16), r.Width * (i == 3 ? 0.6 : 1), line), line / 2, line / 2);
                break;
            case 1:
                for (int i = 0; i < 5; i++)
                    dc.DrawRoundedRectangle(new SolidColorBrush(i == 0 ? Color.FromRgb(0x4A, 0x8B, 0xF0) : Color.FromRgb(0xC9, 0xD8, 0xF5)), null,
                        new Rect(r.X, r.Y + r.Height * (0.04 + i * 0.18), r.Width * (i == 0 ? 0.7 : i == 4 ? 0.5 : 1), line * (i == 0 ? 1.6 : 1)), line / 2, line / 2);
                break;
            default:
                dc.PushClip(new RectangleGeometry(r, line * 0.5, line * 0.5));
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x7C, 0xC4, 0xFF), Color.FromRgb(0xC9, 0xE8, 0xFF), 90), null, r);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x5C)), null, new Point(r.X + r.Width * 0.72, r.Y + r.Height * 0.28), r.Width * 0.12, r.Width * 0.12);
                var hill = new StreamGeometry();
                using (var g = hill.Open())
                {
                    g.BeginFigure(new Point(r.Left, r.Bottom), true, true);
                    g.LineTo(new Point(r.Left, r.Y + r.Height * 0.72), true, false);
                    g.LineTo(new Point(r.X + r.Width * 0.38, r.Y + r.Height * 0.48), true, false);
                    g.LineTo(new Point(r.X + r.Width * 0.62, r.Y + r.Height * 0.66), true, false);
                    g.LineTo(new Point(r.X + r.Width * 0.80, r.Y + r.Height * 0.56), true, false);
                    g.LineTo(new Point(r.Right, r.Y + r.Height * 0.70), true, false);
                    g.LineTo(new Point(r.Right, r.Bottom), true, false);
                }
                hill.Freeze();
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x4F, 0xB2, 0x6E)), null, hill);
                dc.Pop();
                break;
        }
    }
}
