using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 데스크톱 이동 알약 (#41 루틴 v2, 사용자 결정 2026-10-22): 몽독이 데스크톱을 옮길 때(루틴을 새 데스크톱에 열기, 독에서 다른 데스크톱 앱·루틴 누르기,
/// 알림 누르기, 창 미리 보기에서 고르기) 누른 순간의 커서 모니터 상단바 아래 가운데에 작은 알약:
/// "데스크톱 2로 이동했어요 · Claude  [← 돌아가기]" — 약 2초 뒤 사라짐, 포커스를 가져가지 않음, 상단바 데스크톱 표시도 잠깐 강조.
/// 처음 한 번만 약 5초 + 아랫줄 "Ctrl + Win + ← → 로도 오가요" (Settings.SeenHints "desktop-pill"). 사용자가 직접 Ctrl+Win+←→ 로 옮길 때는 안 띄움(부르는 곳이 몽독 이동뿐).
/// [← 돌아가기] = 원래 데스크톱으로 (VirtualDesktopService.MoveToAsync — 지금과 같은 키 방식).
/// </summary>
internal sealed class DesktopPill : Window
{
    private const string HintKey = "desktop-pill";
    private static DesktopPill? _current;
    private static AppServices? _services;

    /// <summary>앱 시작 때 한 번 (서비스 계층의 AppLauncher 가 <see cref="Notify"/> 로 부를 수 있게).</summary>
    public static void Attach(AppServices services) => _services = services;

    /// <summary>어느 스레드에서나: UI 스레드로 넘겨 알약을 띄움 (Attach 전이면 아무것도 안 함).</summary>
    public static void Notify(MonitorInfo monitor, string text, int from)
    {
        if (_services is not { } s || Application.Current?.Dispatcher is not { } d) return;
        _ = d.BeginInvoke(() => Show(s, monitor, text, from));
    }

    /// <summary>상단바가 데스크톱 표시를 잠깐 강조 (TopBarWindow 가 구독).</summary>
    public static event EventHandler? Shown;

    private readonly DispatcherTimer _life;

    /// <summary>
    /// 알약 띄우기. monitor = 누른 순간의 모니터(옮기기 전에 잡아 둔 것), from = 돌아갈 데스크톱 번호(1부터, 0이면 버튼 없음).
    /// UI 스레드에서. 이미 떠 있으면 바꿔 끼움.
    /// </summary>
    public static void Show(AppServices services, MonitorInfo monitor, string text, int from)
    {
        try
        {
            var s = services.Settings.Current;
            if (AppState.Paused) return;
            bool first = !s.SeenHints.Contains(HintKey, StringComparer.OrdinalIgnoreCase);
            if (first)
            {
                s.SeenHints.Add(HintKey);
                services.Settings.Save();
            }
            _current?.Close();
            var pill = new DesktopPill(services, UiTheme.Palette(s), text, first, from);
            _current = pill;
            pill.Closed += (_, _) => { if (_current == pill) _current = null; };
            pill.PlaceOn(services, monitor);
            Shown?.Invoke(null, EventArgs.Empty);
            Log.Info($"데스크톱 알약 띄움{(first ? " (첫 안내)" : "")}"); // 문구(앱·루틴 이름, 창 제목)는 남기지 않음
        }
        catch (Exception ex) { Log.Warn($"데스크톱 알약 실패: {ex.Message}"); }
    }

    private DesktopPill(AppServices services, UiPalette p, string text, bool first, int from)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = "mongdock Desktop";
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        Foreground = p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => services.DesktopWindows.MakeOverlay(this);
        Content = BuildCard(p, text, first, from, async () =>
        {
            Close();
            Log.Info($"데스크톱 알약: 돌아가기 → 데스크톱 {from}");
            await VirtualDesktopService.MoveToAsync(from);
        });
        Opacity = 0;

        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(first ? 5 : 2) };
        _life.Tick += (_, _) =>
        {
            _life.Stop();
            if (IsMouseOver) { _life.Interval = TimeSpan.FromSeconds(1); _life.Start(); return; } // 마우스를 올리면 기다려 줌
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <summary>알약 모양 (창 없이 — report-test --pillshot 이 화면 밖에서 그림으로 뜸). onBack = [← 돌아가기] 누름.</summary>
    internal static FrameworkElement BuildCard(UiPalette p, string text, bool first, int from, Action? onBack)
    {
        // 기획서 모형 (10-22): 흰(다크는 카드색) 알약 + 얇은 테두리 + 아래로 퍼지는 그림자. 글자는 보통 굵기 13, 아랫줄 12 흐리게.
        // 한 줄이면 양끝이 반원(높이의 반), 두 줄(첫 안내)이면 모서리 16. 오른쪽 [← 돌아가기]는 테두리 있는 작은 알약, 세로 가운데.
        // CornerRadius 를 999 처럼 크게 주면 WPF 가 렌즈 모양으로 그림(10-23 사용자 「알약 형태 이상」) → 실제 높이의 반으로 맞춤.
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
        if (first)
            lines.Children.Add(new TextBlock { Text = Loc.T("Ctrl + Win + ← → 로도 오가요"), FontSize = 12, Foreground = p.SubText, Margin = new Thickness(0, 1, 0, 0) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(lines);
        if (from > 0)
        {
            var back = new Border
            {
                Background = p.CardBackground,
                BorderBrush = p.CardBorder,
                BorderThickness = new Thickness(1),
                Padding = first ? new Thickness(14, 5, 14, 6) : new Thickness(12, 3, 12, 4), // 사용자 10-23 「안쪽 여백 더」
                Margin = new Thickness(first ? 18 : 13, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "← " + Loc.T("돌아가기"), Foreground = p.Text },
            };
            back.SizeChanged += (_, _) => back.CornerRadius = new CornerRadius(back.ActualHeight / 2);
            back.MouseEnter += (_, _) => back.Background = p.SoftAccent;
            back.MouseLeave += (_, _) => back.Background = p.CardBackground;
            back.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onBack?.Invoke();
            };
            row.Children.Add(back);
        }
        var card = new Border
        {
            Background = p.CardBackground,
            BorderBrush = p.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = first ? new Thickness(20, 12, from > 0 ? 12 : 20, 12) : new Thickness(20, 8, from > 0 ? 9 : 20, 8), // 사용자 10-23 「양옆 더, 위아래 조금」— 한 줄은 높이 비례
            Margin = new Thickness(20, 4, 20, 26), // 그림자 자리
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = p.ShadowOpacity + 0.06 },
            Child = row,
        };
        if (!first) card.SizeChanged += (_, _) => card.CornerRadius = new CornerRadius(card.ActualHeight / 2);
        // 글꼴·크기·글자색은 창에서 물려받지 않고 여기서 (창 없이 그릴 때도 같게)
        card.SetResourceReference(TextElement.FontFamilyProperty, UiFonts.Key);
        TextElement.SetFontSize(card, 13);
        TextElement.SetForeground(card, p.Text);
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
        return card;
    }

    /// <summary>그 모니터의 상단바 아래 가운데 (상단바가 없으면 작업 영역 위 가운데).</summary>
    private void PlaceOn(AppServices services, MonitorInfo monitor)
    {
        var s = services.Settings.Current;
        var bounds = monitor.Bounds;
        double barBottom = bounds.Top;
        if (s.TopBar.Enabled && (monitor.IsPrimary || s.TopBar.ShowOnAllMonitors))
            barBottom += s.TopBar.Height;
        double top = Math.Max(monitor.WorkArea.Top, barBottom) + 6;
        Left = -32000;
        Top = Math.Round(top);
        base.Show();
        UpdateLayout();
        Left = Math.Round(bounds.Left + (bounds.Width - ActualWidth) / 2);
        if (services.DesktopWindows.EnsureOnMonitor(this, monitor))
        {
            Left = Math.Round(bounds.Left + (bounds.Width - ActualWidth) / 2);
            Top = Math.Round(top);
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        _life.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _life.Stop();
        base.OnClosed(e);
    }
}
