using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Mongdock.Models;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 항목 하나의 화면 (코드로 구성). 아이콘 + 실행 중 점(아이콘 아래 = 가장자리 쪽) + 알림 점(아이콘 모서리).
/// 확대는 DockWindow 가 매 프레임 <see cref="ApplyScale"/> 로 지정 — 아이콘은 가장자리 반대쪽으로 커지고
/// 슬롯은 독 방향으로 늘어나 이웃과 겹치지 않는다. 알림은 튀지 않고 점만 (바운스 없음).
/// 앱을 켤 때(<see cref="DockItemViewModel.IsLaunching"/>)만 설정에 따라 통통 튀기 또는 실행 점 깜빡임.
/// 누르면 <see cref="Pressed"/> 만 알리고, 클릭/드래그 구분·실행은 DockWindow 가 마우스 캡처로 처리한다
/// (누름 반응 <see cref="PressDown"/>/<see cref="PressUp"/> 도 DockWindow 가 호출).
/// 드래그로 순서를 바꿀 때 이웃 아이콘이 비켜서는 움직임은 <see cref="AnimateShift"/> (RenderTransform 이동).
/// </summary>
internal sealed class DockItemView : Grid
{
    /// <summary>배치 잘림 없음 — 확대 아이콘이 칸 밖으로 그려지는 게 정상 (NoClipBorder 참고).</summary>
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => null;

    private readonly DockLayout _layout;
    private readonly ScaleTransform? _scale;
    private readonly Ellipse? _runningDot;
    private readonly Ellipse? _notifyDot;
    private readonly Grid? _plus;
    private double _plusSize, _plusMargin;
    /// <summary>재생 진행 막대 (#22) 바탕·채움.</summary>
    private readonly System.Windows.Shapes.Path? _soundMark;
    /// <summary>드래그 정렬 중 비켜서기 이동 (독 방향). 레이아웃은 그대로 두고 그림만 옮긴다.</summary>
    private readonly TranslateTransform _shift = new();
    private const double ShiftMs = 150;

    // 누름 반응 (0.92 배 + 살짝 어둡게)
    private readonly Grid? _pressHost;
    private readonly ScaleTransform _press = new(1, 1);
    private readonly Rectangle? _darken;
    private bool _pressed;
    private const double PressScale = 0.92, PressDarken = 0.18, PressDownMs = 80, PressUpMs = 120;

    // 실행 반응: 튀기(아이콘 이동) / 점 깜빡임
    private readonly TranslateTransform _bounce = new();
    private bool _bouncing;          // 튀기 한 번이 진행 중 (끝나면 계속할지 판단)
    private bool _bounceStopped;     // 이번 실행에서는 더 튀지 않음 (드래그 시작)
    private int _bounceGen;          // 강제로 멈춘 뒤 이전 애니메이션의 Completed 무시용
    private bool _blinking;
    private const double BounceUpMs = 300, BounceDownMs = 300, BlinkHalfMs = 450, BlinkLow = 0.35;

    public DockItemViewModel Item { get; }

    /// <summary>현재 확대 배율 (1 = 기본).</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>확대 전 독 방향 길이 (여백 포함).</summary>
    public double BaseLength { get; }

    public event EventHandler? HoverStarted;
    public event EventHandler? HoverEnded;
    /// <summary>왼쪽 버튼 누름 (자동 구분선 제외). 클릭인지 드래그인지는 DockWindow 가 판단.</summary>
    public event EventHandler<MouseButtonEventArgs>? Pressed;

    /// <summary>현재 비켜서기 목표 이동량 (독 방향, DIP).</summary>
    public double ShiftTarget { get; private set; }

    private FrameworkElement? _iconHost;

    /// <summary>
    /// 지금 그려진 아이콘 사각형 (확대·누름 반영, relativeTo 좌표). 확대된 아이콘은 칸보다 커서 화면 안쪽으로 튀어나오는데
    /// 그 부분도 이 아이콘을 누른 것으로 보려고 씀. 구분선은 Rect.Empty.
    /// </summary>
    public Rect DrawnIconBounds(Visual relativeTo)
    {
        if (_iconHost is null || _iconHost.ActualWidth <= 0) return Rect.Empty;
        try { return _iconHost.TransformToVisual(relativeTo).TransformBounds(new Rect(_iconHost.RenderSize)); }
        catch (InvalidOperationException) { return Rect.Empty; }
    }

