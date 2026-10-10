using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// "지금 화면을 루틴으로 저장" 카드 (#24-A, spec-routines §2-①): 지금 데스크톱에 보이는 창을 읽어 모두 체크된 목록으로.
/// 줄마다 앱 아이콘·이름·찾은 열 것·"모니터 2 · 왼쪽 반". 브라우저 줄 아래에는 "열 웹사이트 주소 (선택)" 칸.
/// 이름 칸에 바로 커서, 데스크톱 기본 = 새 데스크톱. [저장] → 루틴 목록에 (독에서 열었으면 독에도 고정).
/// </summary>
internal sealed class RoutineSaveWindow : RoutineCardWindow
{
    private static RoutineSaveWindow? _open;

    private readonly List<(RoutineItem Item, CheckBox Check)> _rows = new();
    private readonly TextBox _name;
    private readonly RoutineDesktop _desktop = new();
    private readonly bool _pinToDock;
    private readonly Button _save;

    /// <summary>시험 그림에서만: 창을 읽는 대신 이 목록.</summary>
    internal static Func<List<RoutineItem>>? CaptureOverride { get; set; }

    public static void Open(AppServices services, bool pinToDock)
    {
        _open?.Close();
        if (!RoutineUi.CanAdd(services))
        {
            ConfirmCardWindow.Ask(services, Loc.T("루틴이 가득 찼어요"), Loc.F($"루틴은 {RoutineDef.MaxRoutines}개까지 만들 수 있어요. 안 쓰는 루틴을 지운 뒤 다시 해 주세요."), Loc.T("확인"), () => { });
            return;
        }
        var w = new RoutineSaveWindow(services, pinToDock, Capture());
        _open = w;
        w.Closed += (_, _) => { if (_open == w) _open = null; };
        w.Show();
        w.Activate();
    }

    internal static List<RoutineItem> Capture()
    {
        if (CaptureOverride is { } fake) return fake();
        try { return RoutineService.CaptureScreen().Select(x => x.Item).ToList(); }
        catch (Exception ex)
        {
            Log.Error("지금 화면 읽기 실패", ex);
            return new List<RoutineItem>();
        }
    }

    internal RoutineSaveWindow(AppServices services, bool pinToDock, List<RoutineItem> items) : base(services, 520, "mongdock Routine Save")
    {
        _pinToDock = pinToDock;
        Body.Children.Add(Heading(Loc.T("지금 화면을 루틴으로 저장")));

        _name = Field(RoutineUi.NextName(services));
        _name.MaxLength = 30;
        Body.Children.Add(LabeledRow(Loc.T("이름"), _name));

        int current = Math.Max(1, VirtualDesktopService.Read().Current);
        Body.Children.Add(LabeledRow(Loc.T("어디서"), Segments(new[] { Loc.T("새 데스크톱 (기본)"), Loc.T("지금 데스크톱"), Loc.F($"데스크톱 {current}") }, 0, i =>
        {
            _desktop.Mode = i switch { 1 => RoutineDesktopMode.Current, 2 => RoutineDesktopMode.Index, _ => RoutineDesktopMode.New };
            _desktop.Index = current;
        }), bottom: 6));

        var list = new StackPanel();
        if (items.Count == 0) list.Children.Add(Muted(Loc.T("저장할 창이 없어요. 루틴에 넣을 앱을 띄운 뒤 다시 눌러 주세요."), 13));
        foreach (var item in items) list.Children.Add(Row(item));
        Body.Children.Add(Scroll(list));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = CardButton(Loc.T("취소"));
        cancel.Click += (_, _) => Close();
        _save = CardButton(Loc.T("저장"), primary: true);
        _save.Margin = new Thickness(8, 0, 0, 0);
        _save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        Body.Children.Add(buttons);
        UpdateSave();

        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter && _save.IsEnabled) { e.Handled = true; Save(); } };
        Loaded += (_, _) =>
        {
            _name.Focus();
            Keyboard.Focus(_name);
            _name.SelectAll();
        };
    }

    private UIElement Row(RoutineItem item)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var line = new DockPanel { LastChildFill = true };
        var check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        check.Checked += (_, _) => UpdateSave();
        check.Unchecked += (_, _) => UpdateSave();
        _rows.Add((item, check));
        DockPanel.SetDock(check, Dock.Left);
        line.Children.Add(check);
        var icon = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0), Source = RoutineIcons.ItemIcon(Services, item, Services.Settings.Current.Dock.IconStyle) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        var pill = Pill(RoutineIcons.PlacementText(item));
        pill.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(pill, Dock.Right);
        line.Children.Add(pill);
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new System.Windows.Documents.Run(RoutineService.ItemName(item)));
        string? open = item.Kind == RoutineItemKind.Path ? item.Target : item.Open;
        if (!string.IsNullOrEmpty(open)) name.Inlines.Add(new System.Windows.Documents.Run("  " + open) { Foreground = P.SubText, FontSize = 11.5 });
        line.Children.Add(name);
        line.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) check.IsChecked = check.IsChecked != true; };
        box.Children.Add(line);
        // 브라우저: 탭 주소는 읽지 않음(못 읽음·개인정보) — 열 주소를 직접 넣는 칸
        if (item.Kind == RoutineItemKind.App && IsBrowser(item))
        {
            var url = Field("");
            url.TextChanged += (_, _) => item.Open = string.IsNullOrWhiteSpace(url.Text) ? null : url.Text.Trim();
            var hinted = Hinted(url, Loc.T("열 웹사이트 주소 (선택)"));
            hinted.Margin = new Thickness(56, 4, 0, 0);
            box.Children.Add(hinted);
        }
        return box;
    }

    private static bool IsBrowser(RoutineItem item)
    {
        string exe = System.IO.Path.GetFileNameWithoutExtension(item.Target).ToLowerInvariant();
        return exe is "chrome" or "msedge" or "whale" or "firefox" or "brave" or "opera" or "vivaldi";
    }

    private void UpdateSave() => _save.IsEnabled = _rows.Any(r => r.Check.IsChecked == true);

    private void Save()
    {
        var items = _rows.Where(r => r.Check.IsChecked == true).Select(r => r.Item).Take(RoutineDef.MaxItems).ToList();
        if (items.Count == 0) return;
        string name = _name.Text.Trim();
        var routine = new RoutineDef
        {
            Name = name.Length > 0 ? name : RoutineUi.NextName(Services),
            Desktop = new RoutineDesktop { Mode = _desktop.Mode, Index = _desktop.Index },
            Items = items,
            FromScreen = true,
        };
        if (!RoutineUi.Add(Services, routine)) return;
        if (_pinToDock) RoutineUi.PinToDock(Services, routine);
        Close();
    }
}
