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
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "MyDock Label";

        _text = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic"),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
        };
        _bubble = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 4, 9, 5),
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

    /// <summary>anchor(DIP, 화면 좌표) 기준으로 표시. 세로 독은 anchor 가 말풍선의 가장자리 쪽 세로 중앙, 가로 독은 가장자리 쪽 가로 중앙.</summary>
    public void ShowAt(string text, Point anchor, Models.DockEdge edge)
    {
        _text.Text = text;
        _bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _bubble.DesiredSize;

        switch (edge)
        {
            case Models.DockEdge.Left:
                Left = anchor.X;
                Top = anchor.Y - size.Height / 2;
                break;
            case Models.DockEdge.Top:
                Left = anchor.X - size.Width / 2;
                Top = anchor.Y;
                break;
            case Models.DockEdge.Bottom:
                Left = anchor.X - size.Width / 2;
                Top = anchor.Y - size.Height;
                break;
            default:
                Left = anchor.X - size.Width;
                Top = anchor.Y - size.Height / 2;
                break;
        }
        if (!IsVisible) Show();
    }
}
