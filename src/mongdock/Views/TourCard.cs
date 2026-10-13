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

/// <summary>둘러보기 한 단계: 제목·설명·움직이는 그림(2~4장). 그림이 없으면 큰 몽독 아이콘.</summary>
internal sealed record TourStep(string Title, string Body, IReadOnlyList<ImageSource> Frames)
{
    /// <summary>[다음] 왼쪽 버튼 — 누르면 실행하고 다음 단계로 (작업 표시줄 다시 보이기·알림 허용·자동 실행 켜기).</summary>
    public (string Label, Action Run)? Action { get; init; }
    /// <summary>[다음 →] 대신 쓸 글자.</summary>
    public string? NextText { get; init; }
    /// <summary>카드 맨 아래 회색 작은 글씨 한 줄.</summary>
    public string? Footnote { get; init; }
    /// <summary>[직접 해 보기] (가리키는 자리가 화면에 있을 때).</summary>
    public bool CanTry { get; init; }
    /// <summary>목록 카드 ("그 밖에 바뀐 것"): 머리글 + 항목들. 있으면 그림 영역 없이 목록.</summary>
    public List<(string Header, List<string> Items)>? Groups { get; init; }
    /// <summary>목록에 다 못 넣은 항목 수 ("외 N개").</summary>
    public int More { get; init; }
    /// <summary>"변경 내역 보기 ›" 링크.</summary>
    public string? Link { get; init; }
    /// <summary>그림 없는 목록 카드에서도 위에 몽독 아이콘.</summary>
    public bool ShowLogo { get; init; }
    /// <summary>설명 아래 버튼 글자 (null = 없음). InlineDone 이면 비활성.</summary>
    public string? InlineText { get; init; }
    public bool InlineDone { get; init; }
    /// <summary>그림을 뒤에서 그리는 중 (그림 영역 크기를 미리 잡아 둠).</summary>
    public bool FramesPending { get; init; }
}

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
    // 장 넘김 1.4초, 마지막 장은 2초 머문 뒤 처음으로, 교차 흐림 180ms (2026-10-14 사용자: 2.5초·1.8초도 느림)
    private const int SlideMs = 1400, LastSlideMs = 2000, FadeMs = 180;

    private readonly UiPalette _p;
    private readonly Image _front = new() { Stretch = Stretch.Uniform };
    private readonly Image _back = new() { Stretch = Stretch.Uniform, Opacity = 0 };
    private readonly StackPanel _frameDots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4) };
    private const int ShortItem = 26; // 2열 목록에 넣을 항목 글자 수 상한
    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly StackPanel _stepDots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _prev;
    private readonly Button _next;
    private readonly Button _action;
    private readonly TextBlock _try;
    private readonly TextBlock _footnote;
    private readonly TextBlock _skip;
    private readonly StackPanel _list = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _link;
    private readonly Grid _picture;
    private readonly StackPanel _content = new();
    private readonly Button _inline;
    private readonly Image _logo = new() { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private Action? _runAction;
    private readonly DispatcherTimer _slide = new() { Interval = TimeSpan.FromMilliseconds(SlideMs) };
    private IReadOnlyList<ImageSource> _frames = Array.Empty<ImageSource>();
    private int _frame;
    private bool _manual; // 사용자가 직접 넘긴 단계는 자동 넘김 멈춤 (다음 단계에서 다시 자동)
    private readonly Border _left, _right;
    private Point? _dragFrom;
    private int _wheel;
    private DateTime _wheelFlip;

    public event Action? PrevClicked;
    public event Action? NextClicked;
    public event Action? SkipClicked;
    public event Action? TryClicked;
    public event Action? LinkClicked;
    public event Action? InlineClicked;

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
        var picture = _picture = new Grid { Height = PictureHeight, ClipToBounds = true };
        picture.Children.Add(new Border { Background = p.PanelTint, CornerRadius = new CornerRadius(16, 16, 0, 0) });
        var frames = new Grid { Margin = new Thickness(18, 18, 18, 26) };
        frames.Children.Add(_back);
        frames.Children.Add(_front);
        picture.Children.Add(frames);
        _logo.Source = AppIcon.WindowIcon; // 그림 없는 단계 (작업 표시줄 안내 등)
        picture.Children.Add(_logo);
        picture.Children.Add(_frameDots);
        // 직접 넘기기: 마우스를 올리면 좌우 가운데 ‹ › 동그라미, 장 점 누르기, 좌우로 끌기, 가로 스크롤(창이 넘겨줌), Shift+←→
        _left = Arrow("‹", -1, HorizontalAlignment.Left);
        _right = Arrow("›", +1, HorizontalAlignment.Right);
        picture.Children.Add(_left);
        picture.Children.Add(_right);
        // 마우스를 올려도 자동 넘김은 계속 (올려 둔 채 보면 멈춰 있어 느리게 느껴짐 — 2026-10-14) — 직접 넘길 때만 멈춤
        picture.MouseEnter += (_, _) => ShowArrows(true);
        picture.MouseLeave += (_, _) => ShowArrows(false);
        picture.MouseLeftButtonDown += (_, e) => { if (_frames.Count > 1) { _dragFrom = e.GetPosition(picture); picture.CaptureMouse(); } };
        picture.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragFrom is not { } from) return;
            _dragFrom = null;
            picture.ReleaseMouseCapture();
            double dx = e.GetPosition(picture).X - from.X;
            if (Math.Abs(dx) >= 40) { e.Handled = true; StepFrame(dx < 0 ? +1 : -1); } // 왼쪽으로 끌면 다음 장
        };
        picture.LostMouseCapture += (_, _) => _dragFrom = null;

        _title = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = p.Text, TextWrapping = TextWrapping.Wrap };
        _body = new TextBlock { FontSize = UiText.Body, Foreground = p.SubText, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 6, 0, 0) };
        _prev = Button(Loc.T("이전"), primary: false);
        _next = Button(Loc.T("다음 →"), primary: true);
        _action = Button("", primary: false);
        _prev.Click += (_, _) => PrevClicked?.Invoke();
        _next.Click += (_, _) => NextClicked?.Invoke();
        _action.Click += (_, _) => { var run = _runAction; run?.Invoke(); NextClicked?.Invoke(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _next.Margin = _action.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(_prev);
        buttons.Children.Add(_action);
        buttons.Children.Add(_next);
        _try = new TextBlock { Text = Loc.T("직접 해 보기") + " ›", FontSize = UiText.Sub, Foreground = p.AccentInk, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        _try.MouseLeftButtonUp += (_, e) => { e.Handled = true; TryClicked?.Invoke(); };
        _footnote = new TextBlock { FontSize = UiText.Sub - 0.5, Foreground = p.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        _link = new TextBlock { FontSize = UiText.Sub, Foreground = p.AccentInk, Cursor = Cursors.Hand, Margin = new Thickness(0, 8, 0, 0) };
        _link.MouseLeftButtonUp += (_, e) => { e.Handled = true; LinkClicked?.Invoke(); };
        // 설명 아래 버튼 (주 버튼 아님 — 회보라 바탕): 누르면 실행만
        _inline = Button("", primary: false);
        _inline.Background = p.PanelTint;
        _inline.Foreground = p.Text;
        _inline.HorizontalAlignment = HorizontalAlignment.Left;
        _inline.Margin = new Thickness(0, 10, 0, 0);
        _inline.Visibility = Visibility.Collapsed;
        _inline.Click += (_, _) => InlineClicked?.Invoke();
        // 아래 줄: 왼쪽 [건너뛰기](회색 글자 버튼) · 가운데 단계 점 · 오른쪽 [이전] [다음 →] (2026-10-14 사용자: 건너뛰기가 그림 위에 떠 있어 이상함)
        _skip = new TextBlock { Text = Loc.T("건너뛰기"), FontSize = UiText.Sub, Foreground = p.SubText, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        _skip.MouseLeftButtonUp += (_, e) => { e.Handled = true; SkipClicked?.Invoke(); };
        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _skip.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(_skip);
        _stepDots.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(_stepDots, 1);
        footer.Children.Add(_stepDots);
        Grid.SetColumn(buttons, 2);
        footer.Children.Add(buttons);
        var bottom = new StackPanel();
        bottom.Children.Add(footer);
        bottom.Children.Add(_footnote);
        var content = _content;
        content.Children.Add(_title);
        content.Children.Add(_body);
        content.Children.Add(_list);
        content.Children.Add(_link);
        content.Children.Add(_inline);
        content.Children.Add(_try);
        var text = new DockPanel { Margin = new Thickness(24, 18, 24, 20), LastChildFill = true };
        DockPanel.SetDock(bottom, Dock.Bottom);
        text.Children.Add(bottom);
        text.Children.Add(content);

        var stack = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(picture, Dock.Top);
        stack.Children.Add(picture);
        stack.Children.Add(text);
        Child = stack;
        _slide.Tick += (_, _) => { ShowFrame(_frame + 1, animate: true); StartSlides(); }; // 마지막 장은 조금 더 머묾
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
        ((TextBlock)_next.Content).Text = step.NextText ?? (index >= count - 1 ? Loc.T("시작하기") : Loc.T("다음 →"));
        _runAction = step.Action?.Run;
        _action.Visibility = step.Action is null ? Visibility.Collapsed : Visibility.Visible;
        if (step.Action is { } act) ((TextBlock)_action.Content).Text = act.Label;
        _try.Visibility = step.CanTry ? Visibility.Visible : Visibility.Collapsed;
        _skip.Visibility = index < count - 1 ? Visibility.Visible : Visibility.Hidden; // 마지막 단계는 [시작하기]만
        _body.Visibility = string.IsNullOrEmpty(step.Body) ? Visibility.Collapsed : Visibility.Visible;
        // 목록 카드 ("그 밖에 바뀐 것")
        _list.Children.Clear();
        if (step.Groups is { } groups)
            foreach (var (header, items) in groups)
            {
                if (groups.Count > 1 || !string.IsNullOrEmpty(header))
                    _list.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Foreground = _p.Text, Margin = new Thickness(0, 6, 0, 2) });
                // 짧은 항목(기능 이름)뿐이면 2열로 — 카드가 아래로 길어지지 않게 (2026-10-14 PM)
                bool twoColumns = items.Count > 3 && items.All(x => x.Length <= ShortItem);
                var grid = new Grid();
                if (twoColumns)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition());
                    grid.ColumnDefinitions.Add(new ColumnDefinition());
                }
                for (int n = 0; n < items.Count; n++)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 2, 8, 2) };
                    var dot = new TextBlock { Text = "•", Foreground = _p.SubText, Margin = new Thickness(2, 0, 8, 0) };
                    DockPanel.SetDock(dot, Dock.Left);
                    row.Children.Add(dot);
                    row.Children.Add(new TextBlock { Text = items[n], Foreground = _p.Text, TextWrapping = twoColumns ? TextWrapping.NoWrap : TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
                    int r = twoColumns ? n / 2 : n;
                    while (grid.RowDefinitions.Count <= r) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(row, r);
                    Grid.SetColumn(row, twoColumns ? n % 2 : 0);
                    grid.Children.Add(row);
                }
                _list.Children.Add(grid);
            }
        if (step.More > 0) _list.Children.Add(new TextBlock { Text = Loc.F($"외 {step.More}개"), Foreground = _p.SubText, Margin = new Thickness(0, 4, 0, 0) });
        _list.Visibility = _list.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _inline.Visibility = step.InlineText is null ? Visibility.Collapsed : Visibility.Visible;
        if (_inline.Content is TextBlock it) it.Text = step.InlineText ?? "";
        _inline.IsEnabled = !step.InlineDone;
        _link.Text = step.Link ?? "";
        _link.Visibility = step.Link is null ? Visibility.Collapsed : Visibility.Visible;
        _footnote.Text = step.Footnote ?? "";
        _footnote.Visibility = string.IsNullOrEmpty(step.Footnote) ? Visibility.Collapsed : Visibility.Visible;
        // 그림이 없는 단계: 그림 영역을 낮추고 큰 몽독 아이콘, 목록 카드는 그림 영역 없음.
        // 그림이 아직 그려지는 중이면(FramesPending) 그림 영역 크기는 그대로
        bool pictures = step.Frames.Count > 0 || step.FramesPending;
        _picture.Visibility = step.Groups is not null && !pictures && !step.ShowLogo ? Visibility.Collapsed : Visibility.Visible;
        _picture.Height = pictures ? PictureHeight : 150;
        _logo.Visibility = pictures ? Visibility.Collapsed : Visibility.Visible;
        _stepDots.Children.Clear();
        for (int i = 0; i < count; i++) // 지금 단계는 길쭉한 알약, 나머지는 점
            _stepDots.Children.Add(new Border { Width = i == index ? 16 : 6, Height = 6, CornerRadius = new CornerRadius(3), Background = i == index ? _p.AccentFill : _p.Divider, Margin = new Thickness(0, 0, 5, 0) });
        _frames = step.Frames;
        _frameDots.Visibility = _frames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _frame = 0;
        _manual = false;
        ShowArrows(_picture.IsMouseOver);
        _back.Source = null;
        _back.Opacity = 0;
        ShowFrame(0, animate: false);
        StartSlides();
        GrowToFit();
    }

    /// <summary>
    /// 뒤에서 그린 장들이 준비되면 채움 (첫 장은 이미 보이는 중 — 자리·순서 그대로 이어서 넘어감).
    /// </summary>
    public void SetFrames(IReadOnlyList<ImageSource> frames)
    {
        bool first = _frames.Count == 0;
        _frames = frames;
        _frameDots.Visibility = _frames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _logo.Visibility = Visibility.Collapsed;
        if (first) ShowFrame(0, animate: false);
        else ShowFrame(_frame, animate: false);
        ShowArrows(_picture.IsMouseOver);
        StartSlides();
    }

    private Border Arrow(string glyph, int dir, HorizontalAlignment side)
    {
        var tint = ((SolidColorBrush)_p.CardBackground).Color;
        var b = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, tint.R, tint.G, tint.B)),
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Direction = 270, Opacity = 0.18, Color = _p.ShadowColor },
            HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 8),
            Cursor = Cursors.Hand, Opacity = 0, IsHitTestVisible = false,
            Child = new TextBlock { Text = glyph, FontSize = 20, Foreground = _p.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(dir < 0 ? -1 : 1, -4, 0, 0) },
        };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; StepFrame(dir); };
        return b;
    }

    private void ShowArrows(bool on)
    {
        on &= _frames.Count > 1;
        foreach (var b in new[] { _left, _right })
        {
            b.IsHitTestVisible = on;
            if (Anim.Enabled) b.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(120)));
            else { b.BeginAnimation(OpacityProperty, null); b.Opacity = on ? 1 : 0; }
        }
    }

    /// <summary>사용자가 장을 직접 넘김 (‹ ›, 끌기, 가로 스크롤, Shift+←→) — 이 단계는 자동 넘김 멈춤.</summary>
    public void StepFrame(int dir) => GoFrame(_frame + dir);

    /// <summary>사용자가 장 점이나 화살표로 고른 장으로.</summary>
    public void GoFrame(int i)
    {
        if (_frames.Count < 2) return;
        _manual = true;
        _slide.Stop();
        int target = (i % _frames.Count + _frames.Count) % _frames.Count;
        if (target != _frame) ShowFrame(target, animate: Anim.Enabled);
    }

    /// <summary>가로 스크롤(트랙패드·기울이는 휠): 그림 위에서만, 한 칸(120)씩 한 장, 넘긴 직후 잠깐은 무시 (트랙패드가 계속 보냄).</summary>
    public bool HorizontalWheel(int delta)
    {
        if (!_picture.IsMouseOver || _frames.Count < 2) return false;
        if ((DateTime.UtcNow - _wheelFlip).TotalMilliseconds < 350) { _wheel = 0; return true; }
        _wheel += delta;
        if (Math.Abs(_wheel) >= 120)
        {
            StepFrame(_wheel > 0 ? +1 : -1);
            _wheel = 0;
            _wheelFlip = DateTime.UtcNow;
        }
        return true;
    }

    /// <summary>
    /// 카드 높이를 모든 단계 중 가장 긴 것에 맞춰 고정 (단계를 넘겨도 카드 크기가 안 바뀜 — 2026-10-14 PM).
    /// 각 단계를 잠깐 채워 재 보고, 다 재면 높이를 정함 (그다음 Show 가 진짜 단계를 채움).
    /// </summary>
    public void FixHeight(IReadOnlyList<TourStep> steps)
    {
        Height = double.NaN;
        double max = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            Show(steps[i], i, steps.Count);
            max = Math.Max(max, NaturalHeight());
        }
        _slide.Stop();
        if (max > 0) Height = Math.Ceiling(max);
    }

    /// <summary>지금 내용이 잘리지 않는 높이 (카드 폭 그대로, 높이 제한 없이 잰 값).</summary>
    private double NaturalHeight()
    {
        if (Child is not FrameworkElement inner) return 0;
        // 앞 단계에서 잰 값이 남지 않게 전부 다시 재게 함 (안 그러면 같은 폭이면 예전 크기를 그대로 씀 — 독일어 상단바 단계가 20px 덜 잡힘)
        inner.InvalidateMeasure();
        foreach (var d in Descendants(inner).OfType<UIElement>()) d.InvalidateMeasure();
        inner.Measure(new Size(CardWidth - BorderThickness.Left - BorderThickness.Right, double.PositiveInfinity));
        return inner.DesiredSize.Height + BorderThickness.Top + BorderThickness.Bottom;
    }

    /// <summary>높이를 고정한 뒤에도 이 단계 내용이 더 길면 늘림 (잘리지 않게 — 창이 가운데 기준으로 다시 맞춤).</summary>
    private void GrowToFit()
    {
        if (double.IsNaN(Height)) return;
        double need = Math.Ceiling(NaturalHeight());
        if (need > Height + 0.5)
        {
            Services.Log.Info($"둘러보기 카드 높이 늘림 {Height:0} → {need:0}");
            Height = need;
        }
    }

    /// <summary>
    /// 점검: 지금 단계가 카드 안에 다 들어가는지. 넘치는 높이(px)와 말줄임된 글 목록.
    /// 카드를 지금 높이로 배치한 뒤, 본문이 받은 높이보다 원하는 높이가 크면 넘침.
    /// </summary>
    internal (double Overflow, List<string> Trimmed) FitForTest()
    {
        double h = double.IsNaN(Height) ? NaturalHeight() : Height;
        Measure(new Size(CardWidth, h));
        Arrange(new Rect(0, 0, CardWidth, h));
        UpdateLayout();
        double overflow = Math.Max(0, _content.DesiredSize.Height - _content.RenderSize.Height);
        var trimmed = new List<string>();
        foreach (var t in Descendants(this).OfType<TextBlock>())
        {
            if (t.Visibility != Visibility.Visible || t.ActualWidth <= 0 || string.IsNullOrEmpty(t.Text) || t.TextWrapping != TextWrapping.NoWrap) continue;
            var probe = new TextBlock { Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (probe.DesiredSize.Width > t.ActualWidth + 0.5) trimmed.Add(t.Text);
        }
        return (overflow, trimmed);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    internal bool ManualForTest => _manual;
    internal int FrameForTest => _frame;
    internal bool SlidingForTest => _slide.IsEnabled;
    internal TimeSpan SlideIntervalForTest => _slide.Interval;
    internal int FrameDotCountForTest => _frameDots.Children.Count;
    internal void ClickFrameDotForTest(int k) => GoFrame(k);

    /// <summary>시험 그림에서만: 몇 번째 그림을 보여 줄지.</summary>
    internal void ShowFrameForTest(int i) => ShowFrame(i, animate: false);

    private void StartSlides()
    {
        _slide.Stop();
        if (_manual) return;
        _slide.Interval = TimeSpan.FromMilliseconds(_frame == _frames.Count - 1 ? LastSlideMs : SlideMs);
        if (_frames.Count > 1 && Anim.Enabled) _slide.Start(); // 윈도우 애니메이션이 꺼져 있으면 첫 장만
    }

    private void ShowFrame(int i, bool animate)
    {
        if (_frames.Count == 0) { _front.Source = null; return; }
        _frame = (i % _frames.Count + _frames.Count) % _frames.Count;
        var next = _frames[_frame];
        if (animate && _front.Source is not null)
        {
            // 교차 흐림: 새 장을 뒤에 불투명하게 깔고 앞 장만 내림, 끝나면 앞뒤 바꿈.
            // (둘 다 반투명으로 겹치면 가운데쯤 그림 영역 바탕이 비쳐 잠깐 밝아짐 — 2026-10-14 "지직")
            _back.BeginAnimation(OpacityProperty, null);
            _back.Source = next;
            _back.Opacity = 1;
            var down = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeMs));
            down.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_back.Source, next)) return; // 그 사이 또 넘김
                _front.Source = next;
                _front.BeginAnimation(OpacityProperty, null);
                _front.Opacity = 1;
                _back.Opacity = 0;
            };
            _front.BeginAnimation(OpacityProperty, down);
        }
        else
        {
            _front.BeginAnimation(OpacityProperty, null);
            _front.Opacity = 1;
            _front.Source = next;
            _back.Opacity = 0;
        }
        _frameDots.Children.Clear();
        for (int k = 0; k < _frames.Count; k++)
        {
            // 점은 6px, 누르는 자리는 18px (넉넉하게)
            int at = k;
            var hit = new Border
            {
                Width = 18, Height = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Child = new Ellipse { Width = 6, Height = 6, Fill = k == _frame ? _p.PanelTintText : _p.PanelTintLine },
            };
            hit.MouseLeftButtonDown += (_, e) => e.Handled = true;
            hit.MouseLeftButtonUp += (_, e) => { e.Handled = true; GoFrame(at); };
            _frameDots.Children.Add(hit);
        }
    }
}

