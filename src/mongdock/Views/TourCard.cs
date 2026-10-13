using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>둘러보기 한 단계: 제목·설명·움직이는 그림(2~4장).</summary>
internal sealed record TourStep(string Title, string Body, IReadOnlyList<ImageSource> Frames);

/// <summary>
/// 둘러보기 가운데 카드 (spec-coach-tour.md, 2026-10-14 사용자): 폭 560, 둥근 16, 판 색.
/// 위 = 그림 영역(16:10) — 여러 장이면 3.5초마다 교차 흐림(300ms)으로 넘어가고, 아래 작은 점으로 몇 번째인지, 마우스를 올리면 멈춤.
/// 아래 = 제목 + 설명 + 단계 점 + [이전] [다음 →], 오른쪽 위 [건너뛰기]. 윈도우 애니메이션이 꺼져 있으면 첫 장만.
/// 내용만 바꿔 쓰는 한 장 (첫 설치·새 기능·설정에서 다시 보기 모두).
/// </summary>
internal sealed class TourCard : Border
{
    public const double CardWidth = 560;
    private const double PictureHeight = CardWidth * 10 / 16;
    private const int SlideMs = 3500, FadeMs = 300;

    private readonly UiPalette _p;
    private readonly Image _front = new() { Stretch = Stretch.Uniform };
    private readonly Image _back = new() { Stretch = Stretch.Uniform, Opacity = 0 };
    private readonly StackPanel _frameDots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly StackPanel _stepDots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _prev;
    private readonly Button _next;
    private readonly DispatcherTimer _slide = new() { Interval = TimeSpan.FromMilliseconds(SlideMs) };
    private IReadOnlyList<ImageSource> _frames = Array.Empty<ImageSource>();
    private int _frame;

    public event Action? PrevClicked;
    public event Action? NextClicked;
    public event Action? SkipClicked;

    public TourCard(UiPalette p)
    {
        _p = p;
        Width = CardWidth;
        CornerRadius = new CornerRadius(16);
        Background = p.CardBackground;
        BorderBrush = p.CardBorder;
        BorderThickness = new Thickness(0.5);
        Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = p.ShadowOpacity * 0.8, Color = p.ShadowColor };

        // 그림 영역 (위 모서리만 둥글게 — 카드 모양대로 잘림)
        RenderOptions.SetBitmapScalingMode(_front, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(_back, BitmapScalingMode.HighQuality);
        var picture = new Grid { Height = PictureHeight, ClipToBounds = true };
        picture.Children.Add(new Border { Background = p.PanelTint, CornerRadius = new CornerRadius(16, 16, 0, 0) });
        var frames = new Grid { Margin = new Thickness(18, 18, 18, 26) };
        frames.Children.Add(_back);
        frames.Children.Add(_front);
        picture.Children.Add(frames);
        picture.Children.Add(_frameDots);
        var skip = new TextBlock
        {
            Text = Loc.T("건너뛰기"),
            FontSize = UiText.Sub,
            Foreground = p.PanelTintText,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 16, 0),
        };
        skip.MouseLeftButtonUp += (_, e) => { e.Handled = true; SkipClicked?.Invoke(); };
        picture.Children.Add(skip);
        picture.MouseEnter += (_, _) => _slide.Stop(); // 마우스를 올리면 멈춤
        picture.MouseLeave += (_, _) => StartSlides();

