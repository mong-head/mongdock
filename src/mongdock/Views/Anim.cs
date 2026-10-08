using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mongdock.Views;

/// <summary>
/// 가벼운 공용 애니메이션 도우미 (맥처럼 짧고 부드럽게, 원격(StarDesk)에서도 부담 적게).
/// - Opacity·RenderTransform 우선 (GPU 합성). 높이 접기/펼치기만 레이아웃 애니메이션.
/// - 시스템 "애니메이션 효과" 가 꺼져 있으면(SystemParameters.ClientAreaAnimation == false) 애니메이션 없이 최종 상태로 바로.
/// - done 콜백은 항상 한 번 호출 (애니메이션이 꺼져 있으면 즉시).
/// </summary>
internal static class Anim
{
    /// <summary>시스템 애니메이션 효과 켜짐 여부.</summary>
    public static bool Enabled
    {
        get
        {
            try { return SystemParameters.ClientAreaAnimation; }
            catch { return true; }
        }
    }

    public static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction QuintOut = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    /// <summary>살짝만 넘쳤다 돌아오는 감속 (배너 들어올 때). 과하지 않게 Amplitude 낮게.</summary>
    public static readonly IEasingFunction SoftBack = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 });

    private static IEasingFunction Frozen(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }

    public static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    public static DoubleAnimation To(double to, double ms, IEasingFunction? ease = null, double delayMs = 0) => new(to, Ms(ms))
    {
        EasingFunction = ease,
        BeginTime = TimeSpan.FromMilliseconds(delayMs),
    };

    public static DoubleAnimation FromTo(double from, double to, double ms, IEasingFunction? ease = null, double delayMs = 0) => new(from, to, Ms(ms))
    {
        EasingFunction = ease,
        BeginTime = TimeSpan.FromMilliseconds(delayMs),
    };

    /// <summary>요소의 RenderTransform 을 (Scale, Translate) 묶음으로 보장해 돌려준다 (이미 있으면 재사용).</summary>
    public static (ScaleTransform Scale, TranslateTransform Shift) Transforms(UIElement el)
    {
        if (el.RenderTransform is TransformGroup g && g.Children.Count == 2
            && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t && !g.IsFrozen)
            return (s, t);
        s = new ScaleTransform(1, 1);
        t = new TranslateTransform(0, 0);
        el.RenderTransform = new TransformGroup { Children = { s, t } };
        return (s, t);
    }

    /// <summary>
    /// 나타나기: 투명→불투명 + (선택) 크기 fromScale→1, 위치 (fromX, fromY)→0. 기준점은 origin(상대 좌표).
    /// </summary>
    public static void Appear(UIElement el, double ms, double fromScale = 1, double fromX = 0, double fromY = 0,
        IEasingFunction? ease = null, double delayMs = 0, Point? origin = null, double fadeMs = -1)
    {
        var (scale, shift) = Transforms(el);
        if (!Enabled)
        {
            Stop(el, scale, shift);
            el.Opacity = 1;
            return;
        }
        ease ??= EaseOut;
        if (origin is { } o) el.RenderTransformOrigin = o;
        el.BeginAnimation(UIElement.OpacityProperty, FromTo(0, 1, fadeMs > 0 ? fadeMs : ms, null, delayMs));
        if (fromScale != 1)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, FromTo(fromScale, 1, ms, ease, delayMs));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, FromTo(fromScale, 1, ms, ease, delayMs));
        }
        if (fromX != 0) shift.BeginAnimation(TranslateTransform.XProperty, FromTo(fromX, 0, ms, ease, delayMs));
        if (fromY != 0) shift.BeginAnimation(TranslateTransform.YProperty, FromTo(fromY, 0, ms, ease, delayMs));
    }

    /// <summary>사라지기: 불투명→투명 + (선택) (toX, toY) 로 이동, 크기 toScale. 끝나면 done.</summary>
    public static void Disappear(UIElement el, double ms, Action? done = null, double toX = 0, double toY = 0, double toScale = 1,
        IEasingFunction? ease = null, double delayMs = 0)
    {
        if (!Enabled)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            done?.Invoke();
            return;
        }
        ease ??= EaseIn;
        var (scale, shift) = Transforms(el);
        var fade = To(0, ms, ease, delayMs);
        if (done != null) fade.Completed += (_, _) => done();
        if (toX != 0) shift.BeginAnimation(TranslateTransform.XProperty, To(toX, ms, ease, delayMs));
        if (toY != 0) shift.BeginAnimation(TranslateTransform.YProperty, To(toY, ms, ease, delayMs));
        if (toScale != 1)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, To(toScale, ms, ease, delayMs));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, To(toScale, ms, ease, delayMs));
        }
        el.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>투명도만 (호버 × 등).</summary>
    public static void Fade(UIElement el, double to, double ms)
    {
        if (!Enabled)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = to;
            return;
        }
        el.BeginAnimation(UIElement.OpacityProperty, To(to, ms, EaseOut));
    }

    /// <summary>TranslateTransform 한 축을 from→0 (레이아웃이 한 번 바뀐 뒤 원래 자리에서 미끄러져 오게 — FLIP).</summary>
    public static void SlideFrom(TranslateTransform t, DependencyProperty axis, double from, double ms, IEasingFunction? ease = null)
    {
        if (!Enabled || Math.Abs(from) < 0.5)
        {
            t.BeginAnimation(axis, null);
            return;
        }
        t.BeginAnimation(axis, FromTo(from, 0, ms, ease ?? EaseOut));
    }

    /// <summary>
    /// 높이를 from→to 로 (실제 Height 애니메이션, 아래 요소가 함께 끌려 옴). 도중엔 ClipToBounds.
    /// clearAtEnd 면 끝난 뒤 애니메이션을 지워 Height 를 원래(자동)로 되돌림 — 펼치기용.
    /// </summary>
    public static void Height(FrameworkElement el, double from, double to, double ms, IEasingFunction? ease, Action? done, bool clearAtEnd)
    {
        if (!Enabled || double.IsNaN(from) || double.IsNaN(to))
        {
            if (clearAtEnd) el.BeginAnimation(FrameworkElement.HeightProperty, null);
            else el.Height = Math.Max(0, to);
            done?.Invoke();
            return;
        }
        bool clip = el.ClipToBounds;
        el.ClipToBounds = true;
        var a = FromTo(Math.Max(0, from), Math.Max(0, to), ms, ease ?? EaseOut);
        a.Completed += (_, _) =>
        {
            if (clearAtEnd)
            {
                el.BeginAnimation(FrameworkElement.HeightProperty, null);
                el.ClipToBounds = clip;
            }
            done?.Invoke();
        };
        el.BeginAnimation(FrameworkElement.HeightProperty, a);
    }

    /// <summary>높이와 바깥 여백을 0 으로 접음 (ease-out). 끝나면 done.</summary>
    public static void Collapse(FrameworkElement el, double ms, Action? done = null, double delayMs = 0)
    {
        el.IsHitTestVisible = false;
        if (!Enabled)
        {
            el.Visibility = Visibility.Collapsed;
            done?.Invoke();
            return;
        }
        if (delayMs > 0)
        {
            // 접기 시작 전까지는 잘라내지 않게(그림자 등) 실제 시작 시점에 높이를 읽고 시작
            var timer = new DispatcherTimer(DispatcherPriority.Render, el.Dispatcher) { Interval = TimeSpan.FromMilliseconds(delayMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Collapse(el, ms, done);
            };
            timer.Start();
            return;
        }
        el.ClipToBounds = true;
        var h = FromTo(el.ActualHeight, 0, ms, EaseOut);
        if (done != null) h.Completed += (_, _) => done();
        el.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(new Thickness(0), Ms(ms)) { EasingFunction = EaseOut });
        el.BeginAnimation(FrameworkElement.HeightProperty, h);
    }

    /// <summary>
    /// 맥 알림 지우기: 오른쪽으로 slide 만큼 밀려나며 흐려지고(fadeMs, ease-in) → 높이가 접힘(collapseMs, ease-out) → done.
    /// </summary>
    public static void SlideAway(FrameworkElement el, Action? done = null, double delayMs = 0,
        double slide = 40, double fadeMs = 180, double collapseMs = 200)
    {
        el.IsHitTestVisible = false;
        if (!Enabled)
        {
            el.Visibility = Visibility.Collapsed;
            done?.Invoke();
            return;
        }
        Disappear(el, fadeMs, () => Collapse(el, collapseMs, done), toX: slide, delayMs: delayMs);
    }

    /// <summary>
    /// 0 높이에서 자연 높이까지 펼치며 페이드 인 (빈 목록 문구 등). width = 측정 기준 폭.
    /// </summary>
    public static void Reveal(FrameworkElement el, double width, double ms = 200)
    {
        if (!Enabled || width <= 0)
        {
            el.Opacity = 1;
            return;
        }
        el.Measure(new Size(width, double.PositiveInfinity));
        double h = el.DesiredSize.Height - el.Margin.Top - el.Margin.Bottom;
        Height(el, 0, h, ms, EaseOut, null, clearAtEnd: true);
        el.BeginAnimation(UIElement.OpacityProperty, FromTo(0, 1, ms + 60, EaseOut, 40));
    }

    private static void Stop(UIElement el, ScaleTransform scale, TranslateTransform shift)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
    }
}
