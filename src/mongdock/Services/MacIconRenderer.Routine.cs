using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mongdock.Services;

internal static partial class MacIconRenderer
{
    /// <summary>
    /// 루틴 자동 아이콘 (#24-A): 몽독 하늘색~연보라 판 위에 담긴 항목 아이콘 최대 4개를 2x2로 (앱 모음 아이콘과 같은 결).
    /// 4개보다 적으면 남은 칸은 옅은 흰 칸. 항목 아이콘은 256 캔버스(여백 포함)라 본체가 칸에 맞게 조금 크게 그림.
    /// </summary>
    public static BitmapSource RoutineGrid(IReadOnlyList<ImageSource?> icons) => Render(dc =>
    {
        dc.DrawGeometry(new LinearGradientBrush(SkyTop, SkyBottom, 75), new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0, 0, 0)), 0.5), Squircle);
        var r = BodyRect;
        double grid = r.Width * 0.70, gap = grid * 0.08, tile = (grid - gap) / 2, radius = tile * 0.24;
        double x0 = r.X + (r.Width - grid) / 2, y0 = r.Y + (r.Height - grid) / 2;
        var empty = new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
        double grow = Canvas / BodySize; // 항목 아이콘의 투명 여백만큼
        for (int i = 0; i < 4; i++)
        {
            double x = x0 + (i % 2) * (tile + gap), y = y0 + (i / 2) * (tile + gap);
            if (i < icons.Count && icons[i] is { } icon)
            {
                double s = tile * grow;
                dc.DrawImage(icon, new Rect(x + (tile - s) / 2, y + (tile - s) / 2, s, s));
            }
            else dc.DrawRoundedRectangle(empty, null, new Rect(x, y, tile, tile), radius, radius);
        }
    });
}
