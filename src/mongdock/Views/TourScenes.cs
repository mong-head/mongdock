using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 둘러보기 카드 그림을 그 자리에서 그림 (2026-10-14 사용자 확정 — 그림 파일 없이, 지금 언어·테마로, 예시 앱으로).
/// 독·상단바·앱 모음 판·상태 판은 몽독이 실제로 그리는 코드(DockItemView·AllAppsPanel·StatusPanelWindow·설정 줄 스타일)로 그리고,
/// 커서 자리와 자르는 영역은 좌표를 적지 않고 그 요소의 자리에서 계산함 (디자인이 바뀌어도 따라감).
/// 장면 속 앱은 사용자 앱이 아니라 예시 앱(기호 아이콘 + 번역된 이름). 언어·테마별로 메모리에만 캐시 (디스크에 저장하지 않음).
/// 점검: <see cref="Check"/> 가 모든 장을 그려 가리킬 요소를 찾았는지·그림이 비어 있지 않은지 (report-test --tourcheck).
/// </summary>
internal static partial class TourScenes
{
    public const double W = 640, H = 400;
    private const double Scale = 1.5; // 카드 그림 영역(약 524 DIP)을 화면 배율 1.5 까지 또렷하게

    /// <summary>단계 키 → 장 수.</summary>
    public static int Count(string? key) => key switch
    {
        "allapps" => 5,
        "dock" or "topbar" or "routines" or "stacks" => 3,
        "settings" or "bounce" => 2,
        _ => 0,
    };

    private static readonly Dictionary<string, ImageSource> Cache = new();

    private static string CacheKey(string key, int i, bool light) => $"{Loc.Code}|{(light ? "l" : "d")}|{key}|{i}";

    public static ImageSource? Cached(string key, int i, bool light) => Cache.TryGetValue(CacheKey(key, i, light), out var img) ? img : null;

