using System.Windows;
using System.Windows.Media;
using MyDock.Converters;
using MyDock.Models;

namespace MyDock.ViewModels;

/// <summary>DockSettings 에서 계산한 독의 크기/색 값 (설정이 바뀔 때마다 새로 만든다).</summary>
public sealed class DockLayout
{
    public DockEdge Edge { get; private init; }
    public bool IsVertical => Edge is DockEdge.Left or DockEdge.Right;
    public double IconSize { get; private init; }
    public double Spacing { get; private init; }
    public double HoverScale { get; private init; }
    /// <summary>독 패널 안쪽 여백 (실행 중 표시 점이 이 안에 들어감).</summary>
    public double Padding { get; private init; }
    /// <summary>화면 가장자리와 패널 사이 여백.</summary>
    public double EdgeMargin { get; private init; }
    public double CornerRadius { get; private init; }
    public double IndicatorSize { get; private init; }
    public double NotificationSize { get; private init; }

    public Brush Background { get; private init; } = Brushes.Transparent;
    public Brush Border { get; private init; } = Brushes.Transparent;
    public Brush Indicator { get; private init; } = Brushes.White;
    public Brush Notification { get; private init; } = Brushes.Red;

    /// <summary>패널의 가장자리 방향 두께 (아이콘 + 패딩*2 + 테두리).</summary>
    public double PanelCross => IconSize + Padding * 2 + 2;

    /// <summary>창 두께(DIP) = 확대된 아이콘 + 패딩*2 + 가장자리 여백 (+ 테두리 1px*2).</summary>
    public double Thickness => Math.Ceiling(IconSize * HoverScale + Padding * 2 + EdgeMargin + 2);

    /// <summary>아이콘이 가장자리 반대쪽으로 커지도록 하는 확대 기준점.</summary>
    public Point ScaleOrigin => Edge switch
    {
        DockEdge.Left => new Point(0, 0.5),
        DockEdge.Bottom => new Point(0.5, 1),
        DockEdge.Top => new Point(0.5, 0),
        _ => new Point(1, 0.5),
    };

    public static DockLayout From(DockSettings s)
    {
        double icon = Clamp(s.IconSize, 16, 256, 52);
        return new DockLayout
        {
            Edge = s.Edge,
            IconSize = icon,
            Spacing = Clamp(s.IconSpacing, 0, 64, 5),
            HoverScale = Clamp(s.HoverScale, 1, 3, 1.36),
            Padding = Math.Round(Math.Max(6, icon * 0.16)),
            EdgeMargin = Clamp(s.Margin, 0, 200, 4),
            CornerRadius = Clamp(s.CornerRadius, 0, 128, 16),
            IndicatorSize = Math.Round(Math.Max(4, icon * 0.09)),
            NotificationSize = Math.Round(Math.Max(8, icon * 0.2)),
            Background = ColorBrushConverter.Parse(s.Background, Color.FromArgb(0xB0, 0x20, 0x20, 0x24)),
            Border = ColorBrushConverter.Parse(s.BorderColor, Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            Indicator = ColorBrushConverter.Parse(s.IndicatorColor, Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
            Notification = ColorBrushConverter.Parse(s.NotificationColor, Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A)),
        };
    }

    private static double Clamp(double v, double min, double max, double fallback)
        => double.IsNaN(v) || double.IsInfinity(v) ? fallback : Math.Clamp(v, min, max);
}