        _title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = p.Text, TextWrapping = TextWrapping.Wrap };
        _body = new TextBlock { FontSize = UiText.Body, Foreground = p.SubText, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 6, 0, 0) };
        _prev = Button(Loc.T("이전"), primary: false);
        _next = Button(Loc.T("다음 →"), primary: true);
        _prev.Click += (_, _) => PrevClicked?.Invoke();
        _next.Click += (_, _) => NextClicked?.Invoke();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _next.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(_prev);
        buttons.Children.Add(_next);
        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_stepDots);
        var text = new StackPanel { Margin = new Thickness(24, 18, 24, 20) };
        text.Children.Add(_title);
        text.Children.Add(_body);
        text.Children.Add(footer);

        var stack = new StackPanel();
        stack.Children.Add(picture);
        stack.Children.Add(text);
        Child = stack;
        _slide.Tick += (_, _) => ShowFrame(_frame + 1, animate: true);
        Unloaded += (_, _) => _slide.Stop();
    }

    private Button Button(string text, bool primary) => new()
    {
        Style = (Style)Application.Current.FindResource("CardButton"),
        Content = new TextBlock { Text = text, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
        Background = primary ? _p.PanelTint : _p.Tile,
        Foreground = primary ? _p.PanelTintText : _p.Text,
        Height = 30,
        Padding = new Thickness(16, 0, 16, 0),
    };

    /// <summary>단계 내용 바꾸기 (index/count = 단계 점, 처음이면 [이전] 숨김, 끝이면 [다음] 글자가 "시작하기").</summary>
    public void Show(TourStep step, int index, int count)
    {
        _title.Text = step.Title;
        _body.Text = step.Body;
        _prev.Visibility = index > 0 ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)_next.Content).Text = index >= count - 1 ? Loc.T("시작하기") : Loc.T("다음 →");
        _stepDots.Children.Clear();
        for (int i = 0; i < count; i++) // 지금 단계는 길쭉한 알약, 나머지는 점
            _stepDots.Children.Add(new Border { Width = i == index ? 16 : 6, Height = 6, CornerRadius = new CornerRadius(3), Background = i == index ? _p.AccentFill : _p.Divider, Margin = new Thickness(0, 0, 5, 0) });
        _frames = step.Frames;
        _frameDots.Visibility = _frames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _frame = 0;
        _back.Source = null;
        _back.Opacity = 0;
        ShowFrame(0, animate: false);
        StartSlides();
    }

    /// <summary>시험 그림에서만: 몇 번째 그림을 보여 줄지.</summary>
    internal void ShowFrameForTest(int i) => ShowFrame(i, animate: false);

    private void StartSlides()
    {
        _slide.Stop();
        if (_frames.Count > 1 && Anim.Enabled) _slide.Start(); // 윈도우 애니메이션이 꺼져 있으면 첫 장만
    }

    private void ShowFrame(int i, bool animate)
    {
        if (_frames.Count == 0) { _front.Source = null; return; }
        _frame = (i % _frames.Count + _frames.Count) % _frames.Count;
        var next = _frames[_frame];
        if (animate && _front.Source is not null)
        {
            // 교차 흐림: 뒤 그림에 새 장을 놓고 올리면서 앞 그림을 내림, 끝나면 앞뒤 바꿈
            _back.Source = next;
            var d = TimeSpan.FromMilliseconds(FadeMs);
            var up = new DoubleAnimation(0, 1, d);
            up.Completed += (_, _) =>
            {
                _front.Source = next;
                _front.BeginAnimation(OpacityProperty, null);
                _front.Opacity = 1;
                _back.BeginAnimation(OpacityProperty, null);
                _back.Opacity = 0;
            };
            _back.BeginAnimation(OpacityProperty, up);
            _front.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, d));
        }
        else _front.Source = next;
        _frameDots.Children.Clear();
        for (int k = 0; k < _frames.Count; k++)
            _frameDots.Children.Add(new Ellipse { Width = 6, Height = 6, Margin = new Thickness(3, 0, 3, 0), Fill = k == _frame ? _p.PanelTintText : _p.PanelTintLine });
    }
}

/// <summary>
/// 둘러보기 강조 틀: 화면 전체를 살짝 어둡게(검정 25%) 하고 그 기능 자리만 둥글게 뚫어 밝게 + 연보라 링.
/// 링은 처음 한 번 은은하게 커졌다 돌아옴(튕김 없음). 자리가 바뀌면 구멍과 링이 미끄러져 이동(250ms). 클릭은 통과(창 쪽에서).
/// </summary>
internal sealed class TourSpotlight : Canvas
{
    private const int MoveMs = 250;
    private readonly RectangleGeometry _full = new();
    private readonly RectangleGeometry _hole = new();
    private readonly Rectangle _ring;
    private readonly UiPalette _p;

