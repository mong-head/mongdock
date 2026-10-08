using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Mongdock.Converters;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 상단바 배터리 (맥 메뉴 막대처럼): [57%] + 배터리 아이콘. 배터리 없는 PC 나 설정에서 끄면 숨김.
/// 채움: 평소 글자색, 방전 중 20% 이하 = 빨강, 절전 모드 = 노랑 (밝은/어두운 바에 맞춘 색). 충전 중 = 번개.
/// </summary>
public partial class TopBarWindow
{
    // 밝은 바(어두운 글자) / 어두운 바(밝은 글자) — 맥 systemRed / systemYellow 계열. 노랑은 흰 바에서 보이게 조금 진하게.
    private static readonly Brush LowOnLight = BrushParser.Frozen(BrushParser.Hex("#FFFF3B30"));
    private static readonly Brush LowOnDark = BrushParser.Frozen(BrushParser.Hex("#FFFF453A"));
    private static readonly Brush SaverOnLight = BrushParser.Frozen(BrushParser.Hex("#FFE0A500"));
    private static readonly Brush SaverOnDark = BrushParser.Frozen(BrushParser.Hex("#FFFFD60A"));

    /// <summary>0 = 글자색 바인딩, 1 = 빨강, 2 = 노랑 (같은 상태면 바인딩을 다시 걸지 않음).</summary>
    private int _batteryTint = -1;
    private bool _batteryTintOnDark;

    private void UpdateBattery()
    {
        var s = _services.Settings.Current.TopBar;
        BatteryInfo? b;
        try { b = _services.Status.Battery; }
        catch { b = null; }

        var vis = b != null && s.ShowBattery ? Visibility.Visible : Visibility.Collapsed;
        if (BatteryButton.Visibility != vis)
        {
            BatteryButton.Visibility = vis;
            if (vis != Visibility.Visible && _panel is { Kind: StatusPanelKind.Battery, IsClosing: false }) _panel.Close();
            Remeasure(RightSection);
        }
        if (b == null || !s.ShowBattery) return;

        bool charging = b.Charge == BatteryCharge.Charging;
        BatteryLevel.Data = BarIcons.BatteryLevel(b.Percent, charging);
        BatteryBolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;

        int tint = b.Saver ? 2 : b.Charge == BatteryCharge.Discharging && b.Percent <= 20 ? 1 : 0;
        bool onDark = BrushParser.Luminance(_rightText) > 0.45; // 밝은 글자 = 어두운 바
        if (tint != _batteryTint || onDark != _batteryTintOnDark)
        {
            _batteryTint = tint;
            _batteryTintOnDark = onDark;
            if (tint == 0)
                BatteryLevel.SetBinding(Shape.FillProperty, new Binding(nameof(Foreground))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Button), 1),
                });
            else
                BatteryLevel.Fill = tint == 1 ? (onDark ? LowOnDark : LowOnLight) : (onDark ? SaverOnDark : SaverOnLight);
        }

        BatteryPercent.Visibility = s.ShowBatteryPercent ? Visibility.Visible : Visibility.Collapsed;
        string text = b.Percent.ToString(CultureInfo.InvariantCulture) + "%";
        if (BatteryPercent.Text != text) BatteryPercent.Text = text;
    }

    /// <summary>
    /// 퍼센트 글자 최소 폭 = 두 자리("88%") 너비, 오른쪽 정렬 (숫자는 tabular 폭이라 두 자리끼리는 흔들리지 않음).
    /// 예전처럼 "100%" 폭으로 고정하면 57% 일 때 왼쪽이 크게 비어 보였음 — 100% 일 때만 살짝 넓어짐.
    /// </summary>
    private void FixBatteryPercentWidth()
    {
        var t = BatteryPercent;
        var typeface = new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double max = 0;
        foreach (var sample in new[] { "88%", "00%" })
        {
            var ft = new FormattedText(sample, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, Brushes.Black, dpi);
            max = Math.Max(max, ft.WidthIncludingTrailingWhitespace);
        }
        t.MinWidth = Math.Ceiling(max);
    }
}
