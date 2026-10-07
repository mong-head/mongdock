using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 독을 드래그로 옮길 때 놓일 자리를 보여주는 반투명 고스트.
/// 드래그 중에는 AppBar 를 건드리지 않고 이것만 움직이고, 놓을 때 한 번만 실제 독을 옮긴다.
/// </summary>
internal sealed class DockGhostWindow : Window
{
    private readonly Border _box;

    public DockGhostWindow(AppServices services)
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
        Title = "MyDock Ghost";
        Opacity = 0.55;

        _box = new Border { BorderThickness = new Thickness(1.5) };
        Content = _box;
        SourceInitialized += (_, _) => services.DesktopWindows.MakeOverlay(this);
    }

    public void ShowAt(Rect rect, DockLayout layout)
    {
        _box.Background = layout.SolidBackground;
        _box.BorderBrush = Brushes.White;
        _box.CornerRadius = new CornerRadius(layout.CornerRadius);
        Left = rect.Left;
        Top = rect.Top;
        Width = Math.Max(1, rect.Width);
        Height = Math.Max(1, rect.Height);
        if (!IsVisible) Show();
    }
}
