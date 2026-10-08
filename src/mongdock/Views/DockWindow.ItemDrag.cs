using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Native;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 아이콘 드래그로 순서 바꾸기 (맥 방식) + 바깥에서 파일 끌어다 놓아 고정.
///
/// 아이콘 드래그:
/// - 아이콘을 누르면 Root 가 마우스를 캡처. DragThreshold 이상 움직이면 드래그, 아니면 놓을 때 클릭(실행/전환).
/// - 드래그 중: 원래 자리는 비고(투명), 커서를 반투명 복사본(<see cref="DockDragIconWindow"/>)이 따라가며,
///   다른 아이콘이 RenderTransform 으로 150ms 동안 비켜서 들어갈 자리를 보여준다 (레이아웃·패널 길이는 그대로).
/// - 핀: 핀 영역 안에서만 이동. 독에서 아이콘 크기 이상 떨어지면 "제거" → 놓으면 고정 해제.
/// - 실행 중 앱(핀 아님): 핀 영역에 놓으면 그 위치에 고정, 실행 중 영역 안이면 표시 순서만 바뀜.
/// - Esc / 오른쪽 클릭 / 캡처 상실 = 취소 (원래 순서). 드래그 중 호버 확대는 끈다(위치 계산을 기본 크기로 고정).
///
/// 파일 드롭: Root.AllowDrop. 핀 영역에 빈 슬롯을 열어 들어갈 자리를 보여주고, 놓으면 그 위치에 핀 추가.
/// </summary>
public partial class DockWindow
{
    private const double ShiftAnimMs = 150;

    // 아이콘 드래그 상태
    private DockItemView? _pressView;
    private bool _itemArmed;           // 누른 상태 (캡처 중, 아직 클릭인지 드래그인지 모름)
    private bool _itemDragging;        // 임계값을 넘어 드래그 중
    private Point _itemStart;          // 누른 위치 (화면 DIP)
    private Point _itemGrab;           // 누른 위치의 아이콘 안 좌표 (복사본이 같은 자리를 잡고 따라오게)
    private int _itemTarget = -1;      // 드래그 항목을 뺀 목록에서 들어갈 위치
    private bool _itemOutside;         // 독에서 멀리 떨어짐 (핀 = 제거, 실행 중 앱 = 변화 없음)
    private long _suppressContextUntil; // 오른쪽 클릭으로 취소한 직후 메뉴가 뜨지 않게 (TickCount64)
    private DockDragIconWindow? _dragIcon;
    private readonly DispatcherTimer _escTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
    /// <summary>드래그 중 들어온 창 목록 변경 → 끝난 뒤 한 번에 반영 (드래그 중 뷰를 다시 만들지 않음).</summary>
    private bool _refreshPending;

    // 파일 드롭 상태
    private bool _fileDragOver;
    private Border? _dropGap;          // 패널 끝에 붙여 패널을 한 칸 늘리는 빈 슬롯
    private double _dropGapLength;     // ComputeBaseLength 에 더할 길이 (창 크기도 한 칸 늘림)
    private int _dropTarget = -1;
    private int _dropSeq;              // DragLeave 직후 다시 DragOver 가 왔는지 확인용

    private bool AnyItemDrag => _itemArmed || _fileDragOver;

    private void InitItemDrag()
    {
        Root.MouseMove += OnItemDragMouseMove;
        Root.MouseLeftButtonUp += OnItemDragMouseUp;
        Root.LostMouseCapture += OnItemDragLostCapture;
        Root.PreviewMouseRightButtonDown += OnItemDragRightDown;
        _escTimer.Tick += (_, _) =>
        {
            // 독은 포커스를 받지 않으므로 키 이벤트가 오지 않는다 → 드래그 중에만 Esc 상태를 직접 확인
            if (_itemDragging && MenuApi.IsKeyDown(0x1B /* VK_ESCAPE */)) CancelItemDrag();
        };

        Root.AllowDrop = true;
        Root.DragEnter += OnFileDragOver;
        Root.DragOver += OnFileDragOver;
        Root.DragLeave += OnFileDragLeave;
        Root.Drop += OnFileDrop;
    }

    // ───────────────────────── 아이콘 누름 / 클릭 ─────────────────────────

