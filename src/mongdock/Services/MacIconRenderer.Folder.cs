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

    /// <summary>color = 판 색 이름(아이콘 고르기, null = 몽독 하늘~연보라). 종류 그림은 판 색의 진한 쪽.</summary>
    public static BitmapSource Folder(FolderGlyph glyph = FolderGlyph.None, bool missing = false, string? color = null) => Render(dc =>
    {
        var (top, bottom) = missing ? (MissingTop, MissingBottom) : PinIconRenderer.PlateColors(color);
        dc.DrawGeometry(new LinearGradientBrush(top, bottom, 75), new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 0.5), Squircle);
        var ink = missing ? Color.FromRgb(0xA0, 0xA0, 0xA8) : color is null or "mongdock" ? FolderInk : bottom;
        DrawFolder(dc, FolderBox(BodyRect, 0.68, down: 0.02), glyph, missing, ink);
    });

    /// <summary>아이콘 고르기 "기호": 판 색 위 흰 기호(Segoe Fluent Icons) 또는 글자 1~2자. 둘 다 없으면 판만.</summary>
    public static BitmapSource Symbol(string? color, string? glyph, string? text) => Render(dc =>
    {
        var (top, bottom) = PinIconRenderer.PlateColors(color);
        dc.DrawGeometry(new LinearGradientBrush(top, bottom, 75), new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 0.5), Squircle);
        var shade = new SolidColorBrush(Color.FromArgb(0x30, 0x20, 0x20, 0x40));
        FormattedText? ft = !string.IsNullOrWhiteSpace(text)
            ? PinIconRenderer.Text(text.Trim(), PinIconRenderer.TextFace, BodySize * (text.Trim().Length > 1 ? 0.40 : 0.52), Brushes.White)
            : !string.IsNullOrEmpty(glyph) ? PinIconRenderer.Text(glyph, PinIconRenderer.GlyphFace, BodySize * 0.54, Brushes.White) : null;
        if (ft is null) return;
        double x = BodyRect.X + (BodySize - ft.Width) / 2, y = BodyRect.Y + (BodySize - ft.Height) / 2;
        // 기호는 선이 가늘어 작은 독에서 흐려짐 → 같은 색 테두리로 굵게
        var outline = string.IsNullOrWhiteSpace(text) ? new Pen(Brushes.White, BodySize * 0.014) { LineJoin = PenLineJoin.Round } : null;
        dc.DrawGeometry(shade, outline is null ? null : new Pen(shade, outline.Thickness), ft.BuildGeometry(new Point(x, y + BodySize * 0.012)));
        dc.DrawGeometry(Brushes.White, outline, ft.BuildGeometry(new Point(x, y)));
    });

    /// <summary>
    /// 독 휴지통 (#24-C): 판 위 흰 휴지통 (뚜껑·손잡이·세로 홈). full 이면 뚜껑 대신 종이 두 장이 비어져 나옴 — 빔/참이 52px 에서도 구분되게.
    /// color = 판 색 이름 (아이콘 바꾸기, null = 몽독 하늘~연보라).
    /// </summary>
    public static BitmapSource Trash(bool full, string? color = null) => Render(dc =>
    {
        var (top, bottom) = PinIconRenderer.PlateColors(color);
        dc.DrawGeometry(new LinearGradientBrush(top, bottom, 75), new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 0.5), Squircle);
        var ink = new SolidColorBrush(color is null or "mongdock" ? FolderInk : bottom);
        var shade = new SolidColorBrush(Color.FromArgb(0x38, 0x6A, 0x5C, 0xC8));
        var white = new LinearGradientBrush(Colors.White, Color.FromRgb(0xF3, 0xF1, 0xFF), 90);
        var r = BodyRect;
        double cx = r.X + r.Width / 2, w = r.Width;

        // 몸통: 위가 조금 넓은 둥근 사다리꼴
        double topY = r.Y + r.Height * 0.40, botY = r.Y + r.Height * 0.80, topHalf = w * 0.22, botHalf = w * 0.18, rad = w * 0.035;
        Geometry Body(double dy)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(cx - topHalf, topY + dy), true, true);
                c.LineTo(new Point(cx + topHalf, topY + dy), true, true);
                c.LineTo(new Point(cx + botHalf, botY - rad + dy), true, true);
                c.ArcTo(new Point(cx + botHalf - rad * 1.5, botY + dy), new Size(rad * 1.5, rad * 1.5), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(cx - botHalf + rad * 1.5, botY + dy), true, true);
                c.ArcTo(new Point(cx - botHalf, botY - rad + dy), new Size(rad * 1.5, rad * 1.5), 0, false, SweepDirection.Clockwise, true, true);
            }
            g.Freeze();
            return g;
        }

        if (full)
        {
            // 종이 두 장 (몸통 뒤에서 비스듬히 비어져 나옴)
            var paper = new SolidColorBrush(Colors.White);
            foreach (var (angle, dx, lift) in new[] { (-14.0, -0.07, 0.15), (11.0, 0.07, 0.19) })
            {
                double pw = w * 0.20, ph = w * 0.26, px = cx + w * dx - pw / 2, py = topY - r.Height * lift;
                dc.PushTransform(new RotateTransform(angle, px + pw / 2, py + ph));
                dc.DrawRoundedRectangle(shade, null, new Rect(px, py + ph * 0.03, pw, ph), w * 0.02, w * 0.02);
                dc.DrawRoundedRectangle(paper, null, new Rect(px, py, pw, ph), w * 0.02, w * 0.02);
                var line = new Pen(new SolidColorBrush(Color.FromArgb(0x70, FolderInk.R, FolderInk.G, FolderInk.B)), w * 0.014) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 0; i < 3; i++)
                    dc.DrawLine(line, new Point(px + pw * 0.2, py + ph * (0.25 + i * 0.18)), new Point(px + pw * (i == 2 ? 0.55 : 0.8), py + ph * (0.25 + i * 0.18)));
                dc.Pop();
            }
        }

        dc.DrawGeometry(shade, null, Body(r.Height * 0.03));
        dc.DrawGeometry(white, null, Body(0));
        // 세로 홈 3줄
        var groove = new Pen(ink, w * 0.028) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (double t in new[] { -0.09, 0.0, 0.09 })
            dc.DrawLine(groove, new Point(cx + w * t, topY + r.Height * 0.08), new Point(cx + w * t * 0.85, botY - r.Height * 0.07));

        if (!full)
        {
            // 뚜껑 + 손잡이
            double lidY = topY - r.Height * 0.085, lidH = r.Height * 0.06, lidHalf = w * 0.27;
            dc.DrawRoundedRectangle(shade, null, new Rect(cx - lidHalf, lidY + lidH * 0.3, lidHalf * 2, lidH), lidH / 2, lidH / 2);
            dc.DrawRoundedRectangle(white, null, new Rect(cx - lidHalf, lidY, lidHalf * 2, lidH), lidH / 2, lidH / 2);
            double hw = w * 0.07, hh = r.Height * 0.05;
            dc.DrawRoundedRectangle(null, new Pen(Brushes.White, w * 0.025), new Rect(cx - hw, lidY - hh, hw * 2, hh + lidH * 0.3), w * 0.02, w * 0.02);
        }
    });

    /// <summary>판 r 가운데 폭 = r 의 width 비율, 높이 = 폭의 0.8.</summary>
    private static Rect FolderBox(Rect r, double width, double down = 0)
    {
        double w = r.Width * width, h = w * 0.80;
        return new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2 + r.Height * down, w, h);
    }

    /// <summary>뒤판(왼쪽 위 탭) → 앞판(+ 그림). 아래로 옅은 그림자 (앱 모음 칸과 같은 톤).</summary>
    private static void DrawFolder(DrawingContext dc, Rect box, FolderGlyph glyph, bool missing, Color ink)
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

        if (glyph != FolderGlyph.None) DrawFolderGlyph(dc, front, glyph, ink);
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
