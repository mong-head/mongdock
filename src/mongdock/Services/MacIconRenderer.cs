using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Mongdock.Services;

/// <summary>
/// macOS(Big Sur 이후) 아이콘 그리드 방식으로 아이콘을 같은 규격으로 정규화한다.
/// - 출력 256x256 (투명 여백 포함). 맥 1024 캔버스 기준 본체 824x824(80.5%) → 256 에서 206px, 사방 25px 여백.
/// - 본체 모양: 슈퍼타원(n=5) 스퀴클 — 모서리 반경이 본체의 약 22% 인 연속곡률 둥근 사각형 근사.
/// - 그림자: 아래로 12/1024, 블러 28/1024, 알파 30%.
/// - 원본이 이미 꽉 찬(둥근) 사각형이면 투명 테두리를 잘라 본체 크기로 맞추고 스퀴클로 자름.
///   아니면(원형 로고 등) 밝은 스퀴클 판 위에 본체의 72% 크기로 가운데 배치 (macOS 26 의 비정형 아이콘 처리).
/// RenderTargetBitmap 을 쓰므로 UI(STA) 스레드에서 호출.
/// </summary>
internal static class MacIconRenderer
{
    public const int Canvas = 256;
    private const double Scale = Canvas / 1024.0;
    private static readonly double BodySize = Math.Round(824 * Scale);           // 206
    private static readonly double BodyOffset = (Canvas - BodySize) / 2;         // 25
    private static readonly Rect BodyRect = new(BodyOffset, BodyOffset, BodySize, BodySize);
    private const double PlateContentRatio = 0.72;
    private const double SuperEllipseN = 5.0;

    private static readonly Geometry Squircle = CreateSquircle(BodyRect, SuperEllipseN);
    private static readonly Geometry ShadowSquircle = CreateSquircle(
        new Rect(BodyRect.X, BodyRect.Y + 12 * Scale, BodyRect.Width, BodyRect.Height), SuperEllipseN);

    /// <summary>원본 아이콘 → 맥 스타일 256x256. 실패 시 null.</summary>
    public static BitmapSource? Normalize(BitmapSource source)
    {
        var px = ReadPixels(ref source, out int w, out int h);
        if (px is null) return null;

        // 1) 알파 bbox (그림자·안티앨리어싱 가장자리 무시: 알파 > 40)
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (px[(y * w + x) * 4 + 3] > 40)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return Plate(null);
        var bbox = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);

        bool filled = IsFilledSquare(px, w, bbox);
        var cropped = new CroppedBitmap(source, bbox);
        cropped.Freeze();

