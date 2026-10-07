using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MyDock.Models;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 독 항목 하나의 화면 (코드로 구성). 아이콘 + 실행 중 점(아이콘 아래 = 가장자리 쪽) + 알림 점(아이콘 모서리).
/// 확대는 DockWindow 가 매 프레임 <see cref="ApplyScale"/> 로 지정 — 아이콘은 가장자리 반대쪽으로 커지고
/// 슬롯은 독 방향으로 늘어나 이웃과 겹치지 않는다. 바운스/흔들림 없음.
/// </summary>
internal sealed class DockItemView : Grid
{
    private readonly DockLayout _layout;
    private readonly ScaleTransform? _scale;
    private readonly Ellipse? _runningDot;
    private readonly Ellipse? _notifyDot;
    private bool _pressed;

    public DockItemViewModel Item { get; }

    /// <summary>현재 확대 배율 (1 = 기본).</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>확대 전 독 방향 길이 (여백 포함).</summary>
    public double BaseLength { get; }

    public event EventHandler? HoverStarted;
    public event EventHandler? HoverEnded;
    public event EventHandler? Clicked;

    public DockItemView(DockItemViewModel item, DockLayout layout)
    {
        Item = item;
        _layout = layout;
        Focusable = false;
        Background = Brushes.Transparent; // 슬롯 전체를 클릭 영역으로

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
        double offset = -Math.Max(d / 2 + 0.5, layout.Padding / 2 + d / 2 - 0.5);
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
        if (_runningDot != null) _runningDot.Visibility = Item.IsRunning ? Visibility.Visible : Visibility.Collapsed;
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // 구분선은 처리하지 않음 → 패널로 올라가 독 드래그 이동 시작점이 됨
        if (Item.IsSeparator) return;
        _pressed = true;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (Item.IsSeparator) return;
        if (_pressed && IsMouseOver) Clicked?.Invoke(this, EventArgs.Empty);
        _pressed = false;
        e.Handled = true;
    }
}
