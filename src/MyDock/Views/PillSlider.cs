using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// MyDockFinder/맥 제어센터 같은 큰 알약 슬라이더: 회색 트랙 위에 흰 채움 + 오른쪽 끝 흰 원형 노브(그림자),
/// 트랙 왼쪽에 값 숫자. 클릭·드래그·휠로 조절 (포커스 없이 마우스만).
/// </summary>
internal sealed class PillSlider : Grid
{
    private const double H = 26;
    private readonly Border _fill;
    private readonly Ellipse _knob;
    private readonly TextBlock _valueText;
    private double _value;
    private readonly Brush _textOnTrack;
    private static readonly Brush TextOnFill = Converters.BrushParser.Frozen(Color.FromArgb(0x8C, 0, 0, 0));

    /// <summary>사용자가 바꾼 값 (0~1).</summary>
    public event EventHandler<double>? UserChanged;

    public bool IsDragging => IsMouseCaptured;

    public PillSlider(UiPalette p, bool showValue = true)
    {
        _textOnTrack = p.SubText;
        Height = H;
        Background = Brushes.Transparent;
        Cursor = Cursors.Arrow;

        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(H / 2),
            Background = p.SliderTrack,
            BorderBrush = p.SliderBorder,
            BorderThickness = new Thickness(0.75),
        });
        _fill = new Border
        {
            CornerRadius = new CornerRadius(H / 2),
            Background = p.SliderFill,
            BorderBrush = p.SliderBorder,
            BorderThickness = new Thickness(0.75),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = H,
        };
        Children.Add(_fill);
        _valueText = new TextBlock
        {
            Foreground = p.SubText,
            FontSize = 13,
            Margin = new Thickness(11, 0, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = showValue ? Visibility.Visible : Visibility.Collapsed,
        };
        Children.Add(_valueText);
        _knob = new Ellipse
        {
            Width = H,
            Height = H,
            Fill = Brushes.White,
            Stroke = p.SliderBorder,
            StrokeThickness = 0.75,
            HorizontalAlignment = HorizontalAlignment.Left,
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.25 },
        };
        Children.Add(_knob);

        SizeChanged += (_, _) => Layout();
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 1);
            Layout();
        }
    }

    private void Layout()
    {
        double w = ActualWidth;
        if (w <= 0) return;
        double x = _value * Math.Max(0, w - H);
        _knob.Margin = new Thickness(x, 0, 0, 0);
        _fill.Width = x + H;
        _valueText.Text = Math.Round(_value * 100).ToString(CultureInfo.InvariantCulture);
        // 채움(밝은색)이 숫자 위까지 오면 어두운 글자, 아니면 트랙 위 글자색 (다크 테마 대비)
        _valueText.Foreground = x + H > 40 ? TextOnFill : _textOnTrack;
    }

    private void SetFromMouse(MouseEventArgs e)
    {
        double w = ActualWidth;
        if (w <= H) return;
        double v = (e.GetPosition(this).X - H / 2) / (w - H);
        Value = v;
        UserChanged?.Invoke(this, _value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        SetFromMouse(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) SetFromMouse(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Value = _value + Math.Sign(e.Delta) * 0.04;
        UserChanged?.Invoke(this, _value);
        e.Handled = true;
    }
}
