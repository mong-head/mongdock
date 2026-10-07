using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 맥 알림 배너: 상단바 아래 오른쪽(포그라운드 창의 모니터)에 둥근 카드가 오른쪽에서 미끄러져 들어와 5초 뒤 사라짐.
/// - 여러 개면 새 것이 위, 아래로 최대 3개. 넘치면 가장 오래된 것부터 사라짐.
/// - 마우스를 올리면 모든 카드의 사라짐 타이머가 멈춤. 카드 클릭 = 앱 열기 + 그 배너 닫기. 호버 시 왼쪽 위 × 로 닫기(목록에는 남음).
/// - 포커스를 뺏지 않음 (ShowActivated=false + MakeOverlay = WS_EX_NOACTIVATE|TOOLWINDOW), Topmost.
/// - 반투명 단색 + 그림자 (블러 없음), 카드 사이 빈 곳은 투명이라 클릭이 아래 창으로 통과.
/// - 카드가 다 사라지면 창을 닫고, 다음 알림 때 그때의 모니터·테마로 새로 만든다.
/// </summary>
internal sealed class NotificationBannerWindow : Window
{
    private const double CardWidth = 344;
    private const double ShadowMargin = 18;
    private const int MaxCards = 3;
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(5);

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly StackPanel _stack = new();
    private readonly List<Entry> _entries = new();
    private readonly DispatcherTimer _tick;
    private DateTime _lastTick = DateTime.UtcNow;
    private bool _closed;

    private sealed class Entry
    {
        public required NotificationItem Item { get; init; }
        public required FrameworkElement Root { get; init; }
        public required TranslateTransform Shift { get; init; }
        public TimeSpan Remaining { get; set; }
        public bool Leaving { get; set; }
    }

    // ───────────────────────── 연결 (App.xaml.cs) ─────────────────────────

    /// <summary>INotificationService.Arrived 를 구독해 배너를 띄움. Dispose 하면 구독 해제 + 배너 닫기.</summary>
    public static IDisposable Attach(AppServices services) => new Host(services);

    private sealed class Host : IDisposable
    {
        private readonly AppServices _services;
        private NotificationBannerWindow? _window;

        public Host(AppServices services)
        {
            _services = services;
            _services.Notifications.Arrived += OnArrived;
            AppState.Changed += OnPausedChanged;
        }

        private void OnArrived(object? sender, NotificationItem item)
        {
            try
            {
                var settings = _services.Settings.Current;
                if (!settings.Notifications.ShowNotificationBanners || AppState.Paused) return;
                var monitor = Monitors.FromHwnd(_services.Windows.ForegroundWindow);
                // 전체 화면 앱(게임·동영상) 위에는 띄우지 않음 — 윈도우도 이때는 토스트를 숨김
                if (_services.DesktopWindows.IsFullscreenOn(monitor.IsPrimary ? "" : monitor.DeviceName)) return;

                if (_window is null || _window._closed)
                {
                    _window = new NotificationBannerWindow(_services, UiTheme.Palette(settings));
                    _window.Closed += (_, _) => _window = null;
                    _window.ShowOn(monitor);
                }
                _window.Add(item);
            }
            catch (Exception ex)
            {
                Log.Error("알림 배너 표시 실패", ex);
            }
        }

        private void OnPausedChanged(object? sender, EventArgs e)
        {
            if (AppState.Paused) _window?.Close();
        }

        public void Dispose()
        {
            _services.Notifications.Arrived -= OnArrived;
            AppState.Changed -= OnPausedChanged;
            try { _window?.Close(); }
            catch (Exception ex) { Log.Warn($"알림 배너 닫기 실패: {ex.Message}"); }
            _window = null;
        }
    }

    // ───────────────────────── 창 ─────────────────────────

    private NotificationBannerWindow(AppServices services, UiPalette palette)
    {
        _services = services;
        _p = palette;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Width = CardWidth + ShadowMargin * 2;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        Title = "mongdock Notifications";
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        Foreground = _p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _stack.Margin = new Thickness(ShadowMargin - 8, 0, ShadowMargin, 14);
        Content = _stack;

        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => OnTick();

        SourceInitialized += (_, _) => _services.DesktopWindows.MakeOverlay(this);
        Closed += (_, _) =>
        {
            _closed = true;
            _tick.Stop();
        };
    }

    /// <summary>모니터의 상단바 아래 오른쪽 (작업 영역 오른쪽 끝에 맞춤).</summary>
    private void ShowOn(MonitorInfo monitor)
    {
        var s = _services.Settings.Current;
        var bounds = monitor.Bounds;
        var work = monitor.WorkArea;
        double barBottom = bounds.Top;
        if (s.TopBar.Enabled && (monitor.IsPrimary || s.TopBar.ShowOnAllMonitors))
            barBottom += s.TopBar.Height;
        double top = Math.Max(work.Top, barBottom) + 8 - 6; // 카드 위 × 자리(6) 만큼 창을 올림
        double left = work.Right - 10 - CardWidth - ShadowMargin;
        Left = Math.Round(left);
        Top = Math.Round(top);
        Show();
        if (_services.DesktopWindows.EnsureOnMonitor(this, monitor))
        {
            Left = Math.Round(left);
            Top = Math.Round(top);
        }
        _lastTick = DateTime.UtcNow;
        _tick.Start();
    }

