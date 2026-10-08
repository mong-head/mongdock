using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.Services.Search;
using Mongdock.ViewModels;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Mongdock.Views;

/// <summary>
/// 맥 "시스템 설정" 같은 설정 창: 왼쪽 사이드바(일반/독/상단바/캘린더/검색/정보/변경 내역) + 오른쪽 내용.
/// - 독·상단바와 달리 일반 창 (포커스를 받아도 됨, 작업 표시줄에 보임). 한 개만 열림 → <see cref="Open"/>.
/// - 원격(StarDesk)에서 마우스만으로: 토글·세그먼트·드롭다운(메뉴)·슬라이더만 쓰고 키보드 입력 칸은 없다.
/// - 바꾸면 바로 Settings.Save() → SettingsChanged 로 독·상단바에 즉시 반영 (DockMenus 와 같은 경로).
///   슬라이더는 드래그 중엔 저장하지 않고 놓을 때(클릭·휠은 짧은 디바운스 후) 저장.
/// - 다른 곳(메뉴·독 드래그·settings.json 편집)에서 설정이 바뀌면 현재 페이지를 다시 그림.
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private enum Page { General, Dock, TopBar, Calendar, Search, About, Changelog }

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
    public static void Open(AppServices services) => Open(services, null);

    /// <summary>설정 창을 "캘린더" 페이지로 열기 (시계 달력의 "캘린더 일정 연결하기…").</summary>
    public static void OpenCalendarPage(AppServices services) => Open(services, Page.Calendar);

    /// <summary>설정 창을 "변경 내역" 페이지로 열기 (정보 페이지 링크, 코치마크 "변경 내역 보기").</summary>
    public static void OpenChangelogPage(AppServices services) => Open(services, Page.Changelog);

    /// <summary>레이아웃이 끝난 뒤 그 요소가 위쪽에 오게 스크롤 (변경 내역 "주요 업데이트"에서 버전 누름) (요소는 그때 다시 찾음 — 그 사이 다시 그려져도 됨).</summary>
    private void ScrollToWhenReady(Func<FrameworkElement?> target)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _scroll?.Content is not UIElement content || target() is not { IsLoaded: true } el) return;
            try
            {
                double y = el.TranslatePoint(new Point(0, 0), content).Y;
                _scroll.ScrollToVerticalOffset(Math.Max(0, y - 8));
            }
            catch (InvalidOperationException) { }
        }, DispatcherPriority.Background);
    }

    private static void Open(AppServices services, Page? page)
    {
        try
        {
            if (_instance is { } w)
            {
                if (!w.IsVisible) w.Show(); // 둘러보기 재생 중 숨겨 둔 창
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                if (page is Page pg && w._page != pg)
                {
                    w.FlushSlider();
                    w._page = pg;
                    w._scroll = null;
                    w.Rebuild();
                }
                w.Activate();
                return;
            }
            _instance = new SettingsWindow(services);
            if (page is Page first)
            {
                _instance._page = first;
                _instance.Rebuild();
            }
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
        _services.Calendars.Changed += OnCalendarsChanged;
        _services.TrayIcons.Changed += OnTrayIconsChanged; // SettingsWindow.Tray.cs
        SystemTheme.Changed += OnSystemThemeChanged;
        Closed += (_, _) =>
        {
            FlushSlider();
            _closed = true;
            _sliderTimer.Stop();
            _services.Settings.SettingsChanged -= OnSettingsChanged;
            _services.Calendars.Changed -= OnCalendarsChanged;
            StopTrayRefresh();
            StopFullscreenWait(); // SettingsWindow.Changelog.cs
            _relativeTimer?.Stop();
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

    /// <summary>구독 캘린더 상태(동기화·오류·목록)가 바뀜 → 캘린더 페이지면 다시 그림.</summary>
    private void OnCalendarsChanged(object? sender, EventArgs e)
    {
        if (_page == Page.Calendar && !_sliderDragging) QueueRebuild();
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
            case Page.Calendar: BuildCalendar(body); break;
            case Page.Search: BuildSearch(body); break;
            case Page.Changelog: BuildChangelog(body); break; // SettingsWindow.Changelog.cs
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
        Page.Calendar => "캘린더",
        Page.Search => "검색",
        Page.Changelog => "변경 내역",
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
        panel.Children.Add(SidebarItem(Page.Calendar, "\uE787", Color.FromRgb(0xFF, 0x3B, 0x30)));
        panel.Children.Add(SidebarItem(Page.Search, "\uE721", Color.FromRgb(0xFF, 0x9F, 0x0A)));
        panel.Children.Add(SidebarItem(Page.About, "\uE946", Color.FromRgb(0x34, 0xC7, 0x59)));
        panel.Children.Add(SidebarItem(Page.Changelog, "\uE81C", Color.FromRgb(0xAF, 0x52, 0xDE)));
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
                Toggle(s.HideWindowsTaskbar, on => Commit(() => _services.Settings.Current.SetHideWindowsTaskbar(on))))));

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
                new[] { (DockEdge.Left, "왼쪽"), (DockEdge.Bottom, "아래"), (DockEdge.Right, "오른쪽"), (DockEdge.Top, "위") },
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
                v => Commit(() => D().MultiWindowClick = v))),
            Row("앱 켤 때", "창이 뜰 때까지 아이콘이 튀거나 실행 점이 깜빡입니다. 알림은 튀지 않습니다.", Segmented(d.LaunchAnimation,
                new[] { (LaunchAnimation.Bounce, "통통 튀기"), (LaunchAnimation.Blink, "점 깜빡이기") },
                v => Commit(() => D().LaunchAnimation = v)))));

        body.Children.Add(Group(
            Row("작업 표시줄 고정 앱 가져오기",
                _taskbarImportResult ?? "윈도우 작업 표시줄에 고정한 앱 중 독에 없는 것을 작업 표시줄 순서대로 끝에 추가합니다.",
                ActionButton("가져오기", () => Commit(() =>
                {
                    var s = _services.Settings.Current;
                    int n = TaskbarPins.AddMissingTo(s, _services.Settings);
                    s.TaskbarPinsImported = true;
                    _taskbarImportResult = n > 0 ? $"{n}개 추가했어요." : "새로 추가할 앱이 없어요.";
                }, rebuild: true)))));
    }

    /// <summary>"작업 표시줄 고정 앱 가져오기" 마지막 결과 (설명 줄에 표시).</summary>
    private string? _taskbarImportResult;

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

    /// <summary>연결된 모니터: 장치 이름(설정에 저장하는 값) + 표시 이름. 표시 번호 순.</summary>
    private static List<(string Device, string Label)> ReadMonitors()
    {
        try
        {
            return Monitors.GetAll()
                .OrderBy(m => m.Number)
                .Select(m => (m.DeviceName, m.DisplayName))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("모니터 목록 읽기 실패", ex);
            return new List<(string, string)>();
        }
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
            Row("최대화 창이 상단바를 가리지 않게", "상단바 높이만큼 화면 공간을 비워 둡니다. 끄면 최대화한 창이 상단바 아래까지 덮습니다.",
                Toggle(t.ReserveSpace, on => Commit(() => T().ReserveSpace = on))),
            Row("창이 상단바에 가려지지 않게 아래로 내리기", "캡처 도구처럼 화면 맨 위에 뜨거나 상단바 밑으로 끌어 놓은 창을 상단바 바로 아래로 옮깁니다. 위 항목이 켜져 있을 때만 동작합니다.",
                Toggle(t.KeepWindowsBelowBar, on => Commit(() => T().KeepWindowsBelowBar = on))),
            Row("높이", null, ValueSlider(t.Height, 20, 40, 1, v => $"{v:0}", v => T().Height = v)),
            Row("글자 크기", null, ValueSlider(t.FontSize, 11, 16, 0.5, v => $"{v:0.#}", v => T().FontSize = v)),
            Row("색", null, ColorModeDropdown(t.ColorMode))));

        body.Children.Add(SectionTitle("표시할 항목"));
        body.Children.Add(Group(
            Row("로고", "끄면 이 창은 트레이 아이콘이나 독 오른쪽 클릭 메뉴에서 엽니다.",
                Toggle(t.ShowLogo, on => Commit(() => T().ShowLogo = on))),
            Row("앱 이름", null, Toggle(t.ShowActiveAppName, on => Commit(() => T().ShowActiveAppName = on))),
            Row("앱 메뉴", "파일·편집·보기… (맥 메뉴 막대처럼)", Toggle(t.ShowAppMenus, on => Commit(() => T().ShowAppMenus = on))),
            Row("앱 창 안 메뉴 줄 숨기기 (실험)", "실험: 메모장·그림판 같은 앱의 창 안 메뉴 줄을 숨기고 상단바에서만 보이게 (옛날식 표준 메뉴 앱만 — 윈도우 11 새 메모장·그림판은 해당 없음)",
                Toggle(t.HideNativeMenuBars, on => Commit(() => T().HideNativeMenuBars = on))),
            Row("가상 데스크톱 버튼", null, Toggle(t.ShowDesktopButtons, on => Commit(() => T().ShowDesktopButtons = on))),
            Row("앱 트레이 아이콘", "작업 표시줄 대신 상단바에 다른 앱 트레이 아이콘을 보여 줍니다(작업 표시줄 숨기기를 켜면 자동으로 켜짐).",
                Toggle(t.ShowTrayIcons, on => Commit(() => T().SetShowTrayIconsByUser(on), rebuild: true))),
            Row("상단바 트레이 아이콘 최대 개수", "바에 둘 아이콘이 이보다 많으면 순서 뒤쪽부터 ⌃ 안으로 들어가요.",
                ValueSlider(t.TrayIconsVisibleCount, 1, 20, 1, v => $"{v:0}개", v => T().TrayIconsVisibleCount = (int)Math.Round(v))),
            Row("상태 아이콘", "Wi-Fi·블루투스·볼륨", Toggle(t.ShowStatusIcons, on => Commit(() => T().ShowStatusIcons = on))),
            Row("빠른 버튼", "검색·빠른 설정·알림 센터", Toggle(t.ShowQuickButtons, on => Commit(() => T().ShowQuickButtons = on))),
            Row("한/영", null, Toggle(t.ShowImeToggle, on => Commit(() => T().ShowImeToggle = on))),
            Row("네트워크 속도", null, Toggle(t.ShowNetworkSpeed, on => Commit(() => T().ShowNetworkSpeed = on)))));

        if (t.ShowTrayIcons) AddTrayArrange(body); // SettingsWindow.Tray.cs

        body.Children.Add(SectionTitle("알림"));
        body.Children.Add(Group(
            Row("알림 배너", "윈도우 알림이 오면 상단바 아래 오른쪽에 맥처럼 표시합니다.",
                Toggle(_services.Settings.Current.Notifications.ShowNotificationBanners,
                    on => Commit(() => _services.Settings.Current.Notifications.ShowNotificationBanners = on))),
            Row("윈도우 기본 알림 팝업 숨기기", "몽독 배너로 보여 준 알림만 숨깁니다. 알람·전화처럼 직접 눌러야 하는 알림은 그대로 뜹니다. 알림 기록은 그대로 남습니다.",
                Toggle(_services.Settings.Current.Notifications.HideWindowsToastPopups,
                    on => Commit(() => _services.Settings.Current.Notifications.HideWindowsToastPopups = on))),
            Row("알림 소리", "윈도우 알림 소리를 바꿉니다. 모든 앱 알림에 같이 적용되고, 몽독을 꺼도 유지됩니다. 처음 한 번은 다시 로그인한 뒤부터 적용돼요. ‘원래대로’로 되돌릴 수 있어요.",
                NotificationSoundDropdown())));
    }

    // ───────────────────────── 페이지: 캘린더 ─────────────────────────

    private const string GoogleCalendarSettingsUrl = "https://calendar.google.com/calendar/r/settings";
    private const string OutlookCalendarSettingsUrl = "https://outlook.live.com/calendar/0/options/calendar/SharedCalendars";
    private const string NaverCalendarUrl = "https://calendar.naver.com/";

    /// <summary>"클립보드에서 추가" 결과 문구 (페이지를 다시 그려도 남게 창에 보관).</summary>
    private string? _calendarMessage;
    private bool _calendarAdding;
    /// <summary>캘린더 페이지가 열려 있는 동안 "10분 전 동기화" 를 갱신 (1분마다).</summary>
    private DispatcherTimer? _relativeTimer;

    /// <summary>
    /// 캘린더: 구독 목록(색 점·이름·상태·켜기·새로고침·삭제) + 클립보드에서 추가(클릭만으로) + 새로고침 주기 + 캘린더 앱 + 주소 얻는 법.
    /// 주소는 화면에 보이지 않는다 (비밀 링크 — 호스트 이름만).
    /// </summary>
    private void BuildCalendar(Panel body)
    {
        var cals = _services.Calendars;
        var feeds = cals.Feeds;

        if (_relativeTimer == null)
        {
            _relativeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
            _relativeTimer.Tick += (_, _) => { if (_page == Page.Calendar) QueueRebuild(); else _relativeTimer.Stop(); };
        }
        _relativeTimer.Start();

        body.Children.Add(new TextBlock
        {
            Text = "Google·Outlook 같은 캘린더의 iCal(ICS) 주소를 연결하면 시계 달력에 일정이 보여요. 주소는 이 PC 의 내 계정에서만 풀리도록 암호화해 저장합니다.",
            Foreground = _p.SubText,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 0, 12),
        });

        body.Children.Add(SectionTitle("구독 캘린더"));
        var rows = new List<UIElement>();
        foreach (var feed in feeds) rows.Add(CalendarFeedRow(feed, cals.GetStatus(feed.Id)));
        if (rows.Count == 0)
        {
            rows.Add(new TextBlock
            {
                Text = "아직 연결한 캘린더가 없어요. 아래 방법으로 iCal 주소를 복사한 뒤 \"클립보드에서 추가\" 를 누르세요.",
                Foreground = _p.SubText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 12, 14, 12),
            });
        }
        // 추가 버튼 + 결과 문구
        var addRow = new Grid { MinHeight = 44, Margin = new Thickness(14, 7, 14, 7) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition());
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var message = new TextBlock
        {
            Text = _calendarAdding ? "가져오는 중…" : _calendarMessage ?? "iCal 주소를 복사한 뒤 누르세요. (https:// 또는 webcal://)",
            Foreground = _p.SubText,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        addRow.Children.Add(message);
        var addButton = ActionButton(_calendarAdding ? "가져오는 중…" : "클립보드에서 추가", AddCalendarFromClipboard);
        addButton.IsEnabled = !_calendarAdding;
        addButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(addButton);
        rows.Add(addRow);
        body.Children.Add(Group(rows.ToArray()));

        body.Children.Add(Group(
            Row("새로고침 주기", "몽독을 일시 정지하면 멈추고, 절전에서 깨어나거나 네트워크가 다시 연결되면 한 번 새로고칩니다.", RefreshIntervalDropdown()),
            Row("캘린더 앱", "시계 달력에서 날짜를 두 번 누르거나 '캘린더에서 열기'·일정을 누르면 엽니다. 웹은 기본 브라우저로 그 날짜를 엽니다.",
                CalendarAppDropdown())));

        body.Children.Add(SectionTitle("iCal 주소 얻는 법"));
        body.Children.Add(Group(
            Row("Google 캘린더", "설정 → 왼쪽 '내 캘린더의 설정'에서 캘린더 선택 → 캘린더 통합 → 'iCal 형식의 비공개 주소' 복사",
                ActionButton("설정 열기", () => _services.Launcher.OpenFile(GoogleCalendarSettingsUrl))),
            Row("Outlook.com", "설정 → 캘린더 → 공유 캘린더 → 캘린더 게시 → 캘린더와 '모든 세부 정보 보기' 선택 → 게시 → ICS 링크 복사",
                ActionButton("설정 열기", () => _services.Launcher.OpenFile(OutlookCalendarSettingsUrl))),
            Row("네이버 캘린더", "네이버 캘린더는 구독용 iCal 주소를 제공하지 않아요 (.ics 파일 내보내기·CalDAV 만). 네이버 웍스는 캘린더 설정 → 외부 공개 → '캘린더 공개' → iCal URL 복사.",
                ActionButton("열기", () => _services.Launcher.OpenFile(NaverCalendarUrl)))));
    }

    /// <summary>구독 한 줄: [색 점] 이름 + 상태 · 켜기 · 새로고침 · 삭제.</summary>
    private Grid CalendarFeedRow(CalendarFeed feed, CalendarFeedStatus status)
    {
        var grid = new Grid { MinHeight = 44, Margin = new Thickness(10, 6, 14, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 색 점: 누르면 8색 팔레트
        var swatch = new Ellipse { Width = 14, Height = 14, Fill = Converters.BrushParser.Parse(feed.Color, Colors.DodgerBlue) };
        var colorButton = new Button
        {
            Style = (Style)FindResource("CardButton"),
            Background = Brushes.Transparent,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = swatch,
            ToolTip = "색 바꾸기",
        };
        colorButton.Click += (_, _) =>
        {
            UiTheme.Apply(_services.Settings.Current);
            var menu = new ContextMenu { PlacementTarget = colorButton, Placement = PlacementMode.Bottom, HorizontalOffset = -10 };
            for (int i = 0; i < CalendarFeed.Palette.Length; i++)
            {
                string hex = CalendarFeed.Palette[i];
                var header = new StackPanel { Orientation = Orientation.Horizontal };
                header.Children.Add(new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = Converters.BrushParser.Parse(hex, Colors.DodgerBlue),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                header.Children.Add(new TextBlock { Text = CalendarFeed.PaletteNames[i], VerticalAlignment = VerticalAlignment.Center });
                var item = new MenuItem
                {
                    Header = header,
                    IsCheckable = false,
                    IsChecked = string.Equals(hex, feed.Color, StringComparison.OrdinalIgnoreCase),
                };
                item.Click += (_, _) => _services.Calendars.Update(feed.Id, f => f.Color = hex);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        grid.Children.Add(colorButton);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        texts.Children.Add(new TextBlock
        {
            Text = feed.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = feed.Enabled ? _p.Text : _p.SubText,
        });
        string host = Uri.TryCreate(feed.Url, UriKind.Absolute, out var u) ? u.Host : "";
        bool failed = status.Error != null;
        texts.Children.Add(new TextBlock
        {
            Text = FeedStatusText(feed, status) + (host.Length > 0 ? $" · {host}" : ""),
            FontSize = 11.5,
            Foreground = failed ? _p.HolidayText : _p.SubText,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var toggle = Toggle(feed.Enabled, on => _services.Calendars.Update(feed.Id, f => f.Enabled = on));
        toggle.ToolTip = "달력에 표시";
        toggle.Margin = new Thickness(0, 0, 8, 0);
        controls.Children.Add(toggle);
        var refresh = ActionButton(status.Busy ? "가져오는 중…" : "새로고침", () => _services.Calendars.Refresh(feed.Id));
        refresh.IsEnabled = !status.Busy;
        refresh.Margin = new Thickness(0, 0, 6, 0);
        controls.Children.Add(refresh);
        controls.Children.Add(ActionButton("삭제", async () =>
        {
            bool ok = await ConfirmCardWindow.AskAsync(_services, $"'{feed.Name}' 구독을 삭제할까요?", "달력에서 이 캘린더의 일정이 사라집니다. 원래 캘린더는 그대로예요.", "삭제");
            if (ok) _services.Calendars.Remove(feed.Id);
        }));
        Grid.SetColumn(controls, 2);
        grid.Children.Add(controls);
        return grid;
    }

    /// <summary>"10분 전 동기화" / "가져오는 중…" / 오류 문구 / "아직 동기화 안 됨".</summary>
    private static string FeedStatusText(CalendarFeed feed, CalendarFeedStatus status)
    {
        if (status.Busy) return "가져오는 중…";
        if (status.Error != null)
            return status.LastSync is DateTime t ? $"{status.Error} (마지막 동기화 {RelativeTime(t)})" : status.Error;
        if (status.LastSync is DateTime last)
            return $"{RelativeTime(last)} 동기화 · 일정 {status.EventCount}개" + (feed.Enabled ? "" : " · 꺼짐");
        return feed.Enabled ? "아직 동기화 안 됨" : "꺼짐";
    }

    private static string RelativeTime(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "방금";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}분 전";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}시간 전";
        return $"{(int)d.TotalDays}일 전";
    }

    /// <summary>클립보드 텍스트가 캘린더 주소면 바로 추가 (키보드 입력 없이). 결과는 추가 버튼 옆 문구로.</summary>
    private async void AddCalendarFromClipboard()
    {
        if (_calendarAdding) return;
        string? text = null;
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (Exception ex) { Log.Warn($"클립보드 읽기 실패: {ex.GetType().Name}"); }
        if (!CalendarFeedService.TryNormalizeUrl(text, out _))
        {
            _calendarMessage = "클립보드에 캘린더 주소가 없어요. 아래 방법으로 iCal 주소(https:// 또는 webcal://)를 복사한 뒤 다시 누르세요.";
            QueueRebuild();
            return;
        }
        _calendarAdding = true;
        _calendarMessage = null;
        QueueRebuild();
        try
        {
            var result = await _services.Calendars.AddAsync(text!);
            _calendarMessage = result.Message;
        }
        catch (Exception ex)
        {
            Log.Error($"캘린더 추가 실패: {ex.GetType().Name}");
            _calendarMessage = "캘린더를 추가하지 못했어요.";
        }
        finally
        {
            _calendarAdding = false;
        }
        if (!_closed) QueueRebuild();
    }

    private UIElement RefreshIntervalDropdown()
    {
        int[] options = { 5, 15, 30, 60 };
        int cur = _services.Settings.Current.Calendar?.RefreshMinutes ?? 15;
        string label = options.Contains(cur) ? $"{cur}분마다" : $"{cur}분마다 (사용자 지정)";
        return Dropdown(label, () =>
        {
            int now = _services.Settings.Current.Calendar?.RefreshMinutes ?? 15;
            return options.Select(m => ($"{m}분마다", m == now, (Action)(() => Commit(() =>
            {
                var s = _services.Settings.Current;
                s.Calendar ??= new CalendarSettings();
                s.Calendar.RefreshMinutes = m;
            }, rebuild: true))));
        });
    }

    // ───────────────────────── 페이지: 검색 ─────────────────────────

    private void BuildSearch(Panel body)
    {
        var t = _services.Settings.Current.TopBar;
        TopBarSettings T() => _services.Settings.Current.TopBar;
        var s = _services.Settings.Current.Search;
        SearchSettings S() => _services.Settings.Current.Search; // 외부 다시 로드로 객체가 바뀌어도 최신 것에 씀

        body.Children.Add(Group(
            Row("검색 버튼", null, Segmented(t.SearchMode,
                new[] { (SearchMode.Spotlight, "몽독 검색 (화면 가운데)"), (SearchMode.Windows, "윈도우 검색") },
                v => Commit(() => T().SearchMode = v))),
            Row("검색 단축키", "기본값 '자동': 입력 언어가 1개면 Win+Space, 여러 개면 언어 전환과 겹치지 않게 Alt+Space 를 씁니다.",
                SpotlightHotkeyDropdown(t.SpotlightHotkey))));

        body.Children.Add(SectionTitle("검색 결과에 표시할 항목"));
        body.Children.Add(Group(
            Row("계산기", "수식(예 12*3+4)을 입력하면 맨 위에 결과. Enter 로 복사합니다.",
                Toggle(s.Calculator, on => Commit(() => S().Calculator = on))),
            Row("응용 프로그램", null, Toggle(s.Apps, on => Commit(() => S().Apps = on))),
            Row("시스템 설정", "블루투스·디스플레이·소리 같은 윈도우 설정 페이지", Toggle(s.Settings, on => Commit(() => S().Settings = on)))));

        bool indexing = WindowsIndexSearch.IsIndexServiceRunning();
        var fileRows = new List<UIElement>();
        if (!indexing)
            fileRows.Add(Row("윈도우 검색 색인이 꺼져 있어요",
                "색인 서비스(Windows Search)가 실행 중이 아니어서 파일·폴더를 찾지 못합니다.",
                ActionButton("색인 옵션 열기", OpenIndexingOptions)));
        fileRows.Add(Row("폴더", null, Toggle(s.Folders, on => Commit(() => S().Folders = on))));
        fileRows.Add(Row("문서", null, Toggle(s.Documents, on => Commit(() => S().Documents = on))));
        fileRows.Add(Row("사진·동영상·음악", null, Toggle(s.Media, on => Commit(() => S().Media = on))));
        fileRows.Add(Row("기타 파일", null, Toggle(s.OtherFiles, on => Commit(() => S().OtherFiles = on))));
        body.Children.Add(Group(fileRows.ToArray()));

        body.Children.Add(Group(
            Row("Windows 검색에서 찾기", "결과 맨 아래에 같은 검색어를 윈도우 검색으로 넘기는 항목",
                Toggle(s.WindowsSearch, on => Commit(() => S().WindowsSearch = on))),
            Row("웹에서 검색", "결과 맨 아래에 웹 검색 항목", Toggle(s.WebSearch, on => Commit(() => S().WebSearch = on))),
            Row("웹 검색 엔진", null, Segmented(s.WebSearchEngine,
                new[] { (WebSearchEngine.Google, "Google"), (WebSearchEngine.Naver, "네이버"), (WebSearchEngine.Bing, "Bing") },
                v => Commit(() => S().WebSearchEngine = v))),
            Row("카테고리별 최대 개수", null,
                ValueSlider(s.MaxPerCategory, 3, 10, 1, v => $"{v:0}개", v => S().MaxPerCategory = (int)Math.Round(v)))));

        // 파일 검색 위치: 각 행 오른쪽 "빼기", 맨 아래 "폴더 추가…" (폴더 고르기 창 — 마우스만으로)
        body.Children.Add(SectionTitle("파일 검색 위치"));
        var folderRows = new List<UIElement>();
        var folders = (s.FileSearchFolders ?? new List<string>()).ToList();
        foreach (string folder in folders)
        {
            string f = folder;
            folderRows.Add(Row(Path.GetFileName(f.TrimEnd('\\')) is { Length: > 0 } name ? name : f, f,
                ActionButton("빼기", () => Commit(() => S().FileSearchFolders.RemoveAll(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)), rebuild: true))));
        }
        if (folders.Count == 0)
            folderRows.Add(Row("위치 없음", "추가한 폴더가 없으면 파일·폴더를 찾지 않습니다.",
                ActionButton("기본값 (사용자 폴더)", () => Commit(() => S().FileSearchFolders = new List<string> { SearchSettings.DefaultFileSearchFolder }, rebuild: true))));
        folderRows.Add(Row("폴더 추가…", "하위 폴더까지 찾습니다. 윈도우 검색 색인에 포함된 위치만 결과에 나옵니다.",
            ActionButton("추가", AddSearchFolder)));
        folderRows.Add(Row("색인 옵션", "윈도우 검색이 색인할 위치를 바꿉니다 (제어판).", ActionButton("열기", OpenIndexingOptions)));
        body.Children.Add(Group(folderRows.ToArray()));
    }

    private void AddSearchFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "검색할 폴더 고르기",
            Multiselect = true,
            InitialDirectory = SearchSettings.DefaultFileSearchFolder,
        };
        if (dlg.ShowDialog(this) != true) return;
        Commit(() =>
        {
            var list = _services.Settings.Current.Search.FileSearchFolders ??= new List<string>();
            foreach (string folder in dlg.FolderNames)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                if (!list.Any(x => string.Equals(x.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    list.Add(folder);
            }
        }, rebuild: true);
    }

    /// <summary>제어판 "색인 옵션" (색인 위치 추가·색인 다시 만들기).</summary>
    private static void OpenIndexingOptions()
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "srchadmin.dll") { UseShellExecute = true })?.Dispose();

    /// <summary>알림 소리: 고르면 한 번 미리 들려 주고 바로 적용 (NotificationSoundService).</summary>
    private UIElement NotificationSoundDropdown()
    {
        var n = _services.Settings.Current.Notifications;
        string? effective = NotificationSoundService.GetEffectiveSound(n);
        string? winDefault = NotificationSoundService.GetWindowsDefault();
        bool managed = NotificationSoundService.IsManagedActive();

        bool Is(string path) => effective is not null &&
            (path.Length == 0 ? effective.Length == 0 : effective.Length > 0 && NotificationSoundService.SamePath(effective, path));

        string label = effective is null ? "알 수 없음"
            : !managed && winDefault is not null && Is(winDefault) ? "윈도우 기본값"
            : NotificationSoundService.Describe(effective);

        void Pick(string path)
        {
            NotificationSoundService.Preview(path);
            Commit(() =>
            {
                if (!NotificationSoundService.Apply(_services.Settings.Current.Notifications, path))
                    MessageBox.Show(this, "알림 소리를 바꾸지 못했습니다. 로그를 확인해 주세요.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }, rebuild: true);
        }

        return Dropdown(label, () =>
        {
            var items = new List<(string, bool, Action)>();
            var nn = _services.Settings.Current.Notifications;
            if (nn.OriginalSound is not null && nn.Sound is not null)
                items.Add(("원래대로", false, () =>
                {
                    NotificationSoundService.Preview(Environment.ExpandEnvironmentVariables(nn.OriginalSound));
                    Commit(() => NotificationSoundService.Restore(_services.Settings.Current.Notifications), rebuild: true);
                }));
            if (winDefault is not null)
                items.Add(("윈도우 기본값", !managed && Is(winDefault), () => Pick(winDefault)));
            items.Add(("무음", Is(""), () => Pick("")));
            foreach (var o in NotificationSoundService.GetCandidates())
                items.Add((o.Label, managed && Is(o.Path), () => Pick(o.Path)));
            items.Add(("직접 고르기…", false, () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "알림 소리 고르기",
                    Filter = "소리 파일 (*.wav)|*.wav",
                    InitialDirectory = NotificationSoundService.MediaDirectory,
                };
                if (dlg.ShowDialog(this) == true) Pick(dlg.FileName);
            }));
            return items;
        });
    }

    /// <summary>
    /// 캘린더 앱: 웹 3개는 항상, 데스크톱 앱은 펼칠 때 설치 감지 — 없으면 회색(선택 불가) + 오른쪽 "설치" 링크(스토어 ID 를 아는 것만).
    /// "메일 및 일정" 은 지원 종료라 감지될 때만 보인다.
    /// </summary>
    private UIElement CalendarAppDropdown()
    {
        var current = _services.Settings.Current.TopBar.CalendarApp;
        return DropdownMenu(CalendarApps.DisplayName(current), menu =>
        {
            var cur = _services.Settings.Current.TopBar.CalendarApp;
            foreach (var app in CalendarApps.All)
            {
                bool installed = CalendarApps.IsInstalled(app);
                if (app == CalendarApp.WindowsCalendar && !installed) continue;
                string label = CalendarApps.DisplayName(app);
                if (installed)
                {
                    menu.Items.Add(DockMenus.Item(label, () => Commit(() => _services.Settings.Current.TopBar.CalendarApp = app, rebuild: true),
                        isChecked: app == cur));
                    continue;
                }
                // 설치 안 됨: 이름 회색 + "설치 안 됨" (+ 스토어 링크). 항목 자체를 눌러도 아무 일 없음
                var header = new DockPanel { LastChildFill = true, MinWidth = 200 };
                string? install = CalendarApps.InstallUri(app);
                if (install != null)
                {
                    var link = new TextBlock
                    {
                        Text = "설치",
                        Foreground = _p.Accent,
                        Cursor = System.Windows.Input.Cursors.Hand,
                        Margin = new Thickness(16, 0, 0, 0),
                        ToolTip = "Microsoft Store 에서 설치",
                    };
                    link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
                    link.MouseLeave += (_, _) => link.TextDecorations = null;
                    link.PreviewMouseLeftButtonUp += (_, ev) =>
                    {
                        ev.Handled = true;
                        menu.IsOpen = false;
                        try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(install) { UseShellExecute = true })) { } }
                        catch (Exception ex) { Log.Error("스토어 열기 실패", ex); }
                    };
                    DockPanel.SetDock(link, Dock.Right);
                    header.Children.Add(link);
                }
                header.Children.Add(new TextBlock { Text = label + " (설치 안 됨)", Foreground = _p.Disabled });
                menu.Items.Add(new MenuItem { Header = header, StaysOpenOnClick = true });
            }
        });
    }

    private UIElement SpotlightHotkeyDropdown(SpotlightHotkey current)
    {
        var options = new[]
        {
            (SpotlightHotkey.Auto, "자동 (언어 1개면 Win+Space, 여러 개면 Alt+Space)"),
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
        head.Children.Add(AboutCoachLinks()); // SettingsWindow.Changelog.cs: 이 버전 둘러보기 ▶ · 변경 내역 보기 ›
        body.Children.Add(head);

        BuildUpdateSection(body); // SettingsWindow.Update.cs
        body.Children.Add(SectionTitle("정보"));

        string folder = Path.GetDirectoryName(_services.Settings.SettingsPath) ?? AppInfo.DataDirectory;
        body.Children.Add(Group(
            Row("GitHub", GitHubUrl, ActionButton("열기", () => _services.Launcher.OpenFile(GitHubUrl))),
            Row("설정 폴더", folder, ActionButton("폴더 열기", () => _services.Launcher.OpenFile(folder))),
            Row("설정 파일", "settings.json (직접 편집하면 저장 즉시 반영)",
                ActionButton("파일 열기", () => _services.Launcher.OpenFile(_services.Settings.SettingsPath)))));

        // 코치마크: 처음 설치했을 때의 기능 둘러보기 (버전별 둘러보기는 위 링크와 변경 내역 페이지)
        body.Children.Add(SectionTitle("안내"));
        body.Children.Add(Group(
            Row("처음 사용 둘러보기", "처음 설치했을 때의 기능 둘러보기를 상단바·독 위에서 다시 봐요.",
                ActionButton("둘러보기 ▶", () => PlayCoach(CoachMarks.BuildTourPages, isTour: true)))));
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
        => DropdownMenu(label, menu =>
        {
            foreach (var (text, isChecked, pick) in items())
                menu.Items.Add(DockMenus.Item(text, pick, isChecked: isChecked));
        });

    /// <summary>드롭다운 (메뉴 항목을 직접 채움 — 회색 항목·링크 같은 특수 항목용).</summary>
    private Button DropdownMenu(string label, Action<ContextMenu> fill)
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
            fill(menu);
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
