using System.Windows;
using System.Windows.Media;
using Mongdock.Converters;
using Mongdock.Models;

namespace Mongdock.ViewModels;

/// <summary>DockSettings + 테마에서 계산한 독의 크기/색 값 (설정이 바뀔 때마다 새로 만든다).</summary>
public sealed class DockLayout
{
    public DockEdge Edge { get; private init; }
    public DockMode Mode { get; private init; }
    public bool IsVertical => Edge is DockEdge.Left or DockEdge.Right;
    public bool IsLight { get; private init; }
    public bool Blur { get; private init; }
    public bool Wave { get; private init; }
    /// <summary>앱을 켤 때 반응 (튀기 / 점 깜빡임).</summary>
    public LaunchAnimation LaunchAnimation { get; private init; }

    public double IconSize { get; private init; }
    public double Spacing { get; private init; }
    public double HoverScale { get; private init; }
    /// <summary>독 패널 안쪽 여백 (실행 중 점이 가장자리 쪽 여백에 들어감).</summary>
    public double Padding { get; private init; }
    /// <summary>화면 가장자리와 패널 사이 여백.</summary>
    public double EdgeMargin { get; private init; }
    public double CornerRadius { get; private init; }
    public double IndicatorSize { get; private init; }
    public double NotificationSize { get; private init; }

    /// <summary>블러 창 틴트 (Blur=true).</summary>
    public Color Tint { get; private init; }
    /// <summary>블러 없을 때 패널 배경.</summary>
    public Brush SolidBackground { get; private init; } = Brushes.Transparent;
    /// <summary>패널 위 1px 안티앨리어싱 테두리 (region 계단 현상 가림).</summary>
    public Brush Border { get; private init; } = Brushes.Transparent;
    public Brush Indicator { get; private init; } = Brushes.White;
    public Brush Notification { get; private init; } = Brushes.Red;
    public Brush Separator { get; private init; } = Brushes.Gray;
    public Brush LabelBackground { get; private init; } = Brushes.Black;
    public Brush LabelForeground { get; private init; } = Brushes.White;
    public Brush LabelBorder { get; private init; } = Brushes.Transparent;

    /// <summary>패널의 가장자리 방향 두께 (아이콘 + 패딩*2 + 테두리).</summary>
    public double PanelCross => IconSize + Padding * 2 + 2;

    /// <summary>아이콘 창 두께: 패널 + 확대로 튀어나오는 부분 + 여유.</summary>
    public double WindowThickness => Math.Ceiling(EdgeMargin + PanelCross + IconSize * (HoverScale - 1) + 8);

    /// <summary>
    /// 실행 튀기 높이: 아이콘의 약 35%. 아이콘 창 안(확대 여유 + 패딩)에서 잘리지 않게 제한 (확대 1 배면 낮게 튐).
    /// </summary>
    public double BounceHeight => Math.Max(4, Math.Min(IconSize * 0.35, IconSize * (HoverScale - 1) + 8 + Padding - 2));

    /// <summary>확대로 패널이 독 방향으로 늘어날 수 있는 길이 (한쪽).</summary>
    public double GrowthRoom => Math.Ceiling(IconSize * (HoverScale - 1) * (Wave ? 1.7 : 0.6) + 12);

    /// <summary>물결 확대 반경: 커서에서 이 거리 이상 떨어진 아이콘은 확대 안 됨.</summary>
    public double WaveRadius => IconSize * 2.5;

    /// <summary>아이콘이 가장자리 반대쪽으로 커지도록 하는 확대 기준점.</summary>
    public Point ScaleOrigin => Edge switch
    {
        DockEdge.Left => new Point(0, 0.5),
        DockEdge.Bottom => new Point(0.5, 1),
        DockEdge.Top => new Point(0.5, 0),
        _ => new Point(1, 0.5),
    };

    /// <summary>블러 창의 DWM 둥근 모서리 반경 (DWMWCP_ROUND).</summary>
    public const double BlurCornerRadius = 8;

