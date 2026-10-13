using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mongdock.Views;

/// <summary>
/// 앱 모음 판 안 움직임 (spec-all-apps-panel.md "앱 모음 판 움직임", 2026-10-13 사용자: "갑자기 리스팅되는 느낌").
/// 맥 느낌: ease-out 만, 튕김·번쩍임·넘침 없음, 하나는 120~250ms, 화면 전체 350ms 안. 위치·투명도(RenderTransform/Opacity)만 움직이고
/// 레이아웃은 그대로. 윈도우 "애니메이션 효과"가 꺼져 있으면(Anim.Enabled) 모두 끔. 화면 밖 칸은 움직이지 않음.
/// - 판 열기: 그림 창으로 판이 커진 뒤, 둘째 구역부터 위에서 차례로(20ms 간격) 흐릿→보임 + 8px 올라옴 (첫 구역은 지금처럼 바로 보임)
/// - 다시 그릴 때(검색·칩·폴더·편집·추가/빼기·숨기기·끌어 놓기): 남는 칸은 새 자리로 미끄러지고(150ms), 새 칸은 위에서부터 차례로 흐릿→보임,
///   빠지는 칸은 옛 자리에서 흐려지며 작아짐(180ms — 옛 칸 그림을 판 위 겹층에 잠깐 그림)
/// - 폴더 열기 (아이폰 폴더처럼): 폴더 칸 사각형에서 펼친 폴더 사각형으로 위치·크기를 보간(250ms, 확대·글자 늘어남 없음),
///   뒤 판은 살짝 어두워지고, 안의 아이콘은 60% 지점부터 흐릿→보임. 닫기: 아이콘이 먼저 흐려지고 사각형이 그 칸으로 줄어듦(200ms), 어둡기도 풀림
/// - 편집 모드에 들어가면 −·× 표시가 0.6→1배로
/// - 마우스 올림: 아이콘 1.06배(100ms)
/// </summary>
internal sealed partial class AllAppsPanel
{
    private const int ExitMs = 180, MaxGhosts = 60;
    private const int SlideMs = 150, FadeMs = 150, SectionMs = 200, StepMs = 20, RowStepMs = 15, FolderMs = 200;
    private static bool Motion => Anim.Enabled;

    private bool _shownEditing;
    /// <summary>판 본문 스크롤 (보이는 칸만 움직이게 — 위치는 이 보기 기준).</summary>
    private ScrollViewer? _bodyScroll;
    private UIElement View => (UIElement?)_bodyScroll ?? _body;
    private double ViewHeight => _bodyScroll is { ViewportHeight: > 0 } sv ? sv.ViewportHeight : _bodyMaxHeight;
    private string? _shownExpanded;
    /// <summary>지금 그려진 펼친 폴더 (열기·닫기 움직임).</summary>
    private FrameworkElement? _expandedView;
    private string? _expandedFromCell;

    // ── 판 열기: 구역 차례로 ──

    // 판 열기: 판이 커지는 동안(그림 창) 70% 지점부터 내용이 겹쳐 나타나고, 다 열린 뒤에는 내용이 따로 다시 나오지 않음
    // (2026-10-13 "열릴 때 두 번 열리는 듯" — 예전엔 판이 다 커진 뒤 구역이 차례로 나타나 두 번 움직이는 것처럼 보였음)
    protected override bool IntroContentFade => Motion;

    /// <summary>빈 판 그림을 뜨기 직전: 첫 구역(루틴 줄)만 남기고 숨김 — 첫 칸은 예전처럼 처음부터 보임.</summary>
    protected override void BeforeIntroPicture()
    {
        if (Sections() is not { } list) return;
        foreach (var s in list.Skip(1)) s.Opacity = 0;
    }

    protected override void AfterIntroPicture()
    {
        if (Sections() is not { } list) return;
        foreach (var s in list) s.Opacity = 1;
    }

    /// <summary>처음 화면의 구역들 (루틴 줄·즐겨찾기·폴더·모든 앱 …).</summary>
    private List<FrameworkElement>? Sections() =>
        _body.Content is Panel root && _cleanup is null && _search.Text.Trim().Length == 0
            ? root.Children.OfType<FrameworkElement>().ToList() : null;

    // ── 다시 그리기: 미끄러지기 + 새 칸 나타나기 ──

