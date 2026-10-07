using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyDock.Models;
using MyDock.Services;
using MyDock.ViewModels;
using Ellipse = System.Windows.Shapes.Ellipse;
using WinForms = System.Windows.Forms;

namespace MyDock.Views;

/// <summary>
/// 맥 "시스템 설정" 같은 설정 창: 왼쪽 사이드바(일반/독/상단바/정보) + 오른쪽 내용.
/// - 독·상단바와 달리 일반 창 (포커스를 받아도 됨, 작업 표시줄에 보임). 한 개만 열림 → <see cref="Open"/>.
/// - 원격(StarDesk)에서 마우스만으로: 토글·세그먼트·드롭다운(메뉴)·슬라이더만 쓰고 키보드 입력 칸은 없다.
/// - 바꾸면 바로 Settings.Save() → SettingsChanged 로 독·상단바에 즉시 반영 (DockMenus 와 같은 경로).
///   슬라이더는 드래그 중엔 저장하지 않고 놓을 때(클릭·휠은 짧은 디바운스 후) 저장.
/// - 다른 곳(메뉴·독 드래그·settings.json 편집)에서 설정이 바뀌면 현재 페이지를 다시 그림.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private enum Page { General, Dock, TopBar, About }

    private const string GitHubUrl = "https://github.com/mong-head/mongdock";
    private const double SidebarWidth = 200;
    private const double ControlWidth = 230;

    private static SettingsWindow? _instance;

    private readonly AppServices _services;
    private readonly DispatcherTimer _sliderTimer;
    private Page _page = Page.General;
    private UiPalette _p;
    private ScrollViewer? _scroll;
    private bool _selfSave;
    private Action? _pendingSlider;
    private bool _sliderDragging;
    private bool _rebuildQueued;
    private bool _closed;

    /// <summary>설정 창 열기. 이미 열려 있으면 (최소화 해제 후) 앞으로.</summary>
    public static void Open(AppServices services)
    {
        try
        {
            if (_instance is { } w)
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Activate();
                return;
            }
            _instance = new SettingsWindow(services);
            _instance.Show();
            _instance.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("설정 창 열기 실패", ex);
        }
    }

    private SettingsWindow(AppServices services)
    {
        _services = services;
        _p = UiTheme.Palette(services.Settings.Current);
        Title = $"{AppInfo.Name} 설정";
        Width = 780;
        Height = 620;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        FontSize = 13;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _sliderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _sliderTimer.Tick += (_, _) =>
        {
            _sliderTimer.Stop();
            if (!_sliderDragging) FlushSlider();
        };

        SourceInitialized += (_, _) => ApplyTitleBarTheme();
        _services.Settings.SettingsChanged += OnSettingsChanged;
        SystemTheme.Changed += OnSystemThemeChanged;
        Closed += (_, _) =>
        {
            FlushSlider();
            _closed = true;
            _sliderTimer.Stop();
            _services.Settings.SettingsChanged -= OnSettingsChanged;
            SystemTheme.Changed -= OnSystemThemeChanged;
            if (_instance == this) _instance = null;
        };

        Rebuild();
    }

    // ───────────────────────── 변경 반영 ─────────────────────────

    /// <summary>설정 변경 → 저장(SettingsChanged 로 독·상단바 즉시 반영). rebuild 면 현재 페이지를 다시 그림(의존 항목 활성/비활성 등).</summary>
    private void Commit(Action change, bool rebuild = false)
    {
        try { change(); }
        catch (Exception ex) { Log.Error("설정 변경 실패", ex); }
        _selfSave = true;
        try { _services.Settings.Save(); }
        catch (Exception ex) { Log.Error("설정 저장 실패", ex); }
        finally { _selfSave = false; }
        UiFonts.Apply(_services.Settings.Current);
        // 독 테마(시스템/밝게/어둡게)가 바뀌면 이 창 색도 따라감
        if (rebuild || UiTheme.IsLight(_services.Settings.Current) != _p.IsLight) QueueRebuild();
    }

    private void FlushSlider()
    {
        var pending = _pendingSlider;
        _pendingSlider = null;
        if (pending != null) Commit(pending);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // 내가 저장한 것은 이미 화면에 반영됨. 슬라이더 조작 중이면 끝난 뒤 저장할 때까지 그대로 둠.
        if (_selfSave || _sliderDragging || _pendingSlider != null) return;
        QueueRebuild();
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(() => { if (IsLoaded) QueueRebuild(); });

    /// <summary>클릭 처리 도중 컨트롤을 갈아 끼우지 않도록 한 박자 늦게 다시 그림.</summary>
    private void QueueRebuild()
    {
        if (_rebuildQueued || _closed) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            if (!_closed) Rebuild();
        }, DispatcherPriority.Background);
    }

    // ───────────────────────── 창 구성 ─────────────────────────

    private void Rebuild()
    {
        var settings = _services.Settings.Current;
        bool themeChanged = UiTheme.IsLight(settings) != _p.IsLight;
        _p = UiTheme.Palette(settings);
        UiTheme.Apply(settings); // 드롭다운 메뉴 색
        UiFonts.Apply(settings);
        if (themeChanged) ApplyTitleBarTheme();

        double scroll = _scroll?.VerticalOffset ?? 0;
        Background = _p.WindowBackground;
        Foreground = _p.Text;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var sidebar = new Border { Background = _p.SidebarBackground, BorderBrush = _p.Divider, BorderThickness = new Thickness(0, 0, 1, 0) };
        sidebar.Child = BuildSidebar();
        root.Children.Add(sidebar);

        var body = new StackPanel { Margin = new Thickness(28, 22, 28, 28), MaxWidth = 640 };
        body.Children.Add(new TextBlock
        {
            Text = PageTitle(_page),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(2, 0, 0, 14),
        });
        switch (_page)
        {
            case Page.General: BuildGeneral(body); break;
            case Page.Dock: BuildDock(body); break;
            case Page.TopBar: BuildTopBar(body); break;
            default: BuildAbout(body); break;
        }
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = body,
        };
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(_scroll);
        Content = root;

        if (scroll > 0)
        {
            var sv = _scroll;
            sv.Loaded += (_, _) => sv.ScrollToVerticalOffset(scroll);
        }
    }

    private static string PageTitle(Page page) => page switch
    {
        Page.General => "일반",
        Page.Dock => "독",
        Page.TopBar => "상단바",
        _ => "정보",
    };

    private UIElement BuildSidebar()
    {
        var panel = new StackPanel { Margin = new Thickness(10, 16, 10, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = AppInfo.Name,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(10, 0, 0, 14),
        });
        // 아이콘 글리프(Segoe Fluent Icons/MDL2) + 맥처럼 색 있는 둥근 사각형
        panel.Children.Add(SidebarItem(Page.General, "\uE713", Color.FromRgb(0x8E, 0x8E, 0x93)));
        panel.Children.Add(SidebarItem(Page.Dock, "\uE8A9", Color.FromRgb(0x0A, 0x84, 0xFF)));
        panel.Children.Add(SidebarItem(Page.TopBar, "\uE700", Color.FromRgb(0x5E, 0x5C, 0xE6)));
        panel.Children.Add(SidebarItem(Page.About, "\uE946", Color.FromRgb(0x34, 0xC7, 0x59)));
        return panel;
    }

    private Border SidebarItem(Page page, string glyph, Color tile)
    {
        bool selected = _page == page;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = Converters.BrushParser.Frozen(tile),
            Margin = new Thickness(0, 0, 9, 0),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 12,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        row.Children.Add(new TextBlock
        {
            Text = PageTitle(page),
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = selected ? _p.AccentText : _p.Text,
        });
        // CardLinkButton = 왼쪽 정렬 + 호버. 선택 표시는 바깥 Border 의 강조색
        var button = new Button
        {
            Style = (Style)FindResource("CardLinkButton"),
            Foreground = selected ? _p.AccentText : _p.Text,
            Padding = new Thickness(8, 5, 8, 5),
            Content = row,
        };
        button.Click += (_, _) =>
        {
            if (_page == page) return;
            FlushSlider();
            _page = page;
            _scroll = null; // 페이지가 바뀌면 맨 위부터
            Rebuild();
        };
        return new Border
        {
            Background = selected ? _p.Accent : Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 1, 0, 1),
            Child = button,
        };
    }

    // ───────────────────────── 페이지: 일반 ─────────────────────────

    private void BuildGeneral(Panel body)
    {
        var s = _services.Settings.Current;
        bool startup;
        try { startup = _services.Startup.IsEnabled; }
        catch { startup = s.StartWithWindows; }

        body.Children.Add(Group(
            Row("로그인 시 자동 실행", "Windows 에 로그인하면 mongdock 을 바로 켭니다.",
                Toggle(startup, on => Commit(() =>
                {
                    // 트레이·독 메뉴(DockMenus.StartWithWindows)와 같은 처리: 레지스트리 등록 + 설정 저장
                    _services.Startup.SetEnabled(on);
                    _services.Settings.Current.StartWithWindows = on;
                }))),
            Row("윈도우 작업 표시줄 숨기기", "mongdock 이 켜져 있는 동안만 숨깁니다. 일시 정지·종료 시 복원.",
                Toggle(s.HideWindowsTaskbar, on => Commit(() => _services.Settings.Current.HideWindowsTaskbar = on)))));

        body.Children.Add(Group(
            Row("글꼴", "상단바·메뉴·패널·독 이름표 글꼴", FontDropdown(s.FontFamily))));
    }

    private UIElement FontDropdown(string current)
    {
        var options = new (string Value, string Label)[]
        {
            ("Pretendard", "Pretendard (기본)"),
            ("Malgun Gothic", "맑은 고딕"),
            ("Segoe UI", "Segoe UI"),
        };
        string cur = string.IsNullOrWhiteSpace(current) ? "Pretendard" : current.Trim();
        var match = options.FirstOrDefault(o => string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase));
        string label = match.Label ?? $"사용자 지정 ({cur})";
        return Dropdown(label, () => options.Select(o => (o.Label,
            string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase),
            (Action)(() => Commit(() => _services.Settings.Current.FontFamily = o.Value, rebuild: true)))));
    }

    // ───────────────────────── 페이지: 독 ─────────────────────────

    private void BuildDock(Panel body)
    {
        var d = _services.Settings.Current.Dock;
        DockSettings D() => _services.Settings.Current.Dock; // 외부 다시 로드로 객체가 바뀌어도 최신 것에 씀

        body.Children.Add(Group(
            Row("독 보이기", null, Toggle(d.Enabled, on => Commit(() => D().Enabled = on))),
            Row("위치", null, Segmented(d.Edge,
                new[] { (DockEdge.Left, "왼쪽"), (DockEdge.Bottom, "아래"), (DockEdge.Right, "오른쪽") },
                v => Commit(() => D().Edge = v))),
            Row("모니터", "독을 둘 모니터. 연결이 끊기면 주 모니터에 표시됩니다.", MonitorDropdown(d.Monitor)),
            Row("동작", null, Segmented(d.Mode,
                new[] { (DockMode.AutoHide, "자동 숨김"), (DockMode.Overlay, "항상 보이기"), (DockMode.Reserve, "공간 차지") },
                v => Commit(() => D().Mode = v)))));

        body.Children.Add(Group(
            Row("아이콘 크기", null, ValueSlider(d.IconSize, 24, 96, 1, v => $"{v:0}", v => D().IconSize = v)),
            Row("간격", null, ValueSlider(d.IconSpacing, 0, 20, 1, v => $"{v:0}", v => D().IconSpacing = v)),
            Row("확대 배율", "1.0 이면 확대하지 않습니다.",
                ValueSlider(d.HoverScale, 1.0, 2.5, 0.1, v => v <= 1.001 ? "끔" : $"{v:0.0}배", v => D().HoverScale = v)),
            Row("파도 확대", "커서 주변 아이콘도 거리에 따라 같이 커집니다.",
                Toggle(d.WaveMagnification, on => Commit(() => D().WaveMagnification = on)))));

        // 독 모양: 유리(블러) = DWM 둥근 모서리 8px 고정 / 단색 반투명 = 모서리 반경 자유
        body.Children.Add(SectionTitle("독 모양"));
        body.Children.Add(Group(
            RadioRow("유리 (블러, 모서리 8px)", "배경이 흐리게 비치는 반투명 유리.", d.Blur,
                () => Commit(() => D().Blur = true, rebuild: true)),
            RadioRow("단색 반투명 (크게 둥글게)", "블러 없이 반투명 단색. 모서리를 크게 둥글릴 수 있고 원격 접속에서 더 가볍습니다.", !d.Blur,
                () => Commit(() => D().Blur = false, rebuild: true)),
            Row("모서리 반경", "블러는 Windows(DWM) 제약으로 모서리가 8px 로 고정돼 단색에서만 바뀝니다.",
                ValueSlider(d.CornerRadius, 8, 28, 1, v => $"{v:0}px", v => D().CornerRadius = v, enabled: !d.Blur))));

        body.Children.Add(Group(
            Row("테마", null, Segmented(d.Theme,
                new[] { (DockTheme.System, "시스템"), (DockTheme.Light, "밝게"), (DockTheme.Dark, "어둡게") },
                v => Commit(() => D().Theme = v))),
            Row("아이콘 모양", "맥: 둥근 사각형으로 크기·여백을 맞춤 / 원본: 앱 아이콘 그대로", Segmented(d.IconStyle,
                new[] { (IconStyle.Mac, "맥"), (IconStyle.Original, "원본") },
                v => Commit(() => D().IconStyle = v)))));

        body.Children.Add(Group(
            Row("실행 중 앱 표시", "고정하지 않은 실행 중 앱도 구분선 뒤에 표시합니다.",
                Toggle(d.ShowRunningApps, on => Commit(() => D().ShowRunningApps = on))),
            Row("다른 데스크톱 창 표시", "다른 가상 데스크톱의 창도 실행 중 점과 창 선택에 표시합니다.",
                Toggle(d.ShowWindowsFromAllDesktops, on => Commit(() => D().ShowWindowsFromAllDesktops = on))),
            Row("창이 여러 개일 때 클릭", null, Segmented(d.MultiWindowClick,
                new[] { (MultiWindowClick.Picker, "창 선택"), (MultiWindowClick.MostRecent, "최근 창") },
                v => Commit(() => D().MultiWindowClick = v)))));
    }

    /// <summary>"주 모니터" + 연결된 모니터 (장치 이름 \\.\DISPLAYn). 목록은 펼칠 때마다 새로 읽음.</summary>
    private UIElement MonitorDropdown(string current)
    {
        var screens = ReadMonitors();
        string label;
        if (string.IsNullOrEmpty(current))
            label = "주 모니터";
        else
        {
            var m = screens.FirstOrDefault(x => string.Equals(x.Device, current, StringComparison.OrdinalIgnoreCase));
            label = m.Device != null ? m.Label : $"연결 안 됨 ({current}) → 주 모니터";
        }
        return Dropdown(label, () =>
        {
            string cur = _services.Settings.Current.Dock.Monitor ?? "";
            var items = new List<(string, bool, Action)>
            {
                ("주 모니터", cur.Length == 0, () => Commit(() => _services.Settings.Current.Dock.Monitor = "", rebuild: true)),
            };
            foreach (var m in ReadMonitors())
            {
                string device = m.Device;
                items.Add((m.Label, string.Equals(device, cur, StringComparison.OrdinalIgnoreCase),
                    () => Commit(() => _services.Settings.Current.Dock.Monitor = device, rebuild: true)));
            }
            return items;
        });
    }

    /// <summary>연결된 모니터: 장치 이름 + "모니터 2 (1920×1080)" 표시 이름.
    /// WinForms Screen 이 EnumDisplayMonitors + GetMonitorInfo(MONITORINFOEX.szDevice) 를 감싼 것. PerMonitorV2 라 Bounds 는 물리 픽셀.</summary>
    private static List<(string Device, string Label)> ReadMonitors()
    {
        var list = new List<(int Number, string Device, string Label)>();
        try
        {
            var screens = WinForms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var sc = screens[i];
                string digits = new(sc.DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
                int number = int.TryParse(digits, out int n) ? n : i + 1;
                string label = $"모니터 {number} ({sc.Bounds.Width}×{sc.Bounds.Height})" + (sc.Primary ? " · 주" : "");
                list.Add((number, sc.DeviceName, label));
            }
            list.Sort((a, b) => a.Number.CompareTo(b.Number));
        }
        catch (Exception ex)
        {
            Log.Error("모니터 목록 읽기 실패", ex);
        }
        return list.Select(x => (x.Device, x.Label)).ToList();
    }

    // ───────────────────────── 페이지: 상단바 ─────────────────────────

    private void BuildTopBar(Panel body)
    {
        var t = _services.Settings.Current.TopBar;
        TopBarSettings T() => _services.Settings.Current.TopBar;

        body.Children.Add(Group(
            Row("상단바 보이기", null, Toggle(t.Enabled, on => Commit(() => T().Enabled = on))),
            Row("모든 모니터에 표시", "끄면 주 모니터에만 표시합니다.",
                Toggle(t.ShowOnAllMonitors, on => Commit(() => T().ShowOnAllMonitors = on))),
            Row("높이", null, ValueSlider(t.Height, 20, 40, 1, v => $"{v:0}", v => T().Height = v)),
            Row("글자 크기", null, ValueSlider(t.FontSize, 11, 16, 0.5, v => $"{v:0.#}", v => T().FontSize = v)),
            Row("색", null, ColorModeDropdown(t.ColorMode))));

        body.Children.Add(SectionTitle("표시할 항목"));
        body.Children.Add(Group(
            Row("로고", "끄면 이 창은 트레이 아이콘이나 독 오른쪽 클릭 메뉴에서 엽니다.",
                Toggle(t.ShowLogo, on => Commit(() => T().ShowLogo = on))),
            Row("앱 이름", null, Toggle(t.ShowActiveAppName, on => Commit(() => T().ShowActiveAppName = on))),
            Row("앱 메뉴", "파일·편집·보기… (맥 메뉴 막대처럼)", Toggle(t.ShowAppMenus, on => Commit(() => T().ShowAppMenus = on))),
            Row("가상 데스크톱 버튼", null, Toggle(t.ShowDesktopButtons, on => Commit(() => T().ShowDesktopButtons = on))),
            Row("상태 아이콘", "Wi-Fi·블루투스·볼륨", Toggle(t.ShowStatusIcons, on => Commit(() => T().ShowStatusIcons = on))),
            Row("빠른 버튼", "검색·빠른 설정·알림 센터", Toggle(t.ShowQuickButtons, on => Commit(() => T().ShowQuickButtons = on))),
            Row("한/영", null, Toggle(t.ShowImeToggle, on => Commit(() => T().ShowImeToggle = on))),
            Row("네트워크 속도", null, Toggle(t.ShowNetworkSpeed, on => Commit(() => T().ShowNetworkSpeed = on)))));

        body.Children.Add(Group(
            Row("검색 버튼", null, Segmented(t.SearchMode,
                new[] { (SearchMode.Spotlight, "몽독 검색 (화면 가운데)"), (SearchMode.Windows, "윈도우 검색") },
                v => Commit(() => T().SearchMode = v))),
            Row("검색 단축키", "Win+Space 는 윈도우 입력 언어 전환과 겹칩니다. 언어가 여러 개면 다른 키를 고르세요",
                SpotlightHotkeyDropdown(t.SpotlightHotkey))));

        body.Children.Add(SectionTitle("알림"));
        body.Children.Add(Group(
            Row("알림 배너", "윈도우 알림이 오면 상단바 아래 오른쪽에 맥처럼 표시합니다.",
                Toggle(_services.Settings.Current.Notifications.ShowNotificationBanners,
                    on => Commit(() => _services.Settings.Current.Notifications.ShowNotificationBanners = on)))));
    }

    private UIElement SpotlightHotkeyDropdown(SpotlightHotkey current)
    {
        var options = new[]
        {
            (SpotlightHotkey.WinSpace, "Win + Space"),
            (SpotlightHotkey.AltSpace, "Alt + Space"),
            (SpotlightHotkey.CtrlSpace, "Ctrl + Space"),
            (SpotlightHotkey.None, "사용 안 함"),
        };
        string label = options.FirstOrDefault(o => o.Item1 == current).Item2 ?? options[0].Item2;
        return Dropdown(label, () =>
        {
            var cur = _services.Settings.Current.TopBar.SpotlightHotkey;
            return options.Select(o => (o.Item2, o.Item1 == cur,
                (Action)(() => Commit(() => _services.Settings.Current.TopBar.SpotlightHotkey = o.Item1, rebuild: true))));
        });
    }

    private UIElement ColorModeDropdown(TopBarColorMode current)
    {
        var options = new[]
        {
            (TopBarColorMode.Transparent, "투명"),
            (TopBarColorMode.Auto, "앱 색에 맞춤"),
            (TopBarColorMode.Blur, "블러"),
            (TopBarColorMode.Fixed, "고정 색"),
        };
        return Dropdown(options.First(o => o.Item1 == current).Item2, () =>
        {
            var cur = _services.Settings.Current.TopBar.ColorMode;
            return options.Select(o => (o.Item2, o.Item1 == cur,
                (Action)(() => Commit(() => _services.Settings.Current.TopBar.ColorMode = o.Item1, rebuild: true))));
        });
    }

    // ───────────────────────── 페이지: 정보 ─────────────────────────

    private void BuildAbout(Panel body)
    {
        var head = new StackPanel { Margin = new Thickness(0, 6, 0, 18), HorizontalAlignment = HorizontalAlignment.Center };
        head.Children.Add(new TextBlock
        {
            Text = AppInfo.Name,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = $"버전 {VersionText()}",
            Foreground = _p.SubText,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });
        head.Children.Add(new TextBlock
        {
            Text = "윈도우용 맥 스타일 독 + 상단바",
            Foreground = _p.SubText,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });
        body.Children.Add(head);

        string folder = Path.GetDirectoryName(_services.Settings.SettingsPath) ?? AppInfo.DataDirectory;
        body.Children.Add(Group(
            Row("GitHub", GitHubUrl, ActionButton("열기", () => _services.Launcher.OpenFile(GitHubUrl))),
            Row("설정 폴더", folder, ActionButton("폴더 열기", () => _services.Launcher.OpenFile(folder))),
            Row("설정 파일", "settings.json (직접 편집하면 저장 즉시 반영)",
                ActionButton("파일 열기", () => _services.Launcher.OpenFile(_services.Settings.SettingsPath)))));
    }

    /// <summary>어셈블리 정보 버전 ("+커밋" 꼬리 제거). 없으면 어셈블리 버전.</summary>
    private static string VersionText()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(SettingsWindow).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "?";
    }

    // ───────────────────────── 컨트롤 도우미 ─────────────────────────

    private TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = _p.SubText,
        Margin = new Thickness(4, 4, 0, 6),
    };

    /// <summary>맥 설정의 둥근 그룹 카드: 행 사이에 얇은 구분선.</summary>
    private Border Group(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
                stack.Children.Add(new Border { Height = 1, Background = _p.Divider, Margin = new Thickness(14, 0, 14, 0), SnapsToDevicePixels = true });
            stack.Children.Add(rows[i]);
        }
        return new Border
        {
            Background = _p.GroupBackground,
            BorderBrush = _p.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 0, 16),
            Child = stack,
        };
    }

    /// <summary>왼쪽 제목(+회색 설명) / 오른쪽 컨트롤.</summary>
    private Grid Row(string title, string? sub, UIElement control)
    {
        var grid = new Grid { MinHeight = 44, Margin = new Thickness(14, 7, 14, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        texts.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(sub))
            texts.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = _p.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(texts);

        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>맥 스위치 (Themes/Controls.xaml MacSwitch).</summary>
    private ToggleButton Toggle(bool value, Action<bool> set)
    {
        var tb = new ToggleButton
        {
            Style = (Style)FindResource("MacSwitch"),
            Background = _p.Accent,
            BorderBrush = _p.CircleOff,
            IsChecked = value,
        };
        tb.Click += (_, _) => set(tb.IsChecked == true);
        return tb;
    }

    /// <summary>세그먼트 버튼 (라디오처럼 하나 선택). 누르면 바로 선택 표시를 바꾸고 저장.</summary>
    private Border Segmented<T>(T current, (T Value, string Label)[] options, Action<T> set) where T : struct, Enum
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<(Button Button, T Value)>();
        void Paint(T selected)
        {
            foreach (var (b, v) in buttons)
            {
                bool on = EqualityComparer<T>.Default.Equals(v, selected);
                b.Background = on ? _p.Accent : Brushes.Transparent;
                b.Foreground = on ? _p.AccentText : _p.Text;
            }
        }
        foreach (var (value, label) in options)
        {
            var b = new Button
            {
                Style = (Style)FindResource("CardButton"),
                Content = label,
                Padding = new Thickness(11, 4, 11, 5),
                MinWidth = 52,
            };
            b.Click += (_, _) =>
            {
                Paint(value);
                set(value);
            };
            buttons.Add((b, value));
            panel.Children.Add(b);
        }
        Paint(current);
        return new Border
        {
            Background = _p.Tile,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = panel,
        };
    }

    /// <summary>드롭다운: 버튼 클릭 → 앱 공통 메뉴(Themes/Menus.xaml) 로 선택지. 항목은 열 때마다 새로 만듦.</summary>
    private Button Dropdown(string label, Func<IEnumerable<(string Label, bool Checked, Action Pick)>> items)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ControlWidth });
        content.Children.Add(new TextBlock
        {
            Text = "\uE70D", // ChevronDown
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 10,
            Foreground = _p.SubText,
            Margin = new Thickness(8, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Button
        {
            Style = (Style)FindResource("CardButton"),
            Background = _p.Tile,
            Foreground = _p.Text,
            Padding = new Thickness(11, 5, 9, 6),
            Content = content,
        };
        button.Click += (_, _) =>
        {
            UiTheme.Apply(_services.Settings.Current);
            var menu = new ContextMenu
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                HorizontalOffset = -10, // 카드 그림자 여백
            };
            foreach (var (text, isChecked, pick) in items())
                menu.Items.Add(DockMenus.Item(text, pick, isChecked: isChecked));
            menu.IsOpen = true;
        };
        return button;
    }

    /// <summary>라디오 행: 행 전체 클릭으로 선택 (원 + 제목 + 설명).</summary>
    private Button RadioRow(string title, string sub, bool selected, Action pick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var circle = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        circle.Children.Add(new Ellipse
        {
            Fill = selected ? _p.Accent : Brushes.Transparent,
            Stroke = selected ? _p.Accent : _p.SubText,
            StrokeThickness = 1.2,
        });
        if (selected)
            circle.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = _p.AccentText });
        grid.Children.Add(circle);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, Foreground = _p.Text });
        texts.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = _p.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var button = new Button
        {
            Style = (Style)FindResource("CardLinkButton"),
            Foreground = _p.Text,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 1, 0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = grid,
        };
        button.Click += (_, _) => { if (!selected) pick(); };
        return button;
    }

    /// <summary>슬라이더 + 값 표시. 드래그 중엔 저장 안 함 → 놓을 때 저장. 클릭(트랙 점프)은 짧은 디바운스 후 저장.</summary>
    private UIElement ValueSlider(double value, double min, double max, double step, Func<double, string> format, Action<double> set, bool enabled = true)
    {
        double start = double.IsNaN(value) ? min : Math.Clamp(value, min, max);
        var label = new TextBlock
        {
            Width = 46,
            TextAlignment = TextAlignment.Right,
            Foreground = _p.SubText,
            VerticalAlignment = VerticalAlignment.Center,
            Text = format(start),
        };
        var slider = new Slider
        {
            Style = (Style)FindResource("MacSlider"),
            Width = ControlWidth - 54,
            Minimum = min,
            Maximum = max,
            Value = start,
            SmallChange = step,
            LargeChange = step,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Foreground = _p.Accent,
            Background = _p.SliderTrack,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            double v = Math.Round(e.NewValue / step) * step;
            v = Math.Round(Math.Clamp(v, min, max), 3);
            label.Text = format(v);
            _pendingSlider = () => set(v);
            if (_sliderDragging) return;
            _sliderTimer.Stop();
            _sliderTimer.Start();
        };
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            _sliderDragging = true;
            _sliderTimer.Stop();
        }));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _sliderDragging = false;
            FlushSlider();
        }));

        var panel = new StackPanel { Orientation = Orientation.Horizontal, IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 };
        panel.Children.Add(slider);
        panel.Children.Add(label);
        return panel;
    }

    private Button ActionButton(string text, Action action)
    {
        var b = new Button
        {
            Style = (Style)FindResource("CardButton"),
            Background = _p.Tile,
            Foreground = _p.Text,
            Content = text,
        };
        b.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Error($"'{text}' 실행 실패", ex); }
        };
        return b;
    }

    // ───────────────────────── 제목 표시줄 색 ─────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>어두운 테마면 제목 표시줄도 어둡게 (Windows 10 20H1+/11). 실패해도 무시.</summary>
    private void ApplyTitleBarTheme()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = _p.IsLight ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { /* 오래된 Windows */ }
    }
}