    /// <summary>한 장 그리기 (캐시 있으면 그것). UI 스레드에서. 실패하면 null.</summary>
    public static ImageSource? Frame(AppServices services, string key, int i, UiPalette p)
    {
        string ck = CacheKey(key, i, p.IsLight);
        if (Cache.TryGetValue(ck, out var hit)) return hit;
        try
        {
            var img = new Scene(services, p).Build(key, i, problems: null);
            if (img is not null) Cache[ck] = img;
            return img;
        }
        catch (Exception ex)
        {
            Log.Warn($"둘러보기 장면 그리기 실패 ({key} {i + 1}): {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 점검 (시작 점검처럼 매번): 모든 단계·장을 그려 문제 목록을 돌려줌 — 그리기 실패, 가리킬 요소를 못 찾음, 그림이 비어 있음(색이 거의 한 가지).
    /// keys = 점검할 단계 (null 이면 전부). 빈 목록 = 통과.
    /// </summary>
    public static List<string> Check(AppServices services, UiPalette p, IEnumerable<string>? keys = null)
    {
        var problems = new List<string>();
        foreach (var key in keys ?? new[] { "dock", "topbar", "allapps", "routines", "stacks", "settings", "bounce" })
            for (int i = 0; i < Count(key); i++)
            {
                var local = new List<string>();
                BitmapSource? img = null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { img = new Scene(services, p).Build(key, i, local); }
                catch (Exception ex) { local.Add($"그리기 실패 {ex.GetType().Name}: {ex.Message}"); }
                // 첫 장은 카드를 열 때 바로 그리므로 0.3초 안 (나머지 장은 뒤에서 그림)
                if (i == 0 && sw.ElapsedMilliseconds > 300) local.Add($"첫 장이 느림 ({sw.ElapsedMilliseconds}ms > 300ms)");
                if (img is null) local.Add("그림 없음");
                else if (ColorCount(img) < 24) local.Add($"그림이 비어 보임 (색 {ColorCount(img)}가지)");
                problems.AddRange(local.Select(x => $"{key} {i + 1}장: {x}"));
            }
        return problems;
    }

    /// <summary>그림의 색 수 (거칠게 — 빈 그림 찾기용).</summary>
    private static int ColorCount(BitmapSource img)
    {
        var conv = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        var set = new HashSet<int>();
        for (int y = 0; y < h; y += 7)
            for (int x = 0; x < w; x += 7)
            {
                int k = (y * w + x) * 4;
                set.Add((px[k] >> 4) | (px[k + 1] >> 4 << 4) | (px[k + 2] >> 4 << 8));
                if (set.Count > 200) return set.Count;
            }
        return set.Count;
    }

    // ───────────────────────── 예시 앱 ─────────────────────────

    /// <summary>장면 속 예시 앱 (기호 아이콘 — 어느 PC 에서나 같음, 이름은 번역).</summary>
    private static readonly (string Key, string Name, string Color, string Glyph)[] Examples =
    {
        ("browser", "브라우저", "blue", ""),
        ("notes", "메모", "orange", ""),
        ("music", "음악", "pink", ""),
        ("photos", "사진", "green", ""),
        ("mail", "메일", "blue", ""),
        ("calendar", "캘린더", "red", ""),
        ("chat", "메신저", "mongdock", ""),
        ("video", "동영상", "black", ""),
        ("calc", "계산기", "gray", ""),
        ("game", "게임", "green", ""),
        ("store", "스토어", "orange", ""),
        ("settings", "설정", "gray", ""),
    };

    private static readonly Dictionary<string, ImageSource> IconCache = new();

    private static ImageSource Icon(string key)
    {
        if (IconCache.TryGetValue(key, out var hit)) return hit;
        var e = Examples.First(x => x.Key == key);
        var img = MacIconRenderer.Symbol(e.Color, e.Glyph, null);
        IconCache[key] = img;
        return img;
    }

    private static AppEntry Entry(string key) => new("tour:" + key, Loc.T(Examples.First(x => x.Key == key).Name), null, null, null, null, null);

    // ───────────────────────── 장면 ─────────────────────────

    private sealed partial class Scene
    {
        // 장면 크기·그림 배율: 둘러보기는 640×400 을 1.5배, 홈페이지 그림(Shots)은 실제 화면처럼 1280×800 을 1.25배
        private readonly double W = TourScenes.W, H = TourScenes.H, Scale = TourScenes.Scale;
        private readonly AppServices _services;
        private readonly UiPalette _p;
        private readonly bool _dark;
        private readonly DockLayout _layout;
        private List<string>? _problems;

        public Scene(AppServices services, UiPalette p)
        {
            _services = services;
            _p = p;
            _dark = !p.IsLight;
            var dock = new DockSettings { Edge = DockEdge.Bottom, IconSize = 52, Theme = _dark ? DockTheme.Dark : DockTheme.Light };
            _layout = DockLayout.From(dock, !_dark);
        }

        public Scene(AppServices services, UiPalette p, double w, double h, double scale) : this(services, p)
        {
            W = w;
            H = h;
            Scale = scale;
        }

        /// <summary>요소를 찾았는지 (못 찾으면 점검 문제로).</summary>
        private T? Need<T>(T? e, string what) where T : FrameworkElement
        {
            if (e is null || e.ActualWidth < 1 || e.ActualHeight < 1) _problems?.Add($"{what}을(를) 못 찾음");
            return e;
        }

        public BitmapSource? Build(string key, int i, List<string>? problems)
        {
            _problems = problems;
            var (g, top) = NewScene(key is "allapps" && i > 1 || key == "settings" && i == 1 ? PanelBg() : null);
            switch (key)
            {
                case "dock": Dock(g, top, i); break;
                case "topbar": TopBar(g, top, i); break;
                case "allapps": AllApps(g, top, i); break;
                case "routines": Routines(g, top, i); break;
                case "stacks": Stacks(g, top, i); break;
                case "settings": Settings(g, top, i); break;
                case "bounce": Bounce(g, top, i); break;
                default: return null;
            }
            return Render(g);
        }

        // ── 바탕 ──

        private Brush Wallpaper() => new LinearGradientBrush(_dark ? Color.FromRgb(0x2B, 0x2D, 0x4A) : Color.FromRgb(0xC9, 0xD6, 0xF2), _dark ? Color.FromRgb(0x4A, 0x33, 0x5C) : Color.FromRgb(0xE9, 0xD9, 0xF0), 35);
        private Brush PanelBg() => new SolidColorBrush(_dark ? Color.FromRgb(0x2B, 0x2B, 0x2D) : Color.FromRgb(0xF9, 0xF9, 0xFA));
        private Brush Ink => _dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F));

        private (Grid G, Canvas Top) NewScene(Brush? bg)
        {
            var g = new Grid { Width = W, Height = H, Background = bg ?? Wallpaper(), ClipToBounds = true };
            var top = new Canvas { IsHitTestVisible = false };
            Panel.SetZIndex(top, 100); // 메뉴·커서는 늘 맨 위 (배치 계산에도 들어가게 처음부터 붙임)
            g.Children.Add(top);
            return (g, top);
        }

        private void Layout(FrameworkElement g)
        {
            g.Measure(new Size(W, H));
            g.Arrange(new Rect(0, 0, W, H));
            g.UpdateLayout();
        }

        private BitmapSource Render(FrameworkElement g)
        {
            Layout(g);
            var rtb = new RenderTargetBitmap((int)(W * Scale), (int)(H * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            rtb.Render(g);
            rtb.Freeze();
            return rtb;
        }

        private static Point Center(FrameworkElement e, FrameworkElement g, double fx = 0.5, double fy = 0.5) =>
            e.TranslatePoint(new Point(e.ActualWidth * fx, e.ActualHeight * fy), g);

        // ── 커서 (맥 화살표, 누름 고리, 끌고 가는 그림) ──

        /// <summary>커서 크기 (둘러보기는 크게 확대한 장면이라 1.6, 홈페이지 그림은 실제 크기라 1.05).</summary>
        private double _cursorScale = 1.6;

        private void Cursor(Canvas top, Point at, bool pressed = false, UIElement? carry = null)
        {
            double cs = _cursorScale / 1.6;
            if (carry is not null)
            {
                carry.Measure(new Size(W, H));
                Canvas.SetLeft(carry, at.X - carry.DesiredSize.Width / 2);
                Canvas.SetTop(carry, at.Y - carry.DesiredSize.Height / 2);
                carry.Opacity = 0.92;
                top.Children.Add(carry);
            }
            if (pressed)
            {
                var ring = new Ellipse { Width = 34 * cs, Height = 34 * cs, Stroke = new SolidColorBrush(Color.FromArgb(0xE0, 0x7C, 0x6C, 0xF0)), StrokeThickness = 3 * cs, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0x6C, 0xF0)) };
                Canvas.SetLeft(ring, at.X - 17 * cs);
                Canvas.SetTop(ring, at.Y - 17 * cs);
                top.Children.Add(ring);
            }
            var arrow = new Path
            {
                Data = Geometry.Parse("M0,0 L0,17 L4.2,13.2 L7.2,20 L10,18.8 L7.1,12.2 L12.6,12.2 Z"),
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransform = new ScaleTransform(_cursorScale, _cursorScale),
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Direction = 270, Opacity = 0.35 },
            };
            Canvas.SetLeft(arrow, at.X);
            Canvas.SetTop(arrow, at.Y);
            top.Children.Add(arrow);
        }

        private static Image IconImage(ImageSource src, double size)
        {
            var img = new Image { Source = src, Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            return img;
        }

        // ── 메뉴 (몽독 메뉴 색: Menu.* 리소스) ──

        /// <summary>메뉴 모양: items 의 "-" = 구분선, highlight = 강조 줄, "›" 로 끝나면 하위 메뉴 표시. 줄 요소는 Tag = 글자.</summary>
        private Border Menu(IEnumerable<string> items, string? highlight)
        {
            var stack = new StackPanel { Margin = new Thickness(5) };
            foreach (var it in items)
            {
                if (it == "-")
                {
                    var line = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
                    line.SetResourceReference(Border.BackgroundProperty, "Menu.Divider");
                    stack.Children.Add(line);
                    continue;
                }
                bool sub = it.EndsWith(" ›", StringComparison.Ordinal);
                string text = sub ? it[..^2] : it;
                var row = new Grid { MinWidth = 170 };
                var label = new TextBlock { Text = text, FontSize = 13.5, Margin = new Thickness(10, 5, 28, 6) };
                row.Children.Add(label);
                if (sub) row.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                var cell = new Border { CornerRadius = new CornerRadius(6), Child = row, Tag = text };
                if (text == highlight)
                {
                    cell.SetResourceReference(Border.BackgroundProperty, "Menu.Accent");
                    foreach (var t in row.Children.OfType<TextBlock>()) t.SetResourceReference(TextBlock.ForegroundProperty, "Menu.AccentText");
                }
                else foreach (var t in row.Children.OfType<TextBlock>()) t.SetResourceReference(TextBlock.ForegroundProperty, "Menu.Text");
                stack.Children.Add(cell);
            }
            var b = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(0.5),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.25 },
            };
            b.SetResourceReference(Border.BackgroundProperty, "Menu.Background");
            b.SetResourceReference(Border.BorderBrushProperty, "Menu.Border");
            return b;
        }

        private static FrameworkElement? MenuRow(Border menu, string text) =>
            Descendants(menu).OfType<Border>().FirstOrDefault(b => b.Tag is string t && t == text);

