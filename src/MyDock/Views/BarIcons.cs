using System.Windows;
using System.Windows.Media;

namespace MyDock.Views;

/// <summary>
/// 상단바 상태 아이콘 (맥 SF Symbols / MyDockFinder 처럼 굵은 채움 도형). 모두 Frozen Geometry,
/// Path.Fill = 상단바 글자색으로 칠한다. 좌표는 DIP 그대로 (Stretch=None) — 크기는 20 DIP 바 아이콘 기준.
/// 선 모양(호·룬·손잡이)은 둥근 끝 펜으로 넓힌 윤곽(GetWidenedPathGeometry)이라 두께가 일정하다.
/// </summary>
internal static class BarIcons
{
    // ───────────────────────── 도우미 ─────────────────────────

    private static Geometry Freeze(Geometry g)
    {
        g.Freeze();
        return g;
    }

    private static Pen RoundPen(double thickness) => new(Brushes.Black, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    /// <summary>선(경로)을 일정 두께의 채운 도형으로.</summary>
    private static Geometry Stroke(string data, double thickness)
        => Geometry.Parse(data).GetWidenedPathGeometry(RoundPen(thickness));

    /// <summary>중심 (cx, cy), 반지름 r, 위쪽 방향 기준 ±half 도의 호 (둥근 끝, 두께 t).</summary>
    private static Geometry Arc(double cx, double cy, double r, double halfDeg, double t, bool pointingRight = false)
    {
        double a = halfDeg * Math.PI / 180;
        Point p1, p2;
        if (pointingRight)
        {
            p1 = new Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
            p2 = new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
        else
        {
            p1 = new Point(cx - r * Math.Sin(a), cy - r * Math.Cos(a));
            p2 = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        }
        var fig = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(p2, new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { fig }).GetWidenedPathGeometry(RoundPen(t));
    }

    private static Geometry Union(params Geometry[] parts)
    {
        Geometry acc = parts[0];
        for (int i = 1; i < parts.Length; i++)
            acc = Geometry.Combine(acc, parts[i], GeometryCombineMode.Union, null);
        return acc;
    }

    private static Geometry Rounded(Geometry shape, double radius)
        => Geometry.Combine(shape, shape.GetWidenedPathGeometry(RoundPen(radius)), GeometryCombineMode.Union, null);

    // ───────────────────────── Wi-Fi (맥 wifi: 점 + 굵은 호 2개) ─────────────────────────
    // 상자 21 x 16 (보이는 크기 ≈ 19 x 14, MyDockFinder 실측). 꼭짓점 (10.5, 14.8) 에서 위로 ±45° 부채꼴.

    private const double WifiCx = 10.5, WifiCy = 14.8, WifiHalf = 45, WifiT = 2.2;

    public static readonly Geometry WifiDot = Freeze(new EllipseGeometry(new Point(WifiCx, WifiCy - 1.7), 1.9, 1.9));
    public static readonly Geometry WifiArc1 = Freeze(Arc(WifiCx, WifiCy, 6.7, WifiHalf, WifiT));
    public static readonly Geometry WifiArc2 = Freeze(Arc(WifiCx, WifiCy, 11.3, WifiHalf, WifiT));
    public static readonly Geometry WifiFull = Freeze(Union(WifiDot, WifiArc1, WifiArc2));

    /// <summary>신호 칸 수(1~3)만큼의 도형. 나머지는 흐리게 깔린 WifiFull 이 보여줌.</summary>
    public static Geometry Wifi(int bars) => bars switch
    {
        >= 3 => WifiFull,
        2 => WifiTwo,
        _ => WifiDot,
    };

    private static readonly Geometry WifiTwo = Freeze(Union(WifiDot, WifiArc1));

    /// <summary>유선: 랜선 단자(RJ45) 정면 — 채운 몸체 + 아래 걸쇠 홈.</summary>
    public static readonly Geometry Ethernet = Freeze(new CombinedGeometry(GeometryCombineMode.Exclude,
        new RectangleGeometry(new Rect(3, 1.5, 13, 12), 2.2, 2.2),
        new GeometryGroup
        {
            Children =
            {
                new RectangleGeometry(new Rect(7, 10, 5, 3.6)),       // 걸쇠 홈
                new RectangleGeometry(new Rect(5.6, 4, 1.3, 3.2)),    // 접점
                new RectangleGeometry(new Rect(8.0, 4, 1.3, 3.2)),
                new RectangleGeometry(new Rect(10.4, 4, 1.3, 3.2)),
                new RectangleGeometry(new Rect(12.8, 4, 1.3, 3.2)),
            },
        }));

    // ───────────────────────── 스피커 (speaker.wave.N.fill / speaker.slash.fill) ─────────────────────────
    // 상자 23 x 18. 나팔 오른쪽 끝 x≈10.6, 세로 중심 9.

    private static readonly Geometry SpeakerBody = Rounded(
        Geometry.Parse("M1.6,6.2 L5.1,6.2 L10.1,2.2 L10.1,15.8 L5.1,11.8 L1.6,11.8 Z"), 1.1);

    private static readonly Geometry[] Waves =
    {
        Arc(10.6, 9, 3.8, 42, 1.75, pointingRight: true),
        Arc(10.6, 9, 7.1, 42, 1.75, pointingRight: true),
        Arc(10.6, 9, 10.4, 42, 1.75, pointingRight: true),
    };

    private static readonly Geometry[] SpeakerLevels =
    {
        Freeze(SpeakerBody.Clone()),
        Freeze(Union(SpeakerBody, Waves[0])),
        Freeze(Union(SpeakerBody, Waves[0], Waves[1])),
        Freeze(Union(SpeakerBody, Waves[0], Waves[1], Waves[2])),
    };

    private static readonly Geometry SpeakerMuted = Freeze(MakeMuted());

    private static Geometry MakeMuted()
    {
        const string slash = "M2.4,1.8 L17.4,16.8";
        var withWave = Union(SpeakerBody, Waves[0], Waves[1]);
        // 사선 주변을 비워서(배경색 틈) 사선이 또렷하게
        var cut = Geometry.Combine(withWave, Stroke(slash, 4.4), GeometryCombineMode.Exclude, null);
        return Geometry.Combine(cut, Stroke(slash, 1.75), GeometryCombineMode.Union, null);
    }

    /// <summary>볼륨 0 / 1~33 / 34~66 / 67~100 → 호 0~3개, 음소거면 사선.</summary>
    public static Geometry Speaker(double volume, bool muted)
    {
        if (muted) return SpeakerMuted;
        int level = volume <= 0.005 ? 0 : volume < 0.34 ? 1 : volume < 0.67 ? 2 : 3;
        return SpeakerLevels[level];
    }

    // ───────────────────────── 나머지 (같은 굵기) ─────────────────────────

    /// <summary>블루투스 룬 (상자 12 x 18).</summary>
    public static readonly Geometry Bluetooth = Freeze(Stroke("M1.8,5.0 L10.2,12.6 L6,16.4 L6,1.6 L10.2,5.4 L1.8,13.0", 1.7));

    /// <summary>굵은 돋보기 (상자 18 x 18).</summary>
    public static readonly Geometry Search = Freeze(Union(
        new EllipseGeometry(new Point(7.6, 7.6), 6.0, 6.0).GetWidenedPathGeometry(RoundPen(1.9)),
        Stroke("M12.3,12.3 L16.6,16.6", 2.4)));

    /// <summary>제어센터: 위 토글 = 채움 + 흰(뚫린) 원, 아래 토글 = 외곽선 + 채운 원 (상자 18 x 16).</summary>
    public static readonly Geometry ControlCenter = Freeze(MakeControlCenter());

    private static Geometry MakeControlCenter()
    {
        var top = Geometry.Combine(
            new RectangleGeometry(new Rect(0.5, 0.5, 17, 6.8), 3.4, 3.4),
            new EllipseGeometry(new Point(13.9, 3.9), 2.1, 2.1),
            GeometryCombineMode.Exclude, null);
        var bottomOutline = new RectangleGeometry(new Rect(1.3, 9.5, 15.4, 5.4), 2.7, 2.7)
            .GetWidenedPathGeometry(new Pen(Brushes.Black, 1.6));
        var knob = new EllipseGeometry(new Point(4.3, 12.2), 2.1, 2.1);
        return Union(top, bottomOutline, knob);
    }

    /// <summary>채운 알림 벨 (상자 17 x 17).</summary>
    public static readonly Geometry Bell = Freeze(Union(
        Rounded(Geometry.Parse(
            "M8.5,1.2 C5.4,1.2 3.8,3.6 3.8,6.4 L3.8,9.6 L2,12.4 L15,12.4 L13.2,9.6 L13.2,6.4 C13.2,3.6 11.6,1.2 8.5,1.2 Z"), 0.9),
        Geometry.Parse("M6.4,13.6 A2.1,2.1 0 0 0 10.6,13.6 Z")));
}
