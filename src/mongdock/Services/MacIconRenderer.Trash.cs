using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Mongdock.Services;

/// <summary>
/// 독 휴지통 — 판(둥근 사각 배경) 없이 통 자체가 아이콘 전체 크기를 차지하는 단독 물체 (맥 휴지통처럼).
/// 시안 A = 반투명 흰~연보라 유리/메시 통, B = 단색 흰 통 + 연보라 세로 줄무늬. 빈 것 / 찬 것(위로 종이 몇 장).
/// 라이트·다크 독 모두에서 보이게 테두리는 중간 톤 연보라 + 바깥 흰 선, 아래 옅은 그림자.
/// </summary>
internal static partial class MacIconRenderer
{
    private static readonly Color TrashRim = Color.FromRgb(0x8C, 0x7C, 0xE6), TrashLav = Color.FromRgb(0xC6, 0xB6, 0xFF);

    /// <summary>판 없는 휴지통. glass = 시안 A(유리·메시), 아니면 시안 B(흰 통 + 줄무늬).</summary>
    public static BitmapSource TrashStandalone(bool full, bool glass)
    {
        const int S = Canvas;
        var root = new ContainerVisual();

        // 아래 옅은 그림자 (바닥에 놓인 느낌)
        var shadow = new DrawingVisual { Effect = new BlurEffect { Radius = 10, KernelType = KernelType.Gaussian } };
        using (var dc = shadow.RenderOpen())
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, 0x20, 0x18, 0x50)), null, new Point(S * 0.5, S * 0.965), S * 0.32, S * 0.03);
        root.Children.Add(shadow);

        var body = new DrawingVisual();
        RenderOptions.SetEdgeMode(body, EdgeMode.Unspecified);
        using (var dc = body.RenderOpen())
        {
            double cx = S * 0.5, top = S * 0.17, bottom = S * 0.95, topHalf = S * 0.375, botHalf = S * 0.30, r = S * 0.07;
            var shape = new StreamGeometry();
            using (var g = shape.Open())
            {
                g.BeginFigure(new Point(cx - topHalf, top), true, true);
                g.LineTo(new Point(cx + topHalf, top), true, true);
                g.LineTo(new Point(cx + botHalf, bottom - r), true, true);
                g.ArcTo(new Point(cx + botHalf - r, bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                g.LineTo(new Point(cx - botHalf + r, bottom), true, true);
                g.ArcTo(new Point(cx - botHalf, bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            }
            shape.Freeze();

            // 찬 것: 통 안·위로 종이 (유리 통이면 안쪽이 비쳐 보임)
            if (full)
            {
                foreach (var (angle, dx, lift, w, h) in new[] { (-16.0, -0.10, 0.10, 0.20, 0.26), (12.0, 0.09, 0.14, 0.21, 0.27), (-3.0, 0.0, 0.18, 0.19, 0.25) })
                {
                    double pw = S * w, ph = S * h, px = cx + S * dx - pw / 2, py = top - S * lift;
                    dc.PushTransform(new RotateTransform(angle, px + pw / 2, py + ph));
                    dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0x90, TrashRim.R, TrashRim.G, TrashRim.B)), S * 0.008), new Rect(px, py, pw, ph), S * 0.015, S * 0.015);
                    var line = new Pen(new SolidColorBrush(Color.FromArgb(0x80, TrashRim.R, TrashRim.G, TrashRim.B)), S * 0.012) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    for (int i = 0; i < 3; i++)
                        dc.DrawLine(line, new Point(px + pw * 0.18, py + ph * (0.22 + i * 0.17)), new Point(px + pw * (i == 2 ? 0.55 : 0.82), py + ph * (0.22 + i * 0.17)));
                    dc.Pop();
                }
            }

            if (glass)
            {
                // 시안 A: 반투명 흰~연보라 유리 + 메시(대각선 격자)
                dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF), Color.FromArgb(0xB0, 0xD8, 0xCE, 0xFF), 90), null, shape);
                dc.PushClip(shape);
                var mesh = new Pen(new SolidColorBrush(Color.FromArgb(0x55, TrashRim.R, TrashRim.G, TrashRim.B)), S * 0.008);
                for (double x = -S; x < S * 2; x += S * 0.055)
                {
                    dc.DrawLine(mesh, new Point(x, top), new Point(x + S * 0.62, bottom));
                    dc.DrawLine(mesh, new Point(x, bottom), new Point(x + S * 0.62, top));
                }
                // 왼쪽 위 반사광
                dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0),
                    null, new RectangleGeometry(new Rect(cx - topHalf, top, topHalf * 0.55, bottom - top)));
                dc.Pop();
            }
            else
            {
                // 시안 B: 단색 흰 통 + 연보라 세로 줄무늬
                dc.DrawGeometry(new LinearGradientBrush(Colors.White, Color.FromRgb(0xF1, 0xEE, 0xFF), 90), null, shape);
                var stripe = new Pen(new SolidColorBrush(TrashLav), S * 0.032) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                foreach (double t in new[] { -0.13, 0.0, 0.13 })
                    dc.DrawLine(stripe, new Point(cx + S * t, top + S * 0.09), new Point(cx + S * t * 0.82, bottom - S * 0.07));
            }
            // 테두리: 바깥 흰 선(다크 독에서 보이게) + 중간 톤 연보라 선(라이트 독에서 보이게)
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)), S * 0.024) { LineJoin = PenLineJoin.Round }, shape);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(TrashRim), S * 0.012) { LineJoin = PenLineJoin.Round }, shape);

            // 위 가장자리 테두리 (통 입구)
            double rimHalf = S * 0.41, rimH = S * 0.055, rimY = top - rimH * 0.55;
            var rim = new Rect(cx - rimHalf, rimY, rimHalf * 2, rimH);
            dc.DrawRoundedRectangle(new LinearGradientBrush(Colors.White, Color.FromRgb(0xE8, 0xE3, 0xFF), 90),
                new Pen(new SolidColorBrush(TrashRim), S * 0.012), rim, rimH / 2, rimH / 2);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), S * 0.006),
                new Rect(rim.X - S * 0.008, rim.Y - S * 0.008, rim.Width + S * 0.016, rim.Height + S * 0.016), rimH / 2 + S * 0.008, rimH / 2 + S * 0.008);
        }
        root.Children.Add(body);

        var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }
}
