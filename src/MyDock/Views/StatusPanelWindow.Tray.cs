using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyDock.Views;

/// <summary>상단바 ⌃ 를 눌렀을 때: 상단바에 다 못 들어간 트레이 아이콘 격자 (한 줄 최대 6개).</summary>
internal sealed partial class StatusPanelWindow
{
    private const double TrayCardPadding = 8;
    private const double TrayCell = 34;
    private const int TrayColumns = 6;
    private WrapPanel? _trayGrid;

    private double TrayCardWidth()
    {
        int n = TrayIconButton.Split(_services).Overflow.Count;
        int cols = Math.Clamp(n, 1, TrayColumns);
        return cols * TrayCell + TrayCardPadding * 2 + 1.5; // 테두리 0.75 × 2
    }

    private UIElement BuildTray()
    {
        _trayGrid = new WrapPanel { Orientation = Orientation.Horizontal };
        FillTray();
        _services.TrayIcons.Changed += OnTrayChanged;
        Closed += (_, _) => _services.TrayIcons.Changed -= OnTrayChanged;
        return _trayGrid;
    }

    private void OnTrayChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (TrayIconButton.Split(_services).Overflow.Count == 0)
        {
            Close();
            return;
        }
        FillTray();
        _card.Width = TrayCardWidth();
    }

    private void FillTray()
    {
        if (_trayGrid is null) return;
        var overflow = TrayIconButton.Split(_services).Overflow;
        var style = (Style)FindStyle("CardButton");
        // 기존 버튼 재사용 (호버 상태 유지)
        var existing = _trayGrid.Children.OfType<TrayIconButton>().ToDictionary(b => b.Info.Key);
        _trayGrid.Children.Clear();
        foreach (var info in overflow)
        {
            if (existing.TryGetValue(info.Key, out var b)) b.Apply(info);
            else
            {
                b = new TrayIconButton(_services, info, style, 18, beforeClick: null)
                {
                    Width = TrayCell,
                    Height = TrayCell,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = _p.Text,
                };
            }
            _trayGrid.Children.Add(b);
        }
    }
}
