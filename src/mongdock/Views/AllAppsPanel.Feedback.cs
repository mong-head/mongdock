using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Mongdock.Views;

/// <summary>
/// 판 칸 마우스 올림·누름 표시 (2026-10-13 사용자: 연보라 판 안에서 누르면 칸이 회색으로 칠해져 어색함):
/// - 회색 채움 없음. 올림 = 칸 바탕에 놓인 판보다 한 단계 밝은 같은 계열 하이라이트(연한 판 위 = 흰색 반투명, 흰 판 위 = 아주 연한 회보라) + 아이콘 1.06배.
/// - 누름 = 아이콘이 0.94배로 살짝 눌리고(80ms) 아이콘 모양 그대로 어두운 막 10% (맥처럼 누른 느낌이 아이콘에서). 칸 바탕은 올림 그대로.
/// - 목록 줄은 바탕 하이라이트만, 누르면 조금 진하게.
/// </summary>
internal sealed partial class AllAppsPanel
{
    private const int PressMs = 80;
    private const double PressScale = 0.94;

    /// <summary>끌기가 시작되면 눌린 아이콘을 원래대로 (끄는 그림이 따로 나오므로).</summary>
    private Action? _pressRelease;

    /// <summary>시험 그림에서만: 앞 칸들을 차례로 올림/누름 상태로 그림 (예: "hover,press").</summary>
    internal static string? FeedbackForTest { get; set; }

    private Brush CellHoverBrush(bool onTint) => onTint ? P.CellHoverOnTint : P.CellHover;

    /// <summary>
    /// 칸 올림·누름. icon = 커지고 눌리는 부분(앱 아이콘·폴더 그림). onTint = 연한 채움 판(추천 묶음·펼친 폴더) 위의 칸.
    /// keepBackground = 바탕을 건드리지 않음 (검색 결과에서 고른 칸).
    /// </summary>
    private void CellFeedback(Border cell, FrameworkElement icon, bool onTint, bool keepBackground = false)
    {
        var scale = icon.RenderTransform as ScaleTransform;
        if (scale is null)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = scale = new ScaleTransform(1, 1);
        }
        // 아이콘 모양 그대로의 어두운 막: 아이콘 옆(같은 Grid 칸)에 겹치고 아이콘과 같은 배율을 씀
        Rectangle? shade = null;
        if (icon.Parent is Grid host)
        {
            shade = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)),
                OpacityMask = new VisualBrush(icon) { Stretch = Stretch.Fill },
                Width = icon.Width,
                Height = icon.Height,
                HorizontalAlignment = icon.HorizontalAlignment,
                VerticalAlignment = icon.VerticalAlignment,
                Margin = icon.Margin,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            host.Children.Insert(host.Children.IndexOf(icon) + 1, shade);
        }
        bool pressed = false, instant = false;
        void To(double v, int ms)
        {
            if (!Motion || ms == 0 || instant)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = scale.ScaleY = v;
                return;
            }
            var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        void Hover(bool on)
        {
            if (!keepBackground) cell.Background = on ? CellHoverBrush(onTint) : Brushes.Transparent;
            if (!pressed) To(on ? 1.06 : 1, 100);
        }
        void Press(bool on)
        {
            if (pressed == on) return;
            pressed = on;
            if (shade is not null) shade.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            To(on ? PressScale : cell.IsMouseOver ? 1.06 : 1, on ? PressMs : 100);
            if (on) _pressRelease = () => Press(false);
            else if (_pressRelease is not null) _pressRelease = null;
        }
        cell.MouseEnter += (_, _) => Hover(true);
        cell.MouseLeave += (_, _) => { Press(false); Hover(false); };
        cell.PreviewMouseLeftButtonDown += (_, _) => Press(true);
        cell.PreviewMouseLeftButtonUp += (_, _) => Press(false);
        cell.LostMouseCapture += (_, _) => Press(false);
        if (FeedbackForTest is { Length: > 0 } states)
        {
            // 칸마다 하나씩 차례로 ("hover,press" = 첫 칸 올림, 둘째 칸 누름, "-" = 그대로)
            int comma = states.IndexOf(',');
            string state = comma < 0 ? states : states[..comma];
            FeedbackForTest = comma < 0 ? null : states[(comma + 1)..];
            instant = true; // 정지 그림이라 애니메이션 없이 바로
            if (state is "hover" or "press") Hover(true);
            if (state == "press") { Press(true); _pressRelease = null; }
            instant = false;
        }
    }

    /// <summary>목록 줄 (앱 고르기·잠자는 앱): 바탕 하이라이트만, 누르면 조금 진하게 (회색 아님 — 판 색 계열).</summary>
    private void RowFeedback(Border row)
    {
        row.MouseEnter += (_, _) => row.Background = P.CellHover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.PreviewMouseLeftButtonDown += (_, _) => row.Background = P.CellPress;
        row.PreviewMouseLeftButtonUp += (_, _) => row.Background = row.IsMouseOver ? P.CellHover : Brushes.Transparent;
    }
}
