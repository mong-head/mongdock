using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 카메라·마이크 점을 눌렀을 때 카드: "카메라 사용 중: Zoom", "마이크 사용 중: Discord" 목록 + 개인 정보 설정 링크.
/// 위치는 맥처럼 표시하지 않음. 열린 동안 사용 목록이 바뀌면 바로 다시 그림.
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private UIElement BuildPrivacy()
    {
        var svc = PrivacyUsageService.Shared;
        var root = new StackPanel();
        root.Children.Add(HeaderRow(Loc.T("카메라·마이크"), null));
        root.Children.Add(Divider());
        var rows = new StackPanel();
        root.Children.Add(rows);
        root.Children.Add(Divider());
        root.Children.Add(LinkRow(Loc.T("카메라 개인 정보 설정…"), () => PrivacyUsageService.OpenSettings(PrivacyCapability.Camera)));
        root.Children.Add(LinkRow(Loc.T("마이크 개인 정보 설정…"), () => PrivacyUsageService.OpenSettings(PrivacyCapability.Microphone)));

        string signature = "\0";
        _refreshers.Add(() =>
        {
            var list = svc.Active;
            string sig = string.Join("|", list.Select(u => $"{u.Capability}:{u.Name}"));
            if (sig == signature) return;
            signature = sig;
            FillPrivacyRows(rows, list, _p);
        });
        EventHandler changed = (_, _) => RefreshAll();
        svc.Changed += changed;
        Closed += (_, _) => svc.Changed -= changed;
        return root;
    }

    /// <summary>사용 중 목록 → 행들 (색 점 + "카메라 사용 중: 앱"). 위치는 뺌. 비었으면 안내 한 줄.</summary>
    internal static void FillPrivacyRows(Panel rows, IReadOnlyList<PrivacyUsage> list, UiPalette p)
    {
        rows.Children.Clear();
        var shown = list.Where(u => u.Capability is PrivacyCapability.Camera or PrivacyCapability.Microphone).ToList();
        if (shown.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = Loc.T("카메라나 마이크를 쓰는 앱이 없어요."),
                FontSize = 14,
                Foreground = p.SubText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
            });
            return;
        }
        foreach (var u in shown)
        {
            bool camera = u.Capability == PrivacyCapability.Camera;
            var row = new DockPanel { MinHeight = 26, Margin = new Thickness(0, 1, 0, 1) };
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = camera ? TopBarWindow.PrivacyCameraBrush : TopBarWindow.PrivacyMicBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 0, 10, 0),
            };
            row.Children.Add(dot);
            var text = new TextBlock
            {
                FontSize = 14.5,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = p.Text,
            };
            text.Inlines.Add(new Run(camera ? Loc.T("카메라 사용 중: ") : Loc.T("마이크 사용 중: ")) { Foreground = p.SubText });
            text.Inlines.Add(new Run(u.Name) { FontWeight = FontWeights.SemiBold });
            text.ToolTip = u.Packaged ? u.Name : u.Key;
            row.Children.Add(text);
            rows.Children.Add(row);
        }
    }
}
