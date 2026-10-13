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
    /// <summary>지금 감춘 항목 (독 순서) — 고정 앱과 고정 안 된 실행 중 앱. _hiddenPins 는 그중 핀 (끌어 놓기 위치 계산용).</summary>
    private List<DockItemViewModel> _hiddenItems = new();
    private List<PinItem> _hiddenPins = new();
    private readonly PinItem _overflowPin = new() { Kind = PinKind.Special, Target = "overflow", Name = "" };
    /// <summary>실행 중으로 본 마지막 때 (실행 기록이 없거나 꺼져 있어도 최근 쓴 앱으로 보게).</summary>
    private readonly Dictionary<PinItem, DateTime> _seenRunning = new();
    private bool _overflowDeferred, _overflowLeaveHooked;

    private bool IsOverflowItem(DockItemViewModel item) => ReferenceEquals(item.Pin, _overflowPin);

    /// <summary>
    /// 넘칠 후보 (#25 사용자: 많이 켜도 ⋯ 가 안 뜨고 아이콘만 작아짐): 고정 앱(탐색기 제외)과 고정 안 된 실행 중 앱.
    /// 지금 앞에 있는 창의 앱·알림이 깜빡이는 앱·켜는 중인 앱은 남김. 루틴·폴더·휴지통·앱 모음·구분선은 후보 아님.
    /// </summary>
    private bool CanOverflow(DockItemViewModel vm)
    {
        if (vm.IsSeparator || vm.IsLaunching || IsOverflowItem(vm)) return false;
        var fg = _services.Windows.ForegroundWindow;
        if (vm.Windows.Any(w => w.Hwnd == fg || _flashed.Contains(w.Hwnd))) return false;
        if (vm.Pin is null) return vm.Windows.Count > 0; // 고정 안 된 실행 중 앱
        return vm.Pin is { Kind: PinKind.Exe or PinKind.Aumid } pin
            && !(pin.Kind == PinKind.Exe && string.Equals(System.IO.Path.GetFileNameWithoutExtension(pin.Target), "explorer", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>감춘 항목을 알아보는 열쇠: 고정 앱은 그 핀, 실행 중 앱은 id ("app:…").</summary>
    private static object HideKey(DockItemViewModel vm) => (object?)vm.Pin ?? vm.Id;

    /// <summary>RefreshItems 끝에서: 넘치면 감출 항목을 빼고 "⋯ +n" 칸을 넣음.</summary>
    private void ApplyOverflow(List<DockItemViewModel> list, Dictionary<string, DockItemViewModel> old)
    {
        foreach (var vm in list)
            if (vm.Pin is { } p && vm.Windows.Count > 0) _seenRunning[p] = DateTime.Now;
        int icons = list.Count(i => !i.IsSeparator);
        if (_overflowCapacity == int.MaxValue || icons <= _overflowCapacity)
        {
            if (_hiddenItems.Count > 0)
            {
                Log.Info("독 넘침 풀림: 감춘 앱 다시 보임");
                if (_folderPanel is OverflowStackPanel open) open.CloseAnimated();
            }
            _hiddenItems.Clear();
            _hiddenPins.Clear();
            _overflowDeferred = false;
            return;
        }
        int need = icons - Math.Max(1, _overflowCapacity - 1); // "⋯" 칸 자리 하나
        var candidates = list.Where(CanOverflow).ToList();
        var before = _hiddenItems.Select(HideKey).ToHashSet();
        List<DockItemViewModel> hide;
        if (PanelBorder.IsMouseOver && DockActive && before.Count > 0)
        {
            // 독 위에 마우스: 감춘 앱을 되도록 그대로 (모자라면 그만큼만 더) — 떠나면 다시 계산
            hide = candidates.Where(c => before.Contains(HideKey(c))).Take(Math.Max(need, 0)).ToList();
            if (hide.Count < need) hide.AddRange(PickHidden(candidates.Where(c => !hide.Contains(c)).ToList(), need - hide.Count));
            _overflowDeferred = true;
            HookOverflowLeave();
        }
        else
        {
            hide = PickHidden(candidates, need);
            _overflowDeferred = false;
        }
        if (hide.Count == 0) { _hiddenItems.Clear(); _hiddenPins.Clear(); return; }
        var keys = hide.Select(HideKey).ToHashSet();
        if (!keys.SetEquals(before))
        {
            Log.Info($"독 넘침: 앱 {hide.Count}개를 ⋯ 칸으로 (고정 {hide.Count(h => h.Pin != null)}, 실행 중 {hide.Count(h => h.Pin == null)}, 보일 칸 {_overflowCapacity})");
            if (_folderPanel is OverflowStackPanel open) open.CloseAnimated(); // 열린 ⋯ 판이 옛 목록이 되지 않게
        }
        _hiddenItems = list.Where(vm => keys.Contains(HideKey(vm))).ToList(); // 독 순서 그대로
        _hiddenPins = _hiddenItems.Where(vm => vm.Pin != null).Select(vm => vm.Pin!).ToList();
        list.RemoveAll(vm => keys.Contains(HideKey(vm)));
        // 실행 중 앱이 다 감춰졌으면 그 앞 자동 구분선도 뺌
        int sep = list.FindIndex(vm => vm.IsAutoSeparator);
        if (sep >= 0 && sep == list.Count - 1) list.RemoveAt(sep);

        // 독의 맨 마지막 칸 (실행 중 앱·휴지통보다도 뒤 — 사용자)
        int at = list.Count;
        string id = $"overflow:{_hiddenItems.Count}";
        var cell = old.GetValueOrDefault(id) is { } existing && ReferenceEquals(existing.Pin, _overflowPin)
            ? existing
            : new DockItemViewModel(id, _overflowPin, false, "", OverflowIcon(_hiddenItems.Count));
        cell.Name = Loc.F($"앱 {_hiddenItems.Count}개 더");
        cell.Windows = Array.Empty<AppWindowInfo>();
        list.Insert(Math.Clamp(at, 0, list.Count), cell);
    }

    /// <summary>
    /// 감출 항목 need 개 (먼저 감춤 = 덜 중요): 나머지·최근에 쓴 고정 앱(오래된 것부터) → 루틴 데스크톱의 루틴 앱 → 지금 데스크톱에 창이 있는 앱
    /// (그 안에서는 가장 오래전에 앞에 있던 것부터). 지금 앞에 있는 앱은 후보에서 이미 빠짐 (#25: 활성 > 이 데스크톱 창 > 루틴 > 최근 사용 > 나머지).
    /// </summary>
    private List<DockItemViewModel> PickHidden(List<DockItemViewModel> candidates, int need)
    {
        if (need <= 0) return new List<DockItemViewModel>();
        int cur = VirtualDesktopService.Read().Current;
        string? routineId = RoutineService.DesktopRoutineId(cur);
        var routine = routineId is null ? null : RoutineUi.Find(_services, routineId);
        return candidates
            .Select((vm, i) =>
            {
                bool here = vm.Windows.Any(w => w.OnCurrentDesktop);
                bool inRoutine = routine is not null && vm.Pin is { } rp && RoutineService.PinInRoutine(routine, rp);
                int tier = here ? 3 : inRoutine ? 2 : 1;
                long score = here
                    ? vm.Windows.Select(w => _lastActive.GetValueOrDefault(w.Hwnd)).DefaultIfEmpty(0).Max()
                    : vm.Pin is { } pin ? LastUsed(pin).Ticks : 0;
                return (Vm: vm, Tier: tier, Score: score, Index: i);
            })
            .OrderBy(x => x.Tier)
            .ThenBy(x => x.Score)
            .ThenByDescending(x => x.Index)
            .Take(need)
            .Select(x => x.Vm)
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
        if (same || view == null || _hiddenItems.Count == 0) return;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var a = toDip.Transform(view.PointToScreen(new Point(0, 0)));
        var b = toDip.Transform(view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight)));
        var style = _services.Settings.Current.Dock.IconStyle;
        var entries = _hiddenItems.Select(vm => new OverflowStackPanel.Entry(vm.Pin is { } p ? PinDisplayName(p) : vm.Name, vm.Icon,
            () => LaunchOverflowItem(vm), () => OverflowMenu(vm))).ToList();
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

    /// <summary>⋯ 판에서 누름: 실행 중이면 그 앱의 가장 최근 창을 앞으로, 아니면 실행.</summary>
    private void LaunchOverflowItem(DockItemViewModel vm)
    {
        if (vm.Windows.Count == 0 && vm.Pin is { } pin) { LaunchOverflowPin(pin); return; }
        _folderPanel?.CloseAnimated();
        var target = vm.Windows.OrderByDescending(w => _lastActive.GetValueOrDefault(w.Hwnd)).FirstOrDefault();
        if (target is not null) _services.Launcher.Activate(target.Hwnd);
    }

    /// <summary>⋯ 판 오른쪽 클릭 = 독 아이콘 메뉴와 같음.</summary>
    private ContextMenu OverflowMenu(DockItemViewModel vm)
    {
        var menu = new ContextMenu();
        if (vm.Pin is null) { BuildRunningMenu(menu, vm); return menu; }
        BuildPinMenu(menu, vm);
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
