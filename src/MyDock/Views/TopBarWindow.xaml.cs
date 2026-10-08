using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MyDock.Converters;
using MyDock.Models;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 맥 메뉴바 스타일 상단바. 포커스를 절대 뺏지 않아야 한다 —
/// 그래야 한/영 버튼을 눌러도 IME 전환이 (상단바가 아닌) 현재 앱에 적용된다.
/// 색 모드: Transparent(배경 투명, 글자색은 배경화면 밝기로) / Auto(바로 아래 앱 색) / Blur / Fixed.
/// </summary>
public partial class TopBarWindow : Window
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private const string DefaultClockFormat = "ddd tt h:mm"; // 예: 수 오전 10:02
    private static readonly Color DarkText = BrushParser.Hex("#E6000000");
    private static readonly Color LightText = BrushParser.Hex("#F2FFFFFF");
    /// <summary>투명 모드에서도 클릭을 받기 위한 사실상 투명한 배경.</summary>
    private static readonly Color AlmostClear = Color.FromArgb(1, 0, 0, 0);

    private readonly AppServices _services;
    private readonly DispatcherTimer _pollTimer;   // 한/영 + 포그라운드 앱 이름 (300ms)
    private readonly DispatcherTimer _clockTimer;  // 시계 (1초)
    private readonly DispatcherTimer _imeRecheck;  // 한/영 클릭 후 재조회 (150ms, 1회)
    private readonly DispatcherTimer _colorTimer;  // Auto 색 샘플링 (500ms)
    private readonly DispatcherTimer _colorSoon;   // 활성화/데스크톱 전환 직후 1회 재계산
    private readonly SolidColorBrush _barBrush = new(Color.FromArgb(1, 0, 0, 0)); // 페이드용 (Freeze 안 함)

    private bool _appBarRegistered;
    private double _registeredHeight;
    private bool _initialized;
    private bool _subscribed;
    private bool _fullscreen;
    private bool _blurOn;
    private IntPtr _lastForeground;
    private Color _leftText = LightText, _rightText = LightText;
    private StatusPanelWindow? _panel;

    /// <summary>이 상단바가 놓일 모니터의 장치 이름 (MonitorInfo.DeviceName). "" 이면 주 모니터(주 모니터가 바뀌면 따라감).</summary>
    public string MonitorDevice { get; }

    /// <summary>이 상단바의 모니터 (분리됐으면 주 모니터 — 곧 App 이 이 창을 닫는다). Left/Top 등 DIP 는 이 모니터 기준.</summary>
    private MonitorInfo Monitor => _services.DesktopWindows.ResolveMonitor(MonitorDevice);

    public TopBarWindow(AppServices services, string monitorDevice)
    {
        _services = services;
        MonitorDevice = monitorDevice ?? "";
        UiFonts.Apply(services.Settings.Current); // 내장 Pretendard 등 UI 글꼴 리소스
        InitializeComponent();
        Bar.Background = _barBrush;

        // 주 모니터가 아니면 처음부터 그 모니터 위에 만들어지게 (WPF 는 새 창을 Left/Top 이 속한 모니터의 DPI 로 만든다).
        // 주 모니터 상단바는 예전처럼 그대로 (AppBar 가 위치를 정함).
        // 모니터를 새로 연결했을 때 그 모니터에 이미 전체 화면 앱이 있으면 처음부터 숨김 (시작 시에는 항상 false)
        _fullscreen = services.DesktopWindows.IsFullscreenOn(MonitorDevice);
        if (_fullscreen) Topmost = false;

        var mon = services.DesktopWindows.ResolveMonitor(MonitorDevice);
        if (!mon.IsPrimary)
        {
            var b = mon.Bounds;
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _pollTimer.Tick += (_, _) => { UpdateIme(); UpdateAppName(); };
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _imeRecheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _imeRecheck.Tick += (_, _) => { _imeRecheck.Stop(); UpdateIme(); };
        _colorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _colorTimer.Tick += (_, _) => UpdateAutoColor();
        _colorSoon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _colorSoon.Tick += (_, _) => { _colorSoon.Stop(); UpdateColors(); };

        ContextMenu = BuildContextMenu();

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => OnDisplayChanged(this, EventArgs.Empty), DispatcherPriority.Loaded);
        Loaded += (_, _) => { if (!_services.Settings.Current.TopBar.Enabled || AppState.Paused || _fullscreen) Hide(); };
        ContentRendered += (_, _) => { Remeasure(LeftSection); Remeasure(RightSection); };
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
        _services.DesktopWindows.DisplayChanged += OnDisplayChanged;
        _services.DesktopWindows.FullscreenAppChanged += OnFullscreenChanged;
        _services.Status.Changed += OnStatusChanged;
        _services.VirtualDesktops.Changed += OnDesktopChanged;
        AppState.Changed += OnSettingsChanged; // 일시 정지/해제
        _services.TrayIcons.Changed += OnTrayIconsChanged;
        _subscribed = true;

        ApplySettings();
        UpdateStatus();
        UpdateDesktopIndex();
    }

    private bool _closed;

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _pollTimer.Stop();
        _clockTimer.Stop();
        _imeRecheck.Stop();
        _colorTimer.Stop();
        _colorSoon.Stop();
        _panel?.Close();
        if (_subscribed)
        {
            _services.Settings.SettingsChanged -= OnSettingsChanged;
            _services.Windows.WindowActivated -= OnWindowActivated;
            _services.Windows.WindowsChanged -= OnWindowsChanged;
            _services.DesktopWindows.DisplayChanged -= OnDisplayChanged;
            _services.DesktopWindows.FullscreenAppChanged -= OnFullscreenChanged;
            _services.Status.Changed -= OnStatusChanged;
            _services.VirtualDesktops.Changed -= OnDesktopChanged;
            AppState.Changed -= OnSettingsChanged;
            _services.TrayIcons.Changed -= OnTrayIconsChanged;
            _subscribed = false;
        }
        UnregisterIfNeeded();
        SetWallpaperWatch(false);
    }

    private bool _wallpaperWatch;

    /// <summary>배경 감시는 구독자가 있을 때만 백엔드가 폴링하므로 Transparent 모드에서만 구독.</summary>
    private void SetWallpaperWatch(bool on)
    {
        if (on == _wallpaperWatch) return;
        _wallpaperWatch = on;
        if (on) _services.DesktopWindows.WallpaperChanged += OnWallpaperChanged;
        else _services.DesktopWindows.WallpaperChanged -= OnWallpaperChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplySettings();

    private void OnWindowActivated(object? sender, IntPtr hwnd)
    {
        UpdateAppName(force: true);
        if (ColorMode == TopBarColorMode.Auto) ScheduleColor(150); // 창이 그려진 뒤 샘플링
    }

    private void OnWindowsChanged(object? sender, EventArgs e) => UpdateAppName(force: true);

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (_closed) return; // 모니터 분리로 App 이 같은 이벤트에서 먼저 닫은 경우
        // 옛 좌표로 떠 있는 패널·메뉴는 닫고 위치·폭(AppBar 포함) 다시 계산
        _panel?.Close();
        CloseAppMenu();
        if (_logoMenu?.IsOpen == true) _logoMenu.IsOpen = false;
        // AppBar 는 다시 등록하지 않음: 작업 영역 변경도 DisplayChanged 로 오므로 재등록하면 무한 반복될 수 있음
        // (배율 변경 시 AppBar 재배치는 백엔드 RegisterTopAppBar 가 처리)
        ApplySettings();
        FixClockWidth();
    }

    private void OnWallpaperChanged(object? sender, EventArgs e)
    {
        if (ColorMode == TopBarColorMode.Transparent) UpdateColors();
    }

    private void OnFullscreenChanged(object? sender, bool anyFullscreen)
    {
        // 이 상단바의 모니터에 전체 화면 앱이 있을 때만 (다른 모니터의 전체 화면은 무시)
        bool fullscreen = anyFullscreen && _services.DesktopWindows.IsFullscreenOn(MonitorDevice);
        if (fullscreen == _fullscreen) return;
        _fullscreen = fullscreen;
        Topmost = !fullscreen;
        if (fullscreen)
        {
            _panel?.Close();
            if (IsVisible) Hide();
        }
        else if (_services.Settings.Current.TopBar.Enabled && !AppState.Paused && IsLoaded && !IsVisible)
        {
            Show();
            UpdateColors();
        }
    }

    private TopBarColorMode ColorMode => _services.Settings.Current.TopBar.ColorMode;

    // ───────────────────────── 설정 반영 ─────────────────────────

    private void ApplySettings()
    {
        if (!_initialized || _closed) return;
        UiFonts.Apply(_services.Settings.Current);
        var s = _services.Settings.Current.TopBar;
        double height = Math.Clamp(double.IsNaN(s.Height) ? 26 : s.Height, 16, 80);

        FontSize = Math.Clamp(double.IsNaN(s.FontSize) ? 13 : s.FontSize, 8, 32);
        LogoButton.Visibility = Vis(s.ShowLogo);
        AppNameButton.Visibility = Vis(s.ShowActiveAppName);
        AppMenuBar.Visibility = Vis(s.ShowAppMenus);
        _menuHwnd = IntPtr.MaxValue; // 설정이 바뀌면 메뉴 다시 구성
        DesktopButtons.Visibility = Vis(s.ShowDesktopButtons);
        DesktopButtons.Background = DesktopGroupPill ? BrushParser.Frozen(Color.FromArgb(0x0D, 0, 0, 0)) : Brushes.Transparent;
        NetSpeed.Visibility = Vis(s.ShowNetworkSpeed);
        StatusIcons.Visibility = Vis(s.ShowStatusIcons);
        QuickButtons.Visibility = Vis(s.ShowQuickButtons);
        ImeButton.Visibility = Vis(s.ShowImeToggle);
        _imeState = 0; // 배지 색 다시 칠하기

        bool active = s.Enabled && !AppState.Paused; // 일시 정지 중이면 꺼진 것처럼
        SetStatusPolling(active && s.ShowNetworkSpeed, active && s.ShowStatusIcons);
        SyncTrayIcons();

        if (!active)
        {
            UnregisterIfNeeded();
            SetWallpaperWatch(false);
            StopTimers();
            _panel?.Close();
            CloseAppMenu();
            if (_logoMenu?.IsOpen == true) _logoMenu.IsOpen = false;
            if (ContextMenu?.IsOpen == true) ContextMenu.IsOpen = false;
            if (IsLoaded && IsVisible) Hide(); // 첫 표시 중이면 Loaded 에서 숨김
            return;
        }

        if (!IsVisible && IsLoaded && !_fullscreen) Show();

        if (_appBarRegistered && (!s.ReserveSpace || _registeredHeight != height))
            UnregisterIfNeeded();

        if (s.ReserveSpace)
        {
            if (!_appBarRegistered)
            {
                Height = height;
                _services.DesktopWindows.RegisterTopAppBar(this, height, MonitorDevice);
                _appBarRegistered = true;
                _registeredHeight = height;
            }
        }
        else
        {
            var mon = Monitor;
            // 다른 DPI 모니터에 있으면 먼저 그 모니터로 (그 뒤 DIP 설정이 정확). 단일 모니터는 아무것도 안 함
            _services.DesktopWindows.EnsureOnMonitor(this, mon);
            var b = mon.Bounds;
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = height;
        }

        ApplyColorMode();

        _lastClockText = null;
        FixClockWidth();
        UpdateClock();
        UpdateIme();
        UpdateAppName(force: true);
        UpdateStatus();
        _clockTimer.Start();
        _pollTimer.Start();
    }

    private void SetStatusPolling(bool speed, bool radios)
    {
        try { _services.Status.SetPolling(speed, radios); }
        catch (Exception ex) { Log.Error("상태 폴링 설정 실패", ex); }
    }

    private void StopTimers()
    {
        _pollTimer.Stop();
        _clockTimer.Stop();
        _colorTimer.Stop();
        _colorSoon.Stop();
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void UnregisterIfNeeded()
    {
        if (!_appBarRegistered) return;
        try { _services.DesktopWindows.UnregisterAppBar(this); }
        catch (Exception ex) { Log.Error("상단바 AppBar 해제 실패", ex); }
        _appBarRegistered = false;
    }

    // ───────────────────────── 색 ─────────────────────────

    private void ApplyColorMode()
    {
        var s = _services.Settings.Current.TopBar;
        _colorTimer.Stop();
        // 구분선은 Fixed 모드에서만. 다른 모드에서 투명 브러시로 두면 그 1 DIP 줄이 비어 배경화면이 선처럼 비치므로 두께 자체를 0 으로
        Bar.BorderBrush = Brushes.Transparent;
        Bar.BorderThickness = new Thickness(0);
        UiTheme.Apply(_services.Settings.Current);
        SetWallpaperWatch(s.Enabled && s.ColorMode == TopBarColorMode.Transparent);

        if (s.ColorMode == TopBarColorMode.Blur)
        {
            var tint = BrushParser.ParseColor(s.Background, BrushParser.Hex("#E0F6F6F6"));
            if (tint.A > 0xE0) tint.A = 0xC0; // 불투명 흰색 기본값이면 블러가 안 보이므로 살짝 투명하게
            try
            {
                _services.DesktopWindows.EnableBlur(this, tint);
                _blurOn = true;
            }
            catch (Exception ex) { Log.Error("상단바 블러 적용 실패", ex); }
            SetBarColor(AlmostClear, animate: false);
            SetTextColors(AutoText(tint), AutoText(tint));
            return;
        }

        if (_blurOn)
        {
            try { _services.DesktopWindows.DisableBlur(this); }
            catch (Exception ex) { Log.Error("상단바 블러 해제 실패", ex); }
            _blurOn = false;
        }

        switch (s.ColorMode)
        {
            case TopBarColorMode.Fixed:
                var bg = BrushParser.ParseColor(s.Background, BrushParser.Hex("#FFFFFFFF"));
                SetBarColor(bg, animate: false);
                SetTextColors(AutoText(bg), AutoText(bg));
                // 아주 옅은 하단 구분선 (흰 바 기준 #14000000)
                Bar.BorderThickness = new Thickness(0, 0, 0, 1);
                Bar.BorderBrush = BrushParser.Frozen(BrushParser.Luminance(bg) > 0.45
                    ? BrushParser.Hex("#14000000") : BrushParser.Hex("#14FFFFFF"));
                break;
            case TopBarColorMode.Auto:
                UpdateAutoColor();
                _colorTimer.Start();
                break;
            default: // Transparent
                SetBarColor(AlmostClear, animate: false);
                UpdateWallpaperText();
                break;
        }
    }

    /// <summary>모드에 맞게 색 다시 계산 (데스크톱 전환·배경 변경 등 후).</summary>
    private void UpdateColors()
    {
        switch (ColorMode)
        {
            case TopBarColorMode.Auto: UpdateAutoColor(); break;
            case TopBarColorMode.Transparent: UpdateWallpaperText(); break;
        }
    }

    private void ScheduleColor(int ms)
    {
        _colorSoon.Stop();
        _colorSoon.Interval = TimeSpan.FromMilliseconds(ms);
        _colorSoon.Start();
    }

    /// <summary>Auto: 상단바 바로 아래 3px 띠(독이 가리는 부분 제외)의 색 → 살짝 밝게 → 배경.</summary>
    private void UpdateAutoColor()
    {
        if (!IsVisible || ColorMode != TopBarColorMode.Auto) return;
        // 메뉴·상태 패널이 바 아래를 덮고 있으면 그 색을 읽게 되므로 건너뜀
        if (_panel != null || ContextMenu?.IsOpen == true || _logoMenu?.IsOpen == true || _openAppMenu?.IsOpen == true) return;
        var band = new Rect(Left, Top + ActualHeight, Math.Max(1, ActualWidth), 3);
        band = ExcludeDock(band, Monitor.DeviceName);
        if (band.IsEmpty) return;

        Color? sampled;
        try { sampled = _services.DesktopWindows.SampleScreenColor(band, Monitor); }
        catch (Exception ex)
        {
            Log.Error("상단바 색 샘플링 실패", ex);
            sampled = null;
        }
        if (sampled is not Color c) return; // 이전 색 유지

        var bg = BrushParser.Lighten(Color.FromRgb(c.R, c.G, c.B), 0.08);
        SetBarColor(bg, animate: true);
        var text = AutoText(bg);
        SetTextColors(text, text);
    }

    /// <summary>띠가 독 패널과 겹치면 독을 뺀 좌/우 중 넓은 쪽만 사용 (독이 같은 모니터에 있을 때만 — DIP 기준이 모니터마다 다름).</summary>
    private static Rect ExcludeDock(Rect band, string monitorDevice)
    {
        var dock = DockState.VisiblePanel;
        if (dock.IsEmpty || !dock.IntersectsWith(band)) return band;
        if (monitorDevice.Length > 0 && DockState.Monitor.Length > 0
            && !string.Equals(monitorDevice, DockState.Monitor, StringComparison.OrdinalIgnoreCase))
            return band;
        var left = new Rect(band.Left, band.Top, Math.Max(0, dock.Left - band.Left), band.Height);
        var right = new Rect(dock.Right, band.Top, Math.Max(0, band.Right - dock.Right), band.Height);
        var best = left.Width >= right.Width ? left : right;
        return best.Width < 4 ? Rect.Empty : best;
    }

    /// <summary>Transparent: 왼쪽/오른쪽 구역 뒤 배경화면 밝기로 각각 글자색 결정.</summary>
    private int _wallpaperRequest;

    private async void UpdateWallpaperText()
    {
        if (ColorMode != TopBarColorMode.Transparent || !IsLoaded) return;
        double h = Math.Max(1, ActualHeight);
        Rect SectionRect(FrameworkElement el)
        {
            if (el.ActualWidth <= 0) return new Rect(Left, Top, Math.Max(1, ActualWidth / 3), h);
            var p = el.TranslatePoint(new Point(0, 0), this);
            return new Rect(Left + p.X, Top, el.ActualWidth, h);
        }

        int request = ++_wallpaperRequest;
        Color? left = null, right = null;
        try
        {
            var mon = Monitor;
            var lt = _services.DesktopWindows.SampleWallpaperColorAsync(SectionRect(LeftSection), mon);
            var rt = _services.DesktopWindows.SampleWallpaperColorAsync(SectionRect(RightSection), mon);
            left = await lt;
            right = await rt;
        }
        catch (Exception ex)
        {
            Log.Error("배경화면 색 샘플링 실패", ex);
        }

        // 그 사이 새 요청이 있었거나 모드가 바뀌었으면 버림
        if (request != _wallpaperRequest || ColorMode != TopBarColorMode.Transparent || !IsLoaded) return;
        SetTextColors(left is Color l ? AutoText(l) : _leftText, right is Color r ? AutoText(r) : _rightText);
    }

    /// <summary>배경 밝기 → 검정/흰색 글자. Foreground 설정이 있으면 그것.</summary>
    private Color AutoText(Color background)
    {
        var custom = _services.Settings.Current.TopBar.Foreground;
        if (!string.IsNullOrWhiteSpace(custom)) return BrushParser.ParseColor(custom, LightText);
        return BrushParser.Luminance(background) > 0.45 ? DarkText : LightText;
    }

    private void SetBarColor(Color c, bool animate)
    {
        if (_barBrush.Color == c && !animate) return;
        if (!animate)
        {
            _barBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _barBrush.Color = c;
            return;
        }
        if (_barBrush.Color == c) return;
        _barBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(c, new Duration(TimeSpan.FromMilliseconds(150))) { FillBehavior = FillBehavior.HoldEnd });
    }

    private void SetTextColors(Color left, Color right)
    {
        if (left != _leftText || LeftSection.GetValue(TextElement.ForegroundProperty) is not SolidColorBrush)
            LeftSection.SetValue(TextElement.ForegroundProperty, BrushParser.Frozen(left));
        if (right != _rightText || RightSection.GetValue(TextElement.ForegroundProperty) is not SolidColorBrush)
        {
            RightSection.SetValue(TextElement.ForegroundProperty, BrushParser.Frozen(right));
            _imeState = 0;
        }
        bool rightChanged = right != _rightText;
        _leftText = left;
        _rightText = right;
        // 메뉴·말풍선 등 창 기본 글자색도 오른쪽 기준으로
        Foreground = BrushParser.Frozen(right);
        if (rightChanged) UpdateIme();
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
        Remeasure(RightSection); // 글자 길이가 바뀌면 오른쪽 구역 폭도 다시 계산 (시계가 잘리는 현상 방지)
    }

    /// <summary>
    /// 시계 영역 고정 폭: 현재 포맷으로 만들 수 있는 가장 넓은 문자열(요일 7개 × 오전/오후 × 12:58/23:58 등)을 재서 MinWidth.
    /// 시각이 바뀌어도 오른쪽 구역(데스크톱 ‹ › 포함)이 좌우로 밀리지 않게 한다.
    /// </summary>
    private void FixClockWidth()
    {
        string fmt = _services.Settings.Current.TopBar.ClockFormat;
        if (string.IsNullOrWhiteSpace(fmt)) fmt = DefaultClockFormat;
        var typeface = new Typeface(Clock.FontFamily, Clock.FontStyle, Clock.FontWeight, Clock.FontStretch);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double max = 0;
        var baseDay = new DateTime(2026, 9, 27); // 일요일, 9월(두 자리 월 포함하려면 아래 12월도)
        foreach (var month in new[] { 9, 12 })
            for (int d = 0; d < 7; d++)
                foreach (var (h, m) in new[] { (0, 0), (10, 58), (12, 58), (22, 58), (23, 59) })
                {
                    string text;
                    try { text = new DateTime(2026, month, 20 + d, h, m, 58).ToString(fmt, Korean); }
                    catch (FormatException) { text = baseDay.ToString(DefaultClockFormat, Korean); }
                    var ft = new FormattedText(text, Korean, FlowDirection.LeftToRight, typeface, Clock.FontSize, Brushes.Black, dpi);
                    max = Math.Max(max, ft.WidthIncludingTrailingWhitespace);
                }
        Clock.MinWidth = Math.Ceiling(max);
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

        // MyDockFinder 스타일 배지: "한" = 글자색으로 채운 배경 + 반전 글자, "A" = 테두리만
        var fg = BrushParser.Frozen(_rightText);
        var inverse = BrushParser.Frozen(BrushParser.Luminance(_rightText) > 0.45
            ? BrushParser.Hex("#FF1E1E1E")
            : BrushParser.Hex("#FFFFFFFF"));
        switch (state)
        {
            case 1:
                ImeText.Text = "한";
                ImeBadge.Background = fg;
                ImeBadge.BorderBrush = fg;
                ImeText.Foreground = inverse;
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
        Remeasure(RightSection);
    }

    /// <summary>앱 이름에 표시 중인 앱의 창 (앱 메뉴용). null 이면 바탕 화면.</summary>
    private AppWindowInfo? _currentApp;

    private void UpdateAppName(bool force = false)
    {
        UpdateAppNameCore(force);
        SyncAppMenus();
    }

    // 바탕 화면이 앞에 있을 때 앱 이름 — 맥처럼 "Finder".
    private const string DesktopAppName = "Finder";

    private static PinItem ExplorerPin() => new()
    {
        Name = DesktopAppName,
        Kind = PinKind.Exe,
        Target = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
    };

    private void UpdateAppNameCore(bool force)
    {
        var fg = _services.Windows.ForegroundWindow;
        if (!force && fg == _lastForeground) return;
        _lastForeground = fg;

        var windows = _services.Windows.Windows;
        bool isDesktop;
        try { isDesktop = fg == IntPtr.Zero || _services.Windows.IsDesktopWindow(fg); }
        catch { isDesktop = fg == IntPtr.Zero; }
        if (isDesktop)
        {
            _currentApp = null;
            SetAppNameText(DesktopAppName);
            return;
        }

        var info = windows.FirstOrDefault(w => w.Hwnd == fg);
        if (info == null)
        {
            // 목록에 없는 창(대화상자, 독/상단바 자신 등)이면 이전 이름 유지. 이전 앱 창이 모두 닫혔으면 바탕 화면(Finder).
            bool previousGone = _currentApp != null && !windows.Any(w => w.Hwnd == _currentApp.Hwnd);
            if (!previousGone && AppName.Text.Length > 0) return;
            _currentApp = null;
            SetAppNameText(DesktopAppName);
            return;
        }
        _currentApp = info;
        SetAppNameText(AppNames.Get(info));
    }

    /// <summary>
    /// 구역 아래 모든 요소의 측정을 무효화. 첫 표시 전후에 바뀐 글자 크기가 부모(버튼·StackPanel)에
    /// 전달되지 않아 처음 잰 폭에 갇히는 현상(실측: TextBlock 48 / 버튼 24)을 막는다. 요소 수가 적어 비용은 무시할 만함.
    /// </summary>
    private static void Remeasure(DependencyObject root)
    {
        if (root is UIElement ui) ui.InvalidateMeasure();
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) Remeasure(VisualTreeHelper.GetChild(root, i));
    }

    private void SetAppNameText(string name)
    {
        if (AppName.Text == name) return;
        AppName.Text = name;
        // 글자 길이가 바뀌면 감싼 버튼·구역도 다시 재야 함 (안 하면 처음 잰 빈 폭에 갇힘)
        Remeasure(LeftSection);
    }

    // ───────────────────────── 상태 아이콘 ─────────────────────────

    private void OnStatusChanged(object? sender, EventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        var st = _services.Status;
        var s = _services.Settings.Current.TopBar;

        if (s.ShowNetworkSpeed)
        {
            UpSpeed.Text = "↑ " + FormatSpeed(st.UploadBytesPerSec);
            DownSpeed.Text = "↓ " + FormatSpeed(st.DownloadBytesPerSec);
        }

        if (!s.ShowStatusIcons) return;

        // 블루투스: 어댑터 없으면 숨김, 꺼짐이면 흐리게
        BluetoothButton.Visibility = st.BluetoothOn == null ? Visibility.Collapsed : Visibility.Visible;
        BluetoothButton.Opacity = st.BluetoothOn == true ? 1 : 0.45;

        // 와이파이: 흐린 전체 부채꼴 + 신호 칸 수만큼 진하게 / 끊김 / 유선
        switch (st.Wifi)
        {
            case WifiState.Connected:
                int bars = st.WifiSignal switch { >= 60 => 3, >= 30 => 2, _ => 1 };
                WifiBack.Visibility = bars == 3 ? Visibility.Collapsed : Visibility.Visible;
                WifiBack.Data = BarIcons.WifiFull;
                WifiFront.Data = BarIcons.Wifi(bars);
                WifiFront.Visibility = Visibility.Visible;
                break;
            case WifiState.Ethernet:
                WifiBack.Visibility = Visibility.Collapsed;
                WifiFront.Data = BarIcons.Ethernet;
                WifiFront.Visibility = Visibility.Visible;
                break;
            default:
                // 끊김/모름: 맥처럼 전체를 흐리게만
                WifiBack.Data = BarIcons.WifiFull;
                WifiBack.Visibility = Visibility.Visible;
                WifiFront.Visibility = Visibility.Collapsed;
                break;
        }

        VolumeIcon.Data = BarIcons.Speaker(st.Volume, st.Muted);
        Remeasure(RightSection);
    }

    private static string FormatSpeed(long bytesPerSec)
    {
        double b = Math.Max(0, bytesPerSec);
        if (b < 1024) return "0KB/s";
        double kb = b / 1024;
        if (kb < 10) return kb.ToString("0.0", CultureInfo.InvariantCulture) + "KB/s";
        if (kb < 1000) return kb.ToString("0", CultureInfo.InvariantCulture) + "KB/s";
        double mb = kb / 1024;
        if (mb < 10) return mb.ToString("0.0", CultureInfo.InvariantCulture) + "MB/s";
        return mb.ToString("0", CultureInfo.InvariantCulture) + "MB/s";
    }

    private FrameworkElement? _panelAnchor;

    private void TogglePanel(StatusPanelKind kind, FrameworkElement anchor)
    {
        // 닫히는 중(페이드 아웃)인 패널은 열린 것으로 치지 않음 — 그 사이 같은 아이콘을 다시 누르면 새로 연다
        var old = _panel is { IsClosing: false } ? _panel : null;
        bool same = old?.Kind == kind;
        _panel = null;
        if (same)
        {
            old!.Close(); // 페이드 아웃 (Closed 는 나중에 오므로 하이라이트는 지금 끔)
            SetActiveAnchor(null);
            return;
        }
        // 다른 아이콘으로 전환: 이전 카드는 바로 닫고 새 카드도 바로 표시 (맥 메뉴 막대처럼 겹침·깜빡임 없이)
        bool switching = old != null;
        old?.CloseImmediately();

        var p = anchor.TranslatePoint(new Point(0, 0), this);
        var rect = new Rect(Left + p.X, Top + p.Y, anchor.ActualWidth, anchor.ActualHeight);
        var panel = new StatusPanelWindow(_services, kind, UiTheme.Palette(_services.Settings.Current));
        panel.Closed += (_, _) =>
        {
            if (_panel != panel) return;
            _panel = null;
            SetActiveAnchor(null);
        };
        _panel = panel;
        SetActiveAnchor(anchor);
        panel.ShowBelow(rect, Top + ActualHeight, Monitor, animate: !switching); // 이 상단바의 모니터 안에
    }

    /// <summary>패널이 열린 아이콘에 회색 알약 하이라이트 (BarButton 의 Tag="Active" 트리거).</summary>
    private void SetActiveAnchor(FrameworkElement? anchor)
    {
        if (_panelAnchor != null) _panelAnchor.Tag = null;
        _panelAnchor = anchor;
        if (anchor != null) anchor.Tag = "Active";
    }

    private void OnBluetoothClick(object sender, RoutedEventArgs e) => TogglePanel(StatusPanelKind.Bluetooth, BluetoothButton);
    private void OnWifiClick(object sender, RoutedEventArgs e) => TogglePanel(StatusPanelKind.Wifi, WifiButton);
    private void OnVolumeClick(object sender, RoutedEventArgs e) => TogglePanel(StatusPanelKind.Volume, VolumeButton);

    /// <summary>볼륨 아이콘 위 휠(원격 트랙패드 스크롤 포함)로 조절.</summary>
    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        Safe(() =>
        {
            var st = _services.Status;
            double next = Math.Clamp(st.Volume + Math.Sign(e.Delta) * 0.04, 0, 1);
            if (st.Muted && e.Delta > 0) st.SetMuted(false);
            st.SetVolume(next);
        });
    }

    // ───────────────────────── 버튼 ─────────────────────────

    private void OnPreviousDesktop(object sender, RoutedEventArgs e) => SwitchDesktop(() => _services.VirtualDesktops.Previous());
    private void OnNextDesktop(object sender, RoutedEventArgs e) => SwitchDesktop(() => _services.VirtualDesktops.Next());

    private void SwitchDesktop(Action action)
    {
        _panel?.Close();
        Safe(action);
        ScheduleColor(600); // 전환 애니메이션 뒤 배경 다시 판단
    }

    private void OnImeToggle(object sender, RoutedEventArgs e)
    {
        Safe(() => _services.Ime.ToggleHangul());
        _imeRecheck.Stop();
        _imeRecheck.Start();
    }

    /// <summary>검색 버튼: Spotlight 모드면 몽독 검색창(열려 있으면 닫기), Windows 모드면 Win+S.</summary>
    private void OnSearch(object sender, RoutedEventArgs e) => Safe(() =>
    {
        if (_services.Settings.Current.TopBar.SearchMode == SearchMode.Spotlight) SpotlightWindow.Toggle(_services);
        else _services.Shell.OpenSearch();
    });
    /// <summary>제어 센터: Win+A 대신 MyDockFinder 같은 타일 패널.</summary>
    private void OnQuickSettings(object sender, RoutedEventArgs e) => TogglePanel(StatusPanelKind.ControlCenter, QuickSettingsButton);
    /// <summary>시계 클릭 = 몽독 달력 카드 (알림 센터는 카드 안 "알림 센터 열기" 링크로).</summary>
    private void OnClockClick(object sender, RoutedEventArgs e) => TogglePanel(StatusPanelKind.Calendar, ClockButton);

    private void OnTaskView(object sender, RoutedEventArgs e) => Safe(() => _services.Shell.OpenTaskView());

    /// <summary>가운데 그룹 배경: true = 옅은 알약(#0D000000), false = 배경 없이 호버만.</summary>
    internal static bool DesktopGroupPill = true;

    private void OnDesktopChanged(object? sender, EventArgs e) => UpdateDesktopIndex();

    /// <summary>"2 / 3" 표시, 첫/마지막 데스크톱이면 ‹/› 흐리게.</summary>
    private void UpdateDesktopIndex()
    {
        int index = 0, count = 0;
        try
        {
            index = _services.VirtualDesktops.CurrentIndex;
            count = _services.VirtualDesktops.Count;
        }
        catch (Exception ex) { Log.Error("가상 데스크톱 정보 조회 실패", ex); }

        bool known = index > 0 && count > 0;
        DesktopIndex.Text = known ? $"{index} / {count}" : "";
        DesktopIndexButton.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        PrevDesktopButton.Opacity = known && index <= 1 ? 0.35 : 1;
        NextDesktopButton.Opacity = known && index >= count ? 0.35 : 1;
    }

    // ───────────────────────── 앱 메뉴 (앱 이름 클릭) ─────────────────────────

    private void OnAppNameClick(object sender, RoutedEventArgs e)
    {
        _panel?.Close();
        UiTheme.Apply(_services.Settings.Current);
        var app = _currentApp;
        string name = app != null ? AppNames.Get(app) : DesktopAppName;
        var appWindows = app == null
            ? new List<AppWindowInfo>()
            : _services.Windows.Windows.Where(w => SameApp(w, app)).ToList();
        var pin = app == null ? null : _services.Settings.Current.Pins.FirstOrDefault(p => p.Kind != PinKind.Separator && SafeMatches(p, app));

        var menu = new ContextMenu
        {
            PlacementTarget = AppNameButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HorizontalOffset = -10,
        };
        OutsideClickWatcher.Attach(menu, _services, BarArea);
        AppNameButton.Tag = "Active";
        menu.Closed += (_, _) => AppNameButton.Tag = null;
        AddAppNameItems(menu, app); // 앱 전용 항목 (VS Code 설정… 등, TopBarWindow.AppMenus.cs)

        bool hasApp = app != null;
        // 경로를 못 읽은 창(관리자 권한 등)은 새 창·독 고정 불가 (빈 경로 Launch 방지)
        bool launchable = app == null || (pin != null && DockWindow.CanLaunch(pin)) || DockWindow.CanPin(app);
        menu.Items.Add(DockMenus.Item($"{name} 새 창", () =>
        {
            // 바탕 화면이면 맥처럼 Finder(파일 탐색기) 새 창.
            var target = app == null ? ExplorerPin() : pin != null && DockWindow.CanLaunch(pin) ? pin : _services.Windows.CreatePin(app);
            if (DockWindow.CanLaunch(target)) _services.Launcher.Launch(target);
        }, enabled: launchable));
        menu.Items.Add(DockMenus.Item($"{name} 최소화", () =>
        {
            foreach (var w in appWindows.Where(w => !w.IsMinimized))
                _services.Launcher.Minimize(w.Hwnd);
        }, enabled: hasApp && appWindows.Any(w => !w.IsMinimized)));
        menu.Items.Add(DockMenus.Item($"{name} 종료",
            () => ConfirmCardWindow.CloseWindows(_services, name, appWindows),
            enabled: hasApp && appWindows.Count > 0));
        menu.Items.Add(new Separator());
        if (pin != null)
        {
            menu.Items.Add(DockMenus.Item("독에서 제거", () =>
            {
                _services.Settings.Current.Pins.Remove(pin);
                _services.Settings.Save();
            }));
        }
        else
        {
            menu.Items.Add(DockMenus.Item("독에 고정", () =>
            {
                _services.Settings.Current.Pins.Add(_services.Windows.CreatePin(app!));
                _services.Settings.Save();
            }, enabled: hasApp && DockWindow.CanPin(app!)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item("바탕 화면 보기", () => _services.Shell.ShowDesktop()));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item($"{AppInfo.Name} 설정 파일 열기", () => _services.Launcher.OpenFile(_services.Settings.SettingsPath)));
        menu.Items.Add(DockMenus.Quit());
        menu.IsOpen = true;
    }

    private bool SameApp(AppWindowInfo a, AppWindowInfo b)
    {
        try { return _services.Windows.GetAppKey(a) == _services.Windows.GetAppKey(b); }
        catch { return string.Equals(a.ProcessPath, b.ProcessPath, StringComparison.OrdinalIgnoreCase); }
    }

    private bool SafeMatches(PinItem pin, AppWindowInfo w)
    {
        if (!DockWindow.CanLaunch(pin) || !DockWindow.CanPin(w)) return false;
        try { return _services.Windows.Matches(pin, w); }
        catch { return false; }
    }

    /// <summary>로고 클릭 → 맥 Apple 메뉴 같은 시스템 메뉴 (MyDock 설정은 하위 메뉴로).</summary>
    private void OnLogoClick(object sender, RoutedEventArgs e)
    {
        _panel?.Close();
        UiTheme.Apply(_services.Settings.Current);
        var menu = new ContextMenu
        {
            PlacementTarget = LogoButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            HorizontalOffset = -10, // 카드 그림자 여백만큼 당겨 로고 왼쪽에 맞춤
            VerticalOffset = 0,
        };
        OutsideClickWatcher.Attach(menu, _services, BarArea);
        _logoMenu = menu;
        LogoButton.Tag = "Active";
        menu.Closed += (_, _) => LogoButton.Tag = null;

        var shell = _services.Shell;
        menu.Items.Add(DockMenus.Item("이 PC 정보", shell.OpenAbout));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item("설정…", shell.OpenSettings));
        menu.Items.Add(DockMenus.Item("Microsoft Store", shell.OpenStore));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item("작업 관리자", shell.OpenTaskManager));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item("절전", shell.Sleep));
        menu.Items.Add(DockMenus.Item("다시 시작…", () => ConfirmCardWindow.Ask(_services,
            "지금 컴퓨터를 다시 시작할까요?", "저장하지 않은 작업은 사라질 수 있어요.", "다시 시작", shell.Restart)));
        menu.Items.Add(DockMenus.Item("시스템 종료…", () => ConfirmCardWindow.Ask(_services,
            "지금 시스템을 종료할까요?", "저장하지 않은 작업은 사라질 수 있어요.", "종료", shell.Shutdown)));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item("화면 잠금", shell.LockScreen));
        menu.Items.Add(DockMenus.Item("로그아웃…", () => ConfirmCardWindow.Ask(_services,
            "지금 로그아웃할까요?", "열려 있는 앱이 모두 닫혀요.", "로그아웃", shell.SignOut)));
        menu.Items.Add(new Separator());

        menu.Items.Add(DockMenus.SettingsWindow(_services, $"{AppInfo.Name} 설정…"));
        var mydock = new MenuItem { Header = AppInfo.Name };
        mydock.Items.Add(DockMenus.DockPosition(_services));
        mydock.Items.Add(DockMenus.DockBehavior(_services));
        mydock.Items.Add(DockMenus.DockThemeMenu(_services));
        mydock.Items.Add(DockMenus.TopBarColor(_services));
        mydock.Items.Add(new Separator());
        mydock.Items.Add(DockMenus.HideDock(_services));
        mydock.Items.Add(DockMenus.Pause());
        mydock.Items.Add(DockMenus.HideTaskbar(_services));
        mydock.Items.Add(new Separator());
        mydock.Items.Add(DockMenus.StartWithWindows(_services));
        mydock.Items.Add(DockMenus.OpenSettings(_services));
        mydock.Items.Add(new Separator());
        mydock.Items.Add(DockMenus.Quit());
        menu.Items.Add(mydock);
        menu.IsOpen = true;
    }

    private ContextMenu? _logoMenu;

    // ───────────────────────── 코치마크 앵커 ─────────────────────────

    /// <summary>
    /// 코치마크(새로운 기능·둘러보기)가 가리킬 요소의 화면 사각형 (이 상단바 모니터 기준 DIP) + 그 모니터.
    /// 상단바가 숨어 있거나(꺼짐·일시 정지·전체 화면) 요소가 안 보이면(설정에서 끔, 트레이 아이콘 없음) null.
    /// </summary>
    public (Rect Rect, MonitorInfo Monitor)? GetAnchorRect(CoachAnchor anchor)
    {
        if (_closed || !IsVisible || _fullscreen) return null;
        FrameworkElement? el = anchor switch
        {
            CoachAnchor.Logo => LogoButton,
            CoachAnchor.AppName => AppNameButton,
            CoachAnchor.Desktops => DesktopButtons,
            CoachAnchor.Tray => TrayArea,
            CoachAnchor.Search => SearchButton,
            CoachAnchor.Clock => ClockButton,
            _ => null,
        };
        if (el is null || !el.IsVisible || el.ActualWidth < 1 || el.ActualHeight < 1) return null;
        try
        {
            var p = el.TranslatePoint(new Point(0, 0), this);
            return (new Rect(Left + p.X, Top + p.Y, el.ActualWidth, el.ActualHeight), Monitor);
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>상단바 자체 영역 (그 안의 클릭은 WPF 가 처리).</summary>
    private IEnumerable<Rect> BarArea() => new[] { OutsideClickWatcher.ScreenRect(Bar) };

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        OutsideClickWatcher.Attach(menu, _services, BarArea);
        // 열기 직전에 최신 체크 상태로 다시 채움
        ContextMenuOpening += (_, _) =>
        {
            _panel?.Close();
            menu.Items.Clear();
            menu.Items.Add(DockMenus.SettingsWindow(_services, $"{AppInfo.Name} 설정…"));
            menu.Items.Add(DockMenus.TopBarColor(_services));
            menu.Items.Add(new Separator());
            menu.Items.Add(DockMenus.OpenSettings(_services));
            menu.Items.Add(DockMenus.Quit());
        };
        menu.Items.Add(DockMenus.OpenSettings(_services));
        return menu;
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Error("상단바 동작 실패", ex); }
    }
}
