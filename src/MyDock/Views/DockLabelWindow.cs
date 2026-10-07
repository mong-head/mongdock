using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyDock.Services;

namespace MyDock.Views;

/// <summary>
/// 독 아이콘 호버 시 앱 이름을 보여주는 작은 말풍선.
/// 세로 독은 창 두께가 좁아 같은 창 안에 이름을 넣을 수 없으므로 별도의 비활성(NOACTIVATE) 창을 쓴다.
/// Popup/ToolTip 과 달리 포커스를 뺏지 않고, ShowActivated=False 로 SW_SHOWNA 표시된다.
/// </summary>
internal sealed class DockLabelWindow : Window
{
    private readonly Border _bubble;
    private readonly TextBlock _text;

    public DockLabelWindow(AppServices services)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        // 크기를 재지 않는 고정 크기 투명 창. 말풍선은 창 안에서 anchor 쪽 변에 정렬된다.
        // (SizeToContent/사전 Measure 는 숨김·재표시 때 크기가 어긋나 글자가 잘리거나 왼쪽 위에 붙었음)
        SizeToContent = SizeToContent.Manual;
        Width = BoxWidth;
        Height = BoxHeight;
        UseLayoutRounding = true;
        Title = "MyDock Label";

        _text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Noto Sans KR, Malgun Gothic"),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        _bubble = new Border
        {
            // 맥처럼 작은 캡슐: 높이 고정, 좌우 패딩 균일, 글자는 가운데
            Height = 24,
            MinWidth = 24,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 0, 11, 1),
            BorderThickness = new Thickness(1),
            Child = _text,
        };
        Content = _bubble;

        SourceInitialized += (_, _) => services.DesktopWindows.MakeOverlay(this);
    }

    public void SetColors(Brush background, Brush foreground, Brush border)
    {
        _bubble.Background = background;
        _bubble.BorderBrush = border;
        _text.Foreground = foreground;
    }

    private const double BoxWidth = 360, BoxHeight = 40;
    private Models.DockEdge _edge;

    /// <summary>
    /// anchor(DIP, 화면 좌표) = 확대된 아이콘의 안쪽 끝 + 간격 지점.
    /// 세로 독은 말풍선의 가장자리 쪽 변 세로 중앙, 가로 독은 가장자리 쪽 변 가로 중앙이 anchor 에 온다.
    /// </summary>
    public void ShowAt(string text, Point anchor, Models.DockEdge edge)
    {
        _edge = edge;
        _text.Text = text;
        (_bubble.HorizontalAlignment, _bubble.VerticalAlignment) = edge switch
        {
            Models.DockEdge.Left => (HorizontalAlignment.Left, VerticalAlignment.Center),
            Models.DockEdge.Top => (HorizontalAlignment.Center, VerticalAlignment.Top),
            Models.DockEdge.Bottom => (HorizontalAlignment.Center, VerticalAlignment.Bottom),
            _ => (HorizontalAlignment.Right, VerticalAlignment.Center),
        };
        MoveTo(anchor);
        if (!IsVisible) Show();
    }

    /// <summary>확대 애니메이션 중 아이콘을 따라 위치만 갱신.</summary>
    public void MoveTo(Point anchor)
    {
        double left, top;
        switch (_edge)
        {
            case Models.DockEdge.Left:
                left = anchor.X;
                top = anchor.Y - BoxHeight / 2;
                break;
            case Models.DockEdge.Top:
                left = anchor.X - BoxWidth / 2;
                top = anchor.Y;
                break;
            case Models.DockEdge.Bottom:
                left = anchor.X - BoxWidth / 2;
                top = anchor.Y - BoxHeight;
                break;
            default:
                left = anchor.X - BoxWidth;
                top = anchor.Y - BoxHeight / 2;
                break;
        }
        left = Math.Round(left);
        top = Math.Round(top);
        if (Left != left) Left = left;
        if (Top != top) Top = top;
    }
}
