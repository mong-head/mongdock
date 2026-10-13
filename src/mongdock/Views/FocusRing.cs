using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Mongdock.Views;

/// <summary>
/// 키보드 포커스 표시 (2026-10-13 사용자: 체크박스에 WPF 기본 점선 사각형이 모양에 안 맞게 나옴).
/// - 몽독 모든 창에서 WPF 기본 점선(FocusVisualStyle)을 없앰 (버튼·체크·라디오·토글·콤보·목록 칸·슬라이더 등).
/// - 대신 키보드(Tab·방향키)로 왔을 때만 연보라 2px 둥근 테두리를 그 칸(또는 FocusRing.Target 이 켜진 조상 — 예: 목록 줄 전체)에 그림.
///   마우스로 누르면 표시하지 않고, 누르는 순간 있던 표시도 지움. 글자 입력 칸은 깜빡이는 커서가 있으니 표시하지 않음.
/// </summary>
internal static class FocusRing
{
    /// <summary>켜 두면 안쪽 칸이 키보드 포커스를 받을 때 이 요소 전체에 표시 (목록 줄 등).</summary>
    public static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached("Target", typeof(bool), typeof(FocusRing), new PropertyMetadata(false));

    public static void SetTarget(DependencyObject d, bool v) => d.SetValue(TargetProperty, v);
    public static bool GetTarget(DependencyObject d) => (bool)d.GetValue(TargetProperty);

    private static readonly Color RingColor = Color.FromRgb(0xA8, 0x96, 0xF2);
    private static RingAdorner? _ring;
    private static bool _done;
    /// <summary>마지막 입력이 키보드였는지 (키를 누르면 true, 마우스를 누르면 false).</summary>
    private static bool _keyboard;

    public static void Init()
    {
        if (_done) return;
        _done = true;
        // 기본 점선 없애기: 포커스를 받는 순간(WPF 가 점선을 그리기 전) 그 요소의 FocusVisualStyle 을 null 로 — 테마 스타일보다 앞섬.
        // 형식 기본값을 바꾸는 방식(OverrideMetadata)은 테마 스타일에 지고, WPF 정적 생성자와 부딪혀 독이 안 뜬 적이 있어 쓰지 않음 (2026-10-13)
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotFocus), true);
        EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostFocus), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => { _keyboard = false; Hide(); }), true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, _) => _keyboard = true), true);
    }

    private static void OnGotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.NewFocus) || e.NewFocus is not UIElement el) return;
        if (el is FrameworkElement fe && fe.FocusVisualStyle is not null) fe.FocusVisualStyle = null;
        if (!_keyboard || el is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox || el is Window) { Hide(); return; }
        Show(TargetOf(el));
    }

    private static void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OldFocus) && e.NewFocus is null) Hide();
    }

    /// <summary>표시할 요소: 몇 단계 위까지 FocusRing.Target 이 켜진 조상이 있으면 그것, 아니면 자기.</summary>
    private static UIElement TargetOf(UIElement el)
    {
        DependencyObject? d = el;
        for (int i = 0; i < 8 && d is not null; i++)
        {
            if (d is UIElement u && GetTarget(u)) return u;
            d = VisualTreeHelper.GetParent(d);
        }
        return el;
    }

    private static void Show(UIElement target)
    {
        Hide();
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer) return;
        double radius = target is Border b ? b.CornerRadius.TopLeft : target is Control ? 6 : 6;
        _ring = new RingAdorner(target, radius);
        layer.Add(_ring);
    }

    public static void Hide()
    {
        if (_ring is null) return;
        if (AdornerLayer.GetAdornerLayer(_ring.AdornedElement) is { } layer) layer.Remove(_ring);
        _ring = null;
    }

    private sealed class RingAdorner : Adorner
    {
        private readonly double _radius;
        private readonly Pen _pen = new(new SolidColorBrush(RingColor), 2);

        public RingAdorner(UIElement adorned, double radius) : base(adorned)
        {
            _radius = Math.Max(2, radius);
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            if (size.Width <= 0 || size.Height <= 0) return;
            dc.DrawRoundedRectangle(null, _pen, new Rect(-1, -1, size.Width + 2, size.Height + 2), _radius + 1, _radius + 1);
        }
    }
}