        return filled ? Filled(cropped, EdgeColor(px, w, bbox)) : Plate(cropped);
    }

    /// <summary>
    /// "꽉 찬 (둥근) 사각형" 판정: 정사각형에 가깝고, bbox 에 맞춘 스퀴클 마스크 안쪽 픽셀의 97% 이상이 불투명.
    /// 즉 스퀴클로 잘랐을 때 빈 틈이 보이지 않을 때만 꽉 채우고, 원형(크롬)·기울어진 정육면체(Notion exe)·
    /// 모서리 반경이 아주 큰 모양 등은 판 위에 올린다.
    /// </summary>
    private static bool IsFilledSquare(byte[] px, int w, Int32Rect b)
    {
        double aspect = (double)b.Width / b.Height;
        if (aspect < 0.9 || aspect > 1.1 || b.Width < 16) return false;
        long inside = 0, opaque = 0;
        for (int y = b.Y; y < b.Y + b.Height; y++)
        {
            double v = Math.Abs((y + 0.5 - b.Y) / b.Height * 2 - 1);
            double vn = Math.Pow(v, SuperEllipseN);
            for (int x = b.X; x < b.X + b.Width; x++)
            {
                double u = Math.Abs((x + 0.5 - b.X) / b.Width * 2 - 1);
                if (Math.Pow(u, SuperEllipseN) + vn > 0.92) continue; // 가장자리 안티앨리어싱 띠는 제외
                inside++;
                if (px[(y * w + x) * 4 + 3] > 128) opaque++;
            }
        }
        return inside > 0 && opaque >= inside * 0.97;
    }

    /// <summary>bbox 가장자리 안쪽 불투명 픽셀의 평균색 (스퀴클로 자를 때 모서리 틈을 메우는 바탕색).</summary>
    private static Color EdgeColor(byte[] px, int w, Int32Rect b)
    {
        long r = 0, g = 0, bl = 0, n = 0;
        int inset = Math.Max(2, b.Width / 16);
        void Add(int x, int y)
        {
            int i = (y * w + x) * 4;
            int a = px[i + 3];
            if (a < 200) return;
            // Pbgra → 직선 색
            bl += px[i] * 255 / a; g += px[i + 1] * 255 / a; r += px[i + 2] * 255 / a; n++;
        }
        for (int x = b.X + inset; x < b.X + b.Width - inset; x += 2)
        {
            Add(x, b.Y + inset);
            Add(x, b.Y + b.Height - 1 - inset);
        }
        for (int y = b.Y + inset; y < b.Y + b.Height - inset; y += 2)
        {
            Add(b.X + inset, y);
            Add(b.X + b.Width - 1 - inset, y);
        }
        if (n == 0) return Colors.White;
        return Color.FromRgb((byte)Math.Min(255, r / n), (byte)Math.Min(255, g / n), (byte)Math.Min(255, bl / n));
    }

    private static BitmapSource Filled(BitmapSource cropped, Color backing)
    {
        return Render(dc =>
        {
            dc.PushClip(Squircle);
            dc.DrawRectangle(new SolidColorBrush(backing), null, BodyRect);
            dc.DrawImage(cropped, BodyRect);
            dc.Pop();
            // 아주 옅은 안쪽 테두리 (밝은 배경 위에서 경계)
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)), 0.5), Squircle);
        });
    }

    /// <summary>
    /// 윈도우 패키지 앱용: 항상 흰 판 위 72% 배치.
    /// removeBackground 면 가장자리가 한 가지 색으로 꽉 찬 타일 배경(예: 파란 설정 타일)을 투명화하고, 남은 그림이 흰색이면 타일 색으로 칠한다.
    /// </summary>
    public static BitmapSource? OnPlate(BitmapSource source, bool removeBackground)
    {
        var px = ReadPixels(ref source, out int w, out int h);
        if (px is null) return null;
        if (removeBackground && RemoveUniformBackground(px, w, h))
        {
            var cleaned = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
            cleaned.Freeze();
            source = cleaned;
        }
        // 투명 여백을 잘라 내용만 판 위에
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (px[(y * w + x) * 4 + 3] > 24)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return Plate(null);
        var cropped = new CroppedBitmap(source, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        cropped.Freeze();
        return Plate(cropped);
    }

    /// <summary>
    /// 테두리 픽셀이 거의 모두 같은 불투명 색(타일 배경)이면 그 색과 비슷한 픽셀을 투명화. 지웠으면 true.
    /// </summary>
    private static bool RemoveUniformBackground(byte[] px, int w, int h)
    {
        if (w < 8 || h < 8) return false;
        // 테두리에서 1~2px 안쪽(안티앨리어싱/둥근 모서리 피함)의 색 분포
        int inset = Math.Max(1, w / 32);
        var border = new List<int>();
        for (int x = inset; x < w - inset; x++) { border.Add(Idx(x, inset)); border.Add(Idx(x, h - 1 - inset)); }
        for (int y = inset; y < h - inset; y++) { border.Add(Idx(inset, y)); border.Add(Idx(w - 1 - inset, y)); }
        int Idx(int x, int y) => (y * w + x) * 4;

        long r = 0, g = 0, b = 0;
        int opaque = 0;
        foreach (int i in border)
        {
            if (px[i + 3] < 240) continue;
            b += px[i]; g += px[i + 1]; r += px[i + 2]; opaque++;
        }
        if (opaque < border.Count * 0.7) return false; // 이미 투명 배경
        byte br = (byte)(r / opaque), bg = (byte)(g / opaque), bb = (byte)(b / opaque);
        int Dist(int i) => Math.Abs(px[i + 2] - br) + Math.Abs(px[i + 1] - bg) + Math.Abs(px[i] - bb);
        int uniform = border.Count(i => px[i + 3] >= 240 && Dist(i) < 30);
        if (uniform < border.Count * 0.9) return false; // 가장자리가 단색이 아님 (그림이 꽉 찬 아이콘)

        // 타일 색과 비슷한 픽셀은 모두 투명화 (톱니 구멍처럼 둘러싸인 부분도 타일 색이면 지움).
        // 경계의 안티앨리어싱 픽셀은 거리에 비례해 알파를 줄인다.
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 0) continue;
            // 프리멀티플라이 → 직선 색으로 비교
            int sr = px[i + 2] * 255 / a, sg = px[i + 1] * 255 / a, sb = px[i] * 255 / a;
            int d = Math.Abs(sr - br) + Math.Abs(sg - bg) + Math.Abs(sb - bb);
            if (d < 40) { px[i] = px[i + 1] = px[i + 2] = px[i + 3] = 0; }
            else if (d < 160)
            {
                double k = (d - 40) / 120.0;
                for (int c = 0; c < 4; c++) px[i + c] = (byte)(px[i + c] * k);
            }
        }

        // 남은 그림이 거의 흰색(타일 위 흰 글리프)이면 흰 판 위에서 안 보이므로 지운 타일 색으로 칠한다.
        long lum = 0, n = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a < 128) continue;
            lum += (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000 * 255 / a;
            n++;
        }
        if (n > 0 && lum / n > 200)
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                px[i] = (byte)(bb * a / 255);
                px[i + 1] = (byte)(bg * a / 255);
                px[i + 2] = (byte)(br * a / 255);
            }
        }
        return true;
    }

    /// <summary>밝은 스퀴클 판 + (있으면) 내용 72% 가운데 배치.</summary>
    public static BitmapSource Plate(BitmapSource? content) => Render(dc =>
    {
        DrawPlate(dc);
        if (content is null) return;
        double box = BodySize * PlateContentRatio;
        double s = Math.Min(box / content.PixelWidth, box / content.PixelHeight);
        double cw = content.PixelWidth * s, ch = content.PixelHeight * s;
        dc.DrawImage(content, new Rect(BodyRect.X + (BodySize - cw) / 2, BodyRect.Y + (BodySize - ch) / 2, cw, ch));
    });

    /// <summary>몽독 앱 아이콘 색(하늘색 #8FD0FF → 연보라 #C6B6FF, tools/make-icon)의 판.</summary>
    internal static readonly Color SkyTop = Color.FromRgb(0x8F, 0xD0, 0xFF), SkyBottom = Color.FromRgb(0xC6, 0xB6, 0xFF);

    /// <summary>
    /// 독 "앱 모음" (#d26): 몽독 하늘색~연보라 판 위에 흰 2x2 둥근 칸 (아래로 옅은 보라 그림자). 칸이 크고 넷뿐이라 16px 에서도 보임.
    /// 칸 배치는 IconService.CreateAllAppsIcon(원본 스타일)과 같은 비율.
    /// </summary>
    public static BitmapSource AllApps() => Render(dc =>
    {
        dc.DrawGeometry(new LinearGradientBrush(SkyTop, SkyBottom, 75), new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 0.5), Squircle);
        DrawAllAppsTiles(dc, BodyRect);
    });

    /// <summary>판 r 안에 흰 2x2 둥근 칸: 격자 = 판의 56%, 칸 사이 = 격자의 12%, 칸 모서리 = 칸의 28%.</summary>
    internal static void DrawAllAppsTiles(DrawingContext dc, Rect r)
    {
        double grid = r.Width * 0.56, gap = grid * 0.12, tile = (grid - gap) / 2, radius = tile * 0.28;
        double x0 = r.X + (r.Width - grid) / 2, y0 = r.Y + (r.Height - grid) / 2;
        var shade = new SolidColorBrush(Color.FromArgb(0x38, 0x6A, 0x5C, 0xC8));
        var white = new LinearGradientBrush(Colors.White, Color.FromRgb(0xF1, 0xEE, 0xFF), 90);
        for (int i = 0; i < 4; i++)
        {
            double x = x0 + (i % 2) * (tile + gap), y = y0 + (i / 2) * (tile + gap);
            dc.DrawRoundedRectangle(shade, null, new Rect(x, y + tile * 0.06, tile, tile), radius, radius);
            dc.DrawRoundedRectangle(white, null, new Rect(x, y, tile, tile), radius, radius);
        }
    }

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    private static void DrawPlate(DrawingContext dc)
    {
        var fill = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xEC, 0xEC, 0xEF), 90);
        dc.DrawGeometry(fill, new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 0.5), Squircle);
    }

    /// <summary>그림자(블러) + 본체를 256x256 Pbgra32 로 렌더.</summary>
    private static BitmapSource Render(Action<DrawingContext> drawBody)
    {
        var root = new ContainerVisual();

        var shadow = new DrawingVisual { Effect = new BlurEffect { Radius = 28 * Scale, KernelType = KernelType.Gaussian } };
        using (var dc = shadow.RenderOpen())
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x4D, 0, 0, 0)), null, ShadowSquircle);
        root.Children.Add(shadow);

        var body = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(body, BitmapScalingMode.HighQuality);
        using (var dc = body.RenderOpen()) drawBody(dc);
        root.Children.Add(body);

        var rtb = new RenderTargetBitmap(Canvas, Canvas, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>슈퍼타원 |x/a|^n + |y/b|^n = 1 을 다각형으로 근사.</summary>
    private static Geometry CreateSquircle(Rect r, double n)
    {
        const int steps = 360;
        double a = r.Width / 2, b = r.Height / 2, cx = r.X + a, cy = r.Y + b, e = 2.0 / n;
        var pts = new Point[steps];
        for (int i = 0; i < steps; i++)
        {
            double t = 2 * Math.PI * i / steps, c = Math.Cos(t), s = Math.Sin(t);
            pts[i] = new Point(cx + a * Math.Sign(c) * Math.Pow(Math.Abs(c), e), cy + b * Math.Sign(s) * Math.Pow(Math.Abs(s), e));
        }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], isFilled: true, isClosed: true);
            ctx.PolyLineTo(pts.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>작업용 Pbgra32 원본 (512 초과면 축소) 과 그 픽셀.</summary>
    private static byte[]? ReadPixels(ref BitmapSource src, out int w, out int h)
    {
        w = h = 0;
        try
        {
            BitmapSource s = src;
            if (s.PixelWidth > 512 || s.PixelHeight > 512)
            {
                double k = 512.0 / Math.Max(s.PixelWidth, s.PixelHeight);
                s = new TransformedBitmap(s, new ScaleTransform(k, k));
            }
            if (s.Format != PixelFormats.Pbgra32) s = new FormatConvertedBitmap(s, PixelFormats.Pbgra32, null, 0);
            w = s.PixelWidth;
            h = s.PixelHeight;
            var buf = new byte[w * h * 4];
            s.CopyPixels(buf, w * 4, 0);
            var copy = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, w * 4);
            copy.Freeze();
            src = copy;
            return buf;
        }
        catch (Exception ex)
        {
            Log.Warn($"아이콘 픽셀 읽기 실패: {ex.Message}");
            return null;
        }
    }
}
