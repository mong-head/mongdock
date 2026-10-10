using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 휴지통 (#24-C): 다른 핀처럼 어디든 옮길 수 있음 (켤 때만 맨 끝에). 빔/참 그림, 이름 말풍선 "휴지통 · 파일 12개 (340MB)",
/// 클릭 = 최근 버린 20개 판, 파일을 끌어 놓으면 휴지통으로(되돌릴 수 있게), 오른쪽 클릭 = 열기 / 비우기… / 아이콘 바꾸기… / 독에서 빼기.
/// </summary>
public partial class DockWindow
{
    private RecycleBinWatcher? _recycle;

    internal static bool IsTrash(PinItem? pin) =>
        pin is { Kind: PinKind.Special } && pin.Target.Equals(DefaultPins.RecycleBinTarget, StringComparison.OrdinalIgnoreCase);

    private DockItemViewModel TrashItem(PinItem pin, Dictionary<string, DockItemViewModel> old)
    {
        _recycle ??= CreateRecycleWatcher();
        bool full = _recycle.Count > 0;
        string id = $"trash:{full}:{IconKey(pin.Icon)}";
        var vm = old.GetValueOrDefault(id);
        if (vm == null || !ReferenceEquals(vm.Pin, pin))
            vm = new DockItemViewModel(id, pin, false, Loc.T("휴지통"), SafeIcon(() => TrashIcon(pin.Icon, full)));
        vm.Windows = Array.Empty<AppWindowInfo>();
        vm.Name = _recycle.Known ? RecycleBinPanel.Summary(_recycle.Count, _recycle.Bytes) : Loc.T("휴지통");
        return vm;
    }

    /// <summary>자동 = 판 없는 흰 통 + 연보라 줄무늬 (빈/찬, 2026-10-10 사용자 결정 B). 색만 고르면 그 색 판 위 휴지통.</summary>
    private static ImageSource TrashIcon(PinIcon? icon, bool full) =>
        PinIconRenderer.Render(icon, c => MacIconRenderer.Trash(full, c), $"trash:{full}") ?? (full ? FullTrash : EmptyTrash);

    private static readonly ImageSource EmptyTrash = MacIconRenderer.TrashStandalone(full: false, glass: false);
    private static readonly ImageSource FullTrash = MacIconRenderer.TrashStandalone(full: true, glass: false);

    private RecycleBinWatcher CreateRecycleWatcher()
    {
        var w = new RecycleBinWatcher();
        w.Changed += () => { if (!_closed) RefreshItems(); };
        return w;
    }

    private void ToggleTrashPanel(DockItemView? view, PinItem pin)
    {
        bool same = _folderPanel != null && ReferenceEquals(_folderPanelPin, pin);
        _folderPanel?.CloseAnimated();
        if (same || view == null) return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var a = toDip.Transform(view.PointToScreen(new Point(0, 0)));
        var b = toDip.Transform(view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight)));
        var panel = new RecycleBinPanel(_services, UiTheme.Palette(_services.Settings.Current), new Rect(a, b), _layout.Edge, _monitor,
            _recycle?.Count ?? 0, _recycle?.Bytes ?? 0);
        panel.EmptyRequested += AskEmptyTrash;
        panel.EditIconRequested += () => EditTrashIcon(pin);
        panel.Closed += (_, _) =>
        {
            if (_folderPanel != panel) return;
            _folderPanel = null;
            _folderPanelPin = null;
            _lastInsideTicks = Environment.TickCount64;
        };
        _folderPanel = panel;
        _label?.Hide(); // 판이 열린 동안 이름표는 숨김 (판 제목 위에 겹쳐 보임 — QA)
        _folderPanelPin = pin;
        _recycle?.CheckSoon();
        NewBadges.Used(NewBadges.RecycleBin);
        panel.Show();
    }

    /// <summary>비우기: 몽독 확인 카드 → 확인하면 백그라운드에서 (윈도우 확인 창·소리 없이).</summary>
    private void AskEmptyTrash()
    {
        long n = _recycle?.Count ?? 0, bytes = _recycle?.Bytes ?? 0;
        if (n <= 0) return;
        ConfirmCardWindow.Ask(_services, Loc.T("휴지통 비우기"),
            Loc.F($"파일 {n}개({RecycleBin.FormatSize(bytes)})를 완전히 지울까요? 되돌릴 수 없어요."), Loc.T("비우기"),
            RecycleBin.EmptyInBackground);
    }

    private void BuildTrashMenu(ContextMenu menu, PinItem pin)
    {
        menu.Items.Add(Item(Loc.T("휴지통 열기"), RecycleBin.OpenInExplorer));
        menu.Items.Add(Item(Loc.T("휴지통 비우기…"), AskEmptyTrash, enabled: (_recycle?.Count ?? 0) > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("아이콘 바꾸기…"), () => EditTrashIcon(pin)));
        menu.Items.Add(new Separator());
        // 다른 핀처럼 어디든 (끌어서도)
        var pins = _services.Settings.Current.Pins;
        int at = pins.IndexOf(pin);
        bool vertical = _layout.IsVertical;
        menu.Items.Add(Item(vertical ? Loc.T("위로 이동") : Loc.T("왼쪽으로 이동"), () => MovePin(pin, -1), enabled: at > 0));
        menu.Items.Add(Item(vertical ? Loc.T("아래로 이동") : Loc.T("오른쪽으로 이동"), () => MovePin(pin, +1), enabled: at >= 0 && at < pins.Count - 1));
        menu.Items.Add(Item(Loc.T("독에서 빼기"), () => ModifyPins(p => p.Remove(pin))));
    }

    private void EditTrashIcon(PinItem pin)
    {
        bool full = (_recycle?.Count ?? 0) > 0;
        IconPickerWindow.Open(_services, pin.Icon, keepDefaultGlyph: true, Loc.T("휴지통이 비었는지 찼는지 그림으로 보여 줘요."),
            preview: icon => TrashIcon(icon, full),
            done: icon => ModifyPins(_ => pin.Icon = icon));
    }

    /// <summary>독 빈자리 "휴지통 보이기" (기존 사용자 — 새 설치는 처음부터 있음). 독에 하나만.</summary>
    private void ShowTrash() => ModifyPins(p =>
    {
        if (!p.Any(IsTrash)) p.Add(DefaultPins.RecycleBinPin());
    });

    /// <summary>파일을 끄는 동안 커서 아래가 휴지통 아이콘이면 그 뷰.</summary>
    private DockItemView? TrashViewUnder(DragEventArgs e)
    {
        foreach (var v in ItemsHost.Children.OfType<DockItemView>())
        {
            if (!IsTrash(v.Item.Pin)) continue;
            var p = e.GetPosition(v);
            return p.X >= 0 && p.Y >= 0 && p.X <= v.ActualWidth && p.Y <= v.ActualHeight ? v : null;
        }
        return null;
    }
}
