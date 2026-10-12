using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독이 넘칠 때 "⋯ +n" 칸 (#25 §2). 아이콘을 하한(28)·간격 1 까지 줄여도 넘치면, 넘칠 후보(실행 중이 아닌 고정 앱 — 루틴·폴더·휴지통·
/// 탐색기·앱 모음 제외)를 독에서만 감추고 휴지통 앞 "⋯ +n" 칸 하나로. 누르면 폴더 판처럼 원래 순서 격자(클릭 = 실행, 오른쪽 클릭 = 독 메뉴).
/// 감추는 순서: 루틴 데스크톱이면 그 루틴 앱은 마지막, 나머지는 최근에 쓴(실행 기록·몽독이 본 실행) 때가 오래된 것부터, 같으면 사용자 순서 뒤쪽부터.
/// 마우스가 독 위에 있는 동안은 감춘 앱을 바꾸지 않고(누르려던 자리가 움직이지 않게) 떠난 뒤 반영. 설정 값은 바꾸지 않음.
/// </summary>
public partial class DockWindow
{
    /// <summary>독에 보일 수 있는 칸 수 ("⋯" 칸 포함). int.MaxValue = 넘치지 않음.</summary>
    private int _overflowCapacity = int.MaxValue;
    /// <summary>지금 감춘 핀 (원래 순서).</summary>
    private List<PinItem> _hiddenPins = new();
    private readonly PinItem _overflowPin = new() { Kind = PinKind.Special, Target = "overflow", Name = "" };
    /// <summary>실행 중으로 본 마지막 때 (실행 기록이 없거나 꺼져 있어도 최근 쓴 앱으로 보게).</summary>
    private readonly Dictionary<PinItem, DateTime> _seenRunning = new();
    private bool _overflowDeferred, _overflowLeaveHooked;

    private bool IsOverflowItem(DockItemViewModel item) => ReferenceEquals(item.Pin, _overflowPin);

    /// <summary>넘칠 후보: 실행 중이 아닌 고정 앱 (탐색기 제외).</summary>
    private static bool CanOverflow(DockItemViewModel vm) =>
        !vm.IsSeparator && vm.Windows.Count == 0 && !vm.IsLaunching
        && vm.Pin is { Kind: PinKind.Exe or PinKind.Aumid } pin
        && !(pin.Kind == PinKind.Exe && string.Equals(System.IO.Path.GetFileNameWithoutExtension(pin.Target), "explorer", StringComparison.OrdinalIgnoreCase));

    /// <summary>RefreshItems 끝에서: 넘치면 감출 핀을 빼고 "⋯ +n" 칸을 넣음.</summary>
    private void ApplyOverflow(List<DockItemViewModel> list, Dictionary<string, DockItemViewModel> old)
    {
        foreach (var vm in list)
            if (vm.Pin is { } p && vm.Windows.Count > 0) _seenRunning[p] = DateTime.Now;
        int icons = list.Count(i => !i.IsSeparator);
        if (_overflowCapacity == int.MaxValue || icons <= _overflowCapacity)
        {
            if (_hiddenPins.Count > 0)
            {
                Log.Info("독 넘침 풀림: 감춘 앱 다시 보임");
                if (_folderPanel is OverflowStackPanel open) open.CloseAnimated();
            }
            _hiddenPins.Clear();
            _overflowDeferred = false;
            return;
        }
        int need = icons - Math.Max(1, _overflowCapacity - 1); // "⋯" 칸 자리 하나
        var candidates = list.Where(CanOverflow).ToList();
        List<PinItem> hide;
        bool mouseOver = PanelBorder.IsMouseOver && DockActive;
        var keep = _hiddenPins.Where(p => candidates.Any(c => ReferenceEquals(c.Pin, p))).ToList();
        if (mouseOver && keep.Count > 0)
        {
            // 독 위에 마우스: 감춘 앱을 되도록 그대로 (모자라면 그만큼만 더, 남으면 뒤에서부터 덜) — 떠나면 다시 계산
            hide = keep.Take(Math.Max(need, 0)).ToList();
            if (hide.Count < need)
                hide.AddRange(PickHidden(candidates.Where(c => !hide.Contains(c.Pin!)).ToList(), need - hide.Count));
            _overflowDeferred = true;
            HookOverflowLeave();
        }
        else
        {
            hide = PickHidden(candidates, need);
            _overflowDeferred = false;
        }
        if (hide.Count == 0) { _hiddenPins.Clear(); return; }
        var pins = _services.Settings.Current.Pins;
        var hiddenSet = hide.ToHashSet();
        if (!hiddenSet.SetEquals(_hiddenPins))
        {
            Log.Info($"독 넘침: 앱 {hide.Count}개를 ⋯ 칸으로 (보일 칸 {_overflowCapacity})");
            if (_folderPanel is OverflowStackPanel open) open.CloseAnimated(); // 열린 ⋯ 판이 옛 목록이 되지 않게
        }
        _hiddenPins = pins.Where(hiddenSet.Contains).ToList(); // 원래 순서
        list.RemoveAll(vm => vm.Pin is { } p && hiddenSet.Contains(p));

        // 휴지통 앞 (없으면 마지막 핀 뒤)
        int at = list.FindIndex(vm => IsTrash(vm.Pin));
        if (at < 0) at = list.FindLastIndex(vm => vm.Pin != null && !IsOverflowItem(vm)) + 1;
        string id = $"overflow:{_hiddenPins.Count}";
        var cell = old.GetValueOrDefault(id) is { } existing && ReferenceEquals(existing.Pin, _overflowPin)
            ? existing
            : new DockItemViewModel(id, _overflowPin, false, "", OverflowIcon(_hiddenPins.Count));
        cell.Name = Loc.F($"앱 {_hiddenPins.Count}개 더");
        cell.Windows = Array.Empty<AppWindowInfo>();
        list.Insert(Math.Clamp(at, 0, list.Count), cell);
    }

    /// <summary>감출 핀 need 개: 루틴 데스크톱의 루틴 앱은 마지막, 그다음 최근에 쓴 때가 오래된 것부터, 같으면 사용자 순서 뒤쪽부터.</summary>
    private List<PinItem> PickHidden(List<DockItemViewModel> candidates, int need)
    {
        if (need <= 0) return new List<PinItem>();
        int cur = VirtualDesktopService.Read().Current;
        string? routineId = RoutineService.DesktopRoutineId(cur);
        var routine = routineId is null ? null : RoutineUi.Find(_services, routineId);
        var pins = _services.Settings.Current.Pins;
        return candidates
            .Select(vm => vm.Pin!)
            .Select(p => (Pin: p, InRoutine: routine is not null && RoutineService.PinInRoutine(routine, p), Used: LastUsed(p), Index: pins.IndexOf(p)))
            .OrderBy(x => x.InRoutine)
            .ThenBy(x => x.Used)
            .ThenByDescending(x => x.Index)
            .Take(need)
            .Select(x => x.Pin)
            .ToList();
    }

    private DateTime LastUsed(PinItem pin)
    {
        DateTime used = AllAppsCatalog.Identity(pin) is { } id && AppUsage.LastUsed(id) is { } d ? d : DateTime.MinValue;
        if (_seenRunning.TryGetValue(pin, out var seen) && seen > used) used = seen;
        return used;
    }

    private void HookOverflowLeave()
    {
        if (_overflowLeaveHooked) return;
        _overflowLeaveHooked = true;
        PanelBorder.MouseLeave += (_, _) =>
        {
            if (!_overflowDeferred || _closed) return;
            Dispatcher.BeginInvoke(() => { if (_overflowDeferred && !_closed && !PanelBorder.IsMouseOver) RefreshItems(); });
        };
    }

    /// <summary>"⋯ +n" 칸 그림: 몽독 점선 둥근 칸 + 회색 ⋯ + 숫자 (판 없음).</summary>
    private static ImageSource OverflowIcon(int n)
    {
        const int px = 128;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var gray = new SolidColorBrush(Color.FromRgb(0x8E, 0x8A, 0x9C));
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xB6, 0xAE, 0xCC)), 4) { DashStyle = new DashStyle(new[] { 2.0, 1.6 }, 0), DashCap = PenLineCap.Round };
            double body = px * 0.805, o = (px - body) / 2;
            dc.DrawRoundedRectangle(null, pen, new Rect(o, o, body, body), body * 0.225, body * 0.225);
            var dots = new FormattedText("⋯", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Symbol"), 44, gray, 1.0);
            dc.DrawText(dots, new Point((px - dots.Width) / 2, px * 0.20));
            var num = new FormattedText("+" + n, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 30, gray, 1.0);
            dc.DrawText(num, new Point((px - num.Width) / 2, px * 0.52));
        }
        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>"⋯" 칸 누름: 감춘 앱 판 (다시 누르면 닫힘).</summary>
    private void ToggleOverflowPanel(DockItemView? view)
    {
        bool same = _folderPanel is OverflowStackPanel;
        _folderPanel?.CloseAnimated();
        if (same || view == null || _hiddenPins.Count == 0) return;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var a = toDip.Transform(view.PointToScreen(new Point(0, 0)));
        var b = toDip.Transform(view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight)));
        var style = _services.Settings.Current.Dock.IconStyle;
        var entries = _hiddenPins.Select(p => new OverflowStackPanel.Entry(PinDisplayName(p), SafeIcon(() => _services.Icons.GetIcon(p, style)),
            () => LaunchOverflowPin(p), () => OverflowMenu(p))).ToList();
        var panel = new OverflowStackPanel(_services, UiTheme.Palette(_services.Settings.Current), entries, new Rect(a, b), _layout.Edge, _monitor);
        panel.Closed += (_, _) =>
        {
            if (_folderPanel != panel) return;
            _folderPanel = null;
            _folderPanelPin = null;
            _lastInsideTicks = Environment.TickCount64;
        };
        _folderPanel = panel;
        _label?.Hide();
        _folderPanelPin = _overflowPin;
        panel.Show();
    }

    /// <summary>⋯ 판에서 실행: 독 실행과 같은 길 (실행 기록 → 다음 계산 때 독으로 돌아옴, 대신 그다음 오래된 앱이 ⋯ 로).</summary>
    private void LaunchOverflowPin(PinItem pin)
    {
        _folderPanel?.CloseAnimated();
        if (!CanLaunch(pin)) return;
        var vm = new DockItemViewModel("overflow-launch", pin, false, PinDisplayName(pin), null);
        _seenRunning[pin] = DateTime.Now;
        LaunchFromDock(vm, pin);
    }

    /// <summary>⋯ 판 오른쪽 클릭 = 독 아이콘 메뉴와 같음.</summary>
    private ContextMenu OverflowMenu(PinItem pin)
    {
        var menu = new ContextMenu();
        BuildPinMenu(menu, new DockItemViewModel("overflow-menu", pin, false, PinDisplayName(pin), null));
        // 왼쪽/오른쪽(위/아래)으로 이동은 핀 순서 기준이라 감춘 앱 판에서는 뜻이 없음 (리뷰)
        var moves = new[] { Loc.T("왼쪽으로 이동"), Loc.T("오른쪽으로 이동"), Loc.T("위로 이동"), Loc.T("아래로 이동") };
        foreach (var mi in menu.Items.OfType<MenuItem>().Where(m => m.Header is string h && moves.Contains(h)).ToList()) menu.Items.Remove(mi);
        return menu;
    }

    /// <summary>
    /// 끌어 놓은 자리(화면 칸 순서 — 감춘 앱·"⋯" 칸은 핀 목록과 다름) → 핀 목록 위치. 놓은 칸 바로 뒤의 진짜 핀 앞에, 없으면 앞 핀 바로 뒤에.
    /// dragged 가 핀이면 그 핀을 뺀 목록 기준.
    /// </summary>
    private int PinsIndexFor(int target, DockItemView? dragged, List<PinItem> pins)
    {
        var visible = Others(dragged).Where(InPinSection).Select(v => v.Item.Pin!).ToList();
        if (_hiddenPins.Count == 0 && !visible.Any(p => ReferenceEquals(p, _overflowPin))) return target; // 감춘 앱이 없으면 예전 그대로
        for (int k = Math.Max(0, target); k < visible.Count; k++)
            if (pins.Contains(visible[k])) return pins.IndexOf(visible[k]);
        for (int k = Math.Min(target, visible.Count) - 1; k >= 0; k--)
            if (pins.Contains(visible[k])) return pins.IndexOf(visible[k]) + 1;
        return target;
    }
}

