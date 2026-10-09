using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>배터리 카드 (맥 메뉴 막대 배터리처럼): 퍼센트, 전원 원천, 남은 시간, 절전 모드, 전원 모드, 설정 링크.</summary>
internal sealed partial class StatusPanelWindow
{
    private UIElement BuildBattery()
    {
        var st = _services.Status;
        var root = new StackPanel();

        var percent = new TextBlock
        {
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        root.Children.Add(HeaderRow(Loc.T("배터리"), percent));

        var source = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        root.Children.Add(source);
        var time = Sub("", 13);
        time.Margin = new Thickness(0, 2, 0, 0);
        root.Children.Add(time);
        root.Children.Add(Divider());

        // 절전 모드: 공식 토글 API 가 없어 표시만 (바꾸기는 아래 "배터리 설정…")
        var saverRow = new DockPanel { LastChildFill = false, MinHeight = 24 };
        saverRow.Children.Add(new TextBlock { Text = Loc.T("절전 모드"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        var saverState = Sub("", 14);
        DockPanel.SetDock(saverState, Dock.Right);
        saverRow.Children.Add(saverState);
        root.Children.Add(saverRow);
        root.Children.Add(Divider());
        root.Children.Add(BuildPowerModeSection()); // 전원 모드 세그먼트 + 구분선 (StatusPanelWindow.Laptop.cs, 사용 불가면 접힘)

        root.Children.Add(LinkRow(Loc.T("배터리 설정…"), () => st.OpenBatterySettings()));
        root.Children.Add(LinkRow(Loc.T("전원 설정…"), () => st.OpenPowerSettings()));

        _refreshers.Add(() =>
        {
            var b = st.Battery;
            if (b == null)
            {
                percent.Text = "";
                source.Text = Loc.T("배터리를 찾을 수 없어요.");
                time.Visibility = Visibility.Collapsed;
                saverState.Text = "";
                return;
            }
            percent.Text = b.Percent.ToString(CultureInfo.InvariantCulture) + "%";
            source.Text = b.Charge switch
            {
                BatteryCharge.Charging => Loc.T("전원 어댑터 연결됨 · 충전 중"),
                BatteryCharge.Full => Loc.T("완전 충전됨"),
                BatteryCharge.NotCharging => Loc.T("전원 어댑터 연결됨 · 충전 안 함"),
                _ => Loc.T("배터리 사용 중"),
            };
            string? t = b.Charge switch
            {
                BatteryCharge.Discharging when b.TimeToEmpty is { } e => Loc.F($"약 {FormatDuration(e)} 남음"),
                BatteryCharge.Charging when b.TimeToFull is { } f => Loc.F($"완전 충전까지 약 {FormatDuration(f)}"),
                _ => null,
            };
            time.Text = t ?? "";
            time.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
            saverState.Text = b.Saver ? Loc.T("켜짐") : Loc.T("꺼짐");
        });
        return root;
    }

    /// <summary>3시간 20분 / 3시간 / 45분.</summary>
    private static string FormatDuration(TimeSpan t)
    {
        int total = Math.Max(1, (int)Math.Round(t.TotalMinutes));
        int h = total / 60, m = total % 60;
        if (h == 0) return Loc.F($"{m}분");
        return m == 0 ? Loc.F($"{h}시간") : Loc.F($"{h}시간 {m}분");
    }
}
