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
internal sealed class AllAppsPanel : DockStackPanel
{
    private const int Cols = 6, FavMax = 8, GroupCols = 4;
    private const double AppCell = 88, AppIcon = 44, GroupTile = 72;

    private readonly IconStyle _style;
    private readonly TextBox _search = new();
    private readonly TextBlock _placeholder = new();
    private readonly ContentControl _body = new() { Focusable = false };
    private IReadOnlyList<AppEntry> _apps = Array.Empty<AppEntry>();
    private Dictionary<string, string> _groupOf = new();
    private string? _expanded;
    private bool _allOpen, _hiddenOpen;
    private string? _renaming;
    private List<AppEntry> _results = new();
    private int _selected;

    /// <summary>"독에 고정" — 독이 핀을 만듦.</summary>
    public event Action<AppEntry>? PinToDockRequested;
    /// <summary>앱을 실행함 (실행 기록·닫기는 판이 함).</summary>
    public event Action<AppEntry>? Launched;

    private AllAppsSettings S => Services.Settings.Current.AllApps;

    public AllAppsPanel(AppServices services, UiPalette palette, Rect anchorDip, DockEdge edge, MonitorInfo monitor)
        : base(services, palette, anchorDip, edge, monitor, "mongdock All Apps")
    {
        _style = services.Settings.Current.Dock.IconStyle;
        SetBody(BuildShell());
        _apps = AllAppsCatalog.Cached ?? Array.Empty<AppEntry>();
        Rebuild();
        // 목록은 백그라운드에서 (처음엔 수백 ms) — 캐시가 있으면 먼저 그것으로 그려 둠
        Task.Run(() => AllAppsCatalog.Apps()).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (IsClosing) return;
                _apps = t.Result;
                Rebuild();
            });
        }, TaskScheduler.Default);
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => { _search.Focus(); Keyboard.Focus(_search); }, DispatcherPriority.Input);
        PreviewKeyDown += OnKey;
    }

    // ───────────────────────── 뼈대 ─────────────────────────

    private UIElement BuildShell()
    {
        var root = new StackPanel { Width = Cols * AppCell + 8 };
        _search.FontSize = 15;
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = P.Text;
        _search.CaretBrush = P.Text;
        _search.SelectionBrush = P.Accent;
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
        _placeholder.FontSize = 15;
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
        root.Children.Add(new ScrollViewer
        {
            Content = _body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(240, Monitor.WorkArea.Height * 0.6 - 120),
            Focusable = false,
        });
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
        _groupOf = _apps.ToDictionary(a => a.Key, a => AllAppsCatalog.GroupOf(S, a), StringComparer.OrdinalIgnoreCase);
        _body.Content = _search.Text.Trim().Length > 0 ? BuildResults() : BuildHome();
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
        var visible = _apps.Where(a => !S.Hidden.Contains(a.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (_apps.Count == 0)
        {
            root.Children.Add(Muted("…"));
            return root;
        }

        // ★ 즐겨찾기 줄
        var fav = Favorites(visible);
        if (fav.Count > 0)
        {
            root.Children.Add(SectionTitle("★ " + Loc.T("즐겨찾기")));
            var row = new WrapPanel { Width = Cols * AppCell };
            foreach (var (app, pinned) in fav) row.Children.Add(AppCellView(app, pinned: pinned));
            root.Children.Add(row);
        }

        // 묶음 (앱이 없는 묶음은 숨김) — 누르면 그 줄 아래에 펼침
        root.Children.Add(SectionTitle(Loc.T("묶음")));
        var groups = AllAppsCatalog.GroupOrder(S)
            .Select(id => (Id: id, Apps: visible.Where(a => _groupOf.GetValueOrDefault(a.Key) == id).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .Where(g => g.Apps.Count > 0 || S.Groups.Any(d => d.Id == g.Id && d.Id.StartsWith("g-")))
            .ToList();
        for (int i = 0; i < groups.Count; i += GroupCols)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var chunk = groups.Skip(i).Take(GroupCols).ToList();
            foreach (var g in chunk) line.Children.Add(GroupTileView(g.Id, g.Apps));
            root.Children.Add(line);
            if (chunk.FirstOrDefault(g => g.Id == _expanded) is { Id: not null } open && open.Apps is not null)
                root.Children.Add(ExpandedGroup(open.Id, open.Apps));
        }

        // 모든 앱 (가나다순, 접힘)
        root.Children.Add(Toggle(Loc.F($"모든 앱 ({visible.Count})"), _allOpen, () => { _allOpen = !_allOpen; Rebuild(); }));
        if (_allOpen)
        {
            var all = new WrapPanel { Width = Cols * AppCell };
            foreach (var app in visible.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)) all.Children.Add(AppCellView(app));
            root.Children.Add(all);
        }
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

    /// <summary>★ 줄: 고정한 앱(핀 표시) → 남은 칸은 자주 쓰는 앱(이 PC 실행 횟수 30일, 기록이 없으면 독 핀).</summary>
    private List<(AppEntry App, bool Pinned)> Favorites(List<AppEntry> visible)
    {
        var byKey = visible.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        var list = S.Favorites.Select(k => byKey.GetValueOrDefault(k)).OfType<AppEntry>().Take(FavMax).Select(a => (a, true)).ToList();
        if (!S.FillFrequent || list.Count >= FavMax) return list;
        var byIdentity = new Dictionary<string, AppEntry>();
        foreach (var a in visible) byIdentity.TryAdd(AllAppsCatalog.Identity(a), a);
        var candidates = AppUsage.Top(FavMax * 2).ToList();
        if (candidates.Count == 0)
            candidates = Services.Settings.Current.Pins.Select(AllAppsCatalog.Identity).OfType<string>().ToList();
        foreach (var id in candidates)
        {
            if (list.Count >= FavMax) break;
            if (byIdentity.TryGetValue(id, out var app) && !list.Any(x => x.Item1.Key == app.Key)) list.Add((app, false));
        }
        return list;
    }

    // ───────────────────────── 칸 ─────────────────────────

    private Border AppCellView(AppEntry app, bool pinned = false, bool selected = false)
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
        stack.Children.Add(new TextBlock
        {
            Text = app.Name,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxHeight = 30,
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
        });
        var cell = new Border
        {
            Width = AppCell - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 6, 2, 4),
            CornerRadius = new CornerRadius(8),
            Background = selected ? P.Tile : Brushes.Transparent,
            BorderBrush = selected ? P.Accent : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Child = stack,
            ToolTip = app.Name,
            Cursor = Cursors.Hand,
            ContextMenu = AppMenu(app),
        };
        cell.MouseEnter += (_, _) => { if (!selected) cell.Background = P.Tile; };
        cell.MouseLeave += (_, _) => { if (!selected) cell.Background = Brushes.Transparent; };
        cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; Launch(app); };
        return cell;
    }

    /// <summary>시험 그림(report-test --allapps)에서만: 아이콘을 바로 그림 (디스패처를 돌리지 않으므로).</summary>
    internal static bool LoadIconsNow;

    /// <summary>아이콘은 판을 먼저 보여 준 뒤 하나씩 (보이는 것부터 — 접힌 칸은 만들지 않으므로).</summary>
    private void LoadIcon(Image image, AppEntry app)
    {
        void Load()
        {
            if (IsClosing) return;
            try { image.Source = Services.Icons.GetIcon(new PinItem { Kind = PinKind.Aumid, Target = app.Key, Name = app.Name }, _style); }
            catch (Exception ex) { Log.Warn($"앱 모음 아이콘 실패: {ex.GetType().Name}"); }
        }
        if (LoadIconsNow) Load();
        else Dispatcher.BeginInvoke(Load, DispatcherPriority.Background);
    }

    /// <summary>묶음 칸: 3x3 아이콘 미리 보기 + 이름 + 앱 수. 누르면 펼침/접힘.</summary>
    private Border GroupTileView(string id, List<AppEntry> apps)
    {
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
            Width = GroupTile,
            Height = GroupTile,
            CornerRadius = new CornerRadius(14),
            Background = P.Tile,
            BorderBrush = open ? P.Accent : Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = mini,
        };
        var stack = new StackPanel();
        stack.Children.Add(tile);
        stack.Children.Add(new TextBlock
        {
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
            ContextMenu = GroupMenu(id),
        };
        cell.MouseEnter += (_, _) => cell.Background = P.Hover;
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _expanded = open ? null : id;
            Rebuild();
        };
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
                ToolTip = Loc.T("묶음 이름 바꾸기"),
                Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = P.SubText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            pencil.MouseEnter += (_, _) => pencil.Background = P.Hover;
            pencil.MouseLeave += (_, _) => pencil.Background = Brushes.Transparent;
            pencil.MouseLeftButtonUp += (_, e) => { e.Handled = true; _renaming = id; Rebuild(); };
            head.Children.Add(pencil);
            stack.Children.Add(head);
        }
        var grid = new WrapPanel { Width = Cols * AppCell - 20 };
        if (apps.Count == 0) stack.Children.Add(Muted(Loc.T("앱을 오른쪽 클릭해 \"묶음 옮기기\"로 넣어요")));
        foreach (var app in apps) grid.Children.Add(AppCellView(app));
        stack.Children.Add(grid);
        box.Child = stack;
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

    private ContextMenu AppMenu(AppEntry app)
    {
        var menu = new ContextMenu();
        menu.Items.Add(DockMenus.Item(Loc.T("실행"), () => Launch(app)));
        bool fav = S.Favorites.Contains(app.Key, StringComparer.OrdinalIgnoreCase);
        menu.Items.Add(fav
            ? DockMenus.Item(Loc.T("고정 해제"), () => { S.Favorites.RemoveAll(k => k.Equals(app.Key, StringComparison.OrdinalIgnoreCase)); Save(false); })
            : DockMenus.Item(Loc.T("맨 위에 고정"), () => { S.Favorites.Add(app.Key); if (S.Favorites.Count > FavMax) S.Favorites.RemoveAt(0); Save(false); }));
        menu.Items.Add(DockMenus.Item(Loc.T("독에 고정"), () => { CloseAnimated(); PinToDockRequested?.Invoke(app); }));
        menu.Items.Add(new Separator());

        string current = _groupOf.GetValueOrDefault(app.Key) ?? AllAppsCatalog.Other;
        var move = new MenuItem { Header = Loc.T("묶음 옮기기") };
        foreach (var id in AllAppsCatalog.GroupOrder(S))
            move.Items.Add(DockMenus.Item(AllAppsCatalog.GroupName(S, id), () => MoveTo(app, id), isChecked: id == current));
        move.Items.Add(new Separator());
        move.Items.Add(DockMenus.Item(Loc.T("새 묶음…"), () => MoveTo(app, NewGroup())));
        menu.Items.Add(move);
        if (current != AllAppsCatalog.Other) menu.Items.Add(DockMenus.Item(Loc.T("묶음에서 빼기"), () => MoveTo(app, AllAppsCatalog.Other)));
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
        return menu;
    }

    private ContextMenu GroupMenu(string id)
    {
        var menu = new ContextMenu();
        var order = AllAppsCatalog.GroupOrder(S);
        int at = order.IndexOf(id);
        menu.Items.Add(DockMenus.Item(Loc.T("이름 바꾸기…"), () => { _expanded = id; _renaming = id; Rebuild(); }));
        if (id != AllAppsCatalog.Other)
        {
            menu.Items.Add(DockMenus.Item(Loc.T("앞으로"), () => MoveGroup(id, -1), enabled: at > 0));
            menu.Items.Add(DockMenus.Item(Loc.T("뒤로"), () => MoveGroup(id, +1), enabled: at >= 0 && at < order.Count - 2)); // "기타"는 늘 끝
        }
        if (id.StartsWith("g-", StringComparison.Ordinal))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(DockMenus.Item(Loc.T("묶음 지우기"), () =>
            {
                S.Groups.RemoveAll(g => g.Id == id);
                foreach (var k in S.Overrides.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) S.Overrides.Remove(k);
                if (_expanded == id) _expanded = null;
                Save();
            }));
        }
        return menu;
    }

    private ContextMenu BuildEmptyMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add(DockMenus.Item(Loc.T("자주 쓰는 앱으로 채우기"), () =>
            {
                S.FillFrequent = !S.FillFrequent;
                if (!S.FillFrequent) AppUsage.Clear(); // 끄면 기록도 지움
                Save(false);
            }, isChecked: S.FillFrequent));
            menu.Items.Add(new Separator());
            menu.Items.Add(DockMenus.Item(Loc.T("묶음 원래대로…"), () =>
            {
                KeepOpenOnDeactivate = true;
                ConfirmCardWindow.Ask(Services, Loc.T("묶음 원래대로"), Loc.T("직접 옮긴 앱과 만든 묶음·이름·순서를 자동 분류로 되돌릴까요? 숨긴 앱과 맨 위에 고정한 앱은 그대로예요."),
                    Loc.T("되돌리기"), () =>
                    {
                        S.Groups.Clear();
                        S.Overrides.Clear();
                        _expanded = null;
                        Save(false);
                    });
                KeepOpenOnDeactivate = false;
            }, enabled: S.Groups.Count > 0 || S.Overrides.Count > 0));
        };
        menu.Items.Add(new MenuItem()); // 열릴 때 채움
        return menu;
    }

    private void MoveTo(AppEntry app, string group)
    {
        if (AllAppsCatalog.Classify(app) == group) S.Overrides.Remove(app.Key);
        else S.Overrides[app.Key] = group;
        Save();
    }

    private string NewGroup()
    {
        EnsureGroupList();
        string id = "g-" + Guid.NewGuid().ToString("N")[..8];
        // "기타" 바로 앞에
        S.Groups.Add(new AppGroupDef { Id = id, Name = Loc.T("새 묶음") });
        _expanded = id;
        _renaming = id;
        return id;
    }

    private void RenameGroup(string id, string name)
    {
        EnsureGroupList();
        var g = S.Groups.FirstOrDefault(x => x.Id == id);
        if (g is null) S.Groups.Add(g = new AppGroupDef { Id = id });
        name = name.Trim();
        g.Name = name.Length == 0 || name == AllAppsCatalog.DefaultName(id) ? null : name;
        Save();
    }

    private void MoveGroup(string id, int dir)
    {
        EnsureGroupList();
        int i = S.Groups.FindIndex(g => g.Id == id), j = i + dir;
        if (i < 0 || j < 0 || j >= S.Groups.Count || S.Groups[j].Id == AllAppsCatalog.Other) return;
        (S.Groups[i], S.Groups[j]) = (S.Groups[j], S.Groups[i]);
        Save();
    }

    /// <summary>순서를 바꾸기 전에 지금 화면 순서를 설정에 적어 둠 (기본 묶음 포함).</summary>
    private void EnsureGroupList()
    {
        foreach (var id in AllAppsCatalog.GroupOrder(S))
            if (!S.Groups.Any(g => g.Id == id)) S.Groups.Add(new AppGroupDef { Id = id });
        // "기타"는 늘 끝
        var other = S.Groups.First(g => g.Id == AllAppsCatalog.Other);
        S.Groups.Remove(other);
        S.Groups.Add(other);
    }
}