/// <summary>"⋯ +n" 판: 감춘 앱 격자 (원래 순서). 클릭 = 실행, 오른쪽 클릭 = 독 아이콘 메뉴.</summary>
internal sealed class OverflowStackPanel : DockStackPanel
{
    public sealed record Entry(string Name, ImageSource? Icon, Action Launch, Func<ContextMenu> Menu);

    public OverflowStackPanel(AppServices services, UiPalette palette, List<Entry> entries, Rect anchorDip, DockEdge edge, MonitorInfo monitor)
        : base(services, palette, anchorDip, edge, monitor, "mongdock Overflow")
    {
        var root = new StackPanel();
        root.Children.Add(Header(Loc.F($"앱 {entries.Count}개 더")));
        root.Children.Add(Grid(entries.Select(e =>
        {
            // 메뉴는 오른쪽 클릭할 때 만듦 (판을 열 때 앱마다 만들면 느림 — 리뷰)
            var menu = new ContextMenu();
            var cell = MakeIconCell(e.Name, e.Icon, e.Name, e.Launch, menu);
            cell.ContextMenuOpening += (_, _) =>
            {
                menu.Items.Clear();
                var built = e.Menu();
                foreach (var item in built.Items.Cast<object>().ToList())
                {
                    built.Items.Remove(item);
                    menu.Items.Add(item);
                }
            };
            return (UIElement)cell;
        }).ToList()));
        SetBody(root);
    }
}
