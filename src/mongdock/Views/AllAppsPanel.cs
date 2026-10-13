using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 앱 모음 판 (#24): 독의 "앱 모음"을 누르면 버튼 옆으로 뜨는 판 (화면을 덮지 않음, 화면 60% 이하).
/// 위에서 아래로: 검색(열리면 바로 입력) → ★ 즐겨찾기 줄(고정한 앱 + 자주 쓰는 앱, 8칸) → 묶음 격자(누르면 그 자리에 펼침)
/// → 모든 앱(가나다순, 접힘) → 숨긴 앱(접힘) → "Windows 시작 메뉴 열기".
/// 바꾸기는 오른쪽 클릭으로 (맨 위에 고정·독에 고정·묶음 옮기기·숨기기, 묶음 이름·순서). 닫기: 바깥 클릭·Esc·실행·버튼 다시.
/// </summary>
internal sealed partial class AllAppsPanel : DockStackPanel
{
    /// <summary>끄는 앱의 데이터 형식 (값 = 앱 키). 독도 받음 (판 → 독 = 고정).</summary>
    public const string AppFormat = "mongdock.allapps.app";
    private const string GroupFormat = "mongdock.allapps.group";

    private const int FavMax = 8;
    private const double AppCell = 88, AppIcon = 44, GroupTile = 72, GroupCell = 132;
    /// <summary>열 수: 판 폭(작업 영역 비율)에 맞춰 — 칸 크기는 그대로, 넓은 화면은 더 많이 보임.</summary>
    private readonly int Cols, GroupCols;
    private readonly double _bodyMaxHeight;

    private readonly IconStyle _style;
    private readonly TextBox _search = new();
    private readonly TextBlock _placeholder = new();
    private readonly ContentControl _body = new() { Focusable = false };
    private IReadOnlyList<AppEntry> _apps = Array.Empty<AppEntry>();
    private Dictionary<string, string> _groupOf = new();
    private List<AppFolder> _folders = new();
    /// <summary>폴더 편집 모드 ([편집] — 이름 칸·× 지우기·− 빼기, 흔들림 없음).</summary>
    private bool _editing;
    /// <summary>"안 쓰는 앱 정리" 카드를 보는 중.</summary>
    private List<CleanupRow>? _cleanup;
    private string? _expanded;
    private bool _allOpen, _hiddenOpen;
    /// <summary>큰 묶음을 펼쳤을 때 처음 3줄 뒤 "더 보기"를 누른 묶음.</summary>
    private readonly HashSet<string> _showAll = new();
    private const int ExpandedRows = 3;
    private string? _renaming;
    private List<AppEntry> _results = new();
    private int _selected;

    /// <summary>"독에 고정" — 독이 핀을 만듦.</summary>
    public event Action<AppEntry>? PinToDockRequested;
    /// <summary>앱을 실행함 (실행 기록·닫기는 판이 함).</summary>
    public event Action<AppEntry>? Launched;

    private AllAppsSettings S => Services.Settings.Current.AllApps;

    /// <summary>열려 있는 판 (몽독 검색 창과 겹쳐 열리지 않게 — 하나 열면 다른 건 닫음).</summary>
    public static AllAppsPanel? Current { get; private set; }

    /// <summary>앱 모음 판은 "특별대우": 독 버튼 옆이 아니라 화면 가운데.</summary>
    protected override bool Centered => true;

    /// <summary>판 폭: 작업 영역 80% (세로 모니터 92%), 640~1800 DIP.</summary>
    private static bool _sizeLogged; // 판 크기는 몽독 실행마다 한 번만 로그

    /// <summary>판 안 크기 배율 (1 = 예전 크기). 한 줄 10.5칸 기준으로, 1배 ~ 아이콘 96px(약 2.18배).</summary>
    internal double UiScale { get; private set; } = 1;
    private double _panelWidth;

    internal static double UiScaleFor(double width) => Math.Clamp((width - 32) / 10.5 / AppCell, 1, 96.0 / AppIcon);

    /// <summary>시험 그림(report-test 크기 비교)에서만: 판 폭·높이를 정함.</summary>
    internal static (double W, double H)? SizeForTest { get; set; }

    internal static double PanelWidth(Rect work) => SizeForTest is { } t ? t.W :
        Math.Clamp(work.Width * (work.Height > work.Width ? 0.92 : 0.80), Math.Min(640, work.Width - 40), 1800);

    /// <summary>판 높이 기준: 작업 영역 88% (최소 480).</summary>
    internal static double PanelHeight(Rect work) => SizeForTest is { } t ? t.H : Math.Max(480, work.Height * 0.88);

    /// <summary>크기가 커서 레이어드 창 대신 일반 창 (열 때 투명 구간 없이).</summary>
    protected override bool Layered => false;

    public AllAppsPanel(AppServices services, UiPalette palette, Rect anchorDip, DockEdge edge, MonitorInfo monitor)
        : base(services, palette, anchorDip, edge, monitor, "mongdock All Apps")
    {
        _style = services.Settings.Current.Dock.IconStyle;
        // 크기 = 독이 있는 모니터 작업 영역 비율 (열 때마다 계산 — 배율·모니터가 바뀌어도 맞게):
        // 폭 80% (세로 모니터 92%), 640~1800 DIP / 높이 88% (최소 480) — 2026-10-13 사용자: 더 크게 (72%·82% 도 "커진 건지 모르겠다", 아이콘·글자는 그대로)
        var work = monitor.WorkArea;
        double width = PanelWidth(work);
        // 판 안 전체 크기 (2026-10-13 사용자: 아이콘도 키움): 한 줄이 10~11칸이 되게 칸·아이콘·글자·폴더·검색 칸을 같은 비율로 (LayoutTransform)
        // — 지금 크기(1배)보다 작아지지 않고, 아이콘은 96px 까지. 안쪽 배치는 1배 단위로 계산(폭 / 배율)
        UiScale = UiScaleFor(width);
        _panelWidth = width;
        double logical = width / UiScale;
        Cols = Math.Max(4, (int)((logical - 32) / AppCell));
        GroupCols = Math.Clamp((int)(Cols * AppCell / GroupCell), 4, 8);
        _bodyMaxHeight = PanelHeight(work) / UiScale - 130; // 검색 칸·아래 링크·여백 빼고 (1배 단위)
        // 아이콘 그림 해상도: 커진 아이콘이 흐리지 않게 (64~96px, 바뀌면 판 아이콘 캐시를 비움)
        int iconPx = (int)Math.Clamp(Math.Ceiling(AppIcon * UiScale * monitor.Scale / 8) * 8, 64, 96);
        if (iconPx != SmallIconPx) { SmallIconPx = iconPx; SmallIcons.Clear(); }
        if (!_sizeLogged) { _sizeLogged = true; Log.Info($"앱 모음 판 크기: 폭 {width:0} DIP (한 줄 {Cols}칸, 크기 {UiScale:0.00}배 — 아이콘 {AppIcon * UiScale:0} DIP), 높이 최대 {PanelHeight(work):0} DIP — 작업 영역 {work.Width:0}x{work.Height:0} DIP"); }
        var shell = BuildShell();
        if (UiScale > 1.001 && shell is FrameworkElement fe)
        {
            fe.LayoutTransform = new ScaleTransform(UiScale, UiScale); // 아이콘·칸 크기만 키우는 배율
            ApplyBaseText(fe); // 글자는 공통 크기 그대로 (AllAppsPanel.Text.cs)
        }
        SetBody(shell);
        _apps = AllAppsCatalog.Cached ?? Array.Empty<AppEntry>();
        Rebuild();
        // 미리 만든 목록으로 다 그린 상태로 열림. 목록이 오래됐으면 뒤에서 새로 만들어 다음 열기에 반영
        // (열려 있는 판은 출렁이지 않게 — 목록이 아예 없었을 때만 채움)
        bool hadApps = _apps.Count > 0;
        // 목록이 있으면 여는 동안은 새로 만들지 않음 (나타나기와 CPU 를 다투지 않게) — 오래됐으면 닫을 때 Warm 이 새로 만듦
        if (!hadApps && !_prewarming)
            Task.Run(() => AllAppsCatalog.Apps()).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (IsClosing || hadApps) return;
                    _apps = t.Result;
                    if (_renaming is null) Rebuild();
                });
            }, TaskScheduler.Default);
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => { _search.Focus(); Keyboard.Focus(_search); }, DispatcherPriority.Input);
        if (!_prewarming) // 미리 만들어 보기(화면에 안 띄움)는 검색 창을 닫거나 "열린 판"이 되지 않음
        {
            SpotlightWindow.CloseIfOpen();
            Current = this;
        }
        void OnRoutines() { if (!IsClosing && _drag is null) Rebuild(); } // 실행 중 점
        RoutineService.Changed += OnRoutines;
        Closed += (_, _) =>
        {
            RoutineService.Changed -= OnRoutines;
            if (Current == this) Current = null;
            CancelDragOnClose(); // 끄는 중에 닫히면(DPI 바뀜 등) 끄는 아이콘을 남기지 않음
            _body.Content = null; // 닫힌 판의 시각 트리를 바로 놓아줌
            ScheduleTrim();
        };
        PreviewKeyDown += OnKey;
    }

    // ───────────────────────── 뼈대 ─────────────────────────

    private UIElement BuildShell()
    {
        var root = new StackPanel { Width = Cols * AppCell + 8 };
        _search.FontSize = UiText.Body;
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = P.Text;
        _search.CaretBrush = P.Text;
        _search.SelectionBrush = P.SoftAccentLine;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.SetResourceReference(FontFamilyProperty, UiFonts.Key);
        InputMethod.SetIsInputMethodEnabled(_search, true);
        _search.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _selected = 0;
            Rebuild();
        };
        _placeholder.Text = Loc.T("앱 검색");
        _placeholder.FontSize = UiText.Body;
        _placeholder.Foreground = P.Disabled;
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.Margin = new Thickness(2, 0, 0, 0);
        var box = new Grid { Height = 34 };
        box.Children.Add(_placeholder);
        box.Children.Add(_search);
        var searchRow = new Border
        {
            Background = P.Tile,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(4, 0, 4, 10),
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                    box,
                },
            },
        };
        root.Children.Add(searchRow);
        // 높이는 작업 영역 75% 로 고정 — 묶음을 펼치거나 검색해도 판 크기가 출렁이지 않게
        var scroll = ThinScroll(_body, _bodyMaxHeight);
        _bodyScroll = scroll;
        scroll.Height = _bodyMaxHeight;
        // 본문 위에 떠 있는 잠자는 앱 선택 막대 (스크롤과 상관없이 늘 아래쪽에 — AllAppsPanel.Unused.cs)
        var bodyHost = new Grid();
        bodyHost.Children.Add(scroll);
        bodyHost.Children.Add(BuildSelectionBar());
        root.Children.Add(bodyHost);
        root.Children.Add(Link(Loc.T("Windows 시작 메뉴 열기"), () =>
        {
            CloseAnimated();
            Services.Launcher.Launch(new PinItem { Kind = PinKind.Special, Target = "start" });
        }));
        root.ContextMenu = BuildEmptyMenu();
        return root;
    }

    private void Rebuild()
    {
        if (IsClosing) return;
        var before = SnapshotPositions(); // 판 움직임 (AllAppsPanel.Motion.cs): 남는 칸은 새 자리로 미끄러지고 새 칸은 나타남
        bool wasEditing = _shownEditing;
        string? wasExpanded = _shownExpanded;
        _folders = AppFolders.Visible(Services.Settings.Current, _apps);
        _groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _folders) foreach (var a in f.Apps) _groupOf.TryAdd(a.Key, f.Id);
        _unusedViewShown = false;
        _body.Content = _adding is not null ? BuildAddApps() : _cleanup is not null ? BuildCleanup() : _search.Text.Trim().Length > 0 ? BuildResults() : BuildHome();
        UnscaleText(_body.Content as DependencyObject); // 글자는 판 배율을 받지 않음
        UpdateSelectionBar();
        _shownEditing = _editing;
        _shownExpanded = _expanded;
        AnimateAfterRebuild(before, wasEditing, wasExpanded);
    }

    // ───────────────────────── 검색 ─────────────────────────

    private UIElement BuildResults()
    {
        var byKey = _apps.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        var found = SpotlightMatcher.Search(_apps.Select(a => new SpotlightApp(a.Key, a.Name)).ToList(), _search.Text, SpotlightRecents.Items, 30);
        _results = found.Select(f => byKey.GetValueOrDefault(f.ParsingName)).OfType<AppEntry>().ToList();
        if (_results.Count == 0) return Muted(Loc.T("찾는 앱이 없어요"));
        _selected = Math.Clamp(_selected, 0, _results.Count - 1);
        var grid = new WrapPanel { Width = Cols * AppCell };
        for (int i = 0; i < _results.Count; i++) grid.Children.Add(AppCellView(_results[i], selected: i == _selected));
        return grid;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && CancelDragOnEscape()) { e.Handled = true; return; } // 끄는 중 Esc = 원래 자리로 (판은 그대로)
        if (_search.Text.Trim().Length == 0 || _results.Count == 0)
        {
            if (e.Key == Key.Enter && _search.IsKeyboardFocused) e.Handled = true;
            return;
        }
        int move = e.Key switch { Key.Right => 1, Key.Left => -1, Key.Down => Cols, Key.Up => -Cols, _ => 0 };
        if (move != 0 && !(e.Key is Key.Left or Key.Right && _search.CaretIndex is var c && c > 0 && c < _search.Text.Length))
        {
            _selected = Math.Clamp(_selected + move, 0, _results.Count - 1);
            Rebuild();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Launch(_results[Math.Clamp(_selected, 0, _results.Count - 1)]);
            e.Handled = true;
        }
    }

    // ───────────────────────── 처음 화면 ─────────────────────────

    private UIElement BuildHome()
    {
        var root = new StackPanel();
        _expandedView = null;
        _favRow = null;
        _favCells.Clear();
        _groupGrid = null;
        _groupGridId = null;
        _groupCells.Clear();
        _groupApps.Clear();
        var visible = _apps.Where(a => !S.Hidden.Contains(a.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (_apps.Count == 0)
        {
            root.Children.Add(Muted("…"));
            return root;
        }

        if (CleanupBanner() is { } banner) root.Children.Add(banner);

        // 루틴 (맨 위 — 루틴 칸 + [+ 루틴 추가], 0개면 설명 카드)
        root.Children.Add(RoutineSection());

        // ★ 즐겨찾기 (내가 고른 것) + 줄 끝 추천 칸 (0개면 4개 + 제목 옆 안내, 있으면 2개). 추천 후보도 없고 0개면 점선 안내 칸
        var fav = Favorites(visible);
        var suggestions = S.ShowSuggestions ? Suggestions(Services.Settings.Current, visible, fav.Count == 0 ? 4 : 2) : new List<AppEntry>();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(SectionTitle("★ " + Loc.T("즐겨찾기")));
        if (fav.Count == 0 && suggestions.Count > 0)
            title.Children.Add(new TextBlock { Text = Loc.T("+를 누르면 즐겨찾기에 들어가요"), FontSize = 11.5, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 6, 0, 4) });
        root.Children.Add(title);
        var row = new WrapPanel { Width = Cols * AppCell, Background = Brushes.Transparent };
        _favRow = row;
        foreach (var app in fav)
        {
            var c = AppCellView(app, inFavorites: true); // 즐겨찾기는 모두 내가 고른 것 — 핀 표시 없음
            _favCells.Add(c);
            row.Children.Add(c);
        }
        // 즐겨찾기와 같은 줄에 들어가면 추천 칸 아이콘·이름을 옆 칸과 같은 높이로 (라벨은 위 여백으로 올림)
        if (suggestions.Count > 0) row.Children.Add(SuggestionGroup(suggestions, sameLine: fav.Count > 0 && fav.Count + suggestions.Count < Cols));
        UIElement favContent = fav.Count == 0 && suggestions.Count == 0 ? DropHint() : row;
        var favBox = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Child = favContent, Tag = new DropTag("fav", "") };
        DropTarget(favBox, AppFormat, key => !S.Favorites.Contains(key, StringComparer.OrdinalIgnoreCase), PinToTop);
        root.Children.Add(favBox);

        // 폴더 (쓰는 앱만 — 안 쓰는 앱은 "모든 앱"에만) + 줄 끝 [+ 새 폴더]. 누르면 그 줄 아래에 펼침
        root.Children.Add(FolderTitle());
        var tiles = _folders.Select(f => (Folder: (AppFolder?)f, View: (UIElement)GroupTileView(f))).ToList();
        tiles.Add((null, NewFolderTile()));
        for (int i = 0; i < tiles.Count; i += GroupCols)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var chunk = tiles.Skip(i).Take(GroupCols).ToList();
            foreach (var t in chunk) line.Children.Add(t.View);
            root.Children.Add(line);
            if (chunk.FirstOrDefault(t => t.Folder?.Id == _expanded).Folder is { } open)
            {
                var view = ExpandedGroup(open.Id, open.Apps);
                _expandedView = view as FrameworkElement;
                _expandedFromCell = "group:" + open.Id;
                root.Children.Add(view);
            }
        }

        // 모든 앱 (가나다순, 접힘)
        root.Children.Add(Toggle(Loc.F($"모든 앱 ({visible.Count})"), _allOpen, () => { _allOpen = !_allOpen; Rebuild(); }));
        if (_allOpen) AddAllApps(root, visible); // [전체] [최근 사용] [안 쓰는 앱 n] (AllAppsPanel.Unused.cs)
        var hidden = _apps.Where(a => S.Hidden.Contains(a.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (hidden.Count > 0)
        {
            root.Children.Add(Toggle(Loc.F($"숨긴 앱 ({hidden.Count})"), _hiddenOpen, () => { _hiddenOpen = !_hiddenOpen; Rebuild(); }));
            if (_hiddenOpen)
            {
                var grid = new WrapPanel { Width = Cols * AppCell, Opacity = 0.7 };
                foreach (var app in hidden.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)) grid.Children.Add(AppCellView(app));
                root.Children.Add(grid);
            }
        }
        return root;
    }

    /// <summary>★ 즐겨찾기: 사용자가 고른 앱만 (순서대로).</summary>
    private List<AppEntry> Favorites(List<AppEntry> visible) => Favorites(Services.Settings.Current, visible);

    private static List<AppEntry> Favorites(Settings settings, List<AppEntry> visible)
    {
        var byKey = visible.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        return settings.AllApps.Favorites.Select(k => byKey.GetValueOrDefault(k)).OfType<AppEntry>().ToList();
    }

    /// <summary>시험 그림에서만: 추천 후보 (이 PC 실행 기록 대신).</summary>
    internal static Func<List<string>>? SuggestionsOverride { get; set; }

    /// <summary>
    /// 즐겨찾기 추천: 최근 14일 2일 이상·3회 이상 실행(이 PC 안에서만 셈), 횟수 많은 순.
    /// 이미 즐겨찾기인 앱, 숨긴 앱, 30일 안에 뺀 앱은 빼고 (독 고정 앱은 넣음).
    /// </summary>
    private static List<AppEntry> Suggestions(Settings settings, List<AppEntry> visible, int max)
    {
        var S = settings.AllApps;
        var byIdentity = new Dictionary<string, AppEntry>();
        foreach (var a in visible) byIdentity.TryAdd(AllAppsCatalog.Identity(a), a);
        // 빼는 규칙: 앱 모음에 없음(숨김 포함 — visible 은 숨긴 앱 제외)·이미 즐겨찾기·× 뒤 30일 — 빼고 나서 1순위→2순위로 칸을 채움.
        // 독 고정 앱은 빼지 않음 (2026-10-13 사용자: 독과 판 즐겨찾기는 쓰임이 달라 겹쳐도 됨 — 자주 쓰는 앱이 다 독에 있으면 추천이 0개였음)
        bool Keep(string id) =>
            byIdentity.TryGetValue(id, out var app)
            && !S.Favorites.Contains(app.Key, StringComparer.OrdinalIgnoreCase)
            && !(S.DismissedSuggestions.TryGetValue(app.Key, out var when) && DateTime.Now - when < TimeSpan.FromDays(30));
        // 2순위(윈도우 기록)는 윈도우 기본 앱·도구를 뺌 — 단축키·윈도우가 연 것도 남아서 "자주 쓴 앱"이 아님. 1순위(몽독이 본 기록)는 그대로.
        bool SeedKeep(string id) => byIdentity.TryGetValue(id, out var app) && !UnusedApps.IsSystemApp(app);
        var ids = SuggestionsOverride?.Invoke().Where(Keep).Take(max).ToList() ?? AppUsage.Suggestions(max, Keep, SeedKeep);
        return ids.Select(id => byIdentity[id]).ToList();
    }

    /// <summary>즐겨찾기 0개 + 추천 후보도 없음: 끌어다 놓으라는 안내 (점선 자리).</summary>
    private UIElement DropHint()
    {
        var grid = new Grid { Height = 64 };
        grid.Children.Add(DashedRect(10));
        grid.Children.Add(new TextBlock { Text = Loc.T("앱을 여기로 끌어다 놓으면 즐겨찾기에 들어가요"), Foreground = P.SubText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        return grid;
    }

    /// <summary>시험 그림에서만: 첫 추천 칸에 마우스를 올린 모습으로.</summary>
    internal static bool ShowSuggestionHoverForTest { get; set; }

    /// <summary>
    /// 추천 칸 묶음 (2026-10-13 사용자 C안): 연보라 연한 바탕(둥근 12)으로 감싸고, 왼쪽 위 모서리에 작은 알약 라벨 "✦ 추천"
    /// (반짝이 + 글자, 바탕 연보라·글자 진보라). 즐겨찾기가 0개여도 같은 묶음. 다크 테마는 팔레트의 SoftAccent 색을 따라감.
    /// </summary>
    private FrameworkElement SuggestionGroup(List<AppEntry> suggestions, bool sameLine)
    {
        var cells = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var app in suggestions) cells.Children.Add(SuggestionCell(app));
        var tint = P.SoftAccent.Clone();
        tint.Opacity = 0.55;
        tint.Freeze();
        var box = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = tint,
            Padding = new Thickness(2, 10, 2, 2), // 위는 라벨이 걸칠 자리
            Child = cells,
        };
        var pillText = new TextBlock { FontSize = UiText.Sub - 1, FontWeight = FontWeights.SemiBold, Foreground = P.SoftAccentText, VerticalAlignment = VerticalAlignment.Center };
        pillText.Inlines.Add(new System.Windows.Documents.Run("✦ "));
        pillText.Inlines.Add(new System.Windows.Documents.Run(Loc.T("추천")));
        var pill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = P.SoftAccent,
            BorderBrush = P.SoftAccentLine,
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(7, 1, 8, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, -8, 0, 0), // 묶음 위 테두리에 반쯤 걸침
            IsHitTestVisible = false,
            Child = pillText,
        };
        var host = new Grid { Margin = new Thickness(4, sameLine ? -10 : 10, 0, 0) }; // 같은 줄이면 바탕 위 여백만큼 올려 칸 높이를 맞춤
        host.Children.Add(box);
        host.Children.Add(pill);
        return host;
    }

    /// <summary>
    /// 줄 끝 추천 칸: 즐겨찾기 앱 칸과 같은 틀(아이콘·이름 자리·크기 같음). 추천 표시는 묶음(SuggestionGroup)의 연보라 바탕과 "✦ 추천" 라벨 —
    /// 아이콘은 흐리게 하지 않고 이름은 진보라. 마우스를 올리면 오른쪽 위에 작은 [+](즐겨찾기에 넣기)·×(30일 동안 추천 안 함) 동그라미.
    /// 클릭은 실행, 끌기는 없음(넣기는 [+]·오른쪽 클릭). 추천 칸 위에 놓기도 안 됨(자리 바꾸기 없음).
    /// </summary>
    private Border SuggestionCell(AppEntry app)
    {
        var image = new Image { Width = AppIcon, Height = AppIcon, HorizontalAlignment = HorizontalAlignment.Center }; // 흐리게 하지 않음 (C안 — 묶음 바탕이 추천 표시)
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        LoadIcon(image, app);
        var iconHost = new Grid { Width = AppIcon, Height = AppIcon, HorizontalAlignment = HorizontalAlignment.Center };
        iconHost.Children.Add(image);
        Border Mini(string glyph, string tip, Action run)
        {
            var b = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = P.CardBackground,
                BorderBrush = P.Divider,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; run(); };
            return b;
        }
        var plus = Mini("\uE710", Loc.T("즐겨찾기에 넣기"), () => PinToTop(app.Key));
        var close = Mini("\uE711", Loc.T("30일 동안 추천하지 않아요"), () =>
        {
            S.DismissedSuggestions[app.Key] = DateTime.Now;
            Save(false);
        });
        // 오른쪽 위에 [+] 와 × 나란히 (아이콘 모서리에 살짝 걸치게)
        var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -7, -14, 0), Visibility = ShowSuggestionHoverForTest ? Visibility.Visible : Visibility.Hidden };
        close.Margin = new Thickness(3, 0, 0, 0);
        badges.Children.Add(plus);
        badges.Children.Add(close);
        iconHost.Children.Add(badges);
        var stack = new StackPanel();
        stack.Children.Add(iconHost);
        var nameText = new TextBlock { Uid = "name", Text = app.Name, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center, FontSize = UiText.Sub, Foreground = P.SoftAccentText, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(nameText);
        ShowSuggestionHoverForTest = false; // 첫 칸만
        var cell = new Border
        {
            Width = AppCell - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 6, 2, 4),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            Child = stack,
            Cursor = Cursors.Hand,
            Tag = new DropTag("suggest", app.Key),
        };
        MongToolTip.IfTrimmed(cell, nameText, app.Name); // 잘렸을 때만 전체 이름
        cell.MouseEnter += (_, _) => { cell.Background = P.SoftAccent; badges.Visibility = Visibility.Visible; HoverIcon(image, true); };
        cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; badges.Visibility = Visibility.Hidden; HoverIcon(image, false); };
        Pressable(cell, () => Launch(app), null, null); // 추천 칸은 끌기 없음 (내가 넣은 게 아니라서) — 넣기는 [+]·오른쪽 클릭
        cell.ContextMenu = LazyMenu(m => FillAppMenu(m, app));
        return cell;
    }

    // ───────────────────────── 칸 ─────────────────────────

    private Border AppCellView(AppEntry app, bool pinned = false, bool selected = false, bool inFavorites = false)
    {
        var image = new Image { Width = AppIcon, Height = AppIcon, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        LoadIcon(image, app);
        var iconHost = new Grid { Width = AppIcon, Height = AppIcon, HorizontalAlignment = HorizontalAlignment.Center };
        iconHost.Children.Add(image);
        if (pinned)
            iconHost.Children.Add(new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = P.Accent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -4, -6, 0),
                Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9, Foreground = P.AccentText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        var stack = new StackPanel();
        stack.Children.Add(iconHost);
        // 앱 이름: 한 줄, 넘치면 … — 즐겨찾기·추천·폴더 안·모든 앱 칸 모두 같은 크기 (2026-10-13 사용자: 이름이 크고 두 줄로 넘어감)
        var nameText = new TextBlock
        {
            Uid = "name",
            Text = app.Name,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            FontSize = UiText.Sub,
            Margin = new Thickness(0, 4, 0, 0),
        };
        stack.Children.Add(nameText);
        var cell = new Border
        {
            Width = AppCell - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 6, 2, 4),
            CornerRadius = new CornerRadius(8),
            Background = selected ? P.Tile : Brushes.Transparent,
            BorderBrush = selected ? P.SoftAccentLine : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Child = stack,
            Cursor = Cursors.Hand,
            ContextMenu = LazyMenu(m => FillAppMenu(m, app)),
        };
        MongToolTip.IfTrimmed(cell, nameText, app.Name); // 잘렸을 때만 전체 이름
        cell.MouseEnter += (_, _) => { if (!selected) cell.Background = P.Tile; HoverIcon(image, true); };
        cell.MouseLeave += (_, _) => { if (!selected) cell.Background = Brushes.Transparent; HoverIcon(image, false); };
        cell.Tag = new DropTag(inFavorites ? "favapp" : "app", app.Key);
        Pressable(cell, () => Launch(app), new DragItem(false, app.Key), () =>
        {
            var data = new DataObject();
            data.SetData(AppFormat, app.Key);
            if (app.Shortcut is { } lnk && File.Exists(lnk)) data.SetData(DataFormats.FileDrop, new[] { lnk }); // 독·바탕 화면에 놓으면 바로 가기
            return data;
        });
        // 다른 앱을 이 앱 위에 놓으면 둘로 새 폴더 (★ 줄 칸은 빼고 — 거기 놓으면 즐겨찾기)
        if (!inFavorites) DropTarget(cell, AppFormat, key => key != app.Key, key => MakeGroupOf(app.Key, key));
        return cell;
    }

    /// <summary>시험 그림(report-test --allapps)에서만: 아이콘을 바로 그림 (디스패처를 돌리지 않으므로).</summary>
    internal static bool LoadIconsNow { get; set; }

    /// <summary>판 전용 작은 아이콘 (64px, 독 아이콘 캐시와 따로 — 수백 개를 열어도 독 아이콘을 밀어내지 않음). UI 스레드.</summary>
    private static readonly Dictionary<string, ImageSource> SmallIcons = new(StringComparer.OrdinalIgnoreCase);
    private static int SmallIconPx = 64;

    private static ImageSource? SmallIcon(AppServices services, AppEntry app, IconStyle style)
    {
        string key = style + "|" + app.Key;
        if (SmallIcons.TryGetValue(key, out var small)) return small;
        try
        {
            var full = services.Icons is IconService icons
                ? icons.GetAppsFolderIconUncached(app.Key, style)
                : services.Icons.GetIcon(new PinItem { Kind = PinKind.Aumid, Target = app.Key, Name = app.Name }, style);
            small = Shrink(full);
            if (SmallIcons.Count > 600) SmallIcons.Clear();
            SmallIcons[key] = small;
            return small;
        }
        catch (Exception ex)
        {
            Log.Warn($"앱 모음 아이콘 실패: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// 아이콘: 미리 만들어 둔 것이면 바로(판이 다 그려진 채 나타남). 아니면 칸 자리는 그대로 두고 뒤에서 만들어 100ms 페이드로 조용히.
    /// 그새 지워진 칸은 건너뜀.
    /// </summary>
    private void LoadIcon(Image image, AppEntry app)
    {
        if (SmallIcons.TryGetValue(_style + "|" + app.Key, out var ready) || LoadIconsNow)
        {
            image.Source = ready ?? SmallIcon(Services, app, _style);
            return;
        }
        image.Opacity = 0;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsClosing || PresentationSource.FromVisual(image) is null) return;
            image.Source = SmallIcon(Services, app, _style);
            image.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
        }, DispatcherPriority.Background);
    }

    // ───────────────────────── 미리 준비 ─────────────────────────

    private static bool _warming;
    private static bool _prewarming, _prewarmed;

    /// <summary>
    /// 몽독 시작 뒤 한 번: 판을 화면에 띄우지 않고 만들어 배치하고 그림으로까지 떠 봄 (처음 만들 때만 드는 일 — 코드 준비·글꼴·그림 그리기 —
    /// 를 미리 끝내 첫 열기도 둘째부터처럼 빠르게. QA: 첫 열기 220~250ms, 둘째부터 90~140ms). 만든 판은 바로 버림.
    /// </summary>
    private static void Prewarm(AppServices services)
    {
        _prewarmed = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        AllAppsPanel? w = null;
        try
        {
            _prewarming = true;
            var monitor = Monitors.GetPrimary();
            w = new AllAppsPanel(services, UiTheme.Palette(services.Settings.Current), new Rect(monitor.WorkArea.Left + monitor.WorkArea.Width / 2, monitor.WorkArea.Bottom - 52, 52, 52), DockEdge.Bottom, monitor);
            if (w.Content is FrameworkElement root)
            {
                root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                root.Arrange(new Rect(root.DesiredSize));
                root.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)root.ActualWidth), Math.Max(1, (int)root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root); // 그림 뜨기(나타나기)와 같은 길
            }
            Log.Info($"앱 모음 판 미리 만들어 봄: {clock.ElapsedMilliseconds}ms");
        }
        catch (Exception ex) { Log.Warn($"앱 모음 판 미리 만들기 실패: {ex.GetType().Name}"); }
        finally
        {
            _prewarming = false;
            try { w?.Close(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// 몽독 시작 몇 초 뒤(유휴) 미리: 앱 목록·분류, 그리고 처음 화면에 보이는 아이콘(★ 줄·묶음 미리 보기)을 64px 로.
    /// 아이콘은 하나씩 유휴 우선순위로 만들어 독·다른 창이 버벅이지 않게. 앱 목록이 오래되면(60초) 다음 열기 전에 다시.
    /// </summary>
    public static void Warm(AppServices services)
    {
        if (_warming) return;
        _warming = true;
        var dispatcher = Dispatcher.CurrentDispatcher;
        // 처음 한 번: 윈도우 실행 기록(UserAssist)을 씨앗으로 — 이 PC 안에서만 (기록 켜져 있을 때만)
        var all = services.Settings.Current.AllApps;
        bool seed = all.ShowSuggestions && all.UsageSeededAt is null;
        Task.Run(() =>
        {
            if (seed)
            {
                int n = AppUsage.SeedFromUserAssist(() => services.Settings.Current.AllApps.ShowSuggestions);
                Log.Info($"앱 모음: 윈도우 실행 기록 씨앗 {n}개 (이 PC 안에서만)");
            }
            return AllAppsCatalog.Apps();
        }).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) { _warming = false; return; }
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var settings = services.Settings.Current;
                    if (seed && settings.AllApps.ShowSuggestions) { settings.AllApps.UsageSeededAt = DateTime.Now; settings.AllApps.AutoFoldersDay = null; }
                    var style = settings.Dock.IconStyle;
                    var visible = t.Result.Where(a => !settings.AllApps.Hidden.Contains(a.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                    // 자동 폴더는 하루 한 번 (판이 닫혀 있을 때 — 여기는 시작·판을 닫은 뒤)
                    if (Current is null && AppFolders.RefreshAuto(settings, t.Result)) services.Settings.Save();
                    if (Current is null) PanelIntro.Warm(services); // 나타나기 그림 창도 미리 (첫 열기 지연 없게)
                    var targets = Favorites(settings, visible);
                    if (settings.AllApps.ShowSuggestions) targets.AddRange(Suggestions(settings, visible, 6));
                    foreach (var f in AppFolders.Visible(settings, t.Result)) targets.AddRange(f.Apps.Take(9));
                    var queue = new Queue<AppEntry>(targets.DistinctBy(a => a.Key));
                    void Next()
                    {
                        if (queue.Count == 0)
                        {
                            _warming = false;
                            lock (KeepIcons) { KeepIcons.Clear(); foreach (var a in targets) KeepIcons.Add(style + "|" + a.Key); }
                            ReleaseTemporaryBitmaps(); // 64px 로 줄이며 만든 256px 임시 그림의 네이티브 메모리를 바로 돌려줌
                            Log.Info($"앱 모음 판 미리 준비: 앱 {t.Result.Count}개, 아이콘 {SmallIcons.Count}개");
                            if (Current is null && !_prewarmed) Prewarm(services);
                            return;
                        }
                        SmallIcon(services, queue.Dequeue(), style);
                        dispatcher.BeginInvoke(Next, DispatcherPriority.ApplicationIdle);
                    }
                    Next();
                }
                catch (Exception ex)
                {
                    _warming = false;
                    Log.Error("앱 모음 판 미리 준비 실패", ex);
                }
            }, DispatcherPriority.ApplicationIdle);
        }, TaskScheduler.Default);
    }

    /// <summary>처음 화면 아이콘(★ 줄·묶음 미리 보기) — 판을 닫아도 유지. 나머지(펼친 묶음·모든 앱)는 닫고 2분 뒤 놓아줌.</summary>
    private static readonly HashSet<string> KeepIcons = new(StringComparer.OrdinalIgnoreCase);
    private static System.Windows.Threading.DispatcherTimer? _trimTimer;

    /// <summary>판을 닫으면 2분 뒤: 처음 화면 것 말고는 아이콘 캐시를 비우고 임시 그림 메모리를 돌려줌 (다시 열면 그때 다시).</summary>
    private static void ScheduleTrim()
    {
        _trimTimer?.Stop();
        _trimTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMinutes(2) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer?.Stop();
            if (Current is not null) return; // 열려 있으면 다음에
            int before = SmallIcons.Count;
            lock (KeepIcons)
                foreach (var k in SmallIcons.Keys.Where(k => !KeepIcons.Contains(k)).ToList()) SmallIcons.Remove(k);
            ReleaseTemporaryBitmaps();
            Log.Info($"앱 모음 판 아이콘 정리: {before} → {SmallIcons.Count}개");
        };
        _trimTimer.Start();
    }

    /// <summary>RenderTargetBitmap 등은 GC 가 종료자를 돌릴 때까지 네이티브 메모리를 쥐고 있음 — 관리 힙이 작아 GC 가 드물어 쌓이므로 직접.</summary>
    private static void ReleaseTemporaryBitmaps()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static ImageSource Shrink(ImageSource full)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(full, new Rect(0, 0, SmallIconPx, SmallIconPx));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(SmallIconPx, SmallIconPx, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>묶음 칸: 3x3 아이콘 미리 보기 + 이름 + 앱 수. 누르면 펼침/접힘.</summary>
    private Border GroupTileView(AppFolder folder)
    {
        string id = folder.Id;
        var apps = folder.Apps;
        var mini = new UniformGrid { Rows = 3, Columns = 3, Width = GroupTile - 12, Height = GroupTile - 12 };
        foreach (var app in apps.Take(9))
        {
            var img = new Image { Width = 18, Height = 18, Margin = new Thickness(1) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            LoadIcon(img, app);
            mini.Children.Add(img);
        }
        bool open = _expanded == id;
        var tile = new Border
        {
            Uid = "tile:" + id, // 폴더 열기·닫기 움직임의 시작·끝 사각형 (AllAppsPanel.Motion.cs)
            Width = GroupTile,
            Height = GroupTile,
            CornerRadius = new CornerRadius(14),
            Background = P.Tile,
            BorderBrush = open ? P.SoftAccentLine : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = mini,
        };
        var stack = new StackPanel();
        var tileHost = new Grid { Width = GroupTile + 16, HorizontalAlignment = HorizontalAlignment.Center };
        tileHost.Children.Add(tile);
        if (_editing)
        {
            // 편집 모드: 오른쪽 위 × (폴더 통째 지우기), 왼쪽 위 ≡ (끌어서 순서)
            tileHost.Children.Add(TileBadge("\uE711", Loc.T("폴더 지우기"), HorizontalAlignment.Right, () => AskDeleteFolder(id)));
            if (id != AllAppsCatalog.Other)
                tileHost.Children.Add(new TextBlock { Text = "\uE700", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11, Foreground = P.SubText, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 2, 0, 0), ToolTip = Loc.T("끌어서 순서 바꾸기") });
        }
        stack.Children.Add(tileHost);
        if (_editing)
        {
            var name = MongField.Apply(new TextBox { Uid = "name", Text = AllAppsCatalog.GroupName(S, id), FontSize = 12, FontWeight = FontWeights.SemiBold, MaxLength = 24, TextAlignment = TextAlignment.Center, Margin = new Thickness(2, 5, 2, 0), Padding = new Thickness(2, 1, 2, 1) }, P);
            void CommitName() { if (name.Text.Trim() != AllAppsCatalog.GroupName(S, id)) RenameGroup(id, name.Text); }
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; CommitName(); } };
            name.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) name.Text = AllAppsCatalog.GroupName(S, id); }; // Esc = 취소 (판이 닫히며 저장되지 않게)
            name.LostKeyboardFocus += (_, _) => CommitName();
            stack.Children.Add(name);
        }
        else
            stack.Children.Add(new TextBlock
            {
                Uid = "name",
                Text = AllAppsCatalog.GroupName(S, id),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 0, 0),
            });
        stack.Children.Add(new TextBlock { Text = Loc.F($"{apps.Count}개"), TextAlignment = TextAlignment.Center, FontSize = 11, Foreground = P.SubText });
        double w = Cols * AppCell / GroupCols - 4;
        var cell = new Border
        {
            Width = w,
            Margin = new Thickness(2),
            Padding = new Thickness(4, 6, 4, 6),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Child = stack,
            Cursor = Cursors.Hand,
            ContextMenu = LazyMenu(m => FillGroupMenu(m, id)),
        };
        cell.MouseEnter += (_, _) => cell.Background = P.Hover;
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        cell.Tag = new DropTag("group", id);
        Pressable(cell, () =>
        {
            if (open) { CloseFolderAnimated(); return; } // 줄어들며 닫힘 (AllAppsPanel.Motion.cs)
            _expanded = id;
            Rebuild();
        }, id == AllAppsCatalog.Other ? null : new DragItem(true, id), null);
        // 앱을 놓으면 이 폴더로, 다른 폴더를 놓으면 그 폴더를 이 앞으로
        DropTarget(cell, AppFormat, _ => true, key =>
        {
            if (_apps.FirstOrDefault(a => a.Key == key) is { } moved) MoveTo(moved, id);
        });
        DropTarget(cell, GroupFormat, other => other != id, other => MoveGroupBefore(other, id));
        return cell;
    }

    /// <summary>펼친 묶음: 이름(연필로 바꾸기) + 앱 칸.</summary>
    private UIElement ExpandedGroup(string id, List<AppEntry> apps)
    {
        var box = new Border
        {
            Background = P.Tile,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 8, 8, 6),
            Margin = new Thickness(2, 0, 2, 8),
        };
        var stack = new StackPanel();
        if (_renaming == id)
        {
            var edit = new TextBox
            {
                Text = AllAppsCatalog.GroupName(S, id),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                MaxLength = 24,
                Margin = new Thickness(4, 0, 4, 6),
                Padding = new Thickness(4, 2, 4, 2),
            };
            MongField.Apply(edit, P);
            void Commit()
            {
                if (_renaming != id) return;
                _renaming = null;
                RenameGroup(id, edit.Text);
            }
            edit.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Commit(); }
                else if (e.Key == Key.Escape) { e.Handled = true; _renaming = null; Rebuild(); }
            };
            edit.LostKeyboardFocus += (_, _) => Commit();
            stack.Children.Add(edit);
            Dispatcher.BeginInvoke(() => { edit.Focus(); edit.SelectAll(); }, DispatcherPriority.Input);
        }
        else
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 6) };
            head.Children.Add(new TextBlock { Text = AllAppsCatalog.GroupName(S, id), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var pencil = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(4, 0, 0, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = Loc.T("폴더 이름 바꾸기"),
                Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = P.SubText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            pencil.MouseEnter += (_, _) => pencil.Background = P.Hover;
            pencil.MouseLeave += (_, _) => pencil.Background = Brushes.Transparent;
            pencil.MouseLeftButtonUp += (_, e) => { e.Handled = true; _renaming = id; Rebuild(); };
            head.Children.Add(pencil);
            stack.Children.Add(head);
        }
        // 펼친 칸 너비에 들어가는 열 수 기준으로 처음 3줄만, 나머지는 "더 보기" (도구·개발처럼 큰 묶음이 판을 길게 늘이지 않게)
        int perRow = Math.Max(1, (int)((Cols * AppCell - 20) / AppCell));
        int limit = _showAll.Contains(id) ? apps.Count : perRow * ExpandedRows;
        var grid = new WrapPanel { Width = Cols * AppCell - 20 };
        if (apps.Count == 0) stack.Children.Add(Muted(Loc.T("앱을 여기로 끌어다 놓거나, 오른쪽 클릭 \"폴더에 넣기\"로 넣어요")));
        _groupGrid = grid;
        _groupGridId = id;
        _groupCells.Clear();
        _groupApps.Clear();
        foreach (var app in apps.Take(limit))
        {
            var c = AppCellView(app);
            if (_editing && c.Child is UIElement inner)
            {
                // 편집 모드: 앱마다 − (이 폴더에서 빼기 — 앱은 모든 앱에 그대로)
                var host = new Grid();
                c.Child = null;
                host.Children.Add(inner);
                host.Children.Add(TileBadge("\uE738", Loc.T("이 폴더에서 빼기"), HorizontalAlignment.Left, () =>
                {
                    AppFolders.RemoveFrom(Services.Settings.Current, _apps, id, app.Key);
                    Save();
                }));
                c.Child = host;
            }
            _groupCells.Add(c);
            _groupApps.Add(app);
            grid.Children.Add(c);
        }
        grid.Children.Add(AddAppsCell(id)); // 마지막 칸 [+] — 앱 고르기로 넣기
        stack.Children.Add(grid);
        if (apps.Count > perRow * ExpandedRows)
            stack.Children.Add(Toggle(_showAll.Contains(id) ? Loc.T("접기") : Loc.F($"더 보기 ({apps.Count - limit}개)"), _showAll.Contains(id), () =>
            {
                if (!_showAll.Remove(id)) _showAll.Add(id);
                Rebuild();
            }));
        box.Child = stack;
        box.Tag = new DropTag("groupgrid", id); // 펼친 폴더 안 = 이 폴더로 (놓일 자리에 빈칸, 맨 끝)
        return box;
    }

    private TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Foreground = P.SubText,
        Margin = new Thickness(6, 6, 6, 4),
    };

    private Border Toggle(string text, bool open, Action click)
    {
        var b = new Border
        {
            Padding = new Thickness(6, 6, 6, 6),
            Margin = new Thickness(0, 4, 0, 0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = (open ? "▾  " : "▸  ") + text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = P.SubText },
        };
        b.MouseEnter += (_, _) => b.Background = P.Hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    // ───────────────────────── 동작 ─────────────────────────

    private void Launch(AppEntry app)
    {
        CloseAnimated();
        try
        {
            SpotlightRecents.Add(app.Key);
            Services.Launcher.Launch(new PinItem { Kind = PinKind.Aumid, Target = app.Key, Name = app.Name });
            Launched?.Invoke(app);
        }
        catch (Exception ex)
        {
            Log.Error("앱 모음 실행 실패", ex);
        }
    }

    private void Save(bool customized = true)
    {
        if (customized) S.Customized = true;
        Services.Settings.Save();
        Rebuild();
    }

    /// <summary>메뉴는 열 때 채움 (칸마다 메뉴 항목 20개를 미리 만들지 않게).</summary>
    private static ContextMenu LazyMenu(Action<ContextMenu> fill)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem()); // 열리게 하는 자리 (열 때 지움)
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            fill(menu);
        };
        return menu;
    }

    private void FillAppMenu(ContextMenu menu, AppEntry app)
    {
        menu.Items.Add(DockMenus.Item(Loc.T("실행"), () => Launch(app)));
        bool fav = S.Favorites.Contains(app.Key, StringComparer.OrdinalIgnoreCase);
        menu.Items.Add(fav
            ? DockMenus.Item(Loc.T("즐겨찾기에서 빼기"), () => { S.Favorites.RemoveAll(k => k.Equals(app.Key, StringComparison.OrdinalIgnoreCase)); Save(false); })
            : DockMenus.Item(Loc.T("즐겨찾기에 넣기"), () => PinToTop(app.Key)));
        menu.Items.Add(DockMenus.Item(Loc.T("독에 고정"), () => { CloseAnimated(); PinToDockRequested?.Invoke(app); }));
        menu.Items.Add(RoutineUi.AddToRoutineMenu(Services, () => RoutineUi.ItemFromApp(Services, app), CloseAnimated));
        menu.Items.Add(new Separator());

        string? current = _groupOf.GetValueOrDefault(app.Key);
        var move = new MenuItem { Header = Loc.T("폴더에 넣기") };
        foreach (var f in _folders)
            move.Items.Add(DockMenus.Item(f.Name, () => MoveTo(app, f.Id), isChecked: f.Id == current));
        if (_folders.Count > 0) move.Items.Add(new Separator());
        move.Items.Add(DockMenus.Item(Loc.T("새 폴더…"), () =>
        {
            string id = AppFolders.Create(Services.Settings.Current, _apps, Loc.T("새 폴더"), app.Key);
            _expanded = id;
            _renaming = id;
            Save();
        }));
        menu.Items.Add(move);
        if (current is not null) menu.Items.Add(DockMenus.Item(Loc.T("폴더에서 빼기"), () => { AppFolders.RemoveFrom(Services.Settings.Current, _apps, current, app.Key); Save(); }));
        menu.Items.Add(new Separator());
        string? location = app.TargetPath is { Length: > 0 } t && File.Exists(t) ? t : app.Shortcut;
        menu.Items.Add(DockMenus.Item(Loc.T("파일 위치 열기"), () =>
        {
            CloseAnimated();
            try { using (Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{location}\"") { UseShellExecute = true })) { } }
            catch (Exception ex) { Log.Warn($"파일 위치 열기 실패: {ex.GetType().Name}"); }
        }, enabled: location is not null));
        bool hidden = S.Hidden.Contains(app.Key, StringComparer.OrdinalIgnoreCase);
        menu.Items.Add(hidden
            ? DockMenus.Item(Loc.T("다시 보이기"), () => { S.Hidden.RemoveAll(k => k.Equals(app.Key, StringComparison.OrdinalIgnoreCase)); Save(); })
            : DockMenus.Item(Loc.T("이 앱 숨기기"), () => { S.Hidden.Add(app.Key); S.Favorites.RemoveAll(k => k.Equals(app.Key, StringComparison.OrdinalIgnoreCase)); Save(); }));
    }

    private void FillGroupMenu(ContextMenu menu, string id)
    {
        var order = AllAppsCatalog.GroupOrder(S);
        int at = order.IndexOf(id);
        menu.Items.Add(DockMenus.Item(Loc.T("이름 바꾸기…"), () => { _expanded = id; _renaming = id; Rebuild(); }));
        if (id != AllAppsCatalog.Other)
        {
            menu.Items.Add(DockMenus.Item(Loc.T("앞으로"), () => MoveGroup(id, -1), enabled: at > 0));
            menu.Items.Add(DockMenus.Item(Loc.T("뒤로"), () => MoveGroup(id, +1), enabled: at >= 0 && at < order.Count - 2)); // "기타"는 늘 끝
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item(Loc.T("폴더 지우기…"), () => AskDeleteFolder(id)));
        menu.Items.Add(DockMenus.Item(Loc.T("편집"), () => { _editing = true; Rebuild(); }));
    }

    private ContextMenu BuildEmptyMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(DockMenus.Item(Loc.T("자주 쓰는 앱 기록 (추천·정리에 써요)"), () =>
            {
                S.ShowSuggestions = !S.ShowSuggestions;
                if (!S.ShowSuggestions) { AppUsage.Clear(); RoutineSuggest.Clear(); UnusedApps.Forget(); } // 끄면 기록도 지움 (루틴 추천용 실행 시각 포함)
                Save(false);
            }, isChecked: S.ShowSuggestions));
            menu.Items.Add(DockMenus.Item(Loc.T("잠자는 앱 정리…"), OpenCleanup, enabled: S.ShowSuggestions));
            menu.Items.Add(new Separator());
            menu.Items.Add(DockMenus.Item(Loc.T("폴더 원래대로…"), async () =>
            {
                KeepOpenOnDeactivate = true;
                bool ok = await ConfirmCardWindow.AskAsync(Services, Loc.T("폴더 원래대로"), Loc.T("만든 폴더와 바꾼 폴더·이름·순서를 자동 정리로 되돌릴까요? 숨긴 앱과 즐겨찾기는 그대로예요."), Loc.T("되돌리기"));
                KeepOpenOnDeactivate = false;
                if (!ok) return;
                S.Groups.Clear();
                S.Overrides.Clear();
                S.AutoFoldersDay = null;
                AppFolders.RefreshAuto(Services.Settings.Current, _apps, force: true);
                _expanded = null;
                Save(false);
            }, enabled: S.Groups.Count > 0 || S.Overrides.Count > 0));
        };
        menu.Items.Add(new MenuItem()); // 열릴 때 채움
        return menu;
    }

    // ───────────────────────── 끌기 ─────────────────────────

    /// <summary>놓을 곳: format 데이터가 accept 이면 테두리 강조(번쩍임 없이), 놓으면 drop (끌기가 끝난 뒤 다시 그림).</summary>
    private void DropTarget(Border target, string format, Func<string, bool> accept, Action<string> drop)
    {
        target.AllowDrop = true;
        string? Data(DragEventArgs e)
        {
            try { return e.Data.GetDataPresent(format) ? e.Data.GetData(format) as string : null; }
            catch { return null; }
        }
        Brush? before = null;
        void Over(object? s, DragEventArgs e)
        {
            if (Data(e) is not { } v || !accept(v)) return; // 다른 형식은 다른 처리기(같은 칸의 묶음/앱)가
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            if (before is null) { before = target.BorderBrush ?? Brushes.Transparent; target.BorderBrush = P.SoftAccentLine; }
        }
        void Leave()
        {
            if (before is null) return;
            target.BorderBrush = before;
            before = null;
        }
        target.DragEnter += Over;
        target.DragOver += Over;
        target.DragLeave += (_, _) => Leave();
        target.Drop += (_, e) =>
        {
            Leave();
            if (Data(e) is not { } v || !accept(v)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            Dispatcher.BeginInvoke(() => drop(v)); // 끌기 중엔 칸을 지우지 않음
        };
    }

    private void PinToTop(string key)
    {
        S.Favorites.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)
                                   || (_apps.Count > 0 && !_apps.Any(a => a.Key.Equals(k, StringComparison.OrdinalIgnoreCase)))); // 없어진 앱
        S.Favorites.Add(key);
        Save(); // 즐겨찾기에 넣음 = 직접 바꿈 (통계 allAppsCustomized)
    }

    private void MoveGroupBefore(string moving, string target)
    {
        AppFolders.EnsureOrder(S);
        var g = S.Groups.FirstOrDefault(x => x.Id == moving);
        if (g is null || moving == AllAppsCatalog.Other) return;
        S.Groups.Remove(g);
        int at = S.Groups.FindIndex(x => x.Id == target);
        S.Groups.Insert(at < 0 ? Math.Max(0, S.Groups.Count - 1) : at, g);
        AppFolders.EnsureOrder(S); // "기타"는 끝
        Save();
    }

    private void MoveTo(AppEntry app, string folder)
    {
        if (_folders.FirstOrDefault(f => f.Id == folder) is { } f && f.Apps.Any(a => a.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase))) return; // 이미 그 폴더 — 자동 폴더를 굳히지 않음
        AppFolders.AddTo(Services.Settings.Current, _apps, folder, app.Key);
        Save();
    }

    private void RenameGroup(string id, string name)
    {
        AppFolders.Rename(Services.Settings.Current, _apps, id, name);
        Save();
    }

    /// <summary>폴더 통째 지우기 — 확인 카드 "폴더 '업무'를 지울까요? 앱은 모든 앱에 그대로 있어요".</summary>
    private async void AskDeleteFolder(string id)
    {
        KeepOpenOnDeactivate = true; // 카드가 떠 있는 동안 판이 닫히지 않게 (카드는 모달이 아님)
        bool ok = await ConfirmCardWindow.AskAsync(Services, Loc.T("폴더 지우기"), Loc.F($"폴더 '{AllAppsCatalog.GroupName(S, id)}'를 지울까요? 앱은 모든 앱에 그대로 있어요."), Loc.T("지우기"));
        KeepOpenOnDeactivate = false;
        if (!ok || !IsLoaded) return;
        AppFolders.Delete(Services.Settings.Current, id);
        if (_expanded == id) _expanded = null;
        Save();
    }

    private void MoveGroup(string id, int dir)
    {
        AppFolders.EnsureOrder(S);
        int i = S.Groups.FindIndex(g => g.Id == id), j = i + dir;
        if (i < 0 || j < 0 || j >= S.Groups.Count || S.Groups[j].Id == AllAppsCatalog.Other) return;
        (S.Groups[i], S.Groups[j]) = (S.Groups[j], S.Groups[i]);
        Save();
    }

    // ───────────────────────── 폴더 머리줄·편집 ─────────────────────────

    /// <summary>"폴더" + [편집]/[완료] (+ 편집 중이면 "안 쓰는 앱 정리…").</summary>
    private UIElement FolderTitle()
    {
        var row = new DockPanel { LastChildFill = false };
        var title = SectionTitle(Loc.T("폴더"));
        DockPanel.SetDock(title, Dock.Left);
        row.Children.Add(title);
        var edit = SmallLink(_editing ? Loc.T("완료") : Loc.T("편집"), () => { _editing = !_editing; _renaming = null; Rebuild(); });
        edit.FontWeight = _editing ? FontWeights.SemiBold : FontWeights.Normal;
        DockPanel.SetDock(edit, Dock.Left);
        row.Children.Add(edit);
        if (_editing && S.ShowSuggestions)
        {
            var tidy = SmallLink(Loc.T("잠자는 앱 정리…"), OpenCleanup);
            DockPanel.SetDock(tidy, Dock.Right);
            row.Children.Add(tidy);
        }
        return row;
    }

    private TextBlock SmallLink(string text, Action click)
    {
        var t = new TextBlock { Text = text, FontSize = 12, Foreground = P.SoftAccentText, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 6, 6, 4) };
        t.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return t;
    }

    /// <summary>칸 모서리 작은 동그라미 버튼 (×·− 등).</summary>
    private Border TileBadge(string glyph, string tip, HorizontalAlignment side, Action run)
    {
        var b = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = P.CardBackground,
            BorderBrush = P.Divider,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Uid = "editbadge", // 편집 모드에 들어갈 때 0.6→1배 (AllAppsPanel.Motion.cs)
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; run(); };
        return b;
    }

    /// <summary>폴더 줄 끝 [+ 새 폴더] (늘 보임).</summary>
    /// <summary>"추가"·"추천" 칸 공통 점선 (2026-10-10 사용자: 실선·진한 보라는 구림) — 연한 회보라 1.5px, 대시 4/3.</summary>
    internal Brush DashBrush => P.IsLight ? DashLight : DashDark;
    private static readonly Brush DashLight = Frozen(Color.FromRgb(0xC9, 0xC3, 0xDC)), DashDark = Frozen(Color.FromRgb(0x5E, 0x59, 0x74));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    internal System.Windows.Shapes.Rectangle DashedRect(double radius) => new()
    {
        Stroke = DashBrush,
        StrokeThickness = 1.5,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        RadiusX = radius,
        RadiusY = radius,
        Fill = Brushes.Transparent,
        IsHitTestVisible = false,
    };

    /// <summary>
    /// 점선 칸 가운데 "+" — 글자가 아니라 선 두 개(굵기 2, 길이 = 칸의 35%)라 기준선 없이 정확히 가운데 (2026-10-13 사용자: "+"가 아래로 치우침).
    /// 중간 회색, 마우스를 올리면 연보라 (Stroke 를 바꿈).
    /// </summary>
    internal System.Windows.Shapes.Path PlusMark(double box)
    {
        double len = Math.Round(box * 0.35);
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(new Point(len / 2, 0), new Point(len / 2, len)));
        g.Children.Add(new LineGeometry(new Point(0, len / 2), new Point(len, len / 2)));
        return new System.Windows.Shapes.Path
        {
            Data = g,
            Stroke = P.SubText,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = len,
            Height = len,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    private Border NewFolderTile()
    {
        // 폴더 칸과 같은 크기·모서리, 이름도 폴더 이름과 같은 글자 (점선·회색 "+"만 다름)
        var plus = new Grid { Width = GroupTile, Height = GroupTile, HorizontalAlignment = HorizontalAlignment.Center };
        plus.Children.Add(DashedRect(14));
        var glyph = PlusMark(GroupTile);
        plus.Children.Add(glyph);
        var stack = new StackPanel();
        stack.Children.Add(plus);
        stack.Children.Add(new TextBlock { Text = Loc.T("새 폴더"), TextAlignment = TextAlignment.Center, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 0) });
        stack.Children.Add(new TextBlock { Text = " ", FontSize = 11 }); // 폴더 칸의 "n개" 줄 자리
        var cell = new Border
        {
            Width = Cols * AppCell / GroupCols - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(4, 6, 4, 6),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Child = stack,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("새 폴더 — 앱을 여기로 끌어다 놓아도 돼요"),
            Tag = new DropTag("newfolder", ""),
        };
        cell.MouseEnter += (_, _) => { cell.Background = P.Hover; glyph.Stroke = P.SoftAccentText; };
        cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; glyph.Stroke = P.SubText; };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenAddApps(null); // 이름 입력 + 앱 고르기 (AllAppsPanel.AddApps.cs)
        };
        return cell;
    }
}