    /// <param name="lightweight">가벼운 모드: 블러·물결 확대·호버 확대를 끔 (PerfMode).</param>
    /// <param name="iconSizeOverride">화면에 다 안 들어갈 때 줄인 아이콘 크기 (DockWindow 자동 맞춤). null 이면 설정값.</param>
    /// <param name="spacingOverride">아이콘을 최소까지 줄여도 넘칠 때 줄인 간격. null 이면 설정값.</param>
    public static DockLayout From(DockSettings s, bool systemLight, bool lightweight = false, double? iconSizeOverride = null,
        double? spacingOverride = null)
    {
        double icon = Clamp(s.IconSize, 16, 256, 52);
        if (iconSizeOverride is double fit && fit >= 16 && fit < icon) icon = Math.Floor(fit);
        bool blur = s.Blur && !lightweight;
        bool light = s.Theme switch
        {
            DockTheme.Light => true,
            DockTheme.Dark => false,
            _ => systemLight,
        };

        // MyDockFinder/맥 느낌: 라이트 = 밝은 유리 + 어두운 점, 다크 = 어두운 유리 + 밝은 점
        // 블러 틴트: 백엔드 실측 권장 범위(라이트 #60F0F0F0~, 다크 #A0201E1E 근처) 안쪽
        Color tint = light ? BrushParser.Hex("#60F2F2F2") : BrushParser.Hex("#70282828");
        Color solid = light ? BrushParser.Hex("#D9EFEFF1") : BrushParser.Hex("#D0262628");
        Color border = light ? BrushParser.Hex("#66FFFFFF") : BrushParser.Hex("#33FFFFFF");
        Color dot = light ? BrushParser.Hex("#CC1E1E1E") : BrushParser.Hex("#E0FFFFFF");
        Color sep = light ? BrushParser.Hex("#40000000") : BrushParser.Hex("#40FFFFFF");

        if (!string.IsNullOrWhiteSpace(s.Background))
        {
            tint = BrushParser.ParseColor(s.Background, tint);
            solid = tint;
        }

        return new DockLayout
        {
            Edge = s.Edge,
            Mode = Services.RoutineTriggers.DockHideActive ? DockMode.AutoHide : s.Mode, // 루틴 "독 자동 숨김" (루틴 데스크톱에 있는 동안만)
            IsLight = light,
            Blur = blur,
            Wave = s.WaveMagnification && !lightweight,
            LaunchAnimation = s.LaunchAnimation,
            IconSize = icon,
            Spacing = spacingOverride is double sp && sp >= 0 ? Math.Min(sp, Clamp(s.IconSpacing, 0, 64, 5)) : Clamp(s.IconSpacing, 0, 64, 5),
            HoverScale = lightweight ? 1 : Clamp(s.HoverScale, 1, 3, 1.36),
            Padding = Math.Round(Math.Max(5, icon * 0.12)),
            EdgeMargin = Clamp(s.Margin, 0, 200, 10),
            // 아크릴은 region 으로 잘리지 않고 DWM 둥근 모서리(약 8px 고정)로만 둥글어짐 → 블러일 땐 8 에 맞춤
            CornerRadius = blur ? BlurCornerRadius : Clamp(s.CornerRadius, 0, 128, 16),
            IndicatorSize = Math.Round(Math.Clamp(icon * 0.085, 4, 6)),
            NotificationSize = Math.Round(Math.Max(8, icon * 0.2)),
            Tint = tint,
            SolidBackground = BrushParser.Frozen(solid),
            Border = BrushParser.Parse(s.BorderColor, border),
            Indicator = BrushParser.Parse(s.IndicatorColor, dot),
            Notification = BrushParser.Parse(s.NotificationColor, BrushParser.Hex("#FFFF453A")),
            Separator = BrushParser.Frozen(sep),
            LabelBackground = BrushParser.Frozen(light ? BrushParser.Hex("#F2F4F4F5") : BrushParser.Hex("#F22C2C2E")),
            LabelForeground = BrushParser.Frozen(light ? BrushParser.Hex("#FF1E1E1E") : BrushParser.Hex("#FFF2F2F2")),
            LabelBorder = BrushParser.Frozen(light ? BrushParser.Hex("#26000000") : BrushParser.Hex("#33FFFFFF")),
        };
    }

    private static double Clamp(double v, double min, double max, double fallback)
        => double.IsNaN(v) || double.IsInfinity(v) ? fallback : Math.Clamp(v, min, max);
}