        private void Place(Canvas top, FrameworkElement el, double left, double topY)
        {
            el.Measure(new Size(W, H));
            Canvas.SetLeft(el, Math.Clamp(left, 4, W - el.DesiredSize.Width - 4));
            Canvas.SetTop(el, Math.Clamp(topY, 4, H - el.DesiredSize.Height - 4));
            top.Children.Add(el);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                yield return c;
                foreach (var d in Descendants(c)) yield return d;
            }
        }

        // ── 독 ──

        private sealed record DockParts(Border Panel, List<FrameworkElement> Views, List<DockItemViewModel> Items);

        /// <summary>예시 독: 파일 탐색기(폴더)·앱 모음·예시 앱 4개 (+ 구분선·다운로드 폴더·휴지통).</summary>
        private DockParts MakeDock(bool withStacks = false, bool trashFull = false, string[]? running = null, string? routine = null, string? folderName = null, string? notify = null)
        {
            var views = new List<FrameworkElement>();
            var items = new List<DockItemViewModel>();
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            void Add(string id, bool sep, string name, ImageSource? icon)
            {
                var vm = new DockItemViewModel(id, null, sep, name, icon) { IsRunning = running?.Contains(id) == true };
                var v = new DockItemView(vm, _layout);
                views.Add(v);
                items.Add(vm);
                bar.Children.Add(v);
            }
            Add("files", false, Loc.T("파일 탐색기"), MacIconRenderer.Folder());
            Add("allapps", false, Loc.T("앱 모음"), MacIconRenderer.AllApps());
            foreach (var k in new[] { "browser", "notes", "photos", "chat" }) Add(k, false, Loc.T(Examples.First(x => x.Key == k).Name), Icon(k));
            if (routine is not null) Add("routine", false, routine, MacIconRenderer.Symbol("blue", "\uE821", null));
            if (notify is not null && items.FirstOrDefault(x => x.Id == notify) is { } n) n.HasNotification = true;
            if (withStacks)
            {
                Add("sep", true, "", null);
                Add("downloads", false, folderName ?? Loc.T("다운로드"), folderName is null ? MacIconRenderer.Folder(FolderGlyph.Downloads) : MacIconRenderer.Folder());
                Add("trash", false, Loc.T("휴지통"), MacIconRenderer.Trash(trashFull));
            }
            var panel = new Border
            {
                CornerRadius = new CornerRadius(_layout.CornerRadius),
                Padding = new Thickness(_layout.Padding),
                Background = new SolidColorBrush(_dark ? Color.FromArgb(0xD0, 0x2A, 0x2A, 0x30) : Color.FromArgb(0xC8, 0xF4, 0xF3, 0xF8)),
                BorderBrush = _layout.Border,
                BorderThickness = new Thickness(1),
                Child = bar,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            return new DockParts(panel, views, items);
        }

        /// <summary>독을 장면에 크게 (쉴 때 장면 폭의 약 62% — 마우스를 올려 커져도 장면 안).</summary>
        private DockParts BigDock(Grid g, bool withStacks = false, bool trashFull = false, string[]? running = null)
        {
            var d = MakeDock(withStacks, trashFull, running);
            int n = d.Views.Count;
            double rest = n * (_layout.IconSize + _layout.Spacing) + _layout.Padding * 2;
            double k = W * 0.62 / rest;
            d.Panel.LayoutTransform = new ScaleTransform(k, k);
            d.Panel.Margin = new Thickness(0, 0, 0, 32);
            g.Children.Add(d.Panel);
            return d;
        }

        private void Magnify(List<FrameworkElement> views, int? at)
        {
            double baseLen = _layout.IconSize + _layout.Spacing;
            for (int i = 0; i < views.Count; i++)
            {
                if (views[i] is not DockItemView v || v.Item.IsSeparator) continue;
                double target = 1;
                if (at is int c)
                {
                    double d = Math.Abs(i - c) * baseLen, r = _layout.WaveRadius;
                    target = _layout.Wave ? 1 + (_layout.HoverScale - 1) * Math.Max(0, Math.Cos(Math.PI * Math.Min(d, r) / (2 * r))) : i == c ? _layout.HoverScale : 1;
                }
                v.ApplyScale(target);
            }
        }

        private int Index(DockParts d, string id) => d.Items.FindIndex(x => x.Id == id);

        private void Dock(Grid g, Canvas top, int f)
        {
            if (f == 0)
            {
                // ① 올려서 확대 + 이름표
                var d = BigDock(g, running: new[] { "files", "browser" });
                int at = Index(d, "browser");
                Magnify(d.Views, at);
                Layout(g);
                var icon = Need(d.Views[at], "독 브라우저 아이콘")!;
                var tag = new Border { Background = _layout.LabelBackground, BorderBrush = _layout.LabelBorder, BorderThickness = new Thickness(0.5), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5, 12, 6), Child = new TextBlock { Text = d.Items[at].Name, Foreground = _layout.LabelForeground, FontSize = 15 } };
                tag.Measure(new Size(W, H));
                var head = icon.TranslatePoint(new Point(icon.ActualWidth / 2, 0), g);
                double grow = ((DockItemView)icon).Scale;
                Place(top, tag, head.X - tag.DesiredSize.Width / 2, head.Y - icon.ActualHeight * (grow - 1) - tag.DesiredSize.Height - 14);
                Cursor(top, Center(icon, g, 0.55, 0.5));
            }
            else if (f == 1)
            {
                // ② 실행 중 앱 오른쪽 클릭 → "독에 고정"
                var d = BigDock(g, running: new[] { "files", "browser", "chat" });
                Layout(g);
                var icon = Need(d.Views[Index(d, "chat")], "독 메신저 아이콘")!;
                var menu = Menu(new[] { Loc.T("새 창"), "-", Loc.T("독에 고정"), Loc.T("창 닫기") }, Loc.T("독에 고정"));
                menu.Measure(new Size(W, H));
                var c = Center(icon, g);
                Place(top, menu, c.X - menu.DesiredSize.Width / 2, icon.TranslatePoint(new Point(0, 0), g).Y - menu.DesiredSize.Height - 10);
                Layout(g);
                var row = Need(MenuRow(menu, Loc.T("독에 고정")), "독에 고정 메뉴 줄")!;
                Cursor(top, Center(row, g, 0.35, 0.6));
            }
            else
            {
                // ③ 끌어서 순서 바꾸기: 사진을 브라우저 앞으로 — 빈자리가 열리고 아이콘이 커서를 따라옴
                var d = BigDock(g, running: new[] { "files", "browser" });
                int from = Index(d, "photos"), to = Index(d, "browser");
                var moving = d.Views[from];
                ((Panel)moving.Parent).Children.Remove(moving);
                var bar = (Panel)d.Views[0].Parent;
                var gap = new Border { Width = _layout.IconSize + _layout.Spacing, Height = _layout.IconSize };
                bar.Children.Insert(bar.Children.IndexOf(d.Views[to]), gap);
                Layout(g);
                Need(gap, "독 빈자리");
                var c = Center(gap, g, 0.5, 0.2);
                double k = ((ScaleTransform)d.Panel.LayoutTransform).ScaleX;
                Cursor(top, new Point(c.X + 6, c.Y - 30), carry: IconImage(Icon("photos"), _layout.IconSize * k * 1.05));
            }
        }

        // ── 상단바 ──

        /// <summary>상단바 모양: 왼쪽 로고·지금 앱 이름·메뉴, 오른쪽 와이파이·소리·배터리·시계. 요소 Uid 로 찾음.</summary>
        private Grid Bar(double scale = 1.5, bool menus = true)
        {
            var fg = Ink;
            var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var logo = new Border { Uid = "logo", Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(6), Child = IconImage(AppIcon.WindowIcon ?? MacIconRenderer.AllApps(), 16) };
            left.Children.Add(logo);
            left.Children.Add(new TextBlock { Uid = "app", Text = Loc.T("메모"), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = fg, Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            foreach (var m in menus ? new[] { Loc.T("파일"), Loc.T("편집"), Loc.T("보기") } : Array.Empty<string>())
                left.Children.Add(new Border { Uid = "menu:" + m, Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(6), Child = new TextBlock { Text = m, FontSize = 13, Foreground = fg } });
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            foreach (var (glyph, name) in new[] { ("", "wifi"), ("", "volume"), ("", "battery") })
                right.Children.Add(new TextBlock { Text = glyph, Uid = name, FontFamily = icons, FontSize = 15, Foreground = fg, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            right.Children.Add(new TextBlock { Text = DateTime.Today.AddHours(10).AddMinutes(30).ToString("t", Loc.Culture), Uid = "clock", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var g = new Grid { Height = 32, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(_dark ? Color.FromArgb(0xE0, 0x1E, 0x1E, 0x22) : Color.FromArgb(0xE0, 0xF6, 0xF5, 0xFA)) };
            g.Children.Add(left);
            g.Children.Add(right);
            // 메뉴 이름이 긴 언어(독일어 "Ansicht" 등)는 오른쪽 아이콘과 겹치지 않게 상단바를 조금 작게 (장면 폭 안에 왼쪽·오른쪽 + 사이 24)
            left.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            right.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double need = left.DesiredSize.Width + right.DesiredSize.Width + 24;
            if (need * scale > W) scale = W / need;
            g.LayoutTransform = new ScaleTransform(scale, scale);
            // 점검은 상단바만 따로 배치해서 (장면에 따라 상단바를 잘라 그림으로 넣기도 함)
            g.Measure(new Size(W, double.PositiveInfinity));
            g.Arrange(new Rect(0, 0, W, g.DesiredSize.Height));
            g.UpdateLayout();
            CheckBarOverlap(g);
            return g;
        }

        /// <summary>점검: 장면 속 상단바의 왼쪽(앱 이름·메뉴)과 오른쪽(와이파이·소리·배터리·시계)이 겹치면 문제.</summary>
        private void CheckBarOverlap(FrameworkElement scene)
        {
            if (_problems is null) return;
            var all = Descendants(scene).OfType<FrameworkElement>().Where(e => !string.IsNullOrEmpty(e.Uid) && e.Visibility == Visibility.Visible && e.ActualWidth > 0).ToList();
            var leftEls = all.Where(e => e.Uid == "app" || e.Uid.StartsWith("menu:", StringComparison.Ordinal)).ToList();
            var rightEls = all.Where(e => e.Uid is "wifi" or "volume" or "battery" or "clock").ToList();
            foreach (var a in leftEls)
                foreach (var b in rightEls)
                {
                    var ra = Bounds(a, scene);
                    var rb = Bounds(b, scene);
                    if (ra.IntersectsWith(rb) && Rect.Intersect(ra, rb).Width > 0.5)
                        _problems.Add($"상단바 \"{(a is Border { Child: TextBlock t } ? t.Text : (a as TextBlock)?.Text ?? a.Uid)}\"가 오른쪽 {b.Uid}와 겹침");
                }
        }

        private static FrameworkElement? ByUid(DependencyObject root, string uid) =>
            Descendants(root).OfType<FrameworkElement>().FirstOrDefault(e => e.Uid == uid);

        /// <summary>상태 판(제어 센터·달력)을 실제 코드로 그린 그림.</summary>
        private ImageSource? StatusPanel(StatusPanelKind kind, out Size size, AppServices? services = null)
        {
            size = default;
            StatusPanelWindow? w = null;
            try
            {
                w = new StatusPanelWindow(services ?? _services, kind, _p, 0);
                w.RefreshForScene(); // 달력 날짜처럼 띄울 때 채우는 내용
                var content = (FrameworkElement)w.Content;
                content.Opacity = 1;
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var lists = Descendants(content).OfType<NotificationListView>().ToList();
                if (lists.Count > 0)
                {
                    foreach (var l in lists)
                    {
                        l.RebuildForScene(); // 알림 목록은 화면에 뜰 때(Loaded) 채워짐 → 그림에선 직접
                        l.Measure(new Size(300, double.PositiveInfinity)); // 목록부터 다시 재야 위로 전해짐
                        l.MaxListHeight = 320; // 비어 있을 때 맞춘 높이(0)가 남지 않게 원래 최대 높이로
                        for (DependencyObject? d = l; d is not null; d = VisualTreeHelper.GetParent(d)) (d as UIElement)?.InvalidateMeasure();
                    }
                    content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                }
                size = content.DesiredSize;
                content.Arrange(new Rect(size));
                content.UpdateLayout();
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
                rtb.Render(content);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex)
            {
                _problems?.Add($"상태 판 {kind} 그리기 실패: {ex.GetType().Name}");
                return null;
            }
            finally { w?.Close(); }
        }

        private void TopBar(Grid g, Canvas top, int f)
        {
            var bar = Bar();
            g.Children.Add(bar);
            if (f == 0)
            {
                // ① 지금 앱의 메뉴가 상단바에: 편집 → 메뉴
                var win = MockWindow("notes", 150, 260);
                win.HorizontalAlignment = HorizontalAlignment.Left;
                win.VerticalAlignment = VerticalAlignment.Top;
                win.Margin = new Thickness(250, 120, 0, 0);
                g.Children.Add(win);
                Layout(g);
                var edit = Need(ByUid(bar, "menu:" + Loc.T("편집")), "상단바 편집 메뉴")!;
                var menu = Menu(new[] { Loc.T("실행 취소"), "-", Loc.T("잘라내기"), Loc.T("복사"), Loc.T("붙여넣기") }, Loc.T("복사"));
                var at = edit.TranslatePoint(new Point(0, edit.ActualHeight), g);
                Place(top, menu, at.X - 6, at.Y + 4);
                Layout(g);
                Cursor(top, Center(edit, g), pressed: true);
                return;
            }
            // ② 제어 센터 ③ 달력 — 판을 크게(1배) 잘라 상단바 아래 오른쪽에
            var kind = f == 1 ? StatusPanelKind.ControlCenter : StatusPanelKind.Calendar;
            var img = StatusPanel(kind, out var size);
            Layout(g);
            var anchor = Need(ByUid(bar, f == 1 ? "wifi" : "clock"), f == 1 ? "상단바 와이파이" : "상단바 시계")!;
            if (img is not null)
            {
                double barBottom = bar.TranslatePoint(new Point(0, bar.ActualHeight), g).Y;
                double k = 1.0; // 크게 (화면보다 크면 아래가 잘림)
                var pic = new Border
                {
                    Child = new Image { Source = img, Width = size.Width * k, Height = size.Height * k, Stretch = Stretch.Fill },
                    CornerRadius = new CornerRadius(14),
                    Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
                };
                var c = Center(anchor, g);
                Canvas.SetLeft(pic, Math.Clamp(c.X - size.Width * k / 2, 8, W - size.Width * k - 8));
                Canvas.SetTop(pic, barBottom + 8);
                top.Children.Add(pic);
            }
            Cursor(top, Center(anchor, g), pressed: true);
        }

        /// <summary>창 모양: 제목 줄(앱 아이콘·이름) + 단순한 내용 선.</summary>
        private Border MockWindow(string app, double w, double h)
        {
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 7, 10, 6) };
            title.Children.Add(IconImage(Icon(app), 16));
            title.Children.Add(new TextBlock { Text = Loc.T(Examples.First(x => x.Key == app).Name), FontSize = 12, Foreground = Ink, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var lines = new StackPanel { Margin = new Thickness(12, 4, 12, 10) };
            var lineBrush = new SolidColorBrush(_dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0, 0, 0));
            double[] widths = { 0.9, 0.75, 0.82, 0.5, 0.7, 0.6, 0.85 };
            int count = Math.Max(2, (int)((h - 40) / 16));
            for (int i = 0; i < count; i++) lines.Children.Add(new Border { Height = 7, CornerRadius = new CornerRadius(3.5), Background = lineBrush, Width = (w - 24) * widths[i % widths.Length], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) });
            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(new Border { Height = 1, Background = lineBrush });
            stack.Children.Add(lines);
            return new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(_dark ? Color.FromRgb(0x33, 0x33, 0x3A) : System.Windows.Media.Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Child = stack,
                ClipToBounds = true,
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.2 },
            };
        }

        // ── 앱 모음 판 (예시 앱·예시 설정, 실제 판 코드) ──

        private static readonly string[] PanelApps = { "browser", "notes", "music", "photos", "mail", "calendar", "chat", "video", "calc", "game", "store", "settings" };

        /// <summary>예시 설정으로 판을 만들어 mode 장면을 준비하고, 그 판의 내용(그림 대상)과 판을 돌려줌. 쓰고 나면 Close.</summary>
        private (AllAppsPanel Panel, FrameworkElement Content) MakePanel(string mode, bool site = false, (double W, double H)? size = null)
        {
            var apps = PanelApps.Where(k => !site || k != "game").Select(Entry).ToList();
            var s = new Settings();
            s.Dock.Theme = _dark ? DockTheme.Dark : DockTheme.Light;
            s.AllApps.Favorites.AddRange(new[] { "tour:browser", "tour:mail", "tour:notes" });
            s.AllApps.ShowSuggestions = true;
            string work;
            if (site)
            {
                s.AllApps.CleanupPromptMonth = DateTime.Now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture); // "잠자는 앱이 n개" 띠는 이번 달 닫음 (예시 앱 수와 안 맞음)
                work = AppFolders.Create(s, apps, Loc.T("업무"), "tour:mail", "tour:calendar", "tour:notes", "tour:chat");
                AppFolders.Create(s, apps, Loc.T("도구"), "tour:calc", "tour:settings", "tour:store");
                AppFolders.Create(s, apps, Loc.T("사진·영상"), "tour:photos", "tour:video", "tour:music");
                s.Routines.Add(new RoutineDef { Name = Loc.T("업무 시작"), Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "blue", Glyph = "\uE821" } });
                s.Routines.Add(new RoutineDef { Name = Loc.T("공부"), Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "green", Glyph = "\uE7BE" } });
            }
            else
            {
                work = AppFolders.Create(s, apps, Loc.T("업무"), "tour:mail", "tour:calendar", "tour:notes", "tour:calc", "tour:chat");
                AppFolders.Create(s, apps, Loc.T("사진·영상"), "tour:photos", "tour:video", "tour:music");
                AppFolders.Create(s, apps, Loc.T("게임"), "tour:game", "tour:store");
                s.Routines.Add(new RoutineDef { Name = Loc.T("업무 시작"), Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "blue", Glyph = "" } });
                s.Routines.Add(new RoutineDef { Name = Loc.T("게임"), Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "green", Glyph = "" } });
            }
            var services = _services with { Settings = new SettingsService(s) };
            var monitor = _services.DesktopWindows.ResolveMonitor("");
            AllAppsPanel.SceneMode = true;
            AllAppsPanel.SizeForTest = size ?? (720, 900); // 장면에 맞게 좁은 판 (한 줄 칸 수가 줄어 크게 잘림)
            AllAppsPanel.ExampleIcons = a => a.Key.StartsWith("tour:", StringComparison.Ordinal) ? Icon(a.Key[5..]) : null;
            AllAppsPanel.SuggestionsOverride = () => new List<string> { AllAppsCatalog.Identity(Entry("photos")), AllAppsCatalog.Identity(Entry("music")) };
            AllAppsPanel.UnusedOverride = list => list.Where(a => a.Key is "tour:video" or "tour:game" or "tour:store" or "tour:calc" || (site && a.Key == "tour:music"))
                .Select((a, i) => new UnusedApps.Row(a, DateTime.Now.AddDays(-80 - 30 * i), (long)(i + 2) * 310_000_000)).ToList();
            try
            {
                var panel = new AllAppsPanel(services, UiTheme.Palette(s), new Rect(monitor.WorkArea.Left + monitor.WorkArea.Width / 2, monitor.WorkArea.Bottom - 60, 52, 52), DockEdge.Bottom, monitor);
                panel.SceneSetup(apps, mode, work);
                var content = (FrameworkElement)panel.Content;
                content.Opacity = 1;
                if (size is null) foreach (var sv in Descendants(content).OfType<ScrollViewer>()) { sv.MaxHeight = double.PositiveInfinity; sv.Height = double.NaN; } // 홈페이지 그림은 실제 판 크기 그대로
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                content.Arrange(new Rect(content.DesiredSize));
                content.UpdateLayout();
                return (panel, content);
            }
            finally
            {
                AllAppsPanel.SceneMode = false;
                AllAppsPanel.SizeForTest = null;
                AllAppsPanel.ExampleIcons = null;
                AllAppsPanel.SuggestionsOverride = null;
                AllAppsPanel.UnusedOverride = null;
            }
        }

        /// <summary>판 내용의 region 을 장면에 맞게 잘라 넣고, 판 좌표 → 장면 좌표 변환을 돌려줌.</summary>
        private Func<Point, Point> Crop(Grid g, FrameworkElement content, Rect region)
        {
            region.Intersect(new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            if (region.IsEmpty || region.Width < 4 || region.Height < 4) { _problems?.Add("자를 영역이 비어 있음"); return p => p; }
            double k = Math.Min((W - 24) / region.Width, (H - 24) / region.Height);
            double ox = (W - region.Width * k) / 2, oy = (H - region.Height * k) / 2;
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(region.Width * k * Scale), (int)Math.Ceiling(region.Height * k * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(k, k));
                dc.DrawRectangle(PanelBg(), null, new Rect(0, 0, region.Width, region.Height));
                dc.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region, Stretch = Stretch.Fill }, null, new Rect(0, 0, region.Width, region.Height));
                dc.Pop();
            }
            rtb.Render(dv);
            rtb.Freeze();
            g.Children.Add(new Image { Source = rtb, Width = region.Width * k, Height = region.Height * k, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(ox, oy, 0, 0) });
            return p => new Point(ox + (p.X - region.X) * k, oy + (p.Y - region.Y) * k);
        }

        private static Rect Bounds(FrameworkElement e, FrameworkElement root) => new(e.TranslatePoint(new Point(0, 0), root), new Size(e.ActualWidth, e.ActualHeight));

        private static Rect Union(IEnumerable<Rect> rects)
        {
            Rect r = Rect.Empty;
            foreach (var x in rects) r.Union(x);
            return r;
        }

        private void AllApps(Grid g, Canvas top, int f)
        {
            if (f == 0)
            {
                // ① 독의 앱 모음 아이콘 누르기
                var d = BigDock(g, running: new[] { "files", "browser" });
                int at = Index(d, "allapps");
                Magnify(d.Views, at);
                Layout(g);
                var icon = Need(d.Views[at], "독 앱 모음 아이콘")!;
                var tag = new Border { Background = _layout.LabelBackground, BorderBrush = _layout.LabelBorder, BorderThickness = new Thickness(0.5), CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5, 12, 6), Child = new TextBlock { Text = d.Items[at].Name, Foreground = _layout.LabelForeground, FontSize = 15 } };
                tag.Measure(new Size(W, H));
                var head = icon.TranslatePoint(new Point(icon.ActualWidth / 2, 0), g);
                double grow = ((DockItemView)icon).Scale;
                Place(top, tag, head.X - tag.DesiredSize.Width / 2, head.Y - icon.ActualHeight * (grow - 1) - tag.DesiredSize.Height - 14);
                Cursor(top, Center(icon, g, 0.55, 0.5), pressed: true);
                return;
            }
            string mode = f switch { 1 or 2 => "favorites", 3 => "folder", _ => "unused" };
            var (panel, content) = MakePanel(mode);
            try
            {
                if (f == 1)
                {
                    // ② 판 전체 (루틴 줄·즐겨찾기·폴더·모든 앱 한눈에, 축소) — 바탕화면 위에 뜬 판
                    var whole = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
                    Need(panel.SceneFavRow, "즐겨찾기 줄");
                    if (!panel.SceneRoutineCells().Any(c => c.ActualWidth > 0)) _problems?.Add("판 전체: 루틴 칸을 못 찾음");
                    if (!panel.SceneFolderTiles().Any(c => c.ActualWidth > 0)) _problems?.Add("판 전체: 폴더 칸을 못 찾음");
                    Crop(g, content, whole);
                    if (g.Children[^1] is Image img)
                    {
                        // 둥근 판 + 그림자 (그림자는 감싼 틀에 — 잘린 그림에 주면 그림자도 잘림)
                        g.Children.Remove(img);
                        img.Clip = new RectangleGeometry(new Rect(0, 0, img.Width, img.Height), 12, 12);
                        var frame = new Border { Child = img, Margin = img.Margin, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.25 } };
                        img.Margin = new Thickness(0);
                        g.Children.Add(frame);
                    }
                }
                else if (f == 2)
                {
                    // ③ 즐겨찾기 + 추천: 즐겨찾기 줄 (제목 포함), 커서는 첫 추천 칸의 [+]
                    var row = Need(panel.SceneFavRow, "즐겨찾기 줄")!;
                    var cells = Descendants(row).OfType<Border>().Where(b => b.Tag is not null && b.ActualWidth > 0).ToList();
                    var region = Union(cells.Select(c => Bounds(c, content)));
                    region = new Rect(region.X - 12, region.Y - 34, region.Width + 24, region.Height + 46); // 위 = "★ 즐겨찾기" 제목 줄까지
                    var map = Crop(g, content, region);
                    var first = Need(panel.SceneSuggestions().FirstOrDefault(), "첫 추천 칸")!;
                    var b = Bounds(first, content);
                    Cursor(top, map(new Point(b.Right - b.Width * 0.18, b.Top + b.Height * 0.12)), pressed: true);
                }
                else if (f == 3)
                {
                    // ④ 폴더 펼치기 + 순서 바꾸기: 열린 폴더 칸 + 펼친 판, 둘째 앱을 끌고 감
                    var plate = Need(panel.SceneExpanded, "펼친 폴더")!;
                    var tile = Need(panel.SceneOpenFolderTile(), "열린 폴더 칸")!;
                    var appCells = panel.SceneFolderCells.Where(c => c.ActualWidth > 0).ToList();
                    var region = Union(new[] { Bounds(tile, content), Bounds(plate, content) }.Concat(appCells.Select(c => Bounds(c, content))));
                    region.Width = Math.Min(region.Width, (appCells.Count + 1) * appCells.FirstOrDefault()?.ActualWidth * 1.15 ?? region.Width);
                    region = new Rect(region.X - 10, region.Y - 4, region.Width + 20, region.Height + 14); // 위는 폴더 칸 바로 위까지 (제목 줄이 반쯤 걸리지 않게)
                    var map = Crop(g, content, region);
                    if (appCells.Count >= 2)
                    {
                        var c1 = Bounds(appCells[1], content);
                        var c0 = Bounds(appCells[0], content);
                        double k = map(new Point(1, 0)).X - map(new Point(0, 0)).X;
                        Cursor(top, map(new Point((c0.Left + c1.Left) / 2 + c0.Width * 0.3, c1.Top + c1.Height * 0.35)), carry: IconImage(Icon("calendar"), 44 * k * 1.05));
                    }
                    else _problems?.Add("펼친 폴더 앱 칸이 2개보다 적음");
                }
                else
                {
                    // ⑤ 잠자는 앱 정리: 목록 + 떠 있는 선택 막대, 커서는 [숨기기]
                    var rows = panel.SceneUnusedRows().Where(r => r.ActualWidth > 0).ToList();
                    var bar = Need(panel.SceneSelectionBar, "잠자는 앱 선택 막대")!;
                    if (rows.Count == 0) _problems?.Add("잠자는 앱 줄을 못 찾음");
                    var region = Union(rows.Select(r => Bounds(r, content)).Append(Bounds(bar, content)));
                    region = new Rect(region.X - 12, region.Y - 6, region.Width + 24, region.Height + 22); // 위는 첫 줄 바로 위 (머리글이 반쯤 걸리지 않게)
                    var map = Crop(g, content, region);
                    var hide = Need(Descendants(bar).OfType<Button>().FirstOrDefault(), "선택 막대 숨기기 버튼")!;
                    var hb = Bounds(hide, content);
                    Cursor(top, map(new Point(hb.Left + hb.Width * 0.5, hb.Top + hb.Height * 0.55)), pressed: true);
                }
            }
            finally { panel.Close(); }
        }

        // ── 루틴 ──

        private void Routines(Grid g, Canvas top, int f)
        {
            if (f == 0)
            {
                // ① 판 맨 위 루틴 줄에서 루틴 누르기
                var (panel, content) = MakePanel("favorites");
                try
                {
                    var cells = panel.SceneRoutineCells().Where(b => b.ActualWidth > 0).ToList();
                    if (cells.Count == 0) { _problems?.Add("루틴 칸을 못 찾음"); return; }
                    var region = Union(cells.Select(c => Bounds(c, content)));
                    region = new Rect(region.X - 16, region.Y - 40, region.Width * 1.6 + 32, region.Height + 56);
                    var map = Crop(g, content, region);
                    var b = Bounds(cells[0], content);
                    Cursor(top, map(new Point(b.Left + b.Width * 0.5, b.Top + b.Height * 0.38)), pressed: true);
                }
                finally { panel.Close(); }
            }
            else if (f == 1)
            {
                // ② 새 데스크톱에 앱이 한꺼번에 — 상단바 데스크톱 알약 + 창 셋 (앱 메뉴는 빼서 알약과 안 겹치게)
                var bar = Bar(menus: false);
                g.Children.Add(bar);
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(13),
                    Padding = new Thickness(14, 4, 14, 5),
                    Background = new SolidColorBrush(_dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0)),
                    Child = new TextBlock { Text = $"‹   {Loc.T("업무 시작")} · 2/3   ›", FontSize = 13, Foreground = Ink },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    LayoutTransform = new ScaleTransform(1.25, 1.25), // 긴 루틴 이름(영어 등)도 상태 아이콘에 닿지 않게
                };
                var center = new Grid { Height = 48, VerticalAlignment = VerticalAlignment.Top };
                center.Children.Add(pill);
                g.Children.Add(center);
                var area = new Canvas();
                foreach (var (app, x, y, w, h) in new[] { ("browser", 40.0, 90.0, 300.0, 270.0), ("mail", 360.0, 86.0, 240.0, 130.0), ("calendar", 360.0, 230.0, 240.0, 140.0) })
                {
                    var win = MockWindow(app, w, h);
                    Canvas.SetLeft(win, x);
                    Canvas.SetTop(win, y);
                    area.Children.Add(win);
                }
                g.Children.Add(area);
                Layout(g);
                Need(pill, "데스크톱 알약");
                Cursor(top, Center(pill, g, 0.62, 0.7));
            }
            else
            {
                // ③ 헤드셋을 연결하면 "열까요?" 알림
                var bar = Bar();
                g.Children.Add(bar);
                var banner = Banner(Loc.F($"{Loc.T("업무 시작")} 루틴을 열까요?"), Loc.F($"{Loc.T("헤드셋")}를 연결하면"), new[] { Loc.T("열기"), Loc.T("오늘은 안 함") });
                banner.HorizontalAlignment = HorizontalAlignment.Right;
                banner.VerticalAlignment = VerticalAlignment.Top;
                banner.Margin = new Thickness(0, 64, 18, 0);
                g.Children.Add(banner);
                Layout(g);
                var open = Need(Descendants(banner).OfType<Border>().FirstOrDefault(b => b.Tag as string == Loc.T("열기")), "알림 열기 버튼")!;
                Cursor(top, Center(open, g, 0.45, 0.6), pressed: true);
            }
        }

        /// <summary>몽독 알림 배너 모양: 아이콘·mongdock·제목·내용 + 버튼 (첫 버튼 강조). 버튼 Tag = 글자.</summary>
        private Border Banner(string title, string body, string[] buttons)
        {
            var head = new DockPanel();
            var icon = IconImage(MacIconRenderer.Symbol("blue", "", null), 38);
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(icon, System.Windows.Controls.Dock.Left);
            head.Children.Add(icon);
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = AppInfo.Name, FontSize = 12, Foreground = _p.SubText });
            texts.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = _p.Text, TextWrapping = TextWrapping.Wrap });
            texts.Children.Add(new TextBlock { Text = body, FontSize = 13.5, Foreground = _p.Text, TextWrapping = TextWrapping.Wrap });
            head.Children.Add(texts);
            var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 12, 0, 0) };
            for (int i = 0; i < buttons.Length; i++)
                row.Children.Add(new Border
                {
                    Tag = buttons[i],
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(i == 0 ? 0 : 4, 0, i == buttons.Length - 1 ? 0 : 4, 0),
                    Padding = new Thickness(10, 6, 10, 7),
                    Background = i == 0 ? _p.PanelTint : _p.Tile,
                    Child = new TextBlock { Text = buttons[i], FontSize = 13.5, FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal, Foreground = i == 0 ? _p.PanelTintText : _p.Text, HorizontalAlignment = HorizontalAlignment.Center },
                });
            var stack = new StackPanel();
            stack.Children.Add(head);
            stack.Children.Add(row);
            return new Border
            {
                Width = 360,
                Padding = new Thickness(16, 14, 16, 14),
                CornerRadius = new CornerRadius(16),
                Background = _p.CardBackground,
                BorderBrush = _p.CardBorder,
                BorderThickness = new Thickness(0.5),
                Child = stack,
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
            };
        }

        // ── 독 폴더·휴지통 ──

        private void Stacks(Grid g, Canvas top, int f)
        {
            if (f == 0)
            {
                // ① 독 빈 곳 오른쪽 클릭 → "독에 폴더 추가…"
                var d = BigDock(g, withStacks: false, running: new[] { "files", "browser" });
                Layout(g);
                var menu = Menu(new[] { Loc.T("지금 화면을 루틴으로 저장…"), Loc.T("새 루틴…"), "-", Loc.F($"{AppInfo.Name} 설정…"), "-", Loc.T("구분선 추가"), Loc.T("독에 폴더 추가…") }, Loc.T("독에 폴더 추가…"));
                menu.Measure(new Size(W, H));
                var panelBox = Bounds(d.Panel, g);
                Place(top, menu, panelBox.Right - menu.DesiredSize.Width * 0.7, panelBox.Top - menu.DesiredSize.Height - 8);
                Layout(g);
                var row = Need(MenuRow(menu, Loc.T("독에 폴더 추가…")), "독에 폴더 추가 메뉴 줄")!;
                Cursor(top, Center(row, g, 0.4, 0.6));
            }
            else if (f == 1)
            {
                // ② 폴더를 누르면 펼쳐짐
                var d = BigDock(g, withStacks: true, running: new[] { "files" });
                Layout(g);
                var folder = Need(d.Views[Index(d, "downloads")], "독 다운로드 폴더")!;
                var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Rows = 2 };
                foreach (var c in new[] { "#FF4F8DF5", "#FF2EB67D", "#FFF29B38", "#FFE5534B", "#FF8C7BF0", "#FF5AC8E0" }) grid.Children.Add(Doc(c, 46));
                var stack = new Border { Child = grid, Padding = new Thickness(10), CornerRadius = new CornerRadius(14), Background = PanelBg(), Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22 } };
                stack.Measure(new Size(W, H));
                var fc = Center(folder, g);
                Place(top, stack, fc.X - stack.DesiredSize.Width / 2, folder.TranslatePoint(new Point(0, 0), g).Y - stack.DesiredSize.Height - 14);
                Cursor(top, new Point(fc.X + 4, fc.Y), pressed: true);
            }
            else
            {
                // ③ 파일을 휴지통으로 끌기 (휴지통 확대·참)
                var d = BigDock(g, withStacks: true, trashFull: true, running: new[] { "files" });
                int trash = Index(d, "trash");
                Magnify(d.Views, trash);
                Layout(g);
                var t = Need(d.Views[trash], "독 휴지통")!;
                Cursor(top, Center(t, g, 0.5, 0.4), carry: Doc("#FF4F8DF5", 46));
            }
        }

        private static Border Doc(string color, double size) => new()
        {
            Width = size,
            Height = size * 1.2,
            Margin = new Thickness(size * 0.2),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)new BrushConverter().ConvertFromString(color)!,
            Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size * 0.48, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };

        // ── 설정 ──

        private void Settings(Grid g, Canvas top, int f)
        {
            if (f == 0)
            {
                // ① 상단바 로고 메뉴 → mongdock › 설정… (어디서 여는지)
                var bar = Bar();
                g.Children.Add(bar);
                Layout(g);
                var logo = Need(ByUid(bar, "logo"), "상단바 로고")!;
                var menu = Menu(new[] { Loc.T("이 PC 정보"), "-", Loc.T("설정…"), "-", Loc.T("작업 관리자"), "-", Loc.T("절전"), Loc.T("화면 잠금"), "-", AppInfo.Name + " ›" }, AppInfo.Name);
                var at = logo.TranslatePoint(new Point(0, logo.ActualHeight), g);
                Place(top, menu, at.X - 4, at.Y + 4);
                Layout(g);
                var mong = Need(MenuRow(menu, AppInfo.Name), "로고 메뉴 mongdock 줄")!;
                var sub = Menu(new[] { Loc.T("설정…"), "-", Loc.T("독 자동으로 숨기기"), Loc.T("일시 정지") }, Loc.T("설정…"));
                var mb = Bounds(mong, g);
                Place(top, sub, mb.Right + 2, mb.Top - 6);
                Layout(g);
                var item = Need(MenuRow(sub, Loc.T("설정…")), "하위 메뉴 설정 줄")!;
                Cursor(top, Center(item, g, 0.35, 0.6));
                return;
            }
            // ② 위치·자동 숨기기 — 설정 창과 같은 줄 모양을 크게
            var card = new Border { CornerRadius = new CornerRadius(12), Background = _p.CardBackground, BorderBrush = _p.CardBorder, BorderThickness = new Thickness(0.5), Padding = new Thickness(4) };
            var rows = new StackPanel { Width = 380 };
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, on) in new[] { (Loc.T("왼쪽"), false), (Loc.T("아래"), true), (Loc.T("오른쪽"), false), (Loc.T("위"), false) })
                seg.Children.Add(new Button { Style = (Style)Application.Current.FindResource("CardButton"), Content = label, Padding = new Thickness(10, 4, 10, 5), MinWidth = 46, Background = on ? _p.Accent : Brushes.Transparent, Foreground = on ? _p.AccentText : _p.Text });
            var segBox = new Border { Background = _p.Tile, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = seg };
            var toggle = new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)Application.Current.FindResource("MacSwitch"), Background = _p.AccentFill, BorderBrush = _p.CircleOff, IsChecked = true };
            rows.Children.Add(SettingsRow(Loc.T("위치"), null, segBox));
            rows.Children.Add(new Border { Height = 1, Background = _p.Divider, Margin = new Thickness(14, 0, 14, 0) });
            rows.Children.Add(SettingsRow(Loc.T("독 자동으로 숨기기"), Loc.T("평소엔 숨어 있다가 마우스를 화면 끝에 대면 나타나요."), toggle));
            card.Child = rows;
            card.LayoutTransform = new ScaleTransform(1.45, 1.45);
            card.HorizontalAlignment = HorizontalAlignment.Center;
            card.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(card);
            Layout(g);
            Need(toggle, "자동 숨기기 스위치");
            Cursor(top, Center(toggle, g, 0.7, 0.5), pressed: true);
        }

        private Grid SettingsRow(string title, string? sub, FrameworkElement control)
        {
            var grid = new Grid { MinHeight = 44, Margin = new Thickness(14, 7, 14, 7) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            texts.Children.Add(new TextBlock { Text = title, FontSize = UiText.Body, Foreground = _p.Text, TextWrapping = TextWrapping.Wrap });
            if (sub is not null) texts.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = _p.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            grid.Children.Add(texts);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            return grid;
        }

        // ── 알림 튀기기 (업데이트 새 기능) ──

        private void Bounce(Grid g, Canvas top, int f)
        {
            var d = BigDock(g, running: new[] { "files", "browser", "chat" });
            int at = Index(d, "chat");
            d.Items[at].HasNotification = true;
            Layout(g);
            var icon = Need(d.Views[at], "독 메신저 아이콘")!;
            if (f == 0)
            {
                // 아이콘이 위로 튀어 오른 순간 (알림 점과 함께)
                icon.RenderTransform = new TranslateTransform(0, -_layout.IconSize * 0.45);
                Layout(g);
            }
            var c = Center(icon, g);
            var bubble = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6, 10, 7), Background = _p.CardBackground, Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.2 }, Child = new TextBlock { Text = Loc.T("새 알림"), FontSize = 13, Foreground = _p.Text } };
            if (f == 1)
            {
                bubble.Measure(new Size(W, H));
                Place(top, bubble, c.X - bubble.DesiredSize.Width / 2, icon.TranslatePoint(new Point(0, 0), g).Y - bubble.DesiredSize.Height - 16);
            }
        }
    }
}
