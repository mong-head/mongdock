using System.Windows;
using System.Windows.Controls;
using Mongdock.Models;

namespace Mongdock.Views;

/// <summary>
/// 설정 → 상단바 → "오른쪽 아이콘 순서": 항목마다 ▲(왼쪽으로)·▼(오른쪽으로) + "기본 순서로". 클릭만으로 (원격 사용 고려).
/// 상단바에서 아이콘을 길게 눌러 끌어도 같은 값(TopBar.RightItemsOrder)이 바뀐다 (TopBarWindow.Reorder.cs).
/// </summary>
internal sealed partial class SettingsWindow
{
    private void AddRightOrder(Panel body)
    {
        TopBarSettings T() => _services.Settings.Current.TopBar;
        body.Children.Add(SectionTitle("오른쪽 아이콘 순서"));
        var order = TopBarRightOrder.Resolve(T().RightItemsOrder);
        var rows = new List<UIElement>();
        for (int i = 0; i < order.Count; i++)
        {
            string key = order[i];
            string? sub = i == 0 ? "맨 왼쪽" : null;
            if (!IsRightItemShown(T(), key)) sub = sub == null ? "지금은 꺼져 있음" : sub + " · 지금은 꺼져 있음";
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(SmallButton("▲", "왼쪽으로", i > 0,
                () => Commit(() => T().RightItemsOrder = TopBarRightOrder.Nudge(T().RightItemsOrder, key, -1), rebuild: true)));
            buttons.Children.Add(SmallButton("▼", "오른쪽으로", i < order.Count - 1,
                () => Commit(() => T().RightItemsOrder = TopBarRightOrder.Nudge(T().RightItemsOrder, key, +1), rebuild: true)));
            rows.Add(Row(TopBarRightOrder.Label(key), sub, buttons));
        }
        rows.Add(Row("시계", "항상 맨 오른쪽", new Border()));
        var reset = ActionButton("기본 순서로", () => Commit(() => T().RightItemsOrder = new List<string>(), rebuild: true));
        reset.IsEnabled = !TopBarRightOrder.IsDefault(T().RightItemsOrder);
        reset.Opacity = reset.IsEnabled ? 1 : 0.35;
        rows.Add(Row("기본 순서로 되돌리기", "상단바에서 아이콘을 0.4초쯤 길게 누른 채 좌우로 끌어도 순서를 바꿀 수 있어요. 끄는 중 오른쪽 클릭 = 취소.", reset));
        body.Children.Add(Group(rows.ToArray()));
    }

    private static bool IsRightItemShown(TopBarSettings t, string key) => key switch
    {
        TopBarRightOrder.Desktops => t.ShowDesktopButtons,
        TopBarRightOrder.NetSpeed => t.ShowNetworkSpeed,
        TopBarRightOrder.Tray => t.ShowTrayIcons,
        TopBarRightOrder.Bluetooth or TopBarRightOrder.Wifi or TopBarRightOrder.Volume => t.ShowStatusIcons,
        TopBarRightOrder.Search or TopBarRightOrder.ControlCenter => t.ShowQuickButtons,
        TopBarRightOrder.Ime => t.ShowImeToggle,
        TopBarRightOrder.Battery => t.ShowBattery,
        _ => true,
    };
}
