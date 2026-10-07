using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MyDock.Converters;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 맥 메뉴바 스타일 상단바. 포커스를 절대 뺏지 않아야 한다 —
/// 그래야 한/영 버튼을 눌러도 IME 전환이 (상단바가 아닌) 현재 앱에 적용된다.
/// </summary>
public partial class TopBarWindow : Window
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private const string DefaultClockFormat = "ddd tt h:mm"; // 예: 수 오전 10:02

    private readonly AppServices _services;
    private readonly DispatcherTimer _pollTimer;   // 한/영 + 포그라운드 앱 이름 (300ms)
    private readonly DispatcherTimer _clockTimer;  // 시계 (1초)
    private readonly DispatcherTimer _imeRecheck;  // 한/영 클릭 후 재조회 (150ms, 1회)

    private bool _appBarRegistered;
    private double _registeredHeight;
    private bool _initialized;
    private bool _subscribed;
    private IntPtr _lastForeground;

    public TopBarWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _pollTimer.Tick += (_, _) => { UpdateIme(); UpdateAppName(); };
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _imeRecheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _imeRecheck.Tick += (_, _) => { _imeRecheck.Stop(); UpdateIme(); };

        ContextMenu = BuildContextMenu();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { if (!_services.Settings.Current.TopBar.Enabled) Hide(); };
        Closed += OnClosed;
    }

    // ───────────────────────── 수명 주기 ─────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _services.DesktopWindows.MakeOverlay(this);
        _initialized = true;

        _services.Settings.SettingsChanged += OnSettingsChanged;
        _services.Windows.WindowActivated += OnWindowActivated;
        _services.Windows.WindowsChanged += OnWindowsChanged;
        _subscribed = true;

        ApplySettings();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _pollTimer.Stop();
        _clockTimer.Stop();
        _imeRecheck.Stop();
        if (_subscribed)
        {
            _services.Settings.SettingsChanged -= OnSettingsChanged;
            _services.Windows.WindowActivated -= OnWindowActivated;
            _services.Windows.WindowsChanged -= OnWindowsChanged;
            _subscribed = false;
        }
        if (_appBarRegistered)
        {
            _services.DesktopWindows.UnregisterAppBar(this);
            _appBarRegistered = false;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings();
    private void OnWindowActivated(object? sender, IntPtr hwnd) => UpdateAppName(force: true);
    private void OnWindowsChanged(object? sender, EventArgs e) => UpdateAppName(force: true);

    // ───────────────────────── 설정 반영 ─────────────────────────

    private void ApplySettings()
    {
        if (!_initialized) return;
        var s = _services.Settings.Current.TopBar;
        double height = Math.Clamp(double.IsNaN(s.Height) ? 28 : s.Height, 16, 80);

        Bar.Background = ColorBrushConverter.Parse(s.Background, Color.FromArgb(0xC0, 0x16, 0x16, 0x18));
        Foreground = ColorBrushConverter.Parse(s.Foreground, Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));
        FontSize = Math.Clamp(double.IsNaN(s.FontSize) ? 13 : s.FontSize, 8, 32);
        LogoButton.Visibility = Vis(s.ShowLogo);
        AppName.Visibility = Vis(s.ShowActiveAppName);
        DesktopButtons.Visibility = Vis(s.ShowDesktopButtons);
        QuickButtons.Visibility = Vis(s.ShowQuickButtons);
        ImeButton.Visibility = Vis(s.ShowImeToggle);
        _imeState = 0; // 배지 색 다시 칠하기

        if (!s.Enabled)
        {
            UnregisterIfNeeded();
            _pollTimer.Stop();
            _clockTimer.Stop();
            if (IsLoaded && IsVisible) Hide(); // 첫 표시 중이면 Loaded 에서 숨김
            return;
        }

        if (!IsVisible && IsLoaded) Show();

        if (_appBarRegistered && (!s.ReserveSpace || _registeredHeight != height))
            UnregisterIfNeeded();

        if (s.ReserveSpace)
        {
            if (!_appBarRegistered)
            {
                Height = height;
                _services.DesktopWindows.RegisterTopAppBar(this, height);
                _appBarRegistered = true;
                _registeredHeight = height;
            }
        }
        else
        {
            var b = _services.DesktopWindows.GetPrimaryScreenBounds();
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = height;
        }

        _lastClockText = null;
        UpdateClock();
        UpdateIme();
        UpdateAppName(force: true);
        _clockTimer.Start();
        _pollTimer.Start(); // 한/영 + 앱 이름 갱신
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void UnregisterIfNeeded()
    {
        if (!_appBarRegistered) return;
        _services.DesktopWindows.UnregisterAppBar(this);
        _appBarRegistered = false;
    }

    // ───────────────────────── 표시 갱신 ─────────────────────────

    private string? _lastClockText;

    private void UpdateClock()
    {
        string fmt = _services.Settings.Current.TopBar.ClockFormat;
        string text;
        try
        {
            text = DateTime.Now.ToString(string.IsNullOrWhiteSpace(fmt) ? DefaultClockFormat : fmt, Korean);
        }
        catch (FormatException)
        {
            text = DateTime.Now.ToString(DefaultClockFormat, Korean);
        }
        // 분 단위 포맷이면 텍스트가 바뀔 때만 갱신
        if (text == _lastClockText) return;
        _lastClockText = text;
        Clock.Text = text;
    }

    private int _imeState; // 0 = 아직 안 그림, 1 = 한, 2 = A, 3 = 모름

    private void UpdateIme()
    {
        if (ImeButton.Visibility != Visibility.Visible) return;
        bool? hangul;
        try { hangul = _services.Ime.IsHangulMode(); }
        catch { hangul = null; }
        int state = hangul switch { true => 1, false => 2, null => 3 };
        if (state == _imeState) return;
        _imeState = state;

        // MyDockFinder 스타일 배지: "한" = 글자색으로 채운 배경 + 바 배경색 글자, "A" = 테두리만
        var fg = Foreground;
        var barColor = (Bar.Background as SolidColorBrush)?.Color ?? Color.FromRgb(0x16, 0x16, 0x18);
        var dark = new SolidColorBrush(Color.FromRgb(barColor.R, barColor.G, barColor.B));
        dark.Freeze();
        switch (state)
        {
            case 1:
                ImeText.Text = "한";
                ImeBadge.Background = fg;
                ImeBadge.BorderBrush = fg;
                ImeText.Foreground = dark;
                ImeBadge.Opacity = 1;
                break;
            case 2:
                ImeText.Text = "A";
                ImeBadge.Background = Brushes.Transparent;
                ImeBadge.BorderBrush = fg;
                ImeText.Foreground = fg;
                ImeBadge.Opacity = 1;
                break;
            default:
                ImeText.Text = "–";
                ImeBadge.Background = Brushes.Transparent;
                ImeBadge.BorderBrush = fg;
                ImeText.Foreground = fg;
                ImeBadge.Opacity = 0.5;
                break;
        }
    }

    private void UpdateAppName(bool force = false)
    {
        var fg = _services.Windows.ForegroundWindow;
        if (!force && fg == _lastForeground) return;
        _lastForeground = fg;

        var info = _services.Windows.Windows.FirstOrDefault(w => w.Hwnd == fg);
        // 목록에 없는 창(바탕화면, 대화상자, 독/상단바 자신 등)이면 이전 이름 유지
        if (info == null) return;
        string name = AppNames.Get(info);
        if (AppName.Text != name) AppName.Text = name;
    }

    // ───────────────────────── 버튼 ─────────────────────────

    private void OnPreviousDesktop(object sender, RoutedEventArgs e) => Safe(() => _services.VirtualDesktops.Previous());
    private void OnNextDesktop(object sender, RoutedEventArgs e) => Safe(() => _services.VirtualDesktops.Next());
    private void OnNewDesktop(object sender, RoutedEventArgs e) => Safe(() => _services.VirtualDesktops.New());

    private void OnImeToggle(object sender, RoutedEventArgs e)
    {
        Safe(() => _services.Ime.ToggleHangul());
        _imeRecheck.Stop();
        _imeRecheck.Start();
    }

    private void OnSearch(object sender, RoutedEventArgs e) => Safe(() => _services.Shell.OpenSearch());
    private void OnQuickSettings(object sender, RoutedEventArgs e) => Safe(() => _services.Shell.OpenQuickSettings());
    private void OnNotifications(object sender, RoutedEventArgs e) => Safe(() => _services.Shell.OpenNotificationCenter());

    /// <summary>로고 클릭 → 로고 아래에 MyDock 메뉴.</summary>
    private void OnLogoClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = LogoButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        menu.Items.Add(DockMenus.Item("시작 메뉴", () => _services.Shell.OpenStartMenu()));
        menu.Items.Add(DockMenus.Item("작업 보기", () => _services.Shell.OpenTaskView()));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.DockPosition(_services));
        menu.Items.Add(DockMenus.OpenSettings(_services));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Quit());
        menu.IsOpen = true;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(DockMenus.OpenSettings(_services));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Quit());
        return menu;
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MyDock] topbar action failed: {ex}"); }
    }
}
