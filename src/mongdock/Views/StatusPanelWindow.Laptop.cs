using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 노트북용 제어 항목: 화면 밝기(디스플레이마다 슬라이더) + 야간 모드 상태(누르면 설정), 전원 모드(최고 효율/균형/최고 성능) 세그먼트.
/// 지원 안 되는 항목은 숨김 (데스크톱 + DDC/CI 미지원 모니터면 밝기 행 없음, 전원 모드 사용 불가면 세그먼트 없음).
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private const string SunGlyph = "";   // Brightness
    private const string MoonGlyph = "";  // QuietHours (달)
    /// <summary>밝기 드래그 중 보내는 간격 (DDC 는 느려서 서비스가 마지막 값만 합쳐 보냄).</summary>
    private static readonly TimeSpan BrightnessThrottle = TimeSpan.FromMilliseconds(50);

    // ───────────────────────── 디스플레이 (밝기 + 야간 모드) ─────────────────────────

    /// <summary>제어 센터 "디스플레이" 타일. 밝기도 야간 모드도 없으면 접힘.</summary>
    private UIElement BuildDisplayTile()
    {
        var brightness = BrightnessService.Instance;
        var stack = new StackPanel();
        var title = Heading("디스플레이");
        title.FontSize = 13.5;
        title.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(title);

        var rows = new StackPanel();
        stack.Children.Add(rows);

        // 야간 모드: 켜짐 여부는 레지스트리에서 읽어 보여 주고, 누르면 윈도우 야간 모드 설정을 엶.
        // (레지스트리에 써서 바꾸는 방법은 이 윈도우 빌드에서 반영이 확인되지 않아 쓰지 않음 — NightLightService.TrySet)
        // 상태를 읽지 못하면 "야간 모드 설정…" 링크만.
        var night = ToggleLine(MoonGlyph, "야간 모드", () =>
        {
            NightLightService.OpenSettings();
            Close();
            return Task.CompletedTask;
        });
        night.Button.ToolTip = "야간 모드 설정 열기";
        var nightRow = (FrameworkElement)night.Button.Parent;
        nightRow.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(nightRow);
        var nightLink = LinkRow("야간 모드 설정…", NightLightService.OpenSettings);
        nightLink.Margin = new Thickness(-6, 6, -6, -4);
        stack.Children.Add(nightLink);

        var tile = Tile(stack);
        tile.Margin = new Thickness(0, 10, 0, 0);

        string signature = "";
        var sliders = new Dictionary<string, PillSlider>();
        _refreshers.Add(() =>
        {
            var list = brightness.Displays;
            string sig = string.Join("|", list.Select(d => d.Id + ":" + d.Name));
            if (sig != signature)
            {
                signature = sig;
                rows.Children.Clear();
                sliders.Clear();
                foreach (var d in list)
                {
                    var slider = BrightnessRow(rows, d, showName: list.Count > 1);
                    sliders[d.Id] = slider;
                }
            }
            foreach (var d in list)
                if (sliders.TryGetValue(d.Id, out var s) && !s.IsDragging) s.Value = d.Percent / 100.0;

            bool? nightOn = NightLightService.IsOn;
            nightRow.Visibility = nightOn is null ? Visibility.Collapsed : Visibility.Visible;
            nightLink.Visibility = nightOn is null ? Visibility.Visible : Visibility.Collapsed;
            SetCircle(night.Circle, MoonGlyph, nightOn == true);
            night.State.Text = nightOn == true ? "켬 · 눌러서 설정" : "끔 · 눌러서 설정";

            // 밝기 행이 없으면 제목 아래 간격 없이 야간 모드만
            rows.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            nightRow.Margin = new Thickness(0, list.Count > 0 ? 10 : 0, 0, 0);
        });

        // 열 때마다 백그라운드에서 다시 조회 (DDC 는 느림) — 그동안은 지난번 값
        EventHandler onChanged = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (!_closed) RefreshAll();
        }, DispatcherPriority.Background);
        brightness.Changed += onChanged;
        Closed += (_, _) => brightness.Changed -= onChanged;
        brightness.Refresh();
        return tile;
    }

    /// <summary>[이름] + 해 아이콘 + 알약 슬라이더 한 줄. 드래그 중에는 50ms 마다 마지막 값만 보냄.</summary>
    private PillSlider BrightnessRow(Panel into, DisplayBrightness d, bool showName)
    {
        if (showName)
        {
            var name = Sub(d.Name, 12.5);
            name.Margin = new Thickness(0, into.Children.Count == 0 ? 0 : 8, 0, 4);
            into.Children.Add(name);
        }
        var row = new DockPanel { LastChildFill = true };
        var sun = new TextBlock
        {
            Text = SunGlyph,
            FontFamily = IconFont,
            FontSize = 15,
            Foreground = _p.SubText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        row.Children.Add(sun);
        var slider = new PillSlider(_p);
        row.Children.Add(slider);
        into.Children.Add(row);

        string id = d.Id;
        int? pending = null;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = BrightnessThrottle };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (pending is int v)
            {
                pending = null;
                BrightnessService.Instance.Set(id, v);
            }
        };
        slider.UserChanged += (_, v) =>
        {
            pending = (int)Math.Round(v * 100);
            if (!timer.IsEnabled) timer.Start();
        };
        Closed += (_, _) =>
        {
            // 닫기 직전 마지막 값은 보냄
            timer.Stop();
            if (pending is int v) BrightnessService.Instance.Set(id, v);
        };
        return slider;
    }

    // ───────────────────────── 전원 모드 ─────────────────────────

    /// <summary>제어 센터 "전원 모드" 타일 (사용 불가면 접힘).</summary>
    private UIElement BuildPowerModeTile()
    {
        var stack = new StackPanel();
        var title = Heading("전원 모드");
        title.FontSize = 13.5;
        title.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(title);
        stack.Children.Add(PowerModeSegments());
        var tile = Tile(stack);
        tile.Margin = new Thickness(0, 10, 0, 0);
        _refreshers.Add(() => tile.Visibility = PowerModeService.Available ? Visibility.Visible : Visibility.Collapsed);
        return tile;
    }

    /// <summary>배터리 카드의 "전원 모드" 줄 + 세그먼트 + 구분선 (사용 불가면 접힘).</summary>
    private UIElement BuildPowerModeSection()
    {
        var section = new StackPanel();
        var title = new TextBlock { Text = "전원 모드", FontSize = 15, Margin = new Thickness(0, 0, 0, 6) };
        section.Children.Add(title);
        section.Children.Add(PowerModeSegments());
        section.Children.Add(Divider());
        _refreshers.Add(() => section.Visibility = PowerModeService.Available ? Visibility.Visible : Visibility.Collapsed);
        return section;
    }

    /// <summary>최고 효율 / 균형 / 최고 성능 세그먼트 (선택 = 강조색).</summary>
    private Border PowerModeSegments()
    {
        var modes = new (PowerMode Mode, string Label)[]
        {
            (PowerMode.BestEfficiency, "최고 효율"),
            (PowerMode.Balanced, "균형"),
            (PowerMode.BestPerformance, "최고 성능"),
        };
        var grid = new Grid();
        var buttons = new Button[modes.Length];
        for (int i = 0; i < modes.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var mode = modes[i].Mode;
            var b = new Button
            {
                Style = (Style)FindStyle("CardButton"),
                Background = Brushes.Transparent,
                Foreground = _p.Text,
                Padding = new Thickness(4, 5, 4, 5),
                Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = new TextBlock { Text = modes[i].Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                ToolTip = modes[i].Label,
            };
            b.Click += async (_, _) =>
            {
                if (PowerModeService.Current == mode) return;
                bool ok = false;
                try { ok = await Task.Run(() => PowerModeService.TrySet(mode)); }
                catch (Exception ex) { Log.Error("전원 모드 변경 실패", ex); }
                if (!ok) Log.Warn($"전원 모드를 {mode} 로 바꾸지 못함");
                if (!_closed) RefreshAll();
            };
            Grid.SetColumn(b, i);
            grid.Children.Add(b);
            buttons[i] = b;
        }
        var track = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = _p.CircleOff,
            Padding = new Thickness(2),
            Child = grid,
        };
        _refreshers.Add(() =>
        {
            var current = PowerModeService.Current;
            for (int i = 0; i < modes.Length; i++)
            {
                bool on = current == modes[i].Mode;
                buttons[i].Background = on ? _p.Accent : Brushes.Transparent;
                buttons[i].Foreground = on ? _p.AccentText : _p.Text;
            }
        });
        return track;
    }
}
