using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 설정 → 상단바 → "트레이 아이콘 정리": 지금 있는 트레이 아이콘마다 "바에 표시" 스위치 + 위/아래 순서 버튼,
/// 맨 아래 "윈도우 설정대로 되돌리기" (몽독 저장값 지우기). 클릭만으로 (원격 사용 고려).
/// </summary>
internal sealed partial class SettingsWindow
{
    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase);
    private const int TrayRefreshDebounceMs = 300;
    private DispatcherTimer? _trayRefreshTimer;

    /// <summary>
    /// 트레이 아이콘 목록이 바뀜(앱 추가·제거·아이콘 변경) → 상단바 페이지의 "트레이 아이콘 정리" 를 300ms 몰아서 다시 그림.
    /// 생성자에서 구독, 창이 닫힐 때 <see cref="StopTrayRefresh"/> 로 해제.
    /// </summary>
    private void OnTrayIconsChanged(object? sender, EventArgs e)
    {
        if (_closed || _page != Page.TopBar || !_services.Settings.Current.TopBar.ShowTrayIcons) return;
        if (_trayRefreshTimer is null)
        {
            _trayRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TrayRefreshDebounceMs) };
            _trayRefreshTimer.Tick += (_, _) =>
            {
                _trayRefreshTimer.Stop();
                if (_closed || _page != Page.TopBar) return;
                // 슬라이더를 끄는 중이면 끝난 뒤에 (다시 그리면 끌기가 끊김)
                if (_sliderDragging || _pendingSlider != null) { _trayRefreshTimer.Start(); return; }
                QueueRebuild();
            };
        }
        _trayRefreshTimer.Stop();
        _trayRefreshTimer.Start();
    }

    private void StopTrayRefresh()
    {
        _services.TrayIcons.Changed -= OnTrayIconsChanged;
        _trayRefreshTimer?.Stop();
    }

    private void AddTrayArrange(Panel body)
    {
        body.Children.Add(SectionTitle("트레이 아이콘 정리"));
        var top = _services.Settings.Current.TopBar;
        var slots = TrayIconLayout.Compute(_services.TrayIcons.Icons, top);
        var rows = new List<UIElement>();
        if (slots.Count == 0)
        {
            rows.Add(Row("표시할 트레이 아이콘이 없어요", "앱이 트레이 아이콘을 만들면 여기 나타나요.", new Border()));
        }
        else
        {
            var bar = slots.Where(s => s.OnBar).ToList();
            var more = slots.Where(s => !s.OnBar).ToList();
            foreach (var s in slots)
            {
                var group = s.OnBar ? bar : more;
                int i = group.IndexOf(s);
                rows.Add(TrayArrangeRow(s, canUp: i > 0, canDown: i < group.Count - 1));
            }
        }
        rows.Add(Row("윈도우 설정대로 되돌리기",
            NotifyIconSettingsReader.Shared.IsAvailable
                ? "몽독에서 옮긴 자리를 지우고 윈도우의 '작업 표시줄에 항상 표시' 설정을 따릅니다. 상단바에서 아이콘을 끌어 ⌃ 에 넣거나 꺼낼 수도 있어요."
                : "몽독에서 옮긴 자리를 지웁니다 (이 윈도우에는 트레이 아이콘 설정이 없어 모두 ⌃ 안으로). 상단바에서 아이콘을 끌어 옮길 수도 있어요.",
            ActionButton("되돌리기", () => Commit(() => _services.Settings.Current.TopBar.TrayIconPlacement.Clear(), rebuild: true))));
        body.Children.Add(Group(rows.ToArray()));
    }

    private Grid TrayArrangeRow(TrayIconSlot slot, bool canUp, bool canDown)
    {
        var info = slot.Info;
        string title = AppName(info);
        string tip = string.IsNullOrWhiteSpace(info.Tooltip) ? "" : info.Tooltip.Trim().Replace("\r", " ").Replace("\n", " ");
        if (tip.Length > 60) tip = tip[..60] + "…";
        bool wantsBar = slot.Source == TrayPlacementSource.Windows
            || slot.Source == TrayPlacementSource.Mongdock && _services.Settings.Current.TopBar.TrayIconPlacement.TryGetValue(slot.PlacementKey, out var pl) && pl.OnBar;
        string why = wantsBar && !slot.OnBar ? "최대 개수를 넘어 ⌃ 안"
            : slot.Source switch
            {
                TrayPlacementSource.Mongdock => "몽독에서 옮김",
                TrayPlacementSource.Windows => "윈도우 설정: 항상 표시",
                _ => "기본: ⌃ 안",
            };
        string sub = tip.Length > 0 && !string.Equals(tip, title, StringComparison.OrdinalIgnoreCase) ? $"{tip} · {why}" : why;

        var grid = Row(title, sub, TrayArrangeControls(slot, canUp, canDown));
        // 왼쪽에 아이콘
        var texts = grid.Children[0] as FrameworkElement;
        if (texts is not null && info.Icon is not null)
        {
            grid.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
            foreach (UIElement c in grid.Children) Grid.SetColumn(c, Grid.GetColumn(c) + 1);
            var img = new Image { Source = info.Icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            grid.Children.Add(img);
        }
        return grid;
    }

    private StackPanel TrayArrangeControls(TrayIconSlot slot, bool canUp, bool canDown)
    {
        string key = slot.PlacementKey;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(SmallButton("▲", slot.OnBar ? "왼쪽으로" : "위로", canUp,
            () => Commit(() => TrayIconLayout.Nudge(_services.TrayIcons.Icons, _services.Settings.Current.TopBar, key, -1), rebuild: true)));
        panel.Children.Add(SmallButton("▼", slot.OnBar ? "오른쪽으로" : "아래로", canDown,
            () => Commit(() => TrayIconLayout.Nudge(_services.TrayIcons.Icons, _services.Settings.Current.TopBar, key, +1), rebuild: true)));
        var label = new TextBlock { Text = "바에 표시", FontSize = 12, Foreground = _p.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        panel.Children.Add(label);
        panel.Children.Add(Toggle(slot.OnBar, on => Commit(() =>
        {
            var icons = _services.TrayIcons.Icons;
            var top = _services.Settings.Current.TopBar;
            int end = TrayIconLayout.Compute(icons, top).Count(s => s.OnBar);
            TrayIconLayout.Move(icons, top, key, on, end);
        }, rebuild: true)));
        return panel;
    }

    private Button SmallButton(string glyph, string tooltip, bool enabled, Action action)
    {
        var b = ActionButton(glyph, action);
        b.ToolTip = tooltip;
        b.MinWidth = 0;
        b.Width = 28;
        b.Padding = new Thickness(0);
        b.Margin = new Thickness(2, 0, 2, 0);
        b.IsEnabled = enabled;
        b.Opacity = enabled ? 1 : 0.35;
        return b;
    }

    /// <summary>앱 이름: exe 의 파일 설명(예: "KakaoTalk") → 없으면 exe 이름.</summary>
    private static string AppName(TrayIconInfo info)
    {
        string fallback = System.IO.Path.GetFileNameWithoutExtension(info.ProcessName);
        if (info.ProcessPath.Length == 0) return fallback;
        lock (AppNames)
        {
            if (AppNames.TryGetValue(info.ProcessPath, out var cached)) return cached;
        }
        string name = fallback;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(info.ProcessPath);
            if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription.Trim();
            else if (!string.IsNullOrWhiteSpace(vi.ProductName)) name = vi.ProductName.Trim();
        }
        catch { /* 접근 불가 → exe 이름 */ }
        lock (AppNames) AppNames[info.ProcessPath] = name;
        return name;
    }
}
