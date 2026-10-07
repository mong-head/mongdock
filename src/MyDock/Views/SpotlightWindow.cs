using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyDock.Models;
using MyDock.Native;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 맥 Spotlight 스타일 검색창. 상단바 검색 버튼(SearchMode=Spotlight)으로 열고, 다시 누르면 닫는다.
/// 마우스가 있는 모니터의 가로 가운데, 위에서 약 25% 지점. 시작 메뉴의 모든 앱(shell:AppsFolder)을 검색해 실행.
/// 상단바·독과 달리 키보드 입력을 받아야 하므로 포커스를 가져오는(활성화되는) 창이다 — 대신 Alt+Tab·작업 표시줄에는 숨김(WS_EX_TOOLWINDOW).
/// 비활성화(바깥 클릭·다른 창 활성화)되면 닫힌다. 모든 동작은 마우스 클릭만으로도 가능.
/// </summary>
internal sealed class SpotlightWindow : Window
{
    private const string IconFontName = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly FontFamily IconFont = new(IconFontName);

    private const double CardWidth = 680;
    private const double ShadowMargin = 24;
    private const double RowHeight = 44;
    private const double AppIconSize = 32;
    /// <summary>카드 윗변 위치 = 모니터 높이의 이 비율.</summary>
    private const double TopRatio = 0.25;

    private static SpotlightWindow? _current;
    /// <summary>방금 비활성화로 닫혔으면 같은 클릭의 토글이 다시 열지 않도록.</summary>
    private static long _closedAt;
    private static IReadOnlyList<SpotlightApp> _apps = Array.Empty<SpotlightApp>();
    /// <summary>앱 아이콘 캐시 (파싱 이름 → Frozen 이미지). 창을 다시 열 때 바로 보이게 정적.</summary>
    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly IconStyle _iconStyle;
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly Border _divider;
    private readonly StackPanel _list;
    private readonly List<Result> _results = new();
    private readonly bool? _hangulAtOpen;
    private int _selected;
    private int _generation;
    private bool _closing;
    private long _openedAt;
    private IntPtr _hwnd;

    private enum ResultKind { App, WindowsSearch, Web }

    private sealed class Result
    {
        public ResultKind Kind;
        public SpotlightApp? App;
        public Border Row = null!;
        public TextBlock Label = null!;
        public TextBlock? Glyph;
    }

    /// <summary>상단바 검색 버튼: 열려 있으면 닫고, 아니면 연다.</summary>
    public static void Toggle(AppServices services)
    {
        if (_current is not null)
        {
            _current.CloseSafe();
            return;
        }
        // 열린 창을 닫은 바로 그 클릭(비활성화 → 닫힘 → 버튼 Click)이면 다시 열지 않음
        if (Environment.TickCount64 - _closedAt < 300) return;
        var w = new SpotlightWindow(services);
        _current = w;
        w.ShowOnCursorMonitor();
    }

