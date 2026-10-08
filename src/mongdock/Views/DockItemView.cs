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
/// 슬롯은 독 방향으로 늘어나 이웃과 겹치지 않는다. 바운스/흔들림 없음.
/// 누르면 <see cref="Pressed"/> 만 알리고, 클릭/드래그 구분·실행은 DockWindow 가 마우스 캡처로 처리한다.
/// 드래그로 순서를 바꿀 때 이웃 아이콘이 비켜서는 움직임은 <see cref="AnimateShift"/> (RenderTransform 이동).
/// </summary>
internal sealed class DockItemView : Grid
{
    private readonly DockLayout _layout;
    private readonly ScaleTransform? _scale;
    private readonly Ellipse? _runningDot;
    private readonly Ellipse? _notifyDot;
    /// <summary>드래그 정렬 중 비켜서기 이동 (독 방향). 레이아웃은 그대로 두고 그림만 옮긴다.</summary>
    private readonly TranslateTransform _shift = new();
    private const double ShiftMs = 150;

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
        iconHost.RenderTransform = _scale;

        // 맥 스타일 아이콘은 여백·그림자까지 포함되어 오므로 슬롯 크기 그대로 그린다 (추가 가공 없음)
        var image = new Image { Source = item.Icon, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        iconHost.Children.Add(image);

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

        UpdateDots();
        item.PropertyChanged += OnItemPropertyChanged;
    }

    /// <summary>뷰를 버릴 때 VM 구독 해제.</summary>
    public void Detach() => Item.PropertyChanged -= OnItemPropertyChanged;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateDots();

    private void UpdateDots()
    {
        if (_runningDot != null)
        {
            _runningDot.Visibility = Item.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            _runningDot.Opacity = Item.RunningElsewhereOnly ? 0.4 : 1; // 다른 데스크톱에만 창이 있음
        }
        if (_notifyDot != null) _notifyDot.Visibility = Item.HasNotification ? Visibility.Visible : Visibility.Collapsed;
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
