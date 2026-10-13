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
/// - 다시 그릴 때(검색·칩·폴더·편집·추가/빼기·숨기기·끌어 놓기): 남는 칸은 새 자리로 미끄러지고(150ms), 새 칸은 위에서부터 차례로 흐릿→보임
/// - 폴더 열기: 그 칸 쪽에서 커지며(200ms) / 닫기: 줄어들며 흐려진 뒤 다시 그림
/// - 편집 모드에 들어가면 −·× 표시가 0.6→1배로
/// - 마우스 올림: 아이콘 1.06배(100ms)
/// </summary>
internal sealed partial class AllAppsPanel
{
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

    /// <summary>그림 창을 뜨기 직전: 둘째 구역부터 숨겨 둠 (그림과 진짜 판이 같은 모습 — 바꿔 끼울 때 번쩍이지 않게).</summary>
    protected override void BeforeIntroPicture()
    {
        if (!Motion || Sections() is not { } list) return;
        foreach (var s in list.Skip(1)) s.Opacity = 0;
    }

    /// <summary>진짜 판이 제자리에 놓인 뒤: 숨겨 둔 구역을 위에서부터 차례로.</summary>
    protected override void AfterIntro()
    {
        if (Sections() is not { } list) return;
        if (!Motion) { foreach (var s in list) s.Opacity = 1; return; }
        double viewport = ViewHeight;
        int i = 0;
        foreach (var s in list.Skip(1))
        {
            var top = s.TranslatePoint(new Point(0, 0), View).Y;
            if (top > viewport) { s.Opacity = 1; continue; } // 화면 밖은 그냥
            Appear(s, Math.Min(i++ * StepMs, 150), SectionMs, 8);
        }
    }

    /// <summary>처음 화면의 구역들 (루틴 줄·즐겨찾기·폴더·모든 앱 …).</summary>
    private List<FrameworkElement>? Sections() =>
        _body.Content is Panel root && _cleanup is null && _search.Text.Trim().Length == 0
            ? root.Children.OfType<FrameworkElement>().ToList() : null;

    // ── 다시 그리기: 미끄러지기 + 새 칸 나타나기 ──

    /// <summary>다시 그리기 전 칸 자리 (키 → _body 기준 위치).</summary>
    private Dictionary<string, Point>? SnapshotPositions()
    {
        if (!Motion || !IsLoaded || _body.Content is not FrameworkElement root || root.ActualWidth <= 0) return null;
        var map = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        foreach (var el in Keyed(root))
        {
            var key = MotionKey(el)!;
            if (!map.ContainsKey(key)) map[key] = el.TranslatePoint(new Point(0, 0), View);
        }
        return map;
    }

    /// <summary>다시 그린 뒤(배치가 끝난 다음): 남은 칸 미끄러지기, 새 칸 차례로 나타나기, 폴더·편집 표시.</summary>
    private void AnimateAfterRebuild(Dictionary<string, Point>? before, bool wasEditing, string? wasExpanded)
    {
        bool editEntered = _editing && !wasEditing;
        bool folderOpened = _expanded is not null && _expanded != wasExpanded;
        if (before is null && !editEntered && !folderOpened) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
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
                    if (before.TryGetValue(key, out var was))
                    {
                        double dx = was.X - now.X, dy = was.Y - now.Y;
                        if (Math.Abs(dx) + Math.Abs(dy) > 1 && Math.Abs(dy) < viewport) Slide(el, dx, dy);
                    }
                    else fresh.Add((el, now));
                }
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
            if (folderOpened && _expandedView is { } box)
            {
                // 누른 폴더 칸 쪽에서 커짐
                double originX = 0.5;
                if (_expandedFromCell is { } cellKey && Keyed(root).FirstOrDefault(e => MotionKey(e) == cellKey) is { } cell)
                {
                    var c = cell.TranslatePoint(new Point(cell.ActualWidth / 2, 0), box);
                    originX = box.ActualWidth > 0 ? Math.Clamp(c.X / box.ActualWidth, 0, 1) : 0.5;
                }
                box.RenderTransformOrigin = new Point(originX, 0);
                Grow(box, 0.94, FolderMs);
            }
        });
    }

    /// <summary>폴더 닫기: 펼친 칸이 줄어들며 흐려진 뒤(150ms) 다시 그림.</summary>
    private void CloseFolderAnimated()
    {
        if (!Motion || _expandedView is not { } box || !box.IsLoaded) { _expanded = null; Rebuild(); return; }
        var view = box;
        view.RenderTransformOrigin = new Point(0.5, 0);
        var scale = new ScaleTransform(1, 1);
        view.RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shrink = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(FadeMs)) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeMs)) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (_expandedView == view) { _expanded = null; Rebuild(); } };
        view.BeginAnimation(OpacityProperty, fade);
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
