using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 앱 모음 판 안 끌기 (#24, 독 아이콘 끌기와 같은 느낌): 4px 움직이면 시작 —
/// 아이콘(반투명·1.08배·그림자)이 커서를 따라오고 원래 자리는 흐린 빈 칸, ★ 줄에서는 옆 칸이 120ms 로 비켜나 놓일 자리가 보임,
/// 묶음 칸·다른 앱 위는 대상이 살짝 커지고 테두리 강조, 놓을 수 없는 곳이면 아이콘이 흐려짐.
/// 놓으면 새 자리로 미끄러져 들어가고(150ms), 취소·빈 곳이면 원래 자리로 돌아감. 판 밖으로 나가면(독·바탕 화면) 윈도우 끌기로 넘김.
/// </summary>
internal sealed partial class AllAppsPanel
{
    /// <summary>끌기 시작: 8px 이상 움직이고 150ms 이상 눌렀을 때 (원격 접속에서 클릭 중 흔들림은 클릭으로).</summary>
    private const double DragStartPx = 8, LiftScale = 1.08, ShiftMs = 120, SettleMs = 150;
    private const long DragMinHoldMs = 150;
    private long _pressTicks;

    /// <summary>끌 수 있는 것: 앱 또는 묶음.</summary>
    private sealed record DragItem(bool IsGroup, string Key, bool IsRoutine = false);

    /// <summary>놓을 곳 표시 (칸의 Tag).</summary>
    private sealed record DropTag(string Kind, string Key);

    private sealed class DragState
    {
        public required DragItem Item;
        public required Border Source;
        public required Window Ghost;
        public required Size GhostSize;
        public required Point Grab;          // 칸 안에서 누른 자리
        public Func<DataObject>? OleData;
        public Border? Highlight;
        public int FavIndex = -1;
        public DropTag? Target;
    }

    private DragState? _drag;
    private WrapPanel? _favRow;
    private readonly List<Border> _favCells = new();
    private WrapPanel? _groupGrid;
    private string? _groupGridId;
    private readonly List<Border> _groupCells = new();
    private readonly List<AppEntry> _groupApps = new();
    private int _groupGap = -1;
    private Point _press;
    private Border? _pressedCell;

    /// <summary>누르고 떼면 click, 4px 움직이면 판 안 끌기 (item = null 이면 끌기 없음). oleData = 판 밖으로 나갈 때 윈도우 끌기 데이터.</summary>
    private void Pressable(Border el, Action click, DragItem? item, Func<DataObject>? oleData)
    {
        el.MouseLeftButtonDown += (_, e) =>
        {
            if (_drag is not null) return;
            _press = e.GetPosition(this);
            _pressTicks = Environment.TickCount64;
            _pressedCell = el;
            el.CaptureMouse();
            e.Handled = true;
        };
        el.MouseMove += (_, e) =>
        {
            if (_pressedCell != el || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (_drag is null)
            {
                if (item is null || (Math.Abs(p.X - _press.X) < DragStartPx && Math.Abs(p.Y - _press.Y) < DragStartPx)) return;
                if (Environment.TickCount64 - _pressTicks < DragMinHoldMs) return;
                Log.Info($"앱 모음 판: 끌기 시작 ({(item.IsGroup ? "묶음" : "앱")})");
                BeginDrag(el, item, oleData, e.GetPosition(el));
                UpdateDrag(p);
                return;
            }
            // 그림은 바로 커서로, 놓을 곳 찾기(판 전체 HitTest)는 프레임마다 한 번 — 마우스 이벤트가 밀려 그림이 늦게 따라오지 않게.
            // 판 밖으로 나가면 윈도우 끌기로 넘기는 건 바로 (프레임 콜백 안에서 끌기 루프를 돌리지 않게)
            if (FollowRealCursor && p.X >= -6 && p.Y >= -6 && p.X <= ActualWidth + 6 && p.Y <= ActualHeight + 6)
            {
                _dragPending = p;
                FollowCursor();
            }
            else UpdateDrag(p);
        };
        el.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            bool pressed = _pressedCell == el;
            _pressedCell = null;
            el.ReleaseMouseCapture();
            if (_drag is not null) EndDrag(drop: true);
            else if (pressed) { Log.Info($"앱 모음 판: 클릭 ({(item?.IsGroup == true ? "묶음" : "앱")})"); click(); }
            else Log.Info("앱 모음 판: 클릭 무시 (누름 기록 없음)");
        };
        el.LostMouseCapture += (_, _) =>
        {
            if (_drag is not null && _pressedCell == el)
            {
                Log.Info("앱 모음 판: 끌기 중 마우스 캡처 잃음 → 원래 자리로");
                EndDrag(drop: false);
            }
        };
    }

    private void BeginDrag(Border source, DragItem item, Func<DataObject>? oleData, Point grab)
    {
        double k = UiScale; // 판 안이 커져 있으면(LayoutTransform) 끄는 그림·잡은 자리도 그만큼
        var size = new Size(source.ActualWidth * k, source.ActualHeight * k);
        grab = new Point(grab.X * k, grab.Y * k);
        _drag = new DragState
        {
            Item = item,
            Source = source,
            Ghost = CreateGhost(source, size),
            GhostSize = new Size(size.Width * LiftScale, size.Height * LiftScale),
            Grab = new Point(grab.X * LiftScale, grab.Y * LiftScale),
            OleData = oleData,
        };
        source.Opacity = 0.25; // 원래 자리는 흐린 빈 칸
        source.ToolTip = null;
        Dragging = true; // 끄는 동안은 비활성화·바깥 판정으로 판을 닫지 않음
        // 보이기 전에 창 핸들을 만들고 커서 아래(잡은 자리 그대로)로 먼저 옮긴 뒤 보임 — 첫 프레임이 다른 자리에 그려졌다 날아오지 않게
        new WindowInteropHelper(_drag.Ghost).EnsureHandle();
        MoveGhost(Mouse.GetPosition(this));
        _drag.Ghost.Show();
        StartEdgeScroll();
        StartFollow();
    }

    /// <summary>
    /// 끄는 그림은 매 프레임 실제 커서 자리(잡은 지점 그대로)에 바로 놓음 — 애니메이션·보간 없음 (2026-10-13 사용자: 폴더 안에서 끌면 무겁게, 거리를 두고 늦게 따라옴).
    /// 마우스 이벤트 좌표는 판이 바쁘면(다시 그리기·끝 스크롤) 밀려서 늦은 자리일 수 있어 커서 자리를 직접 읽음. 시험(스크립트 끌기)에서는 끔 — 진짜 커서는 안 움직이므로.
    /// </summary>
    internal static bool FollowRealCursor { get; set; } = true;
    private Point? _dragPending;
    private EventHandler? _followTick;

    private void StartFollow()
    {
        if (!FollowRealCursor) return;
        StopFollow();
        _followTick = (_, _) =>
        {
            if (_drag is null) { StopFollow(); return; }
            FlushDragPending();
            FollowCursor();
        };
        CompositionTarget.Rendering += _followTick;
    }

    private void StopFollow()
    {
        if (_followTick is not null) CompositionTarget.Rendering -= _followTick;
        _followTick = null;
        _dragPending = null;
    }

    /// <summary>밀린 놓을 곳 찾기를 지금 (놓기 직전·프레임마다).</summary>
    private void FlushDragPending()
    {
        if (_dragPending is not { } p || _drag is null) return;
        _dragPending = null;
        UpdateDrag(p);
    }

    private void FollowCursor()
    {
        if (_drag is null || CursorPx() is not { } c) return;
        PlaceGhost(c);
    }

    /// <summary>시험에서만: 진짜 커서 대신 쓸 커서 자리(화면 픽셀) — 프레임마다 따라오는지 진짜 마우스 없이 봄.</summary>
    internal static Func<Point>? CursorForTest { get; set; }

    private static Point? CursorPx() =>
        CursorForTest is { } f ? f() : Native.DesktopApi.GetCursorPos(out var c) ? new Point(c.X, c.Y) : null;

    private System.Windows.Threading.DispatcherTimer? _edgeScroll;
    /// <summary>시험(스크립트 끌기)에서는 끔 — 진짜 커서 자리를 보므로 사용자 커서가 판 끝에 있으면 시험 중 판이 스크롤됨.</summary>
    internal static bool EdgeScrollEnabled { get; set; } = true;

    /// <summary>끄는 동안 판 본문 위·아래 끝(32px)에 닿으면 저절로 스크롤 — 끝에 가까울수록 빠르게, 마우스가 멈춰 있어도 (2026-10-13 사용자).</summary>
    private void StartEdgeScroll()
    {
        if (_bodyScroll is not { } sv || !EdgeScrollEnabled) return;
        _edgeScroll?.Stop();
        _edgeScroll = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _edgeScroll.Tick += (_, _) =>
        {
            if (_drag is null) { _edgeScroll?.Stop(); return; }
            var p = Mouse.GetPosition(sv);
            const double edge = 32;
            if (p.X < 0 || p.X > sv.ActualWidth) return;
            double speed = p.Y < edge ? -(edge - Math.Max(-edge, p.Y)) / edge * 14 : p.Y > sv.ActualHeight - edge ? (p.Y - (sv.ActualHeight - edge)) / edge * 14 : 0;
            if (Math.Abs(speed) < 0.5) return;
            double before = sv.VerticalOffset;
            sv.ScrollToVerticalOffset(Math.Clamp(before + Math.Clamp(speed, -20, 20), 0, sv.ScrollableHeight));
            if (Math.Abs(sv.VerticalOffset - before) > 0.1 || sv.VerticalOffset != before) UpdateDrag(Mouse.GetPosition(this)); // 밑의 놓을 곳 다시 찾기
        };
        _edgeScroll.Start();
    }

    /// <summary>끄는 아이콘: 칸을 그대로 찍은 그림, 1.08배, 반투명, 그림자. 클릭이 통과하고 포커스를 가져가지 않는 창.</summary>
    private Window CreateGhost(Border source, Size size)
    {
        var dpi = VisualTreeHelper.GetDpi(source);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi.DpiScaleX), (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(P.CardBackground, null, new Rect(size), 10 * UiScale, 10 * UiScale);
            dc.DrawRectangle(new VisualBrush(source) { Stretch = Stretch.Fill }, null, new Rect(size)); // 판 배율만큼 (size 가 이미 배율 반영)
        }
        rtb.Render(dv);
        rtb.Freeze();
        var image = new Image
        {
            Source = rtb,
            Width = size.Width * LiftScale,
            Height = size.Height * LiftScale,
            Opacity = 0.9,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            IsHitTestVisible = false,
            Focusable = false,
            Content = new Border { Padding = new Thickness(12), Child = image }, // 그림자 자리
            Left = -32000,
            Top = -32000,
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            long ex = GetWindowLongPtr(h, -20).ToInt64();
            SetWindowLongPtr(h, -20, new IntPtr(ex | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */));
        };
        return w;
    }

    private void MoveGhost(Point panelPoint)
    {
        if (_drag is null) return;
        // 판 좌표 → 화면 픽셀, 잡은 자리(1.08배)와 그림자 여백만큼 빼서 픽셀로 바로 옮김 — DIP 변환·창 위치 반올림 차이로 떨어지지 않게.
        // 실제 마우스로 끌 때는 이벤트 좌표 대신 지금 커서 자리 (이벤트가 밀렸어도 늦은 자리에 놓지 않게)
        if (FollowRealCursor && CursorPx() is { } c) PlaceGhost(c);
        else PlaceGhost(PointToScreen(panelPoint));
    }

    /// <summary>커서 화면 픽셀 → 그림 창 왼쪽 위 (잡은 자리·그림자 여백 12 만큼 뺌).</summary>
    private void PlaceGhost(Point px)
    {
        if (_drag is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var h = new WindowInteropHelper(_drag.Ghost).Handle;
        int x = (int)Math.Round(px.X - (_drag.Grab.X + 12) * dpi.DpiScaleX), y = (int)Math.Round(px.Y - (_drag.Grab.Y + 12) * dpi.DpiScaleY);
        if (h != IntPtr.Zero) SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);
    }

    private static Point GhostTopLeftPx(Window ghost)
    {
        var h = new WindowInteropHelper(ghost).Handle;
        return h != IntPtr.Zero && GetWindowRect(h, out var r) ? new Point(r.Left, r.Top) : new Point();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Native.RECT rect);

    private void UpdateDrag(Point p)
    {
        var d = _drag;
        if (d is null) return;
        MoveGhost(p);

        // 판 밖(독·바탕 화면 쪽): 앱이면 윈도우 끌기로 넘김 — 독에 놓으면 고정
        bool outside = p.X < -6 || p.Y < -6 || p.X > ActualWidth + 6 || p.Y > ActualHeight + 6;
        if (outside && !d.Item.IsGroup && d.OleData is { } ole)
        {
            HandOffToWindows(ole);
            return;
        }

        var target = outside ? null : TargetAt(p, d.Item);
        SetHighlight(target is { Kind: "group" or "app" or "newfolder" } ? FindCell(target) : null);
        if (target is { Kind: "fav" or "favapp" }) ShiftFavorites(p);
        else ShiftFavorites(null);
        ShiftGroup(target is { Kind: "groupgrid" } && !d.Item.IsGroup ? d.Item.Key : null);
        d.Target = target;
        ((FrameworkElement)d.Ghost.Content).Opacity = target is null ? 0.45 : 1; // 놓을 수 없는 곳이면 흐리게
    }

    /// <summary>커서 아래의 놓을 곳 (앱: ★ 줄 / 묶음 칸 / 다른 앱(새 묶음), 묶음: 다른 묶음 칸).</summary>
    private DropTag? TargetAt(Point p, DragItem item)
    {
        DropTag? found = null;
        VisualTreeHelper.HitTest(this, null, r =>
        {
            for (DependencyObject? o = r.VisualHit; o is not null && o != this; o = VisualTreeHelper.GetParent(o))
            {
                if (o is FrameworkElement { Tag: DropTag { Kind: "suggest" } }) return HitTestResultBehavior.Stop; // 추천 칸 위 = 놓을 곳 아님 (자리 바꾸기 없음)
                if (o is not FrameworkElement { Tag: DropTag tag } fe || !Accepts(tag, item)) continue;
                // 펼친 묶음 안 앱 칸: 가운데(반지름 30%)에 놓으면 둘로 새 묶음, 가장자리면 이 묶음의 빈칸으로
                if (tag.Kind == "app" && fe is Border cell && _groupCells.Contains(cell) && _groupGridId is { } gid)
                {
                    var local = TranslatePoint(p, cell);
                    var center = new Point(cell.ActualWidth / 2, cell.ActualHeight / 2);
                    if ((local - center).Length > AppCell * 0.3) { found = new DropTag("groupgrid", gid); return HitTestResultBehavior.Stop; }
                }
                found = tag;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        return found;
    }

    private bool Accepts(DropTag tag, DragItem item) => item.IsRoutine
        ? tag.Kind == "routine" && tag.Key != item.Key // 루틴은 루틴 줄 안에서 순서만 (판 밖 = 독에 고정)
        : tag.Kind == "routine" ? false
        : item.IsGroup
        ? tag.Kind == "group" && tag.Key != item.Key && item.Key != AllAppsCatalog.Other
        : tag.Kind switch
        {
            "fav" => true,
            "favapp" => true,
            "groupgrid" => _groupApps.All(a => a.Key != item.Key) || tag.Key != _groupGridId, // 이미 이 폴더면 놓을 곳 아님
            "newfolder" => !item.IsGroup,
            "group" => true,
            "app" => tag.Key != item.Key,
            _ => false,
        } is var ok && ok;

    private Border? FindCell(DropTag tag)
    {
        Border? hit = null;
        void Walk(DependencyObject o)
        {
            if (hit is not null) return;
            if (o is Border { Tag: DropTag t } b && t == tag) { hit = b; return; }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
        Walk(this);
        return hit;
    }

    /// <summary>묶음 칸·다른 앱 위: 살짝 커지고(1.05) 테두리 강조.</summary>
    private void SetHighlight(Border? cell)
    {
        var d = _drag;
        if (d is null || d.Highlight == cell) return;
        if (d.Highlight is { } old)
        {
            Animate(old, 1);
            old.BorderBrush = Brushes.Transparent;
        }
        d.Highlight = cell;
        if (cell is not null)
        {
            // 폴더 칸 위: 1.08배 + 진한 테두리 ("놓으면 들어감" — 2026-10-13 사용자), 앱 칸 위: 1.05배 + 연한 테두리
            bool folder = cell.Tag is DropTag { Kind: "group" };
            Animate(cell, folder ? 1.08 : 1.05);
            if (cell.BorderThickness.Left < 1) cell.BorderThickness = new Thickness(1.5);
            cell.BorderBrush = folder ? P.Accent : P.SoftAccentLine;
        }

        static void Animate(Border b, double to)
        {
            if (b.RenderTransform is not ScaleTransform st)
            {
                b.RenderTransformOrigin = new Point(0.5, 0.5);
                b.RenderTransform = st = new ScaleTransform(1, 1);
            }
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ShiftMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    /// <summary>★ 줄: 놓일 자리를 만들려고 옆 칸들이 부드럽게 비켜남 (끄는 칸이 줄 안이면 그 자리는 먼저 메움). null = 원래대로.</summary>
    private void ShiftFavorites(Point? p)
    {
        var d = _drag;
        if (d is null || _favRow is null) return;
        int src = _favCells.IndexOf(d.Source);
        int index = -1;
        if (p is { } pt)
        {
            var local = TranslatePoint(pt, _favRow);
            index = Math.Clamp((int)Math.Round(local.X / AppCell), 0, Math.Max(0, _favCells.Count - (src >= 0 ? 1 : 0)));
        }
        d.FavIndex = index;
        for (int i = 0; i < _favCells.Count; i++)
        {
            var cell = _favCells[i];
            if (cell == d.Source) continue;
            int eff = src >= 0 && i > src ? i - 1 : i; // 끄는 칸을 뺀 순서
            double x = (eff - i) * AppCell + (index >= 0 && eff >= index ? AppCell : 0);
            if (cell.RenderTransform is not TranslateTransform tt) cell.RenderTransform = tt = new TranslateTransform();
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(ShiftMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>
    /// 펼친 묶음: 끄는 앱이 들어갈 이름순 자리에 빈칸을 만들고 뒤 칸들이 120ms 로 비켜남 (여러 줄 — 줄 끝은 다음 줄 처음으로).
    /// key = null 이면 원래대로.
    /// </summary>
    private void ShiftGroup(string? key)
    {
        if (_groupGrid is null) return;
        int gap = key is not null ? _groupApps.Count : -1; // 넣으면 맨 끝에 들어감
        if (gap == _groupGap) return;
        _groupGap = gap;
        int perRow = Math.Max(1, (int)(_groupGrid.Width / AppCell));
        double rowH = _groupCells.Count > 0 ? _groupCells[0].ActualHeight + 4 : AppCell;
        for (int i = 0; i < _groupCells.Count; i++)
        {
            int to = gap >= 0 && i >= gap ? i + 1 : i;
            double dx = (to % perRow - i % perRow) * AppCell, dy = (to / perRow - i / perRow) * rowH;
            var cell = _groupCells[i];
            if (cell.RenderTransform is not TranslateTransform tt) cell.RenderTransform = tt = new TranslateTransform();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, TimeSpan.FromMilliseconds(ShiftMs)) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, TimeSpan.FromMilliseconds(ShiftMs)) { EasingFunction = ease });
        }
    }

    /// <summary>펼친 묶음 빈칸 자리 (판 좌표).</summary>
    private Point GroupGapOrigin()
    {
        if (_groupGrid is null) return new Point();
        int perRow = Math.Max(1, (int)(_groupGrid.Width / AppCell));
        double rowH = _groupCells.Count > 0 ? _groupCells[0].ActualHeight + 4 : AppCell;
        int g = Math.Max(0, _groupGap);
        return _groupGrid.TranslatePoint(new Point(g % perRow * AppCell, g / perRow * rowH), this);
    }

    /// <summary>판 밖으로: 판 안 끌기를 접고 윈도우 끌기로 (독에 놓으면 고정, 바탕 화면엔 바로 가기 복사). 복사/링크만.</summary>
    private void HandOffToWindows(Func<DataObject> ole)
    {
        Log.Info("앱 모음 판: 판 밖으로 → 윈도우 끌기로 넘김");
        StopFollow();
        var d = _drag!;
        _drag = null;
        d.Ghost.Close();
        SetHighlightOff(d);
        _pressedCell = null;
        d.Source.ReleaseMouseCapture();
        try { DragData(d.Source, ole(), DragDropEffects.Copy | DragDropEffects.Link); }
        finally
        {
            d.Source.Opacity = 1;
            Dragging = false;
            ResetFavShift();
            ShiftGroup(null);
        }
    }

    private void SetHighlightOff(DragState d)
    {
        if (d.Highlight is { } h)
        {
            if (h.RenderTransform is ScaleTransform st) { st.BeginAnimation(ScaleTransform.ScaleXProperty, null); st.BeginAnimation(ScaleTransform.ScaleYProperty, null); st.ScaleX = st.ScaleY = 1; }
            h.BorderBrush = Brushes.Transparent;
        }
        d.Highlight = null;
    }

    private void ResetFavShift()
    {
        foreach (var c in _favCells)
            if (c.RenderTransform is TranslateTransform tt) { tt.BeginAnimation(TranslateTransform.XProperty, null); tt.X = 0; }
    }

    /// <summary>판이 닫힐 때 끄는 중이면: 아이콘 창을 바로 닫고 반영 없이 끝.</summary>
    private void CancelDragOnClose()
    {
        StopFollow();
        var d = _drag;
        _drag = null;
        Dragging = false;
        if (d is null) return;
        try { d.Ghost.Close(); } catch (InvalidOperationException) { /* 이미 닫힘 */ }
        Log.Info("앱 모음 판: 끄는 중 판이 닫힘 — 끌기 취소");
    }

    /// <summary>놓기(drop) 또는 취소: 아이콘이 새 자리(또는 원래 자리)로 미끄러져 들어간 뒤 반영.</summary>
    private void EndDrag(bool drop)
    {
        if (drop) FlushDragPending(); // 마지막 자리의 놓을 곳으로
        StopFollow();
        var d = _drag;
        if (d is null) return;
        if (PresentationSource.FromVisual(this) is null || PresentationSource.FromVisual(d.Source) is null) { CancelDragOnClose(); return; } // 판·칸이 화면에서 떨어짐
        _drag = null;
        var target = drop ? d.Target : null;
        Rect to;
        Action? apply = null;
        if (target is { Kind: "fav" or "favapp" } && !d.Item.IsGroup && _favRow is not null)
        {
            int index = Math.Max(0, d.FavIndex);
            var origin = _favRow.TranslatePoint(new Point(index * AppCell, 0), this);
            to = new Rect(origin, d.GhostSize);
            apply = () => PinAt(d.Item.Key, index);
        }
        else if (target is { Kind: "groupgrid" } && !d.Item.IsGroup)
        {
            to = new Rect(GroupGapOrigin(), d.GhostSize);
            apply = () => { if (_apps.FirstOrDefault(a => a.Key == d.Item.Key) is { } app) MoveTo(app, target.Key); };
        }
        else if (target is not null && FindCell(target) is { } cell)
        {
            to = new Rect(cell.TranslatePoint(new Point(0, 0), this), new Size(cell.ActualWidth, cell.ActualHeight));
            apply = target.Kind switch
            {
                "routine" => () => MoveRoutineBefore(d.Item.Key, target.Key),
                "group" when d.Item.IsGroup => () => MoveGroupBefore(d.Item.Key, target.Key),
                "group" => () => { if (_apps.FirstOrDefault(a => a.Key == d.Item.Key) is { } app) MoveTo(app, target.Key); },
                "app" => () => MakeGroupOf(target.Key, d.Item.Key),
                "newfolder" => () =>
                {
                    string id = AppFolders.Create(Services.Settings.Current, _apps, Loc.T("새 폴더"), d.Item.Key);
                    _expanded = id;
                    _renaming = id;
                    Save();
                },
                _ => null,
            };
        }
        else
        {
            to = new Rect(d.Source.TranslatePoint(new Point(0, 0), this), new Size(d.Source.ActualWidth, d.Source.ActualHeight)); // 원래 자리로
        }
        SetHighlightOff(d);
        Log.Info($"앱 모음 판: 끌기 끝 — {(apply is null ? (drop ? "놓을 곳 없음, 원래 자리로" : "취소, 원래 자리로") : $"{target!.Kind} 에 놓음{(target.Kind is "fav" or "favapp" ? $" (자리 {d.FavIndex})" : "")}")}");
        var dpi = VisualTreeHelper.GetDpi(this);
        var destPx = PointToScreen(to.TopLeft);
        SlideGhost(d.Ghost, new Point(destPx.X - 12 * dpi.DpiScaleX, destPx.Y - 12 * dpi.DpiScaleY), () =>
        {
            d.Source.Opacity = 1;
            Dragging = false;
            _groupGap = -1;
            if (apply is not null)
            {
                _justDropped = d.Item.Key; // 놓은 칸은 끄는 그림이 간 그 자리에 바로 (미끄러지기·나타나기 없음 — 나머지 칸만 움직임)
                apply(); // 저장 → 다시 그림 (비켜난 칸도 새 자리로)
                _justDropped = null;
            }
            else ResetFavShift();
            // 끄는 그림은 다시 그린 판이 화면에 나온 다음 프레임에 닫음 — 먼저 닫으면 (모든 앱을 펼쳐 둔 판은 다시 그리기가 길어) 그 칸이 잠깐 비어 보였음
            int frames = 0;
            EventHandler? tick = null;
            tick = (_, _) =>
            {
                if (++frames < 3) return;
                CompositionTarget.Rendering -= tick;
                d.Ghost.Close();
            };
            CompositionTarget.Rendering += tick;
        });
    }

    /// <summary>끄는 아이콘을 목표 위치(화면 픽셀)로 150ms 미끄러뜨림 (되튐 없음).</summary>
    private void SlideGhost(Window ghost, Point to, Action done)
    {
        // 놓을 때는 _drag 가 이미 비워진 뒤라 그 그림 창을 직접 봄 — 예전엔 빈 창을 봐서 (0,0) 화면 왼쪽 위에서 날아왔음 (2026-10-13 사용자 제보, 프레임으로 확인)
        var from = GhostTopLeftPx(ghost);
        double fromX = from.X, fromY = from.Y;
        var h = new WindowInteropHelper(ghost).Handle;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / SettleMs);
            double e = 1 - Math.Pow(1 - t, 3); // ease-out
            if (h != IntPtr.Zero)
                SetWindowPos(h, IntPtr.Zero, (int)Math.Round(fromX + (to.X - fromX) * e), (int)Math.Round(fromY + (to.Y - fromY) * e), 0, 0, 0x0001 | 0x0004 | 0x0010);
            if (t < 1) return;
            timer.Stop();
            done();
        };
        timer.Start();
    }

    /// <summary>★ 줄의 index 자리에 고정 (이미 고정돼 있으면 그 자리로 옮김).</summary>
    private void PinAt(string key, int index)
    {
        var pinned = S.Favorites.Where(k => !k.Equals(key, StringComparison.OrdinalIgnoreCase)
                                            && (_apps.Count == 0 || _apps.Any(a => a.Key.Equals(k, StringComparison.OrdinalIgnoreCase)))).ToList();
        pinned.Insert(Math.Clamp(index, 0, pinned.Count), key);
        S.Favorites.Clear();
        S.Favorites.AddRange(pinned);
        Save();
    }

    private void MakeGroupOf(string a, string b)
    {
        string id = AppFolders.Create(Services.Settings.Current, _apps, Loc.T("새 폴더"), a, b);
        _expanded = id;
        _renaming = id;
        Save();
    }

    /// <summary>Esc: 끄는 중이면 원래 자리로 (판은 닫지 않음).</summary>
    private bool CancelDragOnEscape()
    {
        if (_drag is null) return false;
        _pressedCell?.ReleaseMouseCapture();
        _pressedCell = null;
        EndDrag(drop: false);
        return true;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
