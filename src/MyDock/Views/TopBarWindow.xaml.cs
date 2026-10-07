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

    public TopBarWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Bar.Background = _barBrush;

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
        Loaded += (_, _) => { if (!_services.Settings.Current.TopBar.Enabled || _fullscreen) Hide(); };
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
        _services.DesktopWindows.WallpaperChanged += OnWallpaperChanged;
        _services.DesktopWindows.FullscreenAppChanged += OnFullscreenChanged;
        _services.Status.Changed += OnStatusChanged;
        _subscribed = true;

        ApplySettings();
        UpdateStatus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
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
            _services.DesktopWindows.WallpaperChanged -= OnWallpaperChanged;
            _services.DesktopWindows.FullscreenAppChanged -= OnFullscreenChanged;
            _services.Status.Changed -= OnStatusChanged;
            _subscribed = false;
        }
        UnregisterIfNeeded();
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
        ApplySettings();
    }

    private void OnWallpaperChanged(object? sender, EventArgs e)
    {
        if (ColorMode == TopBarColorMode.Transparent) UpdateColors();
    }

    private void OnFullscreenChanged(object? sender, bool fullscreen)
    {
        _fullscreen = fullscreen;
        Topmost = !fullscreen;
        if (fullscreen)
        {
            _panel?.Close();
            if (IsVisible) Hide();
        }
        else if (_services.Settings.Current.TopBar.Enabled && IsLoaded && !IsVisible)
        {
            Show();
            UpdateColors();
        }
    }

    private TopBarColorMode ColorMode => _services.Settings.Current.TopBar.ColorMode;

    // ───────────────────────── 설정 반영 ─────────────────────────

    private void ApplySettings()
    {
        if (!_initialized) return;
        var s = _services.Settings.Current.TopBar;
        double height = Math.Clamp(double.IsNaN(s.Height) ? 28 : s.Height, 16, 80);

        FontSize = Math.Clamp(double.IsNaN(s.FontSize) ? 13 : s.FontSize, 8, 32);
        LogoButton.Visibility = Vis(s.ShowLogo);
        AppName.Visibility = Vis(s.ShowActiveAppName);
        DesktopButtons.Visibility = Vis(s.ShowDesktopButtons);
        NetSpeed.Visibility = Vis(s.ShowNetworkSpeed);
        StatusIcons.Visibility = Vis(s.ShowStatusIcons);
        QuickButtons.Visibility = Vis(s.ShowQuickButtons);
        ImeButton.Visibility = Vis(s.ShowImeToggle);
        _imeState = 0; // 배지 색 다시 칠하기

        if (!s.Enabled)
        {
            UnregisterIfNeeded();
            StopTimers();
            _panel?.Close();
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

        ApplyColorMode();

        _lastClockText = null;
        UpdateClock();
        UpdateIme();
        UpdateAppName(force: true);
        UpdateStatus();
        _clockTimer.Start();
        _pollTimer.Start();
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

        if (s.ColorMode == TopBarColorMode.Blur)
        {
            var tint = BrushParser.ParseColor(s.Background, BrushParser.Hex("#E0F6F6F6"));
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
                var bg = BrushParser.ParseColor(s.Background, BrushParser.Hex("#E0F6F6F6"));
                SetBarColor(bg, animate: false);
                SetTextColors(AutoText(bg), AutoText(bg));
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
        var band = new Rect(Left, Top + ActualHeight, Math.Max(1, ActualWidth), 3);
        band = ExcludeDock(band);
        if (band.IsEmpty) return;

        Color? sampled;
        try { sampled = _services.DesktopWindows.SampleScreenColor(band); }
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

    /// <summary>띠가 독 패널과 겹치면 독을 뺀 좌/우 중 넓은 쪽만 사용.</summary>
    private static Rect ExcludeDock(Rect band)
    {
        var dock = DockState.VisiblePanel;
        if (dock.IsEmpty || !dock.IntersectsWith(band)) return band;
        var left = new Rect(band.Left, band.Top, Math.Max(0, dock.Left - band.Left), band.Height);
        var right = new Rect(dock.Right, band.Top, Math.Max(0, band.Right - dock.Right), band.Height);
        var best = left.Width >= right.Width ? left : right;
        return best.Width < 4 ? Rect.Empty : best;
    }

    /// <summary>Transparent: 왼쪽/오른쪽 구역 뒤 배경화면 밝기로 각각 글자색 결정.</summary>
    private void UpdateWallpaperText()
    {
        if (ColorMode != TopBarColorMode.Transparent || !IsLoaded) return;
        double h = Math.Max(1, ActualHeight);
        Rect SectionRect(FrameworkElement el)
        {
            if (el.ActualWidth <= 0) return new Rect(Left, Top, Math.Max(1, ActualWidth / 3), h);
            var p = el.TranslatePoint(new Point(0, 0), this);
            return new Rect(Left + p.X, Top, el.ActualWidth, h);
        }

        Color Sample(Rect r, Color previous)
        {
            try
            {
                return _services.DesktopWindows.SampleWallpaperColor(r) is Color c ? AutoText(c) : previous;
            }
            catch (Exception ex)
            {
                Log.Error("배경화면 색 샘플링 실패", ex);
                return previous;
            }
        }

        SetTextColors(Sample(SectionRect(LeftSection), _leftText), Sample(SectionRect(RightSection), _rightText));
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
        RightSection.InvalidateMeasure(); // 글자 길이가 바뀌면 오른쪽 구역 폭도 다시 계산 (시계가 잘리는 현상 방지)
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
        RightSection.InvalidateMeasure();
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
        if (AppName.Text != name)
        {
            AppName.Text = name;
            LeftSection.InvalidateMeasure();
        }
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
                int bars = st.WifiSignal switch { >= 75 => 4, >= 50 => 3, >= 25 => 2, _ => 1 };
                WifiBack.Visibility = bars == 4 ? Visibility.Collapsed : Visibility.Visible;
                WifiBack.Text = "";
                WifiFront.Text = bars switch { 4 => "", 3 => "", 2 => "", _ => "" };
                break;
            case WifiState.Ethernet:
                WifiBack.Visibility = Visibility.Collapsed;
                WifiFront.Text = "";
                break;
            case WifiState.Disconnected:
                WifiBack.Visibility = Visibility.Collapsed;
                WifiFront.Text = "";
                break;
            default:
                WifiBack.Visibility = Visibility.Collapsed;
                WifiFront.Text = "";
                break;
        }

        VolumeButton.Content = VolumeGlyph(st.Volume, st.Muted);
        RightSection.InvalidateMeasure();
    }

    /// <summary>볼륨 글리프 (음소거 / 0 / 낮음 / 중간 / 높음).</summary>
    public static string VolumeGlyph(double volume, bool muted)
    {
        if (muted) return "";
        return volume switch
        {
            <= 0.001 => "",
            < 0.34 => "",
            < 0.67 => "",
            _ => "",
        };
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

    private void TogglePanel(StatusPanelKind kind, FrameworkElement anchor)
    {
        bool same = _panel?.Kind == kind;
        _panel?.Close();
        _panel = null;
        if (same) return;

        var p = anchor.TranslatePoint(new Point(0, 0), this);
        var rect = new Rect(Left + p.X, Top + p.Y, anchor.ActualWidth, anchor.ActualHeight);
        var panel = new StatusPanelWindow(_services, kind, SystemTheme.AppsUseLightTheme());
        panel.Closed += (_, _) => { if (_panel == panel) _panel = null; };
        _panel = panel;
        panel.ShowBelow(rect, Top + ActualHeight);
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
    private void OnNewDesktop(object sender, RoutedEventArgs e) => SwitchDesktop(() => _services.VirtualDesktops.New());

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

    private void OnSearch(object sender, RoutedEventArgs e) => Safe(() => _services.Shell.OpenSearch());
    private void OnQuickSettings(object sender, RoutedEventArgs e) => Safe(() => _services.Shell.OpenQuickSettings());
    private void OnNotifications(object sender, RoutedEventArgs e) => Safe(() => _services.Shell.OpenNotificationCenter());

    /// <summary>로고 클릭 → 로고 아래에 MyDock 메뉴.</summary>
    private void OnLogoClick(object sender, RoutedEventArgs e)
    {
        _panel?.Close();
        var menu = new ContextMenu
        {
            PlacementTarget = LogoButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        menu.Items.Add(DockMenus.Item("시작 메뉴", () => _services.Shell.OpenStartMenu()));
        menu.Items.Add(DockMenus.Item("작업 보기", () => _services.Shell.OpenTaskView()));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.DockPosition(_services));
        menu.Items.Add(DockMenus.DockBehavior(_services));
        menu.Items.Add(DockMenus.DockThemeMenu(_services));
        menu.Items.Add(DockMenus.TopBarColor(_services));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.StartWithWindows(_services));
        menu.Items.Add(DockMenus.OpenSettings(_services));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Quit());
        menu.IsOpen = true;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        // 열기 직전에 최신 체크 상태로 다시 채움
        ContextMenuOpening += (_, _) =>
        {
            _panel?.Close();
            menu.Items.Clear();
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