    private SpotlightWindow(AppServices services)
    {
        _services = services;
        var settings = services.Settings.Current;
        _p = UiTheme.Palette(settings);
        _iconStyle = settings.Dock.IconStyle;
        // 열기 전 포그라운드 앱의 한/영 상태 (열린 뒤 입력칸에 그대로 적용)
        try { _hangulAtOpen = services.Ime.IsHangulMode(); } catch { _hangulAtOpen = null; }

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = CardWidth + ShadowMargin * 2;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        Title = "mongdock Spotlight";
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        Foreground = _p.Text;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        // ── 검색 줄: 돋보기 + 큰 입력칸 ──
        var magnifier = new Path
        {
            Data = BarIcons.Search,
            Fill = _p.SubText,
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            LayoutTransform = new ScaleTransform(1.25, 1.25),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        };
        _box = new TextBox
        {
            FontSize = 24,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _p.Text,
            CaretBrush = _p.Text,
            SelectionBrush = _p.Accent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
        };
        _box.SetResourceReference(FontFamilyProperty, UiFonts.Key);
        InputMethod.SetIsInputMethodEnabled(_box, true);
        _box.TextChanged += (_, _) => OnQueryChanged();
        _placeholder = new TextBlock
        {
            Text = "Spotlight 검색",
            FontSize = 24,
            Foreground = _p.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            IsHitTestVisible = false,
        };
        var boxHost = new Grid();
        boxHost.Children.Add(_placeholder);
        boxHost.Children.Add(_box);
        var searchRow = new DockPanel { Height = 58, Margin = new Thickness(20, 0, 20, 0), LastChildFill = true };
        DockPanel.SetDock(magnifier, Dock.Left);
        searchRow.Children.Add(magnifier);
        searchRow.Children.Add(boxHost);

        _divider = new Border { Height = 1, Background = _p.Divider, Visibility = Visibility.Collapsed };
        _list = new StackPanel { Margin = new Thickness(8, 6, 8, 8), Visibility = Visibility.Collapsed };

        var content = new StackPanel();
        content.Children.Add(searchRow);
        content.Children.Add(_divider);
        content.Children.Add(_list);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(0.75),
            Background = _p.CardBackground,
            BorderBrush = _p.CardBorder,
            Child = content,
        };
        // 그림자는 뒤의 별도 Border 에만 (글자 흐려짐 방지)
        var root = new Grid { Margin = new Thickness(ShadowMargin, 4, ShadowMargin, ShadowMargin + 8) };
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = _p.CardBackground,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = _p.ShadowOpacity },
        });
        root.Children.Add(card);
        Content = root;

        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => OnSourceInitialized();
        Activated += (_, _) => FocusBox();
        Deactivated += (_, _) => CloseSafe();
        Closed += (_, _) =>
        {
            _closing = true;
            _generation++;
            _services.Windows.WindowActivated -= OnOtherWindowActivated;
            if (ReferenceEquals(_current, this)) _current = null;
            _closedAt = Environment.TickCount64;
        };
    }

    // ───────────────────────── 표시 / 위치 / 활성화 ─────────────────────────

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            // Alt+Tab 에서 숨김 (활성화는 가능해야 하므로 NOACTIVATE 는 넣지 않음)
            long ex = User32.GetWindowLong(_hwnd, User32.GWL_EXSTYLE);
            long nex = (ex | User32.WS_EX_TOOLWINDOW) & ~User32.WS_EX_APPWINDOW;
            if (nex != ex) User32.SetWindowLong(_hwnd, User32.GWL_EXSTYLE, nex);
        }
        catch (Exception ex)
        {
            Log.Warn($"Spotlight 창 스타일 설정 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 마우스가 있는 모니터(= 방금 누른 상단바의 모니터) 가로 가운데, 위에서 25% 지점에 표시.
    /// 모니터마다 배율이 달라도 어긋나지 않게 위치는 물리 픽셀(SetWindowPos)로 맞춘다.
    /// </summary>
    private void ShowOnCursorMonitor()
    {
        IntPtr monitor = IntPtr.Zero;
        RECT bounds = default;
        double scale = 1.0;
        try
        {
            if (DesktopApi.GetCursorPos(out POINT pt)) monitor = DesktopApi.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor == IntPtr.Zero) monitor = DesktopApi.PrimaryMonitor;
            if (!DesktopApi.TryGetMonitorRects(monitor, out bounds, out _)) monitor = IntPtr.Zero;
            scale = DesktopApi.GetMonitorScale(monitor);
        }
        catch (Exception ex)
        {
            Log.Warn($"Spotlight 모니터 계산 실패: {ex.Message}");
            monitor = IntPtr.Zero;
        }

        if (monitor == IntPtr.Zero)
        {
            // 모니터를 모르면 주 모니터 기준 DIP 로
            var screen = _services.DesktopWindows.GetPrimaryScreenBounds();
            Left = Math.Round(screen.Left + (screen.Width - Width) / 2);
            Top = Math.Round(screen.Top + screen.Height * TopRatio - 4);
            Show();
        }
        else
        {
            // 핸들을 먼저 만들어 대상 모니터로 옮긴 뒤(배율 변경 처리) 표시, 표시 후 한 번 더 정확히 맞춤
            new WindowInteropHelper(this).EnsureHandle();
            PlaceOn(bounds, scale);
            Show();
            PlaceOn(bounds, scale);
        }
        _openedAt = Environment.TickCount64;
        _services.Windows.WindowActivated += OnOtherWindowActivated;

        ForceForeground();
        OnQueryChanged();
        LoadAppsAsync();
    }

    private void PlaceOn(RECT bounds, double scale)
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;
        int widthPx = (int)Math.Round(Width * scale);
        int x = bounds.Left + (bounds.Width - widthPx) / 2;
        int y = bounds.Top + (int)Math.Round(bounds.Height * TopRatio - 4 * scale);
        User32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 상단바는 NOACTIVATE 라 클릭해도 몽독이 포그라운드가 아니어서 Show 만으로는 활성화가 막힐 수 있다.
    /// AppLauncher.ActivateWindow(SetForegroundWindow → 빈 입력으로 포그라운드 잠금 해제 → 포그라운드 스레드에
    /// AttachThreadInput 후 재시도)를 재사용한다.
    /// </summary>
    private void ForceForeground()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
            if (_hwnd != IntPtr.Zero && User32.GetForegroundWindow() != _hwnd)
                AppLauncher.ActivateWindow(_hwnd);
            Activate();
        }
        catch (Exception ex)
        {
            Log.Warn($"Spotlight 활성화 실패: {ex.Message}");
        }
        FocusBox();
        // 활성화가 늦게 반영되는 경우를 위해 한 번 더
        Dispatcher.BeginInvoke(new Action(FocusBox), DispatcherPriority.Input);
    }

    private void FocusBox()
    {
        if (_closing) return;
        _box.Focus();
        Keyboard.Focus(_box);
        _box.CaretIndex = _box.Text.Length;
        ApplyImeState();
    }

    private bool _imeApplied;

    /// <summary>열기 전 앱이 한글 모드였으면 입력칸도 한글로 (영문이었으면 영문). 한 번만.</summary>
    private void ApplyImeState()
    {
        if (_imeApplied || _hangulAtOpen is null || !_box.IsKeyboardFocused) return;
        _imeApplied = true;
        try
        {
            InputMethod.Current.ImeState = InputMethodState.On;
            InputMethod.Current.ImeConversionMode = _hangulAtOpen == true
                ? ImeConversionModeValues.Native
                : ImeConversionModeValues.Alphanumeric;
        }
        catch (Exception ex)
        {
            Log.Warn($"Spotlight IME 상태 적용 실패: {ex.Message}");
        }
    }

    /// <summary>활성화 자체가 실패해 Deactivated 가 오지 않는 경우의 보조 닫기: 다른 창이 활성화되면 닫음.</summary>
    private void OnOtherWindowActivated(object? sender, IntPtr hwnd)
    {
        if (_closing || hwnd == IntPtr.Zero || hwnd == _hwnd) return;
        if (Environment.TickCount64 - _openedAt < 400) return; // 여는 동안의 포그라운드 변화
        if (User32.GetForegroundWindow() == _hwnd) return;
        CloseSafe();
    }

    private void CloseSafe()
    {
        if (_closing) return;
        _closing = true;
        // Deactivated 처리 중에 바로 Close 하면 예외가 날 수 있어 디스패처로 미룸
        Dispatcher.BeginInvoke(() =>
        {
            try { Close(); }
            catch (Exception ex) { Log.Error("Spotlight 닫기 실패", ex); }
        });
    }

    // ───────────────────────── 앱 목록 / 검색 ─────────────────────────

    /// <summary>shell:AppsFolder 열거는 처음에 수백 ms 걸릴 수 있어 백그라운드에서 (AppsFolder 가 60초 캐시).</summary>
    private async void LoadAppsAsync()
    {
        try
        {
            var apps = await Task.Run(() =>
                AppsFolder.Enumerate()
                    .Where(a => !string.IsNullOrWhiteSpace(a.DisplayName))
                    .Select(a => new SpotlightApp(a.ParsingName, a.DisplayName.Trim()))
                    .ToList());
            _apps = apps;
            if (!_closing) OnQueryChanged();
        }
        catch (Exception ex)
        {
            Log.Error("Spotlight 앱 목록 읽기 실패", ex);
        }
    }

    private void OnQueryChanged()
    {
        if (_closing) return;
        string q = _box.Text;
        _placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        IReadOnlyList<SpotlightApp> found;
        try { found = SpotlightMatcher.Search(_apps, q, SpotlightRecents.Items); }
        catch (Exception ex)
        {
            Log.Error("Spotlight 검색 실패", ex);
            found = Array.Empty<SpotlightApp>();
        }
        BuildRows(found, q.Trim());
    }

    private void BuildRows(IReadOnlyList<SpotlightApp> apps, string query)
    {
        int gen = ++_generation;
        _results.Clear();
        _list.Children.Clear();

        foreach (var app in apps)
            AddRow(new Result { Kind = ResultKind.App, App = app }, app.Name, null, gen);
        if (query.Length > 0)
        {
            if (apps.Count > 0) _list.Children.Add(new Border { Height = 1, Background = _p.Divider, Margin = new Thickness(10, 4, 10, 4) });
            AddRow(new Result { Kind = ResultKind.WindowsSearch }, $"Windows 검색에서 ‘{query}’ 찾기", "", gen);
            AddRow(new Result { Kind = ResultKind.Web }, $"웹에서 ‘{query}’ 검색", "", gen);
        }

        bool any = _results.Count > 0;
        _divider.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        Select(any ? 0 : -1);
    }

    private void AddRow(Result r, string text, string? glyph, int gen)
    {
        var iconHost = new Grid { Width = AppIconSize, Height = AppIconSize, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null)
        {
            r.Glyph = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFont,
                FontSize = 18,
                Foreground = _p.SubText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconHost.Children.Add(r.Glyph);
        }
        else if (r.App is not null)
        {
            var image = new Image { Width = AppIconSize, Height = AppIconSize, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            iconHost.Children.Add(image);
            if (IconCache.TryGetValue(r.App.ParsingName, out var cached)) image.Source = cached;
            else QueueIcon(r.App, image, gen);
        }

        r.Label = new TextBlock
        {
            Text = text,
            FontSize = 16,
            Foreground = _p.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(iconHost, Dock.Left);
        panel.Children.Add(iconHost);
        panel.Children.Add(r.Label);

        r.Row = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 12, 0),
            Background = Brushes.Transparent,
            Child = panel,
            Cursor = Cursors.Hand,
        };
        int index = _results.Count;
        // 마우스를 실제로 움직였을 때만 선택 이동 (키보드로 고른 항목이 커서 위치 때문에 바뀌지 않게)
        r.Row.MouseMove += (_, _) => { if (_selected != index) Select(index); };
        r.Row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Execute(index); };
        _results.Add(r);
        _list.Children.Add(r.Row);
    }

    /// <summary>아이콘은 목록을 먼저 그린 뒤 Background 우선순위로 하나씩 (입력·렌더링을 막지 않게). 맥 스타일 렌더링은 UI 스레드 필요.</summary>
    private void QueueIcon(SpotlightApp app, Image image, int gen)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || gen != _generation) return;
            if (!IconCache.TryGetValue(app.ParsingName, out var icon))
            {
                try
                {
                    icon = _services.Icons.GetIcon(new PinItem { Name = app.Name, Kind = PinKind.Aumid, Target = app.ParsingName }, _iconStyle);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Spotlight 아이콘 실패: {app.ParsingName} ({ex.Message})");
                    return;
                }
                if (IconCache.Count > 400) IconCache.Clear();
                IconCache[app.ParsingName] = icon;
            }
            image.Source = icon;
        }, DispatcherPriority.Background);
    }

    private void Select(int index)
    {
        _selected = index;
        for (int i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            bool on = i == index;
            r.Row.Background = on ? _p.Accent : Brushes.Transparent;
            r.Label.Foreground = on ? _p.AccentText : _p.Text;
            if (r.Glyph is not null) r.Glyph.Foreground = on ? _p.AccentText : _p.SubText;
        }
        if (index >= 0 && index < _results.Count) _results[index].Row.BringIntoView();
    }

    // ───────────────────────── 키보드 ─────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 한글 조합 중에는 Key 가 ImeProcessed 로 온다 → 실제 키로 판단 (조합 중 글자는 이미 Text 에 포함됨)
        Key key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseSafe();
                break;
            case Key.Down:
                e.Handled = true;
                if (_results.Count > 0) Select((_selected + 1) % _results.Count);
                break;
            case Key.Up:
                e.Handled = true;
                if (_results.Count > 0) Select(_selected <= 0 ? _results.Count - 1 : _selected - 1);
                break;
            case Key.Enter:
                e.Handled = true;
                // 조합 중 Enter: IME 가 글자를 확정한 뒤의 검색 결과로 실행
                if (e.Key == Key.ImeProcessed)
                    Dispatcher.BeginInvoke(() => Execute(_selected), DispatcherPriority.Input);
                else
                    Execute(_selected);
                break;
        }
    }

    // ───────────────────────── 실행 ─────────────────────────

    private void Execute(int index)
    {
        if (_closing || index < 0 || index >= _results.Count) return;
        var r = _results[index];
        string query = _box.Text.Trim();
        CloseSafe();
        try
        {
            switch (r.Kind)
            {
                case ResultKind.App when r.App is not null:
                    SpotlightRecents.Add(r.App.ParsingName);
                    // shell:AppsFolder\<파싱 이름> 실행 (AppLauncher 의 AUMID 경로 — 데스크톱 앱 항목도 동작)
                    _services.Launcher.Launch(new PinItem { Name = r.App.Name, Kind = PinKind.Aumid, Target = r.App.ParsingName });
                    break;
                case ResultKind.WindowsSearch:
                    _ = SendToWindowsSearchAsync(_services, query);
                    break;
                case ResultKind.Web:
                    OpenWebSearch(query);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Spotlight 실행 실패", ex);
        }
    }

    private static void OpenWebSearch(string query)
    {
        if (query.Length == 0) return;
        string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        Log.Info("Spotlight 웹 검색");
    }

    /// <summary>윈도우 검색 창 프로세스 (Win11 SearchHost, Win10 SearchApp/SearchUI, 시작 메뉴 통합 검색).</summary>
    private static readonly string[] SearchHosts = { "SearchHost", "SearchApp", "SearchUI", "StartMenuExperienceHost" };

    /// <summary>
    /// Win+S 로 윈도우 검색을 연 뒤, 검색 창이 실제로 포그라운드가 된 것을 확인하고 나서 검색어를 유니코드 입력으로 보낸다.
    /// (search-ms: 는 탐색기 파일 검색이라 앱 검색에 맞지 않고, 윈도우 검색에 검색어를 넘기는 공식 URI 는 없다.)
    /// 2초 안에 검색 창이 안 뜨면 엉뚱한 앱에 글자가 들어가지 않게 입력하지 않는다.
    /// </summary>
    private static async Task SendToWindowsSearchAsync(AppServices services, string query)
    {
        try
        {
            await Task.Delay(80); // Spotlight 창이 닫히고 포그라운드가 넘어갈 시간
            services.Shell.OpenSearch();
            if (query.Length == 0) return;

            var sw = Stopwatch.StartNew();
            bool ready = false;
            while (sw.ElapsedMilliseconds < 2000)
            {
                await Task.Delay(60);
                IntPtr fg = User32.GetForegroundWindow();
                if (fg == IntPtr.Zero) continue;
                User32.GetWindowThreadProcessId(fg, out uint pid);
                string? name = pid != 0 ? Kernel32.ProcessName(pid) : null;
                if (name is not null && SearchHosts.Any(h => name.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
                {
                    ready = true;
                    break;
                }
            }
            if (!ready)
            {
                Log.Warn("윈도우 검색 창이 포그라운드가 되지 않아 검색어 입력 생략");
                return;
            }
            await Task.Delay(250); // 검색 상자가 입력을 받을 준비가 될 때까지
            if (!User32.Send(UnicodeInputs(query))) Log.Warn($"윈도우 검색 검색어 입력 실패 err={User32.LastSendError}");
        }
        catch (Exception ex)
        {
            Log.Error("윈도우 검색으로 넘기기 실패", ex);
        }
    }

    private const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>문자열을 KEYEVENTF_UNICODE 입력으로 (IME·자판 배열과 무관하게 한글도 그대로 입력됨).</summary>
    private static INPUT[] UnicodeInputs(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            list.Add(new INPUT { type = User32.INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } } });
            list.Add(new INPUT { type = User32.INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | User32.KEYEVENTF_KEYUP } } });
        }
        return list.ToArray();
    }
}
