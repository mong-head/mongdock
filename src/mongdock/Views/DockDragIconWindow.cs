using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 아이콘을 끌어 순서를 바꿀 때 커서를 따라다니는 반투명 복사본.
/// 독 밖으로 끌어내 놓으면 고정 해제되는 상태에서는 아래에 "제거" 라벨을 붙인다.
/// 독 창 밖으로도 나가야 하므로 별도의 비활성(NOACTIVATE) 투명 창. 마우스는 독 창이 캡처하고 있어 이 창은 입력을 받지 않는다.
/// </summary>
internal sealed class DockDragIconWindow : Window
{
    private const double LabelHeight = 22;
    private const double LabelGap = 4;
    private const double MinWidth_ = 64;

    private readonly Grid _iconHost;
    private readonly Image _image;
    private readonly Rectangle _line;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private double _iconSize = 48;

    public DockDragIconWindow(AppServices services)
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
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = true;
        Title = "mongdock Drag";

        _image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _line = new Rectangle { SnapsToDevicePixels = true, Visibility = Visibility.Collapsed };
        _iconHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.8,
        };
        _iconHost.Children.Add(_image);
        _iconHost.Children.Add(_line);

        _labelText = new TextBlock
        {
            Text = Loc.T("제거"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _label = new Border
        {
            Height = LabelHeight,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(LabelHeight / 2),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _labelText,
            Visibility = Visibility.Collapsed,
        };

        var root = new Grid();
        root.Children.Add(_iconHost);
        root.Children.Add(_label);
        Content = root;
        SourceInitialized += (_, _) => services.DesktopWindows.MakeOverlay(this);
    }

    /// <summary>끌 항목의 모양을 준비 (아이콘 또는 구분선).</summary>
    public void Prepare(ImageSource? icon, bool separator, DockLayout layout)
    {
        _iconSize = layout.IconSize;
        _iconHost.Width = _iconHost.Height = _iconSize;
        _image.Source = separator ? null : icon;
        _image.Visibility = separator ? Visibility.Collapsed : Visibility.Visible;
        _line.Visibility = separator ? Visibility.Visible : Visibility.Collapsed;
        _line.Fill = layout.Separator;
        if (layout.IsVertical)
        {
            _line.Width = _iconSize * 0.72;
            _line.Height = 2;
        }
        else
        {
            _line.Width = 2;
            _line.Height = _iconSize * 0.72;
        }

        _label.Background = layout.LabelBackground;
        _label.BorderBrush = layout.LabelBorder;
        _labelText.Foreground = layout.LabelForeground;
        _label.Margin = new Thickness(0, _iconSize + LabelGap, 0, 0);

        Width = Math.Max(_iconSize, MinWidth_);
        Height = _iconSize + LabelGap + LabelHeight;
        SetRemove(false);
    }

    /// <summary>아이콘 왼쪽 위가 iconTopLeft(화면 DIP)에 오도록 이동 (처음이면 표시).</summary>
    public void MoveTo(Point iconTopLeft)
    {
        Left = Math.Round(iconTopLeft.X - (Width - _iconSize) / 2);
        Top = Math.Round(iconTopLeft.Y);
        if (!IsVisible) Show();
    }

    /// <summary>놓으면 독에서 제거되는 상태 표시 ("제거" 라벨 + 더 흐리게).</summary>
    public void SetRemove(bool remove)
    {
        _label.Visibility = remove ? Visibility.Visible : Visibility.Collapsed;
        _iconHost.Opacity = remove ? 0.5 : 0.8;
    }
}