    /// <summary>다시 그리기 전 칸 자리 (키 → _body 기준 위치).</summary>
    private Dictionary<string, (Point At, FrameworkElement El)>? SnapshotPositions()
    {
        if (!Motion || !IsLoaded || _body.Content is not FrameworkElement root || root.ActualWidth <= 0) return null;
        var map = new Dictionary<string, (Point, FrameworkElement)>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in Keyed(root))
        {
            var key = MotionKey(el)!;
            if (!map.ContainsKey(key)) map[key] = (el.TranslatePoint(new Point(0, 0), View), el);
        }
        return map;
    }

    /// <summary>다시 그린 뒤(배치가 끝난 다음): 남은 칸 미끄러지기, 새 칸 차례로 나타나기, 폴더·편집 표시.</summary>
    private void AnimateAfterRebuild(Dictionary<string, (Point At, FrameworkElement El)>? before, bool wasEditing, string? wasExpanded)
    {
        bool editEntered = _editing && !wasEditing;
        bool folderOpened = _expanded is not null && _expanded != wasExpanded;
        if (before is null && !editEntered && !folderOpened) return;
        // 배치를 바로 끝내고 같은 틀 안에서 움직임을 걸어 둠 — 미루면 새 모습이 한 프레임 먼저 그려져 번쩍임 (실제 창 프레임 확인)
        if (IsLoaded) UpdateLayout();
        {

            if (IsClosing || _body.Content is not FrameworkElement root) return;
            double viewport = ViewHeight;
            if (before is not null)
            {
                var fresh = new List<(FrameworkElement El, Point At)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var el in Keyed(root))
                {
                    var key = MotionKey(el)!;
                    if (!seen.Add(key)) continue;
                    var now = el.TranslatePoint(new Point(0, 0), View);
                    if (now.Y > viewport || now.Y + el.ActualHeight < 0) continue; // 화면 밖은 움직이지 않음
                    if (before.TryGetValue(key, out var old))
                    {
                        var was = old.At;
                        double dx = was.X - now.X, dy = was.Y - now.Y;
                        if (Math.Abs(dx) + Math.Abs(dy) > 1 && Math.Abs(dy) < viewport) Slide(el, dx, dy);
                    }
                    else if (el != _expandedView) fresh.Add((el, now)); // 펼친 폴더는 폴더 움직임이 따로
                }
                // 빠진 칸: 옛 자리에서 흐려지며 작아짐 (보이던 것만)
                var gone = before.Where(kv => !seen.Contains(kv.Key) && kv.Value.At.Y < viewport && kv.Value.At.Y + kv.Value.El.ActualHeight > 0)
                    .Take(MaxGhosts).Select(kv => kv.Value).ToList();
                if (gone.Count > 0) Ghosts(gone);
                // 새로 들어온 칸: 위에서부터 차례로 (최대 10단계 = 150ms)
                int step = 0;
                double lastY = double.NaN;
                foreach (var (el, at) in fresh.OrderBy(f => f.At.Y).ThenBy(f => f.At.X))
                {
                    if (!double.IsNaN(lastY) && at.Y - lastY > 4) step = Math.Min(step + 1, 10);
                    lastY = at.Y;
                    Appear(el, step * RowStepMs, FadeMs, 6);
                }
            }
            if (editEntered)
                foreach (var badge in Descendants(root).OfType<FrameworkElement>().Where(e => e.Uid == "editbadge"))
                    Grow(badge, 0.6, FadeMs);
            if (folderOpened && _expandedView is { } box && _expanded is { } id) FolderOpen(box, id);
            else if (_expanded is null) FolderDimOff(animate: false);
            else UpdateFolderHole();
        }
    }

    /// <summary>빠진 칸들의 옛 모습(VisualBrush — 판에서 떨어진 칸도 마지막 모습을 그림)을 본문 위 겹층에 놓고 흐려지며 0.9배로 줄인 뒤 치움.</summary>
    private void Ghosts(List<(Point At, FrameworkElement El)> gone)
    {
        if (_bodyScroll is not { } scroll || System.Windows.Documents.AdornerLayer.GetAdornerLayer(scroll) is not { } layer) return;
        var ghost = new GhostLayer(scroll);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var (at, el) in gone)
        {
            if (el.ActualWidth <= 0 || el.ActualHeight <= 0) continue;
            var r = new System.Windows.Shapes.Rectangle
            {
                Width = el.ActualWidth,
                Height = el.ActualHeight,
                Fill = new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
                IsHitTestVisible = false,
            };
            ghost.Add(r, at);
            var shrink = new DoubleAnimation(1, 0.9, TimeSpan.FromMilliseconds(ExitMs)) { EasingFunction = ease };
            r.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            r.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            r.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ExitMs)) { EasingFunction = ease });
        }
        layer.Add(ghost);
        var done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ExitMs + 30) };
        done.Tick += (_, _) => { done.Stop(); layer.Remove(ghost); };
        done.Start();
    }

    /// <summary>빠지는 칸 그림을 잠깐 얹는 겹층 (본문 스크롤 영역 안으로 잘림, 마우스는 통과).</summary>
    private sealed class GhostLayer : System.Windows.Documents.Adorner
    {
        private readonly VisualCollection _children;
        private readonly List<(UIElement El, Point At)> _items = new();

        public GhostLayer(UIElement adorned) : base(adorned)
        {
            _children = new VisualCollection(this);
            IsHitTestVisible = false;
            ClipToBounds = true;
        }

        public void Add(UIElement el, Point at) { _children.Add(el); _items.Add((el, at)); }
        protected override int VisualChildrenCount => _children.Count;
        protected override Visual GetVisualChild(int index) => _children[index];

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var (el, at) in _items) el.Arrange(new Rect(at, el.DesiredSize));
            return finalSize;
        }

        protected override Size MeasureOverride(Size constraint)
        {
            foreach (var (el, _) in _items) el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Geometry GetLayoutClip(Size layoutSlotSize) => new RectangleGeometry(new Rect(AdornedElement.RenderSize));
    }

    // ── 폴더 열기·닫기 (아이폰 폴더처럼) ──

    private const int FolderOpenMs = 250, FolderCloseMs = 200, FolderContentFadeMs = 100;
    private FolderLayer? _folderLayer;

    private Rect? RectIn(FrameworkElement el) =>
        el.ActualWidth > 0 && el.IsDescendantOf(View) ? new Rect(el.TranslatePoint(new Point(0, 0), View), new Size(el.ActualWidth, el.ActualHeight)) : null;

    private FrameworkElement? TileOf(string id) =>
        _body.Content is DependencyObject root ? Descendants(root).OfType<FrameworkElement>().FirstOrDefault(e => e.Uid == "tile:" + id) : null;

    private FolderLayer? EnsureFolderLayer()
    {
        if (_folderLayer is not null) return _folderLayer;
        if (_bodyScroll is not { } scroll || System.Windows.Documents.AdornerLayer.GetAdornerLayer(scroll) is not { } layer) return null;
        _folderLayer = new FolderLayer(scroll, P.IsLight ? Color.FromArgb(0x1A, 0, 0, 0) : Color.FromArgb(0x40, 0, 0, 0));
        layer.Add(_folderLayer);
        scroll.ScrollChanged += (_, _) => UpdateFolderHole();
        return _folderLayer;
    }

    /// <summary>열기: 폴더 칸 사각형 → 펼친 폴더 사각형 (Canvas 위치·크기 보간 — 확대가 아니라 글자·아이콘이 늘어나지 않음).</summary>
    private void FolderOpen(FrameworkElement box, string id)
    {
        if (!Motion || TileOf(id) is not { } tile || RectIn(tile) is not { } from || RectIn(box) is not { } to || EnsureFolderLayer() is not { } layer) return;
        if (box.RenderTransform is TranslateTransform) box.RenderTransform = null; // 새 칸으로 나타나기가 걸려 있으면 풀기
        box.BeginAnimation(OpacityProperty, null);
        box.Opacity = 0; // 진짜 펼친 폴더는 다 커진 뒤에 (그동안은 겹층의 카드가 대신)
        // 펼친 폴더가 판 아래로 잘리면 같은 250ms 동안 부드럽게 올려 다 보이게 (카드는 올라간 뒤 자리로)
        if (_bodyScroll is { } sv && to.Bottom > sv.ViewportHeight - 8)
        {
            double delta = Math.Min(to.Bottom - sv.ViewportHeight + 8, Math.Max(0, to.Top - 8)); // 폴더 위쪽이 판 위로 나가지 않을 만큼만
            delta = Math.Min(delta, sv.ScrollableHeight - sv.VerticalOffset);
            if (delta > 1)
            {
                to = new Rect(to.X, to.Y - delta, to.Width, to.Height);
                ScrollBy(sv, delta, FolderOpenMs);
            }
        }
        var content = (box as Border)?.Child as FrameworkElement;
        layer.Morph(from, to, FolderOpenMs, P.Tile, 14, 12, content, contentFrom: 0, contentTo: 1, contentBeginMs: (int)(FolderOpenMs * 0.6), contentMs: FolderContentFadeMs, () =>
        {
            box.Opacity = 1;
            UpdateFolderHole();
        });
        layer.Dim(true, FolderOpenMs, to);
    }

    /// <summary>닫기: 안의 아이콘이 먼저 흐려지고(80ms) 사각형이 폴더 칸으로 줄어듦(200ms), 어둡기도 풀림 → 다시 그림.</summary>
    private void CloseFolderAnimated()
    {
        string? id = _expanded;
        if (!Motion || id is null || _expandedView is not { } box || TileOf(id) is not { } tile || RectIn(box) is not { } from || RectIn(tile) is not { } to || EnsureFolderLayer() is not { } layer)
        {
            _expanded = null;
            Rebuild();
            return;
        }
        var content = (box as Border)?.Child as FrameworkElement;
        box.Opacity = 0;
        layer.Morph(from, to, FolderCloseMs, P.Tile, 12, 14, content, contentFrom: 1, contentTo: 0, contentBeginMs: 0, contentMs: 80, () =>
        {
            layer.Clear();
            if (_expanded == id) { _expanded = null; Rebuild(); }
        });
        layer.Dim(false, FolderCloseMs, null);
    }

    /// <summary>본문 스크롤을 ms 동안 ease-out 으로 delta 만큼 (ScrollViewer 는 애니메이션 속성이 없어 렌더 프레임마다).</summary>
    private static void ScrollBy(ScrollViewer sv, double delta, int ms)
    {
        double from = sv.VerticalOffset;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            double t = Math.Min(1, clock.ElapsedMilliseconds / (double)ms);
            sv.ScrollToVerticalOffset(from + delta * (1 - Math.Pow(1 - t, 3)));
            if (t >= 1) CompositionTarget.Rendering -= tick;
        };
        CompositionTarget.Rendering += tick;
    }

    private void UpdateFolderHole()
    {
        if (_folderLayer is null) return;
        if (_expanded is null || _expandedView is not { } box || RectIn(box) is not { } r) { _folderLayer.Dim(false, 0, null); return; }
        _folderLayer.Hole(r);
    }

    private void FolderDimOff(bool animate) => _folderLayer?.Dim(false, animate ? FolderCloseMs : 0, null);

    /// <summary>폴더 움직임 겹층: 뒤 판 어둡게(펼친 폴더 자리만 뚫림) + 칸↔폴더 사이를 오가는 카드. 마우스는 통과, 본문 스크롤 안으로 잘림.</summary>
    private sealed class FolderLayer : System.Windows.Documents.Adorner
    {
        private readonly Canvas _canvas = new() { IsHitTestVisible = false };
        private readonly System.Windows.Shapes.Path _dim;
        private readonly Color _dimColor;
        private Border? _card;
        private Rect _hole;

        public FolderLayer(UIElement adorned, Color dim) : base(adorned)
        {
            IsHitTestVisible = false;
            _dimColor = dim;
            _dim = new System.Windows.Shapes.Path { Fill = new SolidColorBrush(dim), Opacity = 0, IsHitTestVisible = false };
            _canvas.Children.Add(_dim);
            AddVisualChild(_canvas);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _canvas;
        protected override Size MeasureOverride(Size constraint) { _canvas.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { _canvas.Arrange(new Rect(finalSize)); UpdateDim(); return finalSize; }
        protected override Geometry GetLayoutClip(Size layoutSlotSize) => new RectangleGeometry(new Rect(AdornedElement.RenderSize));

        private void UpdateDim()
        {
            var all = new RectangleGeometry(new Rect(AdornedElement.RenderSize));
            _dim.Data = _hole.IsEmpty ? all : new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(_hole, 12, 12));
        }

        public void Hole(Rect r) { _hole = r; UpdateDim(); }

        public void Dim(bool on, int ms, Rect? hole)
        {
            if (hole is { } h) _hole = h;
            else if (!on && ms == 0) _hole = Rect.Empty;
            UpdateDim();
            var a = new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(Math.Max(1, ms))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _dim.BeginAnimation(OpacityProperty, a);
        }

        public void Clear()
        {
            if (_card is not null) { _canvas.Children.Remove(_card); _card = null; }
        }

        /// <summary>카드 사각형을 from → to 로 (위치·크기 보간), 안의 모습(content 그림)은 contentBeginMs 부터 contentFrom→contentTo.</summary>
        public void Morph(Rect from, Rect to, int ms, Brush background, double radiusFrom, double radiusTo, FrameworkElement? content,
            double contentFrom, double contentTo, int contentBeginMs, int contentMs, Action done)
        {
            Clear();
            var inner = new System.Windows.Shapes.Rectangle
            {
                // 내용은 늘이지 않고 왼쪽 위 기준으로 잘라 보임 (늘어남 없음)
                Fill = content is not null ? new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, Math.Max(1, content.ActualWidth), Math.Max(1, content.ActualHeight)), ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(8, 8, Math.Max(1, content.ActualWidth), Math.Max(1, content.ActualHeight)), TileMode = TileMode.None } : null,
                Opacity = contentFrom,
            };
            _card = new Border { Background = background, CornerRadius = new CornerRadius(radiusFrom), Child = inner, ClipToBounds = true, IsHitTestVisible = false };
            _canvas.Children.Add(_card);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(ms);
            Canvas.SetLeft(_card, from.X);
            Canvas.SetTop(_card, from.Y);
            _card.Width = from.Width;
            _card.Height = from.Height;
            _card.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(from.X, to.X, d) { EasingFunction = ease });
            _card.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(from.Y, to.Y, d) { EasingFunction = ease });
            _card.BeginAnimation(WidthProperty, new DoubleAnimation(from.Width, to.Width, d) { EasingFunction = ease });
            var h = new DoubleAnimation(from.Height, to.Height, d) { EasingFunction = ease };
            var card = _card;
            h.Completed += (_, _) => { if (_card == card) { Clear(); } done(); };
            _card.BeginAnimation(HeightProperty, h);
            inner.BeginAnimation(OpacityProperty, new DoubleAnimation(contentFrom, contentTo, TimeSpan.FromMilliseconds(contentMs)) { BeginTime = TimeSpan.FromMilliseconds(contentBeginMs), EasingFunction = ease });
        }
    }

    private static IEnumerable<FrameworkElement> Keyed(DependencyObject root) =>
        Descendants(root).OfType<FrameworkElement>().Where(e => MotionKey(e) is not null);

    /// <summary>움직임으로 따라갈 칸의 이름: 끌기용 표시(앱·폴더 칸) 또는 Uid(목록 줄).</summary>
    private static string? MotionKey(FrameworkElement e) =>
        e.Tag is DropTag d ? d.Kind + ":" + d.Key : e.Uid is { Length: > 2 } u && u.StartsWith("m:", StringComparison.Ordinal) ? u : null;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    // ── 움직임 조각 ──

    private static void Slide(FrameworkElement el, double dx, double dy)
    {
        if (el.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true }) && el.RenderTransform is not TranslateTransform) return; // 다른 움직임 중
        var t = new TranslateTransform(dx, dy);
        el.RenderTransform = t;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(SlideMs)) { EasingFunction = ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(SlideMs)) { EasingFunction = ease });
    }

    /// <summary>흐릿→보임 + 아래에서 rise px 올라옴 (delay 뒤 시작, 그동안은 안 보임).</summary>
    private static void Appear(FrameworkElement el, int delayMs, int ms, double rise)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.Opacity = 1;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        if (el.RenderTransform is null or MatrixTransform { Matrix.IsIdentity: true })
        {
            var t = new TranslateTransform(0, rise);
            el.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, TimeSpan.FromMilliseconds(ms)) { BeginTime = begin, EasingFunction = ease });
        }
        // 시작 전 구간도 안 보이게 (BeginTime 동안은 애니메이션 값이 없어 원래 값 1 이 보임)
        if (delayMs > 0)
        {
            el.Opacity = 0;
            var hold = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delayMs + ms), FillBehavior = FillBehavior.Stop };
            hold.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            hold.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(begin)));
            hold.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + ms)), ease));
            hold.Completed += (_, _) => el.Opacity = 1;
            el.BeginAnimation(OpacityProperty, hold);
        }
    }

    /// <summary>from 배 → 1배 + 흐릿→보임 (편집 표시·폴더 열기).</summary>
    private static void Grow(FrameworkElement el, double from, int ms)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (el.RenderTransformOrigin == default) el.RenderTransformOrigin = new Point(0.5, 0.5);
        var s = new ScaleTransform(from, from);
        el.RenderTransform = s;
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>마우스 올림: 아이콘 1.06배 (100ms) / 내림: 1배.</summary>
    private static void HoverIcon(FrameworkElement icon, bool on)
    {
        if (!Motion) return;
        if (icon.RenderTransform is not ScaleTransform s)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = s = new ScaleTransform(1, 1);
        }
        var a = new DoubleAnimation(on ? 1.06 : 1, TimeSpan.FromMilliseconds(100)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>칩 숫자가 처음 붙을 때 흐릿→보임.</summary>
    private static void FadeIn(UIElement el) =>
        el.BeginAnimation(OpacityProperty, Motion ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs)) { FillBehavior = FillBehavior.Stop } : null);
}