/// <summary>
/// 둘러보기 강조 틀: 화면 전체를 살짝 어둡게(검정 35%) 하고 그 기능 자리만 둥글게 뚫어 밝게 + 연보라 링.
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
            Fill = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), // 35% (2026-10-14 사용자 확정 — 25% 는 라이트 바탕에서 약함)
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
            _hole.BeginAnimation(RectangleGeometry.RectProperty, null);
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
        var from = _hole.Rect;
        bool move = animate && Anim.Enabled && !from.IsEmpty;
        // 멀리 옮길 때(겹치지 않음)는 미끄러지지 않고 그 자리에서 닫혔다 새 자리에서 열림 —
        // 미끄러지면 큰 구멍이 화면 가운데를 쓸고 지나가 잠깐 밝아짐 (2026-10-14 "지직": 상단바 → 독 50 밝기 튐)
        bool jump = move && !Rect.Inflate(from, 40, 40).IntersectsWith(hole);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var ring = Rect.Inflate(hole, 2, 2);
        _ring.RadiusX = _ring.RadiusY = rad + 2;
        _ring.Visibility = Visibility.Visible;
        if (jump)
        {
            var half = TimeSpan.FromMilliseconds(MoveMs / 2);
            var all = TimeSpan.FromMilliseconds(MoveMs);
            Rect Dot(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2, 0, 0);
            var a = new RectAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingRectKeyFrame(Dot(from), KeyTime.FromTimeSpan(half), new CubicEase { EasingMode = EasingMode.EaseIn }));
            a.KeyFrames.Add(new DiscreteRectKeyFrame(Dot(hole), KeyTime.FromTimeSpan(half)));
            a.KeyFrames.Add(new EasingRectKeyFrame(hole, KeyTime.FromTimeSpan(all), new CubicEase { EasingMode = EasingMode.EaseOut }));
            _hole.BeginAnimation(RectangleGeometry.RectProperty, a);
            // 링: 흐려졌다가 새 자리에서 다시
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(MoveMs / 3))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(half)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(all)));
            _ring.BeginAnimation(OpacityProperty, fade);
            foreach (var dp in new[] { LeftProperty, TopProperty, WidthProperty, HeightProperty })
            {
                double v = dp == LeftProperty ? ring.Left : dp == TopProperty ? ring.Top : dp == WidthProperty ? ring.Width : ring.Height;
                var k = new DoubleAnimationUsingKeyFrames();
                k.KeyFrames.Add(new DiscreteDoubleKeyFrame(v, KeyTime.FromTimeSpan(half)));
                _ring.BeginAnimation(dp, k);
            }
            Pulse();
            return;
        }
        _ring.BeginAnimation(OpacityProperty, null);
        _ring.Opacity = 1;
        if (move) _hole.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(hole, TimeSpan.FromMilliseconds(MoveMs)) { EasingFunction = ease });
        else { _hole.BeginAnimation(RectangleGeometry.RectProperty, null); _hole.Rect = hole; }
        // 링: 구멍 테두리 바로 바깥
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