    private void OnItemPressed(object? sender, MouseButtonEventArgs e)
    {
        if (sender is not DockItemView view || _itemArmed || _dragArmed || _fileDragOver) return;
        _pressView = view;
        _itemStart = ToScreenDip(e.GetPosition(this));
        _itemGrab = e.GetPosition(view);
        _itemDragging = false;
        _itemOutside = false;
        _itemTarget = -1;
        // 버튼을 이 창 위에서 누른 상태라 NOACTIVATE 창이어도 캡처가 창 밖까지 유지된다 (독 이동 드래그와 같은 방식)
        _itemArmed = Root.CaptureMouse();
        if (!_itemArmed)
        {
            // 캡처 실패 → 드래그 없이 바로 클릭으로
            _pressView = null;
            OnItemClicked(view, EventArgs.Empty);
        }
    }

    private void OnItemDragMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_itemArmed) return;
        e.Handled = true;
        var view = _pressView;
        bool dragged = _itemDragging;
        int target = _itemTarget;
        bool outside = _itemOutside;
        bool click = !dragged && view != null && new Rect(view.RenderSize).Contains(e.GetPosition(view));

        EndItemDrag(animateBack: false);
        if (view == null) return;
        if (click)
        {
            if (!view.Item.IsSeparator) OnItemClicked(view, EventArgs.Empty);
        }
        else if (dragged)
        {
            try { CommitItemDrag(view, target, outside); }
            catch (Exception ex) { Log.Error("독 아이콘 드래그 반영 실패", ex); }
        }
        FlushPendingRefresh();
    }

    private void OnItemDragLostCapture(object sender, MouseEventArgs e)
    {
        if (_itemArmed) CancelItemDrag();
    }

    private void OnItemDragRightDown(object sender, MouseButtonEventArgs e)
    {
        if (!_itemArmed) return;
        // 드래그 중 오른쪽 클릭 = 취소 (마우스만으로 취소할 수 있게)
        _suppressContextUntil = Environment.TickCount64 + 1500;
        CancelItemDrag();
        e.Handled = true;
    }

    // ───────────────────────── 드래그 진행 ─────────────────────────

    private void OnItemDragMouseMove(object sender, MouseEventArgs e)
    {
        if (!_itemArmed || _pressView == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelItemDrag();
            return;
        }

        var p = ToScreenDip(e.GetPosition(this));
        if (!_itemDragging)
        {
            if (Math.Abs(p.X - _itemStart.X) < DragThreshold && Math.Abs(p.Y - _itemStart.Y) < DragThreshold) return;
            BeginItemDrag();
        }
        UpdateItemDrag(p, e);
    }

    private void BeginItemDrag()
    {
        var view = _pressView!;
        _itemDragging = true;
        _label?.Hide();
        _picker?.Close();
        // 드래그 중에는 확대를 끈다: 들어갈 자리를 기본 크기 기준으로 계산하고, 확대가 섞이면 흔들려 보임
        ResetMagnification(animate: true);
        _itemTarget = ItemsHost.Children.IndexOf(view);
        view.Opacity = 0; // 원래 자리는 비움 (자리 자체는 이웃이 비켜서며 옮겨 감)

        if (_dragIcon != null && !_dragIcon.IsVisible && !MatchesMonitorDpi(_dragIcon))
        {
            _dragIcon.Close();
            _dragIcon = null;
        }
        _dragIcon ??= new DockDragIconWindow(_services);
        _dragIcon.Prepare(view.Item.Icon, view.Item.IsSeparator, _layout);
        _escTimer.Start();
    }

    private void UpdateItemDrag(Point screen, MouseEventArgs e)
    {
        var view = _pressView!;
        var l = _layout;

        // 복사본: 누른 자리를 잡은 채 커서를 따라감
        double gx = Math.Clamp(_itemGrab.X, 0, l.IconSize), gy = Math.Clamp(_itemGrab.Y, 0, l.IconSize);
        var iconTopLeft = new Point(screen.X - gx, screen.Y - gy);
        if (_dragIcon != null)
        {
            bool first = !_dragIcon.IsVisible;
            _dragIcon.MoveTo(iconTopLeft);
            if (first && _services.DesktopWindows.EnsureOnMonitor(_dragIcon, _monitor)) _dragIcon.MoveTo(iconTopLeft);
        }

        // 독(패널)에서 얼마나 떨어졌는지
        var a = ToScreenDip(PanelBorder.TranslatePoint(new Point(0, 0), this));
        var b = ToScreenDip(PanelBorder.TranslatePoint(new Point(PanelBorder.ActualWidth, PanelBorder.ActualHeight), this));
        var panel = new Rect(a, b);
        double dx = Math.Max(0, Math.Max(panel.Left - screen.X, screen.X - panel.Right));
        double dy = Math.Max(0, Math.Max(panel.Top - screen.Y, screen.Y - panel.Bottom));
        bool outside = Math.Max(dx, dy) >= l.IconSize;
        bool removable = view.Item.Pin != null;
        _itemOutside = outside;
        _dragIcon?.SetRemove(outside && removable);

        int from = ItemsHost.Children.IndexOf(view);
        if (from < 0)
        {
            CancelItemDrag();
            return;
        }

        int target;
        if (outside)
        {
            target = from; // 멀리 떨어지면 이웃은 제자리 (다시 가져오면 이어서 자리 보여줌)
        }
        else
        {
            var others = Others(view);
            int pins = others.Count(v => v.Item.Pin != null);
            bool pinnable = view.Item.Pin != null || (view.Item.Windows.Count > 0 && CanPin(view.Item.Windows[0]));
            bool running = view.Item.Pin == null;
            target = NearestSlot(others, view.BaseLength, AlongInBase(e),
                k => (pinnable && k <= pins) || (running && k > pins));
            if (target < 0) target = from;
        }

        if (target != _itemTarget)
        {
            _itemTarget = target;
            ApplyGapShifts(view, target);
        }
    }

    /// <summary>드래그 항목을 뺀 나머지 뷰 (화면 순서).</summary>
    private List<DockItemView> Others(DockItemView? dragged)
        => ItemsHost.Children.OfType<DockItemView>().Where(v => v != dragged).ToList();

    /// <summary>
    /// 커서의 독 방향 좌표를 "확대 전 기본 배치" 기준(아이템 영역 시작 = 0)으로.
    /// 패널은 창 안에서 가운데 정렬이고 드래그 중엔 확대가 꺼지므로 패널 중심 ± 기본 길이/2 가 아이템 영역.
    /// </summary>
    private double AlongInBase(DragEventArgs e) => AlongInBase(e.GetPosition(PanelBorder));
    private double AlongInBase(MouseEventArgs e) => AlongInBase(e.GetPosition(PanelBorder));

    private double AlongInBase(Point local)
    {
        double total = 0;
        foreach (var child in ItemsHost.Children)
        {
            if (child is DockItemView v) total += v.BaseLength;
            else if (child == _dropGap) total += _dropGapLength;
        }
        double along = _layout.IsVertical ? local.Y - PanelBorder.ActualHeight / 2 : local.X - PanelBorder.ActualWidth / 2;
        return along + total / 2;
    }

    /// <summary>
    /// others 사이에 길이 gap 인 빈 칸을 넣을 위치 k(0..n) 중, 빈 칸 중심이 커서에 가장 가까운 곳.
    /// 빈 칸이 들어간 배치 기준으로 계산하므로 경계에서 왔다 갔다 하지 않는다. 허용 위치가 없으면 -1.
    /// </summary>
    private static int NearestSlot(List<DockItemView> others, double gap, double cursor, Func<int, bool> allowed)
    {
        int best = -1;
        double bestD = double.MaxValue, s = 0;
        for (int k = 0; k <= others.Count; k++)
        {
            if (allowed(k))
            {
                double d = Math.Abs(cursor - (s + gap / 2));
                if (d < bestD)
                {
                    bestD = d;
                    best = k;
                }
            }
            if (k < others.Count) s += others[k].BaseLength;
        }
        return best;
    }

    /// <summary>
    /// 드래그 항목의 자리(길이 d)가 others 의 k 번째 앞으로 옮겨 간 것처럼 이웃을 비켜 세움.
    /// 패널 안 전체 길이는 같으므로 각 뷰의 이동량 = 새 위치 - 원래 위치.
    /// </summary>
    private void ApplyGapShifts(DockItemView dragged, int k)
    {
        double d = dragged.BaseLength, orig = 0, packed = 0;
        int m = 0;
        foreach (var v in ItemsHost.Children.OfType<DockItemView>())
        {
            if (v == dragged)
            {
                orig += d;
                continue;
            }
            double now = packed + (m >= k ? d : 0);
            v.AnimateShift(now - orig, animate: true);
            orig += v.BaseLength;
            packed += v.BaseLength;
            m++;
        }
    }

    private void ClearShifts(bool animate)
    {
        foreach (var v in ItemsHost.Children.OfType<DockItemView>()) v.AnimateShift(0, animate && v.ShiftTarget != 0);
    }

    // ───────────────────────── 드래그 끝 ─────────────────────────

    /// <summary>드래그 취소 (원래 순서로). 설정 변경·화면 변경·Esc·캡처 상실 등.</summary>
    private void CancelItemDrag()
    {
        if (!_itemArmed) return;
        EndItemDrag(animateBack: true);
        FlushPendingRefresh();
    }

    private void EndItemDrag(bool animateBack)
    {
        bool wasDragging = _itemDragging;
        var view = _pressView;
        _itemArmed = false; // 먼저 내려야 캡처 해제 → LostMouseCapture 에서 다시 취소하지 않음
        _itemDragging = false;
        _pressView = null;
        _itemTarget = -1;
        _itemOutside = false;
        _escTimer.Stop();
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        _dragIcon?.Hide();
        if (view != null) view.Opacity = 1;
        if (wasDragging) ClearShifts(animateBack);
        _lastInsideTicks = Environment.TickCount64;
    }

    /// <summary>놓은 결과 반영. 핀 변경은 저장 → SettingsChanged → ApplyAll 로 새로 그려짐.</summary>
    private void CommitItemDrag(DockItemView view, int target, bool outside)
    {
        var item = view.Item;
        var pins = _services.Settings.Current.Pins;

        if (outside)
        {
            // 핀(기본 핀·구분선 포함)을 독 밖에 놓으면 고정 해제. 실행 중이면 RefreshItems 가 실행 중 영역에 다시 넣는다
            if (item.Pin is PinItem removed && pins.Contains(removed)) ModifyPins(p => p.Remove(removed));
            return;
        }
        if (target < 0) return;

        if (item.Pin is PinItem pin)
        {
            int from = pins.IndexOf(pin);
            if (from < 0 || from == target) return;
            ModifyPins(p =>
            {
                p.RemoveAt(from);
                p.Insert(Math.Clamp(target, 0, p.Count), pin);
            });
            return;
        }

        // 실행 중 앱: 핀 영역(target <= 핀 수)이면 그 위치에 고정 ("독에 고정" 과 같은 CreatePin), 아니면 표시 순서만
        int pinCount = Others(view).Count(v => v.Item.Pin != null);
        if (target <= pinCount)
        {
            if (item.Windows.Count == 0 || !CanPin(item.Windows[0])) return;
            var newPin = _services.Windows.CreatePin(item.Windows[0]);
            ModifyPins(p => p.Insert(Math.Clamp(target, 0, p.Count), newPin));
            return;
        }

        if (!item.Id.StartsWith("app:", StringComparison.Ordinal)) return;
        string key = item.Id["app:".Length..];
        int pos = target - pinCount - 1; // 자동 구분선 다음부터 실행 중 영역
        int old = _runningOrder.IndexOf(key);
        if (old < 0 || pos < 0) return;
        _runningOrder.RemoveAt(old);
        _runningOrder.Insert(Math.Clamp(pos, 0, _runningOrder.Count), key);
        _refreshPending = false;
        RefreshItems(); // 표시 순서만 바뀜 (저장 안 함)
    }

    /// <summary>드래그 중 미뤄 둔 항목 갱신을 반영 (재진입을 피해 다음 디스패처 차례에).</summary>
    private void FlushPendingRefresh()
    {
        if (!_refreshPending) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || AnyItemDrag || !_refreshPending) return;
            _refreshPending = false;
            RefreshItems();
        }, DispatcherPriority.Background);
    }

    // ───────────────────────── 바깥 파일 끌어다 놓기 ─────────────────────────

    private static bool HasFiles(DragEventArgs e)
    {
        try { return e.Data.GetDataPresent(DataFormats.FileDrop); }
        catch { return false; }
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_itemArmed || !HasFiles(e) || _windowsHidden)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link
                  : (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                  : DragDropEffects.None;
        if (e.Effects == DragDropEffects.None) return;

        _dropSeq++;
        _lastInsideTicks = Environment.TickCount64;
        if (!_fileDragOver)
        {
            _fileDragOver = true;
            _label?.Hide();
            _picker?.Close();
            ResetMagnification();
            // 패널 끝에 한 칸짜리 빈 슬롯을 붙여 패널을 늘리고, 들어갈 위치 뒤의 아이콘을 그만큼 밀어 빈 자리를 보여줌
            double slot = _layout.IconSize + _layout.Spacing;
            _dropGapLength = slot;
            _dropGap = new Border { IsHitTestVisible = true, Background = null };
            if (_layout.IsVertical)
            {
                _dropGap.Width = _layout.IconSize;
                _dropGap.Height = slot;
            }
            else
            {
                _dropGap.Width = slot;
                _dropGap.Height = _layout.IconSize;
            }
            ItemsHost.Children.Add(_dropGap);
            _dropTarget = -1;
            Root.Background = HitBrush; // 확대 여유 영역 위에서도 드롭 대상으로 (가장자리에서 깜빡이지 않게)
            Place(); // 창 길이도 한 칸 늘림
        }

        var others = Others(null);
        int pins = others.Count(v => v.Item.Pin != null);
        int target = NearestSlot(others, _dropGapLength, AlongInBase(e), k => k <= pins);
        if (target >= 0 && target != _dropTarget)
        {
            _dropTarget = target;
            int m = 0;
            foreach (var v in others) v.AnimateShift(m++ >= target ? _dropGapLength : 0, animate: true);
        }
    }

    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_fileDragOver) return;
        // 패널 안의 아이콘 사이를 지날 때도 DragLeave 가 올라온다 → 곧바로 DragOver 가 다시 오면 무시
        int seq = _dropSeq;
        Dispatcher.BeginInvoke(() =>
        {
            if (_fileDragOver && seq == _dropSeq) EndFileDrag();
        }, DispatcherPriority.Background);
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_fileDragOver) return;
        int target = _dropTarget;
        string[] paths = Array.Empty<string>();
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] p) paths = p;
        }
        catch (Exception ex) { Log.Error("끌어 놓은 파일 읽기 실패", ex); }
        EndFileDrag();
        if (paths.Length == 0 || target < 0) return;

        try
        {
            var newPins = paths.Select(path => PinFactory.CreatePin(path, _services.Settings))
                               .OfType<PinItem>().ToList();
            if (newPins.Count == 0) return;
            ModifyPins(list =>
            {
                int at = Math.Clamp(target, 0, list.Count);
                foreach (var pin in newPins)
                {
                    // 이미 같은 핀이 있으면 새로 만들지 않고 그 핀을 이 위치로 옮김
                    int existing = list.FindIndex(x => x.Kind == pin.Kind
                        && string.Equals(x.Target, pin.Target, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Arguments ?? "", pin.Arguments ?? "", StringComparison.Ordinal));
                    var insert = pin;
                    if (existing >= 0)
                    {
                        insert = list[existing];
                        list.RemoveAt(existing);
                        if (existing < at) at--;
                    }
                    list.Insert(Math.Clamp(at, 0, list.Count), insert);
                    at++;
                }
            });
            Log.Info($"독에 파일 {newPins.Count}개 고정 (위치 {target})");
        }
        catch (Exception ex)
        {
            Log.Error("끌어 놓은 파일 고정 실패", ex);
        }
    }

    /// <summary>파일 드래그 표시 정리 (빈 슬롯 제거, 이동 원위치, 창 길이 복구).</summary>
    private void EndFileDrag()
    {
        if (!_fileDragOver) return;
        _fileDragOver = false;
        _dropTarget = -1;
        if (_dropGap != null) ItemsHost.Children.Remove(_dropGap);
        _dropGap = null;
        _dropGapLength = 0;
        Root.Background = null;
        ClearShifts(animate: false);
        _lastInsideTicks = Environment.TickCount64;
        if (!_closed) Place();
        FlushPendingRefresh();
    }
}
