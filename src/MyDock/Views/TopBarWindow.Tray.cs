using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>상단바 오른쪽 트레이 아이콘 영역 (블루투스 왼쪽). 모든 모니터의 상단바가 같은 서비스 목록을 보여 준다.</summary>
public partial class TopBarWindow
{
    private readonly Dictionary<string, TrayIconButton> _trayButtons = new();
    private Button? _trayMore;
    private List<string> _trayOrder = new();

    private void OnTrayIconsChanged(object? sender, EventArgs e) => SyncTrayIcons();

    private void SyncTrayIcons()
    {
        if (!_initialized || _closed) return;
        var s = _services.Settings.Current.TopBar;
        bool show = s.Enabled && s.ShowTrayIcons && !AppState.Paused;
        if (!show)
        {
            if (TrayArea.Children.Count > 0) TrayArea.Children.Clear();
            _trayButtons.Clear();
            _trayOrder.Clear();
            TrayArea.Visibility = Visibility.Collapsed;
            if (_panel?.Kind == StatusPanelKind.Tray) _panel.Close();
            return;
        }

        var (onBar, overflow) = TrayIconButton.Split(_services);
        var style = (Style)FindResource("BarButton");
        var keys = new List<string>(onBar.Count + 1);
        foreach (var info in onBar)
        {
            if (_trayButtons.TryGetValue(info.Key, out var b)) b.Apply(info);
            else
            {
                b = new TrayIconButton(_services, info, style, 16, beforeClick: () => _panel?.Close())
                {
                    Padding = new Thickness(5, 0, 5, 0),
                    MinWidth = 0,
                };
                _trayButtons[info.Key] = b;
            }
            keys.Add(info.Key);
        }
        foreach (var gone in _trayButtons.Keys.Except(keys).ToList()) _trayButtons.Remove(gone);
        if (overflow.Count > 0) keys.Add("⌃");

        // 순서·구성이 바뀔 때만 자식 다시 배치 (호버 중인 버튼이 빠졌다 들어가며 깜빡이지 않게)
        if (!keys.SequenceEqual(_trayOrder))
        {
            TrayArea.Children.Clear();
            foreach (var k in keys) TrayArea.Children.Add(k == "⌃" ? TrayMoreButton() : _trayButtons[k]);
            _trayOrder = keys;
            Remeasure(RightSection);
        }
        TrayArea.Visibility = keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (overflow.Count == 0 && _panel?.Kind == StatusPanelKind.Tray) _panel.Close();
    }

    private Button TrayMoreButton()
    {
        if (_trayMore != null) return _trayMore;
        var b = new Button { Style = (Style)FindResource("BarButton"), Padding = new Thickness(5, 0, 5, 0), MinWidth = 0, ToolTip = "트레이 아이콘 더 보기" };
        var path = new ShapePath
        {
            Width = 14,
            Height = 16,
            Data = BarIcons.ChevronUp,
            LayoutTransform = (Transform)FindResource("BarIconScale"),
        };
        path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = b });
        b.Content = path;
        b.Click += (_, _) => TogglePanel(StatusPanelKind.Tray, b);
        _trayMore = b;
        return b;
    }
}
