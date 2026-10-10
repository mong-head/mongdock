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
    private sealed record DragItem(bool IsGroup, string Key);

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
            }
            UpdateDrag(p);
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
        var size = new Size(source.ActualWidth, source.ActualHeight);
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
        _drag.Ghost.Show();
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
            dc.DrawRoundedRectangle(P.CardBackground, null, new Rect(size), 10, 10);
            dc.DrawRectangle(new VisualBrush(source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
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
        _drag.Ghost.Left = Left + panelPoint.X - _drag.Grab.X - 12;
        _drag.Ghost.Top = Top + panelPoint.Y - _drag.Grab.Y - 12;
    }

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
        SetHighlight(target is { Kind: "group" or "app" } ? FindCell(target) : null);
        if (target is { Kind: "fav" or "favapp" }) ShiftFavorites(p);
        else ShiftFavorites(null);
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
                if (o is FrameworkElement { Tag: DropTag tag } && Accepts(tag, item)) { found = tag; return HitTestResultBehavior.Stop; }
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(p));
        return found;
    }

    private bool Accepts(DropTag tag, DragItem item) => item.IsGroup
        ? tag.Kind == "group" && tag.Key != item.Key && item.Key != AllAppsCatalog.Other
        : tag.Kind switch
        {
            "fav" => true,
            "favapp" => true,
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
            Animate(cell, 1.05);
            cell.BorderBrush = P.Accent;
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

    /// <summary>판 밖으로: 판 안 끌기를 접고 윈도우 끌기로 (독에 놓으면 고정, 바탕 화면엔 바로 가기 복사). 복사/링크만.</summary>
    private void HandOffToWindows(Func<DataObject> ole)
    {
        Log.Info("앱 모음 판: 판 밖으로 → 윈도우 끌기로 넘김");
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
            ResetFavShift();
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

    /// <summary>놓기(drop) 또는 취소: 아이콘이 새 자리(또는 원래 자리)로 미끄러져 들어간 뒤 반영.</summary>
    private void EndDrag(bool drop)
    {
        var d = _drag;
        if (d is null) return;
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
        else if (target is not null && FindCell(target) is { } cell)
        {
            to = new Rect(cell.TranslatePoint(new Point(0, 0), this), new Size(cell.ActualWidth, cell.ActualHeight));
            apply = target.Kind switch
            {
                "group" when d.Item.IsGroup => () => MoveGroupBefore(d.Item.Key, target.Key),
                "group" => () => { if (_apps.FirstOrDefault(a => a.Key == d.Item.Key) is { } app) MoveTo(app, target.Key); },
                "app" => () => MakeGroupOf(target.Key, d.Item.Key),
                _ => null,
            };
        }
        else
        {
            to = new Rect(d.Source.TranslatePoint(new Point(0, 0), this), new Size(d.Source.ActualWidth, d.Source.ActualHeight)); // 원래 자리로
        }
        SetHighlightOff(d);
        Log.Info($"앱 모음 판: 끌기 끝 — {(apply is null ? (drop ? "놓을 곳 없음, 원래 자리로" : "취소, 원래 자리로") : $"{target!.Kind} 에 놓음{(target.Kind is "fav" or "favapp" ? $" (자리 {d.FavIndex})" : "")}")}");
        SlideGhost(d.Ghost, new Point(Left + to.X - 12, Top + to.Y - 12), () =>
        {
            d.Ghost.Close();
            d.Source.Opacity = 1;
            if (apply is not null) apply(); // 저장 → 다시 그림 (비켜난 칸도 새 자리로)
            else ResetFavShift();
        });
    }

    /// <summary>끄는 아이콘을 목표 위치로 150ms 미끄러뜨림 (되튐 없음).</summary>
    private void SlideGhost(Window ghost, Point to, Action done)
    {
        double fromX = ghost.Left, fromY = ghost.Top;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / SettleMs);
            double e = 1 - Math.Pow(1 - t, 3); // ease-out
            ghost.Left = fromX + (to.X - fromX) * e;
            ghost.Top = fromY + (to.Y - fromY) * e;
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
        if (pinned.Count > FavMax) pinned.RemoveRange(FavMax, pinned.Count - FavMax);
        S.Favorites.Clear();
        S.Favorites.AddRange(pinned);
        Save(false);
    }

    private void MakeGroupOf(string a, string b)
    {
        string id = NewGroup();
        S.Overrides[a] = id;
        S.Overrides[b] = id;
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
