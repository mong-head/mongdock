using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Native;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 맥 Spotlight 스타일 검색창. 상단바 검색 버튼(SearchMode=Spotlight)으로 열고, 다시 누르면 닫는다.
/// 마우스가 있는 모니터의 가로 가운데, 위에서 약 25% 지점.
/// 검색 대상(설정 Search 로 켜고 끔): 계산기 · 앱(shell:AppsFolder) · 윈도우 설정 · 파일/폴더(윈도우 검색 색인) · 웹/Windows 검색.
/// 결과는 카테고리 머리글로 묶고 맨 위에 "최상위 히트" (SpotlightSearchSession). 목록이 길면 화면 높이 55% 까지 + 얇은 스크롤바.
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
    private const double HeaderHeight = 26;
    /// <summary>파일 경로 부제의 최대 폭 (카드 폭 − 여백·아이콘·스크롤바·"폴더에서 보기" 버튼 자리).</summary>
    private const double SubtitleMaxWidth = CardWidth - 16 - 22 - 44 - 10 - 110;
    /// <summary>결과 목록 최대 높이 = 모니터 높이의 이 비율 (넘치면 스크롤).</summary>
    private const double MaxListRatio = 0.55;
    private const double AppIconSize = 32;
    /// <summary>카드 윗변 위치 = 모니터 높이의 이 비율.</summary>
    private const double TopRatio = 0.25;

    private static SpotlightWindow? _current;
    /// <summary>방금 비활성화로 닫혔으면 같은 클릭의 토글이 다시 열지 않도록.</summary>
    private static long _closedAt;
    /// <summary>앱 아이콘 캐시 (IconKey → Frozen 이미지). 창을 다시 열 때 바로 보이게 정적. 파일 아이콘은 ShellFileIcons 가 확장자별로 캐시.</summary>
    private static readonly Dictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly AppServices _services;
    private readonly UiPalette _p;
    private readonly IconStyle _iconStyle;
    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly Border _divider;
    private readonly StackPanel _list;
    private readonly Grid _listHost;
    private readonly ScrollViewer _scroll;
    private readonly SpotlightSearchSession _session;
    private readonly Border _highlight;
    private readonly TranslateTransform _highlightShift = new();
    private readonly Grid _root;
    /// <summary>각 결과 행의 목록 안 위쪽 위치 (구분선 포함, 레이아웃 전에도 하이라이트를 놓을 수 있게 직접 셈).</summary>
    private double _nextRowTop;
    private readonly List<Result> _results = new();
    private readonly bool? _hangulAtOpen;
    private int _selected;
    private int _generation;
    private bool _closing;
    private long _openedAt;
    private IntPtr _hwnd;
    /// <summary>사용자가 키보드·마우스로 선택을 옮겼는지 (늦게 온 파일 결과가 선택을 바꾸지 않게). 검색어가 바뀌면 초기화.</summary>
    private bool _userMoved;
    private Point _lastMouse = new(double.NaN, double.NaN);

    private sealed class Result
    {
        public SpotlightItem Item = null!;
        public Border Row = null!;
        public TextBlock Label = null!;
        public TextBlock? Sub;
        public TextBlock? Glyph;
        public Border? RevealButton;
        /// <summary>카테고리 머리글 바로 아래 행 (스크롤로 보이게 할 때 머리글까지).</summary>
        public bool FirstInSection;
        public double Top;
    }

    /// <summary>검색창이 열려 있는지 (코치마크가 앵커 클릭 뒤 닫힘을 감지).</summary>
    public static bool IsOpen => _current is not null;

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
        _list = new StackPanel();
        // 선택 하이라이트는 행 배경이 아니라 행 뒤의 한 장 — 선택이 바뀌면 그 자리로 미끄러져 감 (툭 점프하지 않게)
        _highlight = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(8),
            Background = _p.Accent,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RenderTransform = _highlightShift,
        };
        _listHost = new Grid { Margin = new Thickness(8, 6, 8, 8) };
        _listHost.Children.Add(_highlight);
        _listHost.Children.Add(_list);
        // 결과가 많으면 최대 높이(모니터 높이 55%, 표시할 때 계산) + 얇은 스크롤바 (Themes/Controls.xaml ThinScrollBar)
        _scroll = new ScrollViewer
        {
            Content = _listHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
            Focusable = false,
            MaxHeight = 480,
            Visibility = Visibility.Collapsed,
        };
        if (TryFindResource("ThinScrollBar") is Style thin) _scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), thin);

        var content = new StackPanel();
        content.Children.Add(searchRow);
        content.Children.Add(_divider);
        content.Children.Add(_scroll);

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
        _root = root;

        // 검색 공급자: 계산기 · 앱 · 설정 · 파일(느림, 디바운스) · 웹/Windows 검색. 켜고 끄기는 매 검색마다 최신 설정으로.
        _session = new SpotlightSearchSession(new ISpotlightProvider[]
        {
            new CalculatorProvider(),
            new AppSearchProvider(services, _iconStyle),
            new SettingsSearchProvider(),
            new FileSearchProvider(),
            new FallbackProvider(q => _ = SendToWindowsSearchAsync(_services, q)),
        }, () => _services.Settings.Current.Search ?? new SearchSettings());
        _session.Updated += OnResults;

        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => OnSourceInitialized();
        Activated += (_, _) => FocusBox();
        Deactivated += (_, _) => CloseSafe();
        Closed += (_, _) =>
        {
            // CloseSafe 를 거쳤으면 _current·_closedAt 은 닫기 시작할 때 이미 정리됨
            if (!_closing) _closedAt = Environment.TickCount64;
            _closing = true;
            _generation++;
            _session.Updated -= OnResults;
            _session.Dispose();
            _services.Windows.WindowActivated -= OnOtherWindowActivated;
            if (ReferenceEquals(_current, this)) _current = null;
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

        // 결과 목록 최대 높이: 모니터 높이(DIP)의 55% − 검색 줄
        double monitorDip = monitor != IntPtr.Zero && scale > 0
            ? bounds.Height / scale
            : _services.DesktopWindows.GetPrimaryScreenBounds().Height;
        if (monitorDip > 0) _scroll.MaxHeight = Math.Max(RowHeight * 4, monitorDip * MaxListRatio - 60);

        // 열기: 페이드 + 아주 약한 확대(0.97→1, 150ms, ease-out)
        Anim.Appear(_root, 150, fromScale: 0.97, origin: new Point(0.5, 0.3));

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
        // 닫기 시작 = 토글 기준으로는 이미 닫힘 (페이드 중에 단축키를 다시 누르면 새 창을 연다)
        if (ReferenceEquals(_current, this)) _current = null;
        _closedAt = Environment.TickCount64;
        _root.IsHitTestVisible = false;
        // Deactivated 처리 중에 바로 Close 하면 예외가 날 수 있어 디스패처로 미룸. 빠른 페이드 아웃(100ms) 뒤 닫음
        Dispatcher.BeginInvoke(() =>
            Anim.Disappear(_root, 100, () =>
            {
                try { Close(); }
                catch (Exception ex) { Log.Error("Spotlight 닫기 실패", ex); }
            }, ease: Anim.EaseOut));
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
            AppSearchProvider.Apps = apps;
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
        // 빠른 공급자(계산기·앱·설정·웹)는 바로 OnResults, 파일은 디바운스 뒤 한 번 더
        try { _session.Search(q); }
        catch (Exception ex) { Log.Error("Spotlight 검색 실패", ex); }
    }

    /// <summary>
    /// 검색 결과 그리기. isUpdate = 같은 검색어에 늦게 온 결과(파일)를 합친 것 →
    /// 사용자가 이미 선택을 옮겼으면 그 항목을 그대로 선택하고 스크롤도 유지 (선택이 튀지 않게).
    /// </summary>
    private void OnResults(string query, IReadOnlyList<SpotlightSection> sections, bool isUpdate)
    {
        if (_closing || !string.Equals(query, _box.Text, StringComparison.Ordinal)) return;
        string? keepKey = isUpdate && _userMoved && _selected >= 0 && _selected < _results.Count ? _results[_selected].Item.Key : null;
        double keepOffset = isUpdate ? _scroll.VerticalOffset : 0;
        if (!isUpdate) _userMoved = false;

        BuildRows(sections);

        int index = _results.Count > 0 ? 0 : -1;
        if (keepKey is not null)
        {
            int found = _results.FindIndex(r => r.Item.Key == keepKey);
            if (found >= 0) index = found;
        }
        Select(index, animate: false, ensureVisible: !isUpdate);
        _scroll.ScrollToVerticalOffset(keepOffset);
    }

    private void BuildRows(IReadOnlyList<SpotlightSection> sections)
    {
        int gen = ++_generation;
        _results.Clear();
        _list.Children.Clear();
        _nextRowTop = 0;
        _selected = -1; // 목록이 새로 그려지면 하이라이트는 미끄러지지 않고 바로 놓음

        foreach (var section in sections)
        {
            if (section.Items.Count == 0) continue;
            bool header = section.Header is not null;
            if (header)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = section.Header,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _p.SubText,
                    Height = HeaderHeight,
                    Padding = new Thickness(10, _nextRowTop > 0 ? 8 : 4, 0, 0),
                });
                _nextRowTop += HeaderHeight;
            }
            else if (_results.Count > 0)
            {
                // 웹/Windows 검색: 머리글 없이 구분선만
                _list.Children.Add(new Border { Height = 1, Background = _p.Divider, Margin = new Thickness(10, 4, 10, 4) });
                _nextRowTop += 1 + 4 + 4;
            }
            for (int i = 0; i < section.Items.Count; i++)
                AddRow(section.Items[i], header && i == 0, gen);
        }

        bool any = _results.Count > 0;
        _divider.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddRow(SpotlightItem item, bool firstInSection, int gen)
    {
        var r = new Result { Item = item, FirstInSection = firstInSection };
        var iconHost = new Grid { Width = AppIconSize, Height = AppIconSize, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (item.Glyph is not null)
        {
            r.Glyph = new TextBlock
            {
                Text = item.Glyph,
                FontFamily = IconFont,
                FontSize = 18,
                Foreground = _p.SubText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            iconHost.Children.Add(r.Glyph);
        }
        else if (item.LoadIcon is not null)
        {
            var image = new Image { Width = AppIconSize, Height = AppIconSize, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            iconHost.Children.Add(image);
            if (item.IconKey is not null && IconCache.TryGetValue(item.IconKey, out var cached)) image.Source = cached;
            else QueueIcon(item, image, gen);
        }

        r.Label = new TextBlock
        {
            Text = item.Title,
            FontSize = item.Subtitle is null ? 16 : 14.5,
            Foreground = _p.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(r.Label);
        if (item.Subtitle is not null)
        {
            r.Sub = new TextBlock
            {
                Text = item.SubtitleIsPath ? TrimPathStart(item.Subtitle, 11.5, SubtitleMaxWidth) : item.Subtitle,
                FontSize = 11.5,
                Foreground = _p.SubText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            };
            if (item.SubtitleIsPath) r.Sub.ToolTip = item.Subtitle;
            texts.Children.Add(r.Sub);
        }

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(iconHost, Dock.Left);
        panel.Children.Add(iconHost);
        int index = _results.Count;
        if (item.Reveal is not null)
        {
            // 호버하면 오른쪽에 작은 "폴더에서 보기" (클릭은 행 실행으로 번지지 않게 Handled)
            r.RevealButton = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = _p.Tile,
                Padding = new Thickness(9, 3, 9, 4),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand,
                ToolTip = "Ctrl+Enter",
                Child = new TextBlock { Text = "폴더에서 보기", FontSize = 11.5, Foreground = _p.Text },
            };
            r.RevealButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
            r.RevealButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; Execute(index, reveal: true); };
            DockPanel.SetDock(r.RevealButton, Dock.Right);
            panel.Children.Add(r.RevealButton);
        }
        panel.Children.Add(texts);

        r.Row = new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 12, 0),
            Background = Brushes.Transparent,
            Child = panel,
            Cursor = Cursors.Hand,
        };
        r.Top = _nextRowTop;
        _nextRowTop += RowHeight;
        r.Row.MouseMove += (_, e) =>
        {
            // 마우스를 실제로 움직였을 때만 선택 이동 (키보드 이동·스크롤로 행이 커서 밑을 지나갈 때는 무시)
            Point pos = e.GetPosition(this);
            if (pos == _lastMouse) return;
            _lastMouse = pos;
            if (_selected != index)
            {
                _userMoved = true;
                Select(index);
            }
        };
        if (r.RevealButton is { } reveal)
        {
            r.Row.MouseEnter += (_, _) => reveal.Visibility = Visibility.Visible;
            r.Row.MouseLeave += (_, _) => reveal.Visibility = Visibility.Collapsed;
        }
        r.Row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Execute(index); };
        _results.Add(r);
        _list.Children.Add(r.Row);
    }

    /// <summary>경로가 넘치면 앞부분을 "…" 로 (끝의 폴더 이름이 보이게). WPF TextTrimming 은 뒤만 자르므로 직접 잼.</summary>
    private string TrimPathStart(string text, double fontSize, double maxWidth)
    {
        try
        {
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double Measure(string s) => new FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black, ppd).WidthIncludingTrailingWhitespace;
            if (Measure(text) <= maxWidth) return text;
            // 이분 탐색으로 뒤에서 몇 글자까지 들어가는지
            int lo = 1, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Measure("…" + text[^mid..]) <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return "…" + text[^lo..];
        }
        catch
        {
            return text;
        }
    }

    /// <summary>아이콘은 목록을 먼저 그린 뒤 Background 우선순위로 하나씩 (입력·렌더링을 막지 않게). 맥 스타일 렌더링은 UI 스레드 필요.</summary>
    private void QueueIcon(SpotlightItem item, Image image, int gen)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing || gen != _generation || item.LoadIcon is null) return;
            ImageSource? icon = null;
            if (item.IconKey is null || !IconCache.TryGetValue(item.IconKey, out icon))
            {
                try
                {
                    icon = item.LoadIcon();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Spotlight 아이콘 실패: {item.Key} ({ex.Message})");
                    return;
                }
                if (icon is null) return;
                if (item.IconKey is not null)
                {
                    if (IconCache.Count > 400) IconCache.Clear();
                    IconCache[item.IconKey] = icon;
                }
            }
            image.Source = icon;
        }, DispatcherPriority.Background);
    }

    /// <summary>선택 이동: 하이라이트가 새 행으로 90ms 미끄러짐(ease-out). 목록을 새로 그린 직후엔 바로 놓음.</summary>
    private void Select(int index, bool animate = true, bool ensureVisible = true)
    {
        int previous = _selected;
        _selected = index;
        if (index >= 0 && index < _results.Count)
        {
            double y = _results[index].Top;
            _highlight.Visibility = Visibility.Visible;
            if (animate && previous >= 0 && previous < _results.Count && Anim.Enabled)
                _highlightShift.BeginAnimation(TranslateTransform.YProperty, Anim.To(y, 90, Anim.EaseOut));
            else
            {
                _highlightShift.BeginAnimation(TranslateTransform.YProperty, null);
                _highlightShift.Y = y;
            }
        }
        else
        {
            _highlight.Visibility = Visibility.Collapsed;
        }
        for (int i = 0; i < _results.Count; i++)
        {
            var r = _results[i];
            bool on = i == index;
            r.Label.Foreground = on ? _p.AccentText : _p.Text;
            if (r.Sub is not null)
            {
                r.Sub.Foreground = on ? _p.AccentText : _p.SubText;
                r.Sub.Opacity = on ? 0.85 : 1;
            }
            if (r.Glyph is not null) r.Glyph.Foreground = on ? _p.AccentText : _p.SubText;
        }
        if (ensureVisible && index >= 0 && index < _results.Count) EnsureVisible(index);
    }

    /// <summary>선택 행이 스크롤 영역 안에 보이게 (카테고리 첫 행이면 머리글까지). 레이아웃 전이면 한 박자 뒤에.</summary>
    private void EnsureVisible(int index)
    {
        if (_scroll.ViewportHeight <= 0 || _scroll.ScrollableHeight <= 0)
        {
            if (_scroll.ViewportHeight <= 0)
                Dispatcher.BeginInvoke(() => { if (!_closing && _selected == index && _scroll.ViewportHeight > 0) EnsureVisible(index); }, DispatcherPriority.Loaded);
            return;
        }
        var r = _results[index];
        double padTop = _listHost.Margin.Top;
        double top = padTop + r.Top - (r.FirstInSection ? HeaderHeight : 0);
        double bottom = padTop + r.Top + RowHeight + (index == _results.Count - 1 ? _listHost.Margin.Bottom : 0);
        if (index == 0) top = 0;
        double offset = _scroll.VerticalOffset, viewport = _scroll.ViewportHeight;
        if (top < offset) _scroll.ScrollToVerticalOffset(top);
        else if (bottom > offset + viewport) _scroll.ScrollToVerticalOffset(bottom - viewport);
    }

    // ───────────────────────── 키보드 ─────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 한글 조합 중에는 Key 가 ImeProcessed 로 온다 → 실제 키로 판단 (조합 중 글자는 이미 Text 에 포함됨)
        // Alt 와 함께 누르면 Key.System 으로 온다 (Alt+Enter)
        Key key = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key,
        };
        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseSafe();
                break;
            case Key.Down:
                e.Handled = true;
                if (_results.Count > 0)
                {
                    _userMoved = true;
                    Select((_selected + 1) % _results.Count);
                }
                break;
            case Key.Up:
                e.Handled = true;
                if (_results.Count > 0)
                {
                    _userMoved = true;
                    Select(_selected <= 0 ? _results.Count - 1 : _selected - 1);
                }
                break;
            case Key.Enter:
                e.Handled = true;
                // Ctrl+Enter / Alt+Enter = 폴더에서 보기
                bool reveal = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0;
                // 조합 중 Enter: IME 가 글자를 확정한 뒤의 검색 결과로 실행
                if (e.Key == Key.ImeProcessed)
                    Dispatcher.BeginInvoke(() => Execute(_selected, reveal), DispatcherPriority.Input);
                else
                    Execute(_selected, reveal);
                break;
        }
    }

    // ───────────────────────── 실행 ─────────────────────────

    private void Execute(int index, bool reveal = false)
    {
        if (_closing || index < 0 || index >= _results.Count) return;
        var r = _results[index];
        var item = r.Item;
        if (reveal && item.Reveal is null) return; // 폴더에서 볼 수 없는 항목

        if (!reveal && item.CopyText is not null)
        {
            CopyAndClose(r);
            return;
        }
        CloseSafe();
        try
        {
            if (reveal) item.Reveal!();
            else item.Execute();
        }
        catch (Exception ex)
        {
            Log.Error("Spotlight 실행 실패", ex);
        }
    }

    /// <summary>계산기: 결과를 클립보드로 복사 → 부제에 "복사됨" 잠깐 보여 주고 닫음.</summary>
    private void CopyAndClose(Result r)
    {
        bool ok = false;
        for (int attempt = 0; attempt < 3 && !ok; attempt++)
        {
            try
            {
                r.Item.Execute();
                ok = true;
            }
            catch (Exception ex) when (attempt < 2)
            {
                Log.Warn($"클립보드 복사 재시도: {ex.Message}");
                Thread.Sleep(30); // 다른 앱이 클립보드를 잠깐 잡고 있는 경우
            }
            catch (Exception ex)
            {
                Log.Error("계산 결과 복사 실패", ex);
            }
        }
        if (r.Sub is not null) r.Sub.Text = ok ? "✓ 복사됨" : "복사하지 못했어요";
        _root.IsHitTestVisible = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ok ? 550 : 1200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CloseSafe();
        };
        timer.Start();
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
            await Task.Delay(140); // Spotlight 창이 (100ms 페이드 뒤) 닫히고 포그라운드가 넘어갈 시간
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