    public TourSpotlight(UiPalette p)
    {
        _p = p;
        IsHitTestVisible = false;
        var dim = new Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), // 25%
            Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { _full, _hole } },
        };
        Children.Add(dim);
        _ring = new Rectangle
        {
            Stroke = p.AccentFill,
            StrokeThickness = 2.5,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_ring);
        SizeChanged += (_, _) => _full.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
    }

    /// <summary>강조 자리 (이 캔버스 좌표). null = 구멍 없이 어둡게만. radius = 그 자리 모서리.</summary>
    public void MoveTo(Rect? target, double radius, bool animate)
    {
        _full.Rect = new Rect(0, 0, Math.Max(ActualWidth, Width is > 0 ? Width : 0), Math.Max(ActualHeight, Height is > 0 ? Height : 0));
        if (target is not { } r)
        {
            _hole.Rect = Rect.Empty;
            _ring.Visibility = Visibility.Collapsed;
            return;
        }
        var hole = Rect.Inflate(r, 6, 6); // 자리보다 조금 넉넉하게
        // 화면 끝에 붙은 자리(아래 독 등)는 링이 화면 밖으로 잘리지 않게 안쪽으로 (링 두께 + 여유 4)
        double w = _full.Rect.Width, h = _full.Rect.Height;
        if (w > 0 && h > 0)
        {
            const double m = 4;
            double left = Math.Max(m, hole.Left), top = Math.Max(m, hole.Top);
            double right = Math.Min(w - m, hole.Right), bottom = Math.Min(h - m, hole.Bottom);
            if (right > left && bottom > top) hole = new Rect(left, top, right - left, bottom - top);
        }
        double rad = radius + 6;
        _hole.RadiusX = _hole.RadiusY = rad;
        bool move = animate && Anim.Enabled && !_hole.Rect.IsEmpty;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        if (move) _hole.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(hole, TimeSpan.FromMilliseconds(MoveMs)) { EasingFunction = ease });
        else { _hole.BeginAnimation(RectangleGeometry.RectProperty, null); _hole.Rect = hole; }
        // 링: 구멍 테두리 바로 바깥
        var ring = Rect.Inflate(hole, 2, 2);
        _ring.RadiusX = _ring.RadiusY = rad + 2;
        _ring.Visibility = Visibility.Visible;
        if (move)
        {
            var d = TimeSpan.FromMilliseconds(MoveMs);
            _ring.BeginAnimation(LeftProperty, new DoubleAnimation(ring.Left, d) { EasingFunction = ease });
            _ring.BeginAnimation(TopProperty, new DoubleAnimation(ring.Top, d) { EasingFunction = ease });
            _ring.BeginAnimation(WidthProperty, new DoubleAnimation(ring.Width, d) { EasingFunction = ease });
            _ring.BeginAnimation(HeightProperty, new DoubleAnimation(ring.Height, d) { EasingFunction = ease });
        }
        else
        {
            foreach (var dp in new[] { LeftProperty, TopProperty, WidthProperty, HeightProperty }) _ring.BeginAnimation(dp, null);
            SetLeft(_ring, ring.Left);
            SetTop(_ring, ring.Top);
            _ring.Width = ring.Width;
            _ring.Height = ring.Height;
        }
        Pulse();
    }

    /// <summary>링이 한 번 은은하게 커졌다 돌아옴 (튕김 없음).</summary>
    private void Pulse()
    {
        if (!Anim.Enabled || _ring.RenderTransform is not ScaleTransform st) return;
        var a = new DoubleAnimationUsingKeyFrames { BeginTime = TimeSpan.FromMilliseconds(MoveMs) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.04, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new SineEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
