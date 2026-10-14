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
/// - 폴더 열기: 펼친 폴더가 위에서 아래로 드러나며 흐릿→보임(220ms) / 닫기: 거꾸로 접힌 뒤(200ms) 다시 그림 — 자리·배치는 그대로
/// - 편집 모드에 들어가면 −·× 표시가 0.6→1배로
/// - 마우스 올림: 아이콘 1.06배(100ms)
/// </summary>
internal sealed partial class AllAppsPanel
{
    private const int ExitMs = 180, MaxGhosts = 60;
    private const int SlideMs = 150, FadeMs = 150, SectionMs = 200, StepMs = 20, RowStepMs = 15, FolderMs = 200;
    private static bool Motion => Anim.Enabled;

    private bool _shownEditing;
    /// <summary>방금 놓은 앱·폴더 키 — 다시 그릴 때 그 칸은 움직이지 않고 바로 제자리에.</summary>
    private string? _justDropped;
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
                    if (_justDropped is { } dropped && el.Tag is DropTag dt && dt.Key.Equals(dropped, StringComparison.OrdinalIgnoreCase)) continue; // 놓은 칸: 그 자리에 바로
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

    // ── 폴더 열기·닫기 ──
    // 2026-10-13 사용자: 아이폰식(칸에서 커지는 카드·뒤 판 어둡게·스크롤)은 되돌림 — 열리는 방식·자리·배치는 예전 그대로(폴더 줄 아래에 펼침),
    // 움직임만 매끄럽게: 펼친 폴더가 위에서 아래로 드러나며(잘라 보이는 영역만 커짐 — 확대·글자 늘어남 없음) 흐릿→보임, 아래 줄은 같이 미끄러져 내려감.
    // 닫기는 거꾸로(아래에서 위로 접히며 흐려짐) 뒤 다시 그림 — 아래 줄은 미끄러져 올라옴. ease-out, 열기 220ms·닫기 200ms, 튐·두 번 커짐 없음.

    private const int FolderOpenMs = 220, FolderCloseMs = 200;

    private void FolderOpen(FrameworkElement box, string id)
    {
        if (!Motion || box.ActualHeight <= 0) return;
        if (box.RenderTransform is TranslateTransform) box.RenderTransform = null; // 새 칸 나타나기가 걸려 있으면 풀기
        box.BeginAnimation(OpacityProperty, null);
        Reveal(box, open: true, FolderOpenMs, null);
    }

    private void CloseFolderAnimated()
    {
        string? id = _expanded;
        if (!Motion || id is null || _expandedView is not { } box || box.ActualHeight <= 0)
        {
            _expanded = null;
            Rebuild();
            return;
        }
        Reveal(box, open: false, FolderCloseMs, () => { if (_expanded == id) { _expanded = null; Rebuild(); } });
    }

    /// <summary>펼친 폴더를 위에서부터 드러내기/접기: 보이는 영역(Clip) 높이와 불투명도만 움직임 (레이아웃·크기는 그대로).</summary>
    private static void Reveal(FrameworkElement box, bool open, int ms, Action? done)
    {
        double w = box.ActualWidth, h = box.ActualHeight;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shown = new Rect(0, 0, w, h);
        var hidden = new Rect(0, 0, w, Math.Min(h, 24)); // 이름 줄 높이쯤에서 시작/끝 (0 이면 빈칸이 갑자기 생긴 것처럼 보임)
        var clip = new RectangleGeometry(open ? hidden : shown, 12, 12);
        box.Clip = clip;
        var grow = new RectAnimation(open ? hidden : shown, open ? shown : hidden, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        grow.Completed += (_, _) =>
        {
            if (open) box.Clip = null;
            done?.Invoke();
        };
        clip.BeginAnimation(RectangleGeometry.RectProperty, grow);
        box.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, FillBehavior = open ? FillBehavior.Stop : FillBehavior.HoldEnd });
    }

    /// <summary>
    /// 따라갈 칸들 — 보이고 배치된 것만 (2026-10-13 사용자: 놓을 때 아이콘이 판 왼쪽 위에서 날아옴 — 접혀 있거나 아직 배치 안 된 칸은
    /// 위치가 (0,0)으로 재져서 다시 그린 뒤 거기서부터 미끄러졌음).
    /// </summary>
    private static IEnumerable<FrameworkElement> Keyed(DependencyObject root) =>
        Descendants(root).OfType<FrameworkElement>().Where(e => MotionKey(e) is not null && e.IsVisible && e.ActualWidth > 0 && e.ActualHeight > 0);

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

    /// <summary>칩 숫자가 처음 붙을 때 흐릿→보임.</summary>
    private static void FadeIn(UIElement el) =>
        el.BeginAnimation(OpacityProperty, Motion ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs)) { FillBehavior = FillBehavior.Stop } : null);
}