    private void Add(NotificationItem item)
    {
        if (_closed) return;
        // 같은 알림이 이미 떠 있으면 무시
        if (_entries.Any(e => e.Item.Id == item.Id && !e.Leaving)) return;

        var card = NotificationUi.Card(_services, _p, item, _p.CardBackground);
        card.CornerRadius = new CornerRadius(14);
        card.BorderThickness = new Thickness(0.75);
        card.Padding = new Thickness(12, 11, 12, 11);

        var host = new Grid
        {
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.Hand,
        };
        var (_, shift) = Anim.Transforms(host);
        var shadowed = new Grid { Margin = new Thickness(8, 6, 0, 8) }; // 왼쪽 위는 × 자리
        shadowed.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = _p.CardBackground,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = _p.ShadowOpacity },
        });
        shadowed.Children.Add(card);
        host.Children.Add(shadowed);

        var entry = new Entry { Item = item, Root = host, Shift = shift, Remaining = Life };
        var close = NotificationUi.CloseButton(_p, () => Dismiss(entry, fast: false));
        close.Margin = new Thickness(0, 0, 0, 0);
        host.Children.Add(close);

        host.MouseEnter += (_, _) => NotificationUi.ShowClose(close, true);
        host.MouseLeave += (_, _) => NotificationUi.ShowClose(close, false);
        bool pressed = false;
        host.MouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            e.Handled = true;
        };
        host.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed || e.Handled) return;
            pressed = false;
            e.Handled = true;
            Dismiss(entry, fast: true);
            _services.Notifications.Open(item);
        };

        // 이미 떠 있는 배너의 현재 위치 (새 배너가 위에 끼면 한 번에 내려가지 않고 미끄러져 내려가게 — FLIP)
        var before = new Dictionary<Entry, double>();
        if (_stack.IsLoaded && Anim.Enabled)
            foreach (var e in _entries)
                before[e] = e.Root.TranslatePoint(new Point(0, 0), _stack).Y;

        _entries.Insert(0, entry);
        _stack.Children.Insert(0, host);

        // 최대 3개: 가장 오래된 것부터 내보냄
        foreach (var old in _entries.Where(e => !e.Leaving).Skip(MaxCards).ToList())
            Dismiss(old, fast: true);

        // 들어오기: 오른쪽 바깥에서 300ms, 아주 약한 되튐 감속 + 페이드 200ms
        if (!Anim.Enabled) return;
        Anim.Appear(host, 300, fromX: CardWidth + ShadowMargin * 2, ease: Anim.SoftBack, fadeMs: 200);
        if (before.Count == 0) return;
        _stack.UpdateLayout();
        foreach (var (e, y) in before)
        {
            if (!_entries.Contains(e)) continue;
            double now = e.Root.TranslatePoint(new Point(0, 0), _stack).Y;
            Anim.SlideFrom(e.Shift, TranslateTransform.YProperty, y - now, 300, Anim.QuintOut);
        }
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (IsMouseOver) return; // 마우스가 올라가 있으면 모두 멈춤
        foreach (var e in _entries.Where(e => !e.Leaving).ToList())
        {
            e.Remaining -= elapsed;
            if (e.Remaining <= TimeSpan.Zero) Dismiss(e, fast: false);
        }
    }

    /// <summary>
    /// 오른쪽으로 밀려 나가며 흐려짐(220ms, 클릭으로 열기·넘침은 160ms, ease-in) → 도중부터 높이가 접혀(200ms, ease-out)
    /// 아래 배너가 부드럽게 올라옴 → 스택에서 제거. 마지막이면 창 닫기.
    /// </summary>
    private void Dismiss(Entry entry, bool fast)
    {
        if (entry.Leaving) return;
        entry.Leaving = true;
        entry.Root.IsHitTestVisible = false;
        double ms = fast ? 160 : 220;
        bool slid = false, folded = false;
        void Done()
        {
            if (!slid || !folded) return;
            _stack.Children.Remove(entry.Root);
            _entries.Remove(entry);
            if (_entries.Count == 0 && !_closed) Close();
        }
        if (!Anim.Enabled)
        {
            slid = folded = true;
            Done();
            return;
        }
        Anim.Disappear(entry.Root, ms, () => { slid = true; Done(); }, toX: CardWidth + ShadowMargin * 2);
        Anim.Collapse(entry.Root, 200, () => { folded = true; Done(); }, delayMs: ms * 0.6);
    }
}
