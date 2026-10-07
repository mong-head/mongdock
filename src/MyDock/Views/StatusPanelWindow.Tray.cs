using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyDock.Views;

/// <summary>
/// 상단바 ⌃ 를 눌렀을 때: 상단바에 보이지 않는 트레이 아이콘 격자 (한 줄 최대 6개).
/// 아이콘을 끌어 상단바 트레이 영역에 놓으면 바로 꺼냄 (TrayIconDrag).
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private const double TrayCardPadding = 8;
    private const double TrayCell = 34;
    private const int TrayColumns = 6;
    private WrapPanel? _trayGrid;
    private System.Windows.Threading.DispatcherTimer? _trayRetry;

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
        _services.Settings.SettingsChanged += OnTrayChanged;           // 몽독에서 옮김 (끌어 놓기·설정 창)
        Services.NotifyIconSettingsReader.Shared.Changed += OnTrayChanged; // 윈도우 설정에서 바꿈
        Closed += (_, _) =>
        {
            _services.TrayIcons.Changed -= OnTrayChanged;
            _services.Settings.SettingsChanged -= OnTrayChanged;
            Services.NotifyIconSettingsReader.Shared.Changed -= OnTrayChanged;
        };
        return _trayGrid;
    }

    private void OnTrayChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (TrayIconDrag.IsActive)
        {
            // 끄는 중엔 격자를 바꾸지 않음 (끄는 버튼이 빠지면 마우스 캡처가 끊김) → 놓은 뒤 다시
            if (_trayRetry is null)
            {
                _trayRetry = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _trayRetry.Tick += (_, _) =>
                {
                    _trayRetry!.Stop();
                    OnTrayChanged(this, EventArgs.Empty);
                };
            }
            _trayRetry.Stop();
            _trayRetry.Start();
            return;
        }
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
                b = new TrayIconButton(_services, info, style, 18, beforeClick: null, onBar: false)
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