    /// <summary>＋가 보이는 중인지.</summary>
    public bool PlusVisible => _plusShown;
    private bool _plusShown;

    /// <summary>＋ 보이기·숨기기 (0.12초 흐리게).</summary>
    public void ShowPlus(bool show)
    {
        if (_plus is null || _plusShown == show) return;
        _plusShown = show; // 사라지는 0.12초 동안 누르면 앱 클릭 (리뷰 10-23)
        if (show) _plus.Visibility = Visibility.Visible;
        var a = new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(120));
        if (!show) a.Completed += (_, _) => { if (!_plusShown) _plus.Visibility = Visibility.Collapsed; };
        _plus.BeginAnimation(OpacityProperty, a);
    }

    /// <summary>
    /// p(relativeTo 좌표)가 ＋ 누름 원 안인지 — 그려진 아이콘 사각형(확대 반영)에서 ＋ 중심을 계산, 반지름은 max(12, 보이는 반지름 + 4) DIP.
    /// </summary>
    public bool PlusContains(Point p, Visual relativeTo)
    {
        if (!PlusVisible) return false;
        var r = DrawnIconBounds(relativeTo);
        if (r.IsEmpty || _layout.IconSize <= 0) return false;
        double s = r.Width / _layout.IconSize;
        double off = (_plusMargin + _plusSize / 2) * s;
        double cx = _layout.Edge == DockEdge.Left ? r.Right - off : r.Left + off;
        double cy = _layout.Edge == DockEdge.Top ? r.Bottom - off : r.Top + off;
        double radius = Math.Max(12, _plusSize * s / 2 + 4);
        return (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy) <= radius * radius;
    }

    public DockItemView(DockItemViewModel item, DockLayout layout)
    {
        Item = item;
        _layout = layout;
        Focusable = false;
        Background = Brushes.Transparent; // 슬롯 전체를 클릭 영역으로
        RenderTransform = _shift;

        if (item.IsSeparator)
        {
            double gap = Math.Max(5, layout.Spacing + 4);
            var line = new Rectangle { Fill = layout.Separator, IsHitTestVisible = false, SnapsToDevicePixels = true };
            if (layout.IsVertical)
            {
                line.Width = layout.IconSize * 0.72;
                line.Height = 1;
                Margin = new Thickness(0, gap, 0, gap);
            }
            else
            {
                line.Width = 1;
                line.Height = layout.IconSize * 0.72;
                Margin = new Thickness(gap, 0, gap, 0);
            }
            Children.Add(line);
            BaseLength = 1 + gap * 2;
            return;
        }

        double icon = layout.IconSize;
        double half = layout.Spacing / 2;
        Width = icon;
        Height = icon;
        Margin = layout.IsVertical ? new Thickness(0, half, 0, half) : new Thickness(half, 0, half, 0);
        BaseLength = icon + layout.Spacing;

        // 확대되는 아이콘 묶음 (아이콘 + 알림 점)
        var iconHost = new Grid
        {
            Width = icon,
            Height = icon,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = layout.ScaleOrigin,
            IsHitTestVisible = false,
        };
        _scale = new ScaleTransform(1, 1);
        // 확대(ScaleOrigin 기준) + 실행 튀기(이동). 이동은 기준점과 무관하게 그대로 더해진다
        iconHost.RenderTransform = new TransformGroup { Children = { _scale, _bounce } };

        // 누름 반응: 아이콘 그림만 가운데 기준으로 살짝 작게 + 어둡게 (알림 점은 그대로)
        _pressHost = new Grid
        {
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _press,
            IsHitTestVisible = false,
        };
        // 맥 스타일 아이콘은 여백·그림자까지 포함되어 오므로 슬롯 크기 그대로 그린다 (추가 가공 없음)
        var image = new Image { Source = item.Icon, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _pressHost.Children.Add(image);
        if (item.Icon != null)
        {
            // 아이콘 모양(알파)대로만 검게 덮어 어둡게 — 투명 여백까지 칠하지 않게 OpacityMask 사용
            var mask = new ImageBrush(item.Icon) { Stretch = Stretch.Uniform };
            mask.Freeze();
            _darken = new Rectangle { Fill = Brushes.Black, OpacityMask = mask, Opacity = 0, IsHitTestVisible = false };
            _pressHost.Children.Add(_darken);
        }
        iconHost.Children.Add(_pressHost);
        _iconHost = iconHost;

        double n = layout.NotificationSize;
        _notifyDot = new Ellipse
        {
            Width = n,
            Height = n,
            Fill = layout.Notification,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = layout.Edge == DockEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = new Thickness(icon * 0.04),
            Visibility = Visibility.Collapsed,
        };
        iconHost.Children.Add(_notifyDot);

        // ＋ 「지금 데스크톱에 새 창」 (#41): 아이콘 위에서 0.1초 멈추면 보임. 화면 안쪽이면서 알림 점 반대쪽 모서리 (PM 10-22) —
        // 아래·오른쪽 독 = 위-왼쪽, 왼쪽 독 = 위-오른쪽, 위 독 = 아래-왼쪽. 아이콘과 함께 확대·튀기. 클릭 판정은 DockWindow 가 PlusContains 로.
        // 모양은 기획서 모형 (10-22): 아이콘 그림 모서리에 반쯤 걸친 동그라미(52px 아이콘에 20px), 흰(다크는 카드색) 바탕 + 얇은 테두리 + 작은 그림자,
        // 연보라 ＋ 는 글자 대신 선 두 개로 (글꼴마다 ＋ 가 위·왼쪽으로 치우쳐 보임 — 10-23 그림 확인).
        // 선명하게 (사용자 10-23 「흐릿·계단」): 그림자 효과(DropShadowEffect)는 붙은 요소를 비트맵으로 굽고 확대 때 그 비트맵을 늘려서 흐려짐 →
        // 그림자는 따로 뒤에 깐 흐린 동그라미에만, 동그라미·＋ 는 벡터 그대로. ＋ 는 가운데 정렬한 막대 두 개(선 Path 는 펜 두께 반만큼 위·왼쪽으로 밀림),
        // 크기는 짝수 DIP + UseLayoutRounding 으로 화소에 맞춤.
        _plusSize = Math.Round(Math.Clamp(icon * 0.38, 14, 24) / 2) * 2;
        _plusMargin = -Math.Round(icon * 0.05); // 맥 스타일 아이콘은 그림이 칸 안쪽 약 10% 에서 시작 → 그림 모서리에 걸치게
        double bar = Math.Round(_plusSize * 0.23) * 2, thick = _plusSize >= 18 ? 2 : 1.5;
        var accent = new SolidColorBrush(layout.IsLight ? Color.FromRgb(0x8A, 0x7C, 0xE0) : Color.FromRgb(0xB4, 0xA8, 0xFF));
        _plus = new Grid
        {
            Width = _plusSize,
            Height = _plusSize,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
            VerticalAlignment = layout.Edge == DockEdge.Top ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            HorizontalAlignment = layout.Edge == DockEdge.Left ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(_plusMargin),
            Visibility = Visibility.Collapsed,
            Opacity = 0,
        };
        _plus.Children.Add(new Ellipse
        {
            Fill = Brushes.Black,
            Opacity = layout.IsLight ? 0.16 : 0.45,
            Margin = new Thickness(0, 1.5, 0, -1.5),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4 },
        });
        _plus.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(layout.IsLight ? Colors.White : Color.FromRgb(0x1B, 0x1D, 0x27)),
            Stroke = new SolidColorBrush(layout.IsLight ? Color.FromRgb(0xDF, 0xE2, 0xEE) : Color.FromRgb(0x3A, 0x3D, 0x4E)),
            StrokeThickness = 1,
        });
        _plus.Children.Add(new Rectangle { Width = bar, Height = thick, RadiusX = thick / 2, RadiusY = thick / 2, Fill = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        _plus.Children.Add(new Rectangle { Width = thick, Height = bar, RadiusX = thick / 2, RadiusY = thick / 2, Fill = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        iconHost.Children.Add(_plus);
        Children.Add(iconHost);

        // 실행 중 점: 맥처럼 아이콘 아래(가장자리 쪽) 패딩 안의 작은 원
        double d = layout.IndicatorSize;
        // 점 바깥쪽과 독 테두리 사이를 3 DIP 띄움 (예전: 테두리와 ~1.5 DIP 로 붙어 보임). 아이콘 쪽으로 조금 겹쳐도
        // 아이콘 가장자리는 투명 여백이라 괜찮음
        double offset = -Math.Max(0, layout.Padding - 3);
        _runningDot = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = layout.Indicator,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        switch (layout.Edge)
        {
            case DockEdge.Left:
                _runningDot.HorizontalAlignment = HorizontalAlignment.Left;
                _runningDot.VerticalAlignment = VerticalAlignment.Center;
                _runningDot.Margin = new Thickness(offset, 0, 0, 0);
                break;
            case DockEdge.Bottom:
                _runningDot.HorizontalAlignment = HorizontalAlignment.Center;
                _runningDot.VerticalAlignment = VerticalAlignment.Bottom;
                _runningDot.Margin = new Thickness(0, 0, 0, offset);
                break;
            case DockEdge.Top:
                _runningDot.HorizontalAlignment = HorizontalAlignment.Center;
                _runningDot.VerticalAlignment = VerticalAlignment.Top;
                _runningDot.Margin = new Thickness(0, offset, 0, 0);
                break;
            default:
                _runningDot.HorizontalAlignment = HorizontalAlignment.Right;
                _runningDot.VerticalAlignment = VerticalAlignment.Center;
                _runningDot.Margin = new Thickness(0, 0, offset, 0);
                break;
        }
        Children.Add(_runningDot);

        // 재생 중 소리 표시 (#22·#27): 음악·영상을 재생 중이면 점 대신 같은 자리·같은 색의 작은 스피커 모양 (움직이지 않음, 아이콘을 덮지 않음)
        _soundMark = new System.Windows.Shapes.Path
        {
            Data = SoundMarkGeometry,
            Fill = layout.Indicator,
            Stretch = Stretch.Uniform,
            Height = Math.Round(d * 1.9),
            Width = Math.Round(d * 1.9 * 1.15),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = _runningDot.HorizontalAlignment,
            VerticalAlignment = _runningDot.VerticalAlignment,
            Margin = _runningDot.Margin,
        };
        RenderOptions.SetEdgeMode(_soundMark, EdgeMode.Unspecified);
        Children.Add(_soundMark);

        UpdateDots();
        item.PropertyChanged += OnItemPropertyChanged;
    }

    /// <summary>작은 스피커 모양 (약 11×10 칸: 몸통 + 나팔 + 소리 물결 둘). 채우기만으로 그려 작은 크기에서도 선명.</summary>
    private static readonly Geometry SoundMarkGeometry = MakeSoundMark();

    private static Geometry MakeSoundMark()
    {
        var g = Geometry.Parse("M0,3.5 L2.4,3.5 L5.4,0.8 L5.4,9.2 L2.4,6.5 L0,6.5 Z " +
                               "M7,2.3 A3.8,3.8 0 0 1 7,7.7 L6.3,6.9 A2.7,2.7 0 0 0 6.3,3.1 Z " +
                               "M8.7,0.6 A5.8,5.8 0 0 1 8.7,9.4 L8,8.6 A4.7,4.7 0 0 0 8,1.4 Z");
        g.Freeze();
        return g;
    }

    /// <summary>뷰를 버릴 때 VM 구독 해제.</summary>
    public void Detach()
    {
        Item.PropertyChanged -= OnItemPropertyChanged;
        // 버려진 뷰에서 무한 반복 애니메이션이 계속 돌지 않게
        StopBounce();
        StopBlink();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateDots();

    /// <summary>실행 중이 아닌데 독에서 눌러 실행 중 (창 대기).</summary>
    private bool WaitingForWindow => Item.IsLaunching && !Item.IsRunning;

    private void UpdateDots()
    {
        if (_runningDot != null)
        {
            // 실행을 누르면 창이 뜨기 전에도 점을 바로 보여 줌
            bool sound = Item.PlayingSource is not null && Item.IsRunning;
            _runningDot.Visibility = (Item.IsRunning || Item.IsLaunching) && !sound ? Visibility.Visible : Visibility.Collapsed;
            if (_soundMark != null) _soundMark.Visibility = sound ? Visibility.Visible : Visibility.Collapsed;
            bool blink = WaitingForWindow && _layout.LaunchAnimation == LaunchAnimation.Blink && Anim.Enabled;
            if (blink) StartBlink();
            else
            {
                StopBlink();
                _runningDot.Opacity = Item.IsRunning && Item.RunningElsewhereOnly ? 0.4 : 1; // 다른 데스크톱에만 창이 있음
            }
        }
        if (!Item.IsLaunching) _bounceStopped = false; // 다음 실행 때 다시 튈 수 있게
        if (WantBounce) StartBounce();
        if (_notifyDot != null) _notifyDot.Visibility = Item.HasNotification ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── 누름 반응 ─────────────────────────

    /// <summary>눌림 (아이콘 0.92 배 + 살짝 어둡게, 80ms). DockWindow 가 누름을 받아들였을 때 호출.</summary>
    public void PressDown()
    {
        if (_pressHost == null || _pressed) return;
        _pressed = true;
        AnimatePress(PressScale, PressDarken, PressDownMs, Anim.EaseOut);
    }

    /// <summary>뗌: 원래대로 (animate 면 120ms ease-out, 드래그로 바뀔 때는 즉시).</summary>
    public void PressUp(bool animate = true)
    {
        if (_pressHost == null || !_pressed) return;
        _pressed = false;
        if (animate) AnimatePress(1, 0, PressUpMs, Anim.EaseOut);
        else SetPressNow(1, 0);
    }

    private void AnimatePress(double scale, double darken, double ms, IEasingFunction ease)
    {
        if (!Anim.Enabled)
        {
            SetPressNow(scale, darken); // 애니메이션 꺼짐: 즉시 상태만 바꿈
            return;
        }
        _press.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(scale, ms, ease));
        _press.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(scale, ms, ease));
        _darken?.BeginAnimation(OpacityProperty, Anim.To(darken, ms, ease));
    }

    private void SetPressNow(double scale, double darken)
    {
        _press.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _press.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _press.ScaleX = _press.ScaleY = scale;
        if (_darken != null)
        {
            _darken.BeginAnimation(OpacityProperty, null);
            _darken.Opacity = darken;
        }
    }

    // ───────────────────────── 실행 반응 ─────────────────────────

    /// <summary>점 깜빡임: 투명도 1 ↔ 0.35, 0.9초 주기 (Sine). Opacity 만 바꿔 가볍다.</summary>
    private void StartBlink()
    {
        if (_runningDot == null || _blinking) return;
        _blinking = true;
        var a = new DoubleAnimation(1, BlinkLow, Anim.Ms(BlinkHalfMs))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        a.Freeze();
        _runningDot.BeginAnimation(OpacityProperty, a);
    }

    private void StopBlink()
    {
        if (_runningDot == null || !_blinking) return;
        _blinking = false;
        _runningDot.BeginAnimation(OpacityProperty, null);
    }

    private bool WantBounce => _pressHost != null && WaitingForWindow && !_bounceStopped
                               && _layout.LaunchAnimation == LaunchAnimation.Bounce && Anim.Enabled;

    private DependencyProperty BounceAxis => _layout.IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;

    /// <summary>독 바깥 방향 부호 (아래 독 → 위(-Y), 위 독 → 아래, 왼쪽 독 → 오른쪽, 오른쪽 독 → 왼쪽(-X)).</summary>
    private double BounceSign => _layout.Edge is DockEdge.Bottom or DockEdge.Right ? -1 : 1;

    /// <summary>
    /// 맥처럼 통통 튀기: 한 번(올라감 300ms EaseOut → 내려옴 300ms EaseIn)씩 이어 붙이고, 매번 끝날 때 아직 창을
    /// 기다리는 중이면 다음 번을 시작 → 창이 뜨면 지금 튐을 마친 뒤 멈춘다. RenderTransform 이동만 사용.
    /// </summary>
    private void StartBounce()
    {
        if (_bouncing) return;
        _bouncing = true;
        int gen = ++_bounceGen;
        // 확대된 상태면 그만큼 여유가 줄어듦 → 창 밖으로 잘리지 않게 높이를 줄임
        double h = Math.Max(3, _layout.BounceHeight - _layout.IconSize * (Scale - 1));
        double peak = h * BounceSign;
        var a = new DoubleAnimationUsingKeyFrames { Duration = Anim.Ms(BounceUpMs + BounceDownMs) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(BounceUpMs)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(BounceUpMs + BounceDownMs)),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        a.Completed += (_, _) =>
        {
            if (gen != _bounceGen) return; // 그 사이 강제로 멈춤
            _bouncing = false;
            if (WantBounce) StartBounce();
            else _bounce.BeginAnimation(BounceAxis, null);
        };
        _bounce.BeginAnimation(BounceAxis, a);
    }

    /// <summary>알림 튀기기 전체 길이 (ms): 2번 × (올라감 300 + 위에서 머무름 80 + 내려옴 320) = 1.4초 (2026-10-13 사용자: "너무 짧다").</summary>
    private const double NotifyUp = 300, NotifyHold = 80, NotifyDown = 320;
    public const int NotifyBounceMs = (int)(2 * (NotifyUp + NotifyHold + NotifyDown));

    /// <summary>
    /// 알림이 오면 2번만 튀고 멈춤 (#27): 아이콘 높이의 60%(창 여유 안), 한 번 0.7초(올라감 EaseOut → 위에서 잠깐 → 내려옴 EaseIn), 출렁임 없음.
    /// 실행 튀기가 진행 중이면 그쪽이 먼저(이건 건너뜀). 확대된 만큼 높이를 줄여 창 밖으로 잘리지 않게.
    /// </summary>
    public void NotifyBounce()
    {
        if (_bouncing || !Anim.Enabled) return;
        int gen = ++_bounceGen;
        double h = Math.Max(3, _layout.NotifyBounceHeight - _layout.IconSize * (Scale - 1)) * BounceSign;
        var a = new DoubleAnimationUsingKeyFrames { Duration = Anim.Ms(NotifyBounceMs) };
        for (int i = 0; i < 2; i++)
        {
            double t0 = i * (NotifyUp + NotifyHold + NotifyDown);
            a.KeyFrames.Add(new EasingDoubleKeyFrame(h, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + NotifyUp)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(h, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + NotifyUp + NotifyHold))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t0 + NotifyUp + NotifyHold + NotifyDown)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        }
        a.Completed += (_, _) =>
        {
            if (gen != _bounceGen) return;
            _bounce.BeginAnimation(BounceAxis, null);
            if (WantBounce) StartBounce(); // 그사이 실행 대기가 시작됐으면 이어서
        };
        _bounce.BeginAnimation(BounceAxis, a);
    }

    /// <summary>튀기를 즉시 멈추고 제자리로 (드래그 시작·뷰 버림).</summary>
    private void StopBounce()
    {
        _bounceGen++;
        _bouncing = false;
        _bounce.BeginAnimation(TranslateTransform.XProperty, null);
        _bounce.BeginAnimation(TranslateTransform.YProperty, null);
        _bounce.X = _bounce.Y = 0;
    }

    /// <summary>드래그가 시작됨: 누름 반응을 즉시 되돌리고, 이번 실행의 튀기를 멈춘다 (점 표시는 그대로).</summary>
    public void OnDragStarted()
    {
        PressUp(animate: false);
        if (Item.IsLaunching) _bounceStopped = true;
        StopBounce();
    }

    /// <summary>확대 배율 적용: 아이콘은 RenderTransform, 슬롯은 독 방향 길이만 늘림.</summary>
    public void ApplyScale(double s)
    {
        if (_scale == null || Math.Abs(s - Scale) < 0.0005) return;
        Scale = s;
        _scale.ScaleX = _scale.ScaleY = s;
        double len = _layout.IconSize * s;
        if (_layout.IsVertical) Height = len;
        else Width = len;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Item.IsSeparator) HoverStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!Item.IsSeparator) HoverEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>독 방향으로 along 만큼 비켜섬. animate 면 150ms ease-out (가벼운 RenderTransform 애니메이션).</summary>
    public void AnimateShift(double along, bool animate)
    {
        var prop = _layout.IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        if (animate && Math.Abs(along - ShiftTarget) < 0.01 && _shift.HasAnimatedProperties) return;
        ShiftTarget = along;
        if (!animate)
        {
            _shift.BeginAnimation(prop, null);
            _shift.SetValue(prop, along);
            return;
        }
        var anim = new DoubleAnimation(along, TimeSpan.FromMilliseconds(ShiftMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _shift.BeginAnimation(prop, anim);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // "실행 중 앱" 앞의 자동 구분선은 처리하지 않음 → 패널로 올라가 독 드래그 이동 시작점이 됨.
        // 핀 구분선(설정에 있는 구분선)은 아이콘처럼 끌어서 순서를 바꾼다.
        if (Item.IsAutoSeparator) return;
        e.Handled = true;
        Pressed?.Invoke(this, e);
    }
}
