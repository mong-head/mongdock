using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 루틴 편집 "지금 화면으로 맞추기" 확인 카드 (지금 화면 저장 카드와 같은 모양): 바로 바꾸지 않고
/// ① 그대로 있는 앱(위치·모니터만 새로 — 목록만) ② 새로 켜진 앱 [추가] 체크된 채 ③ 화면에 없는 앱 [빼기] 체크된 채(풀면 유지)
/// ④ 웹사이트·파일·폴더 항목은 화면으로 확인할 수 없어 늘 그대로 → [취소] [맞추기].
/// </summary>
internal sealed class RoutineSyncWindow : RoutineCardWindow
{
    private static RoutineSyncWindow? _open;

    private readonly List<(RoutineItem Item, CheckBox Check)> _add = new();
    private readonly List<(RoutineItem Item, CheckBox Check)> _remove = new();

    /// <summary>apply(뺄 항목, 넣을 항목) — [맞추기]를 눌렀을 때만.</summary>
    public static void Open(AppServices services, RoutineService.SyncPlan plan, int currentCount, Action<List<RoutineItem>, List<RoutineItem>> apply)
    {
        _open?.Close();
        var w = new RoutineSyncWindow(services, plan, currentCount, apply);
        _open = w;
        w.Closed += (_, _) => { if (_open == w) _open = null; };
        w.Show();
        w.Activate();
    }

    private RoutineSyncWindow(AppServices services, RoutineService.SyncPlan plan, int currentCount, Action<List<RoutineItem>, List<RoutineItem>> apply)
        : base(services, 480, "mongdock Routine Sync")
    {
        Body.Children.Add(Heading(Loc.T("지금 화면으로 맞추기")));
        var list = new StackPanel();
        bool any = false;
        if (plan.Kept.Count > 0)
        {
            any = true;
            list.Children.Add(Section(Loc.T("그대로 있는 앱 — 위치·모니터만 지금 화면으로")));
            foreach (var (item, _) in plan.Kept) list.Children.Add(Row(item, null));
        }
        if (plan.New.Count > 0)
        {
            any = true;
            list.Children.Add(Section(Loc.T("새로 켜진 앱 — 추가")));
            foreach (var (item, hwnd) in plan.New)
            {
                var check = DockMenus.Check(P, null, true);
                _add.Add((item, check));
                list.Children.Add(Row(item, check));
            }
        }
        if (plan.Missing.Count > 0)
        {
            any = true;
            list.Children.Add(Section(Loc.T("지금 화면에 없는 앱 — 빼기 (체크를 풀면 그대로 둬요)")));
            foreach (var item in plan.Missing)
            {
                var check = DockMenus.Check(P, null, true);
                _remove.Add((item, check));
                list.Children.Add(Row(item, check));
            }
        }
        if (!any) list.Children.Add(Muted(Loc.T("이 데스크톱에 루틴에 넣을 창이 없어요."), 13));
        Body.Children.Add(Scroll(list));
        if (plan.Fixed > 0)
        {
            var note = Muted(Loc.T("웹사이트·파일·폴더 항목은 화면에서 확인할 수 없어 그대로 둬요."));
            note.Margin = new Thickness(0, 8, 0, 0);
            Body.Children.Add(note);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = CardButton(Loc.T("취소"));
        cancel.Click += (_, _) => Close();
        var ok = CardButton(Loc.T("맞추기"), primary: true);
        ok.Margin = new Thickness(8, 0, 0, 0);
        ok.IsEnabled = any;
        ok.Click += (_, _) =>
        {
            var remove = _remove.Where(r => r.Check.IsChecked == true).Select(r => r.Item).ToList();
            var add = _add.Where(r => r.Check.IsChecked == true).Select(r => r.Item).ToList();
            int room = RoutineDef.MaxItems - (currentCount - remove.Count);
            if (add.Count > room) add = add.Take(Math.Max(0, room)).ToList();
            Close();
            apply(remove, add);
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        Body.Children.Add(buttons);
    }

    private TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = P.SubText,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 6),
    };

    private UIElement Row(RoutineItem item, CheckBox? check)
    {
        var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        if (check is not null)
        {
            check.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(check, Dock.Left);
            line.Children.Add(check);
            line.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) check.IsChecked = check.IsChecked != true; };
        }
        else line.Margin = new Thickness(26, 0, 0, 6); // 체크 칸 자리만큼 들여
        var icon = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0), Source = RoutineIcons.ItemIcon(Services, item, Services.Settings.Current.Dock.IconStyle) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        line.Children.Add(new TextBlock { Text = RoutineService.ItemName(item), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return line;
    }
}
