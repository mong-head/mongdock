using System.Windows;
using System.Windows.Controls;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 설정 → 상단바 → "표시할 항목" (#21: 예전 "표시할 항목" 토글 + "오른쪽 아이콘 순서" 를 한 목록으로).
/// 왼쪽 항목(앱 이름·메뉴)은 스위치만, 오른쪽 항목은 행마다 스위치 + ▲(왼쪽으로)·▼(오른쪽으로). 클릭만으로 (원격 사용 고려).
/// 상단바에서 아이콘을 길게 눌러 끌어도 같은 값(TopBar.RightItemsOrder)이 바뀐다 (TopBarWindow.Reorder.cs).
/// 같은 설정을 쓰는 항목(Wi-Fi·블루투스·소리, 검색·제어 센터)은 스위치가 함께 움직인다.
/// </summary>
internal sealed partial class SettingsWindow
{
    private void AddTopBarItems(Panel body)
    {
        TopBarSettings T() => _services.Settings.Current.TopBar;
        var t = T();
        body.Children.Add(SectionTitle(Loc.T("표시할 항목")));
        var rows = new List<UIElement>
        {
            // 앱 이름과 앱 메뉴는 한 토글 (#21)
            Row(Loc.T("앱 이름과 메뉴"), Loc.T("지금 앱 이름과 그 옆 파일·편집·보기… (맥 메뉴 막대처럼)"),
                Toggle(t.ShowActiveAppName && t.ShowAppMenus, on => Commit(() => { T().ShowActiveAppName = on; T().ShowAppMenus = on; }))),
        };

        bool battery = DeviceInfo.HasBattery, ime = DeviceInfo.ImeToggleUseful;
        var order = TopBarRightOrder.Resolve(t.RightItemsOrder);
        // 기기에 없는 항목(배터리·한/영)은 목록에서 뺌 — 순서는 전체 목록 기준으로 움직임
        var shown = order.Where(k => (k != TopBarRightOrder.Battery || battery) && (k != TopBarRightOrder.Ime || ime)).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            string key = shown[i];
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(SmallButton("▲", Loc.T("왼쪽으로"), i > 0,
                () => Commit(() => T().RightItemsOrder = TopBarRightOrder.Nudge(T().RightItemsOrder, key, -1), rebuild: true)));
            controls.Children.Add(SmallButton("▼", Loc.T("오른쪽으로"), i < shown.Count - 1,
                () => Commit(() => T().RightItemsOrder = TopBarRightOrder.Nudge(T().RightItemsOrder, key, +1), rebuild: true)));
            var toggle = Toggle(IsRightItemShown(t, key), on => Commit(() => SetRightItemShown(T(), key, on), rebuild: true));
            toggle.Margin = new Thickness(10, 0, 0, 0);
            controls.Children.Add(toggle);
            rows.Add(Row(ItemLabel(key), ItemNote(key, battery), controls));
        }
        rows.Add(Row(Loc.T("시계"), Loc.T("항상 맨 오른쪽"), new Border()));
        var reset = ActionButton(Loc.T("기본 순서로"), () => Commit(() => T().RightItemsOrder = new List<string>(), rebuild: true));
        reset.IsEnabled = !TopBarRightOrder.IsDefault(t.RightItemsOrder);
        reset.Opacity = reset.IsEnabled ? 1 : 0.35;
        rows.Add(Row(Loc.T("순서 되돌리기"), Loc.T("상단바에서 아이콘을 0.4초쯤 길게 누른 채 좌우로 끌어도 순서를 바꿀 수 있어요."), reset));
        body.Children.Add(Group(rows.ToArray()));
    }

    /// <summary>목록에 보일 쉬운 이름 (#21 문구).</summary>
    private static string ItemLabel(string key) => key switch
    {
        TopBarRightOrder.Tray => Loc.T("다른 앱 아이콘 (카카오톡 등)"),
        TopBarRightOrder.Volume => Loc.T("소리"),
        TopBarRightOrder.Search => Loc.T("검색 버튼"),
        TopBarRightOrder.ControlCenter => Loc.T("제어 센터 버튼"),
        TopBarRightOrder.Ime => Loc.T("한/영 전환 버튼"),
        _ => TopBarRightOrder.Label(key),
    };

    private static string? ItemNote(string key, bool battery) => key switch
    {
        TopBarRightOrder.Bluetooth or TopBarRightOrder.Wifi or TopBarRightOrder.Volume => Loc.T("Wi-Fi·블루투스·소리는 함께 켜고 꺼져요"),
        TopBarRightOrder.Search or TopBarRightOrder.ControlCenter => Loc.T("검색·제어 센터 버튼은 함께 켜고 꺼져요"),
        TopBarRightOrder.Privacy => Loc.T("앱이 카메라를 쓰면 초록 점, 마이크만 쓰면 주황 점"),
        TopBarRightOrder.Desktops => Loc.T("누르면 데스크톱 보기·새 데스크톱"),
        _ => null,
    };

    private static bool IsRightItemShown(TopBarSettings t, string key) => key switch
    {
        TopBarRightOrder.Desktops => t.ShowDesktopButtons,
        TopBarRightOrder.NetSpeed => t.ShowNetworkSpeed,
        TopBarRightOrder.Tray => t.ShowTrayIcons,
        TopBarRightOrder.Bluetooth or TopBarRightOrder.Wifi or TopBarRightOrder.Volume => t.ShowStatusIcons,
        TopBarRightOrder.Search or TopBarRightOrder.ControlCenter => t.ShowQuickButtons,
        TopBarRightOrder.Ime => t.ShowImeToggle,
        TopBarRightOrder.Battery => t.ShowBattery,
        TopBarRightOrder.Privacy => t.ShowPrivacyIndicator,
        _ => true,
    };

    private static void SetRightItemShown(TopBarSettings t, string key, bool on)
    {
        switch (key)
        {
            case TopBarRightOrder.Desktops: t.ShowDesktopButtons = on; break;
            case TopBarRightOrder.NetSpeed: t.ShowNetworkSpeed = on; break;
            case TopBarRightOrder.Tray: t.SetShowTrayIconsByUser(on); break;
            case TopBarRightOrder.Bluetooth or TopBarRightOrder.Wifi or TopBarRightOrder.Volume: t.ShowStatusIcons = on; break;
            case TopBarRightOrder.Search or TopBarRightOrder.ControlCenter: t.ShowQuickButtons = on; break;
            case TopBarRightOrder.Ime: t.ShowImeToggle = on; break;
            case TopBarRightOrder.Battery: t.ShowBattery = on; break;
            case TopBarRightOrder.Privacy: t.ShowPrivacyIndicator = on; break;
        }
    }
}
