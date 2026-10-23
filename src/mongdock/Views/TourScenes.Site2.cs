using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 홈페이지 v2 (독·상단바 중심, 2026-10-14): 대표 그림 hero2, 독 장면(올림·파도·창 미리 보기·고정·순서 바꾸기),
/// 독 위치·테마, 검색 계산, 루틴 저장. 예시 데이터만.
/// </summary>
internal static partial class TourScenes
{
    public static readonly (string Key, int Count)[] AnimSets2 =
    {
        ("dock", 6), ("dock-style", 3), ("search-calc", 3), ("routine-save", 4),
    };

    public static BitmapSource? AnimFrame2(AppServices services, UiPalette light, UiPalette dark, string key, int i, List<string> problems)
    {
        // 독 위치·테마 3장째는 다크
        var p = key == "dock-style" && i == 2 ? dark : light;
        return new Scene(services, p, 1280, 800, 1.25).BuildAnim2(key, i, problems);
    }

    public static BitmapSource? Hero2(AppServices services, UiPalette p, List<string> problems)
        => new Scene(services, p, 1280, 800, 1.875).BuildHero2(problems);

    private sealed partial class Scene
    {
        public BitmapSource? BuildAnim2(string key, int i, List<string> problems)
        {
            _problems = problems;
            _cursorScale = 1.05;
            var (g, top) = NewScene(null);
            switch (key)
            {
                case "dock": AnimDock(g, top, i); break;
                case "dock-style": AnimDockStyle(g, top, i); break;
                case "search-calc": AnimSearchCalc(g, top, i); break;
                case "routine-save": AnimRoutineSave(g, top, i); break;
                default: return null;
            }
            return Render(g);
        }

        /// <summary>독·상단바가 주인공: 편집 메뉴 펼침 + 독 파도·이름표, 창은 둘만.</summary>
        public BitmapSource? BuildHero2(List<string> problems)
        {
            _problems = problems;
            _cursorScale = 1.05;
            var (g, top) = NewScene(null);
            var bar = ShotBar(true);
            g.Children.Add(bar);
            Put(g, Window("notes", 520, 470), 340, 132);
            Put(g, Window("browser", 560, 430), 610, 150);
            var d = ShotDock(g, running: new[] { "browser", "notes", "files" });
            int at = Index(d, "browser");
            Magnify(d.Views, at);
            Layout(g);
            // 상단바 편집 메뉴
            var edit = Need(ByUid(bar, "menu:" + Loc.T("편집")), "상단바 편집 메뉴")!;
            if (edit is Border eb) eb.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0, 0, 0)); // 펼친 메뉴 이름은 눌린 바탕
            var menu = Menu(new[] { Loc.T("실행 취소"), "-", Loc.T("잘라내기"), Loc.T("복사"), Loc.T("붙여넣기") }, Loc.T("복사"));
            var at2 = edit.TranslatePoint(new Point(0, edit.ActualHeight), g);
            Place(top, menu, at2.X - 6, at2.Y + 4);
            // 독 이름표 + 커서
            DockLabel(g, top, d, at);
            Cursor(top, Center(Need(d.Views[at], "독 브라우저 아이콘")!, g, 0.56, 0.62));
            return Render(g);
        }

        /// <summary>독 아이콘 위 이름표 (커진 아이콘 위).</summary>
        private void DockLabel(Grid g, Canvas top, DockParts d, int at)
        {
            var icon = d.Views[at];
            var tag = new Border { Background = _layout.LabelBackground, BorderBrush = _layout.LabelBorder, BorderThickness = new Thickness(0.5), CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 5), Child = new TextBlock { Text = d.Items[at].Name, Foreground = _layout.LabelForeground, FontSize = 13 } };
            tag.Measure(new Size(W, H));
            var head = icon.TranslatePoint(new Point(icon.ActualWidth / 2, 0), g);
            double grow = ((DockItemView)icon).Scale;
            Place(top, tag, head.X - tag.DesiredSize.Width / 2, head.Y - icon.ActualHeight * (grow - 1) - tag.DesiredSize.Height - 10);
        }

        // ── ② 독 ──

        private void AnimDock(Grid g, Canvas top, int f)
        {
            g.Children.Add(ShotBar(true));
            Put(g, Window("notes", 560, 520), 140, 90);
            if (f >= 4) Put(g, Window("mail", 440, 300), 720, 160);
            var d = ShotDock(g, running: new[] { "browser", "notes", "files", "chat" });
            switch (f)
            {
                case 0:
                {
                    // 독으로 다가감
                    Layout(g);
                    var c = Center(d.Views[Index(d, "notes")], g);
                    Cursor(top, new Point(c.X - 40, c.Y - 130));
                    break;
                }
                case 1:
                case 2:
                {
                    // 파도처럼 커짐 + 이름표 (② 메모, ③ 브라우저 + 창 미리 보기)
                    int at = Index(d, f == 1 ? "notes" : "browser");
                    Magnify(d.Views, at);
                    Layout(g);
                    if (f == 1) DockLabel(g, top, d, at);
                    else WindowPreviews(top, g, d.Views[at]);
                    Cursor(top, Center(Need(d.Views[at], "독 아이콘")!, g, 0.55, 0.6));
                    break;
                }
                case 3:
                {
                    // 실행 중 앱 오른쪽 클릭 → 독에 고정
                    int at = Index(d, "chat");
                    Layout(g);
                    var icon = Need(d.Views[at], "독 메신저 아이콘")!;
                    var menu = Menu(new[] { Loc.T("새 창"), "-", Loc.T("독에 고정"), Loc.T("창 닫기") }, Loc.T("독에 고정"));
                    menu.Measure(new Size(W, H));
                    var c = Center(icon, g);
                    Place(top, menu, c.X - menu.DesiredSize.Width / 2, icon.TranslatePoint(new Point(0, 0), g).Y - menu.DesiredSize.Height - 10);
                    Layout(g);
                    Cursor(top, Center(Need(MenuRow(menu, Loc.T("독에 고정")), "독에 고정 메뉴 줄")!, g, 0.35, 0.6));
                    break;
                }
                case 4:
                {
                    // 끌어서 순서 바꾸기: 사진을 브라우저 앞으로 — 빈자리가 열림
                    int from = Index(d, "photos"), to = Index(d, "browser");
                    var moving = d.Views[from];
                    ((Panel)moving.Parent).Children.Remove(moving);
                    var bar = (Panel)d.Views[0].Parent;
                    var gap = new Border { Width = _layout.IconSize + _layout.Spacing, Height = _layout.IconSize };
                    bar.Children.Insert(bar.Children.IndexOf(d.Views[to]), gap);
                    Layout(g);
                    var c = Center(Need(gap, "독 빈자리")!, g, 0.5, 0.3);
                    Cursor(top, new Point(c.X + 6, c.Y - 24), carry: IconImage(Icon("photos"), _layout.IconSize * 1.05));
                    break;
                }
                default:
                {
                    // 놓음: 새 순서
                    int from = Index(d, "photos"), to = Index(d, "browser");
                    var moving = d.Views[from];
                    var bar = (Panel)moving.Parent;
                    bar.Children.Remove(moving);
                    bar.Children.Insert(bar.Children.IndexOf(d.Views[to]), moving);
                    Layout(g);
                    var c = Center(moving, g);
                    Cursor(top, new Point(c.X + 30, c.Y - 90));
                    break;
                }
            }
        }

        /// <summary>창이 여러 개인 앱: 독 아이콘 위에 창 미리 보기 두 장 (예시 창을 작게).</summary>
        private void WindowPreviews(Canvas top, Grid g, FrameworkElement icon)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var title in new[] { K("시작하기 가이드", "Getting started"), K("백과사전", "Encyclopedia") })
            {
                var img = Snap(BrowserWindow(640, 420));
                img.Width = 200;
                img.Height = 200 * 420 / 640.0;
                var cell = new StackPanel { Margin = new Thickness(6) };
                cell.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = img });
                cell.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = _p.Text, Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, Width = 200 });
                row.Children.Add(cell);
            }
            var card = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(6),
                Background = _p.CardBackground,
                BorderBrush = _p.CardBorder,
                BorderThickness = new Thickness(0.5),
                Child = row,
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
            };
            card.Measure(new Size(W, H));
            var c = Center(icon, g);
            double grow = ((DockItemView)icon).Scale;
            Place(top, card, Math.Clamp(c.X - card.DesiredSize.Width / 2, 8, W - card.DesiredSize.Width - 8), icon.TranslatePoint(new Point(0, 0), g).Y - icon.ActualHeight * (grow - 1) - card.DesiredSize.Height - 12);
        }

        // ── ③ 독 위치·테마 ──

        private void AnimDockStyle(Grid g, Canvas top, int f)
        {
            g.Children.Add(ShotBar(true));
            Put(g, Window("notes", 520, 500), f == 1 ? 210 : 150, 100);
            Put(g, Window("music", 440, 230), f == 1 ? 760 : 700, 420);
            var d = MakeDock(running: new[] { "browser", "notes", "music" });
            if (f == 1)
            {
                // 왼쪽 세로 독
                ((StackPanel)d.Views[0].Parent).Orientation = Orientation.Vertical;
                d.Panel.HorizontalAlignment = HorizontalAlignment.Left;
                d.Panel.VerticalAlignment = VerticalAlignment.Center;
                d.Panel.Margin = new Thickness(8, 32, 0, 0);
            }
            else d.Panel.Margin = new Thickness(0, 0, 0, 8);
            g.Children.Add(d.Panel);
        }

        // ── ④ 검색 계산 ──

        private void AnimSearchCalc(Grid g, Canvas top, int f)
        {
            g.Children.Add(ShotBar(true));
            Put(g, Window("notes", 600, 560), 120, 96);
            ShotDock(g, running: new[] { "notes", "browser" });
            string typed = f == 0 ? "" : f == 1 ? "200*15" : "200*15%";
            var results = f < 2
                ? Array.Empty<(string, string, string)>()
                : new[] { ("calc", "30", K("200 × 15% · Enter 로 결과 복사", "200 × 15% · press Enter to copy")) };
            g.Children.Add(SearchCard(typed, results));
        }

        // ── ⑤ 루틴 저장 ──

        private void AnimRoutineSave(Grid g, Canvas top, int f)
        {
            g.Children.Add(ShotBar(true));
            Put(g, Window("notes", 520, 560), 24, 44);
            Put(g, Window("browser", 700, 380), 560, 44);
            Put(g, Window("music", 400, 240), 560, 440);
            var d = ShotDock(g, running: new[] { "browser", "notes", "music" });
            Layout(g);
            var panelBox = Bounds(d.Panel, g);
            if (f == 0)
            {
                Cursor(top, new Point(panelBox.Right - 30, panelBox.Top - 60));
                return;
            }
            if (f == 1)
            {
                // 독 빈 곳 오른쪽 클릭 → 지금 화면을 루틴으로 저장…
                var menu = Menu(new[] { Loc.T("지금 화면을 루틴으로 저장…"), Loc.T("새 루틴…"), "-", Loc.F($"{AppInfo.Name} 설정…"), "-", Loc.T("구분선 추가"), Loc.T("독에 폴더 추가…") }, Loc.T("지금 화면을 루틴으로 저장…"));
                menu.Measure(new Size(W, H));
                Place(top, menu, panelBox.Right - menu.DesiredSize.Width * 0.6, panelBox.Top - menu.DesiredSize.Height - 8);
                Layout(g);
                Cursor(top, Center(Need(MenuRow(menu, Loc.T("지금 화면을 루틴으로 저장…")), "루틴 저장 메뉴 줄")!, g, 0.4, 0.6));
                return;
            }
            // ③ 저장 카드에 지금 창들이 채워짐 ④ 편집 창
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            RoutineItem App(string exe, string name, RoutinePlacementMode place, double[]? rect = null) => new()
            {
                Kind = RoutineItemKind.App, Target = Path.Combine(sys, exe), Name = name,
                Monitor = new RoutineMonitor { Mode = RoutineMonitorMode.Index, Index = 1 },
                Placement = new RoutinePlacement { Mode = place, Rect = rect },
            };
            var items = new List<RoutineItem>
            {
                App("notepad.exe", K("메모장", "Notepad"), RoutinePlacementMode.Saved, new[] { 0.02, 0.06, 0.4, 0.7 }),
                new()
                {
                    Kind = RoutineItemKind.Url, Target = "https://learn.microsoft.com",
                    Tabs = new List<string> { "https://www.wikipedia.org", "https://github.com/mong-head/mongdock" },
                    Monitor = new RoutineMonitor { Mode = RoutineMonitorMode.Index, Index = 1 },
                    Placement = new RoutinePlacement { Mode = RoutinePlacementMode.Saved, Rect = new[] { 0.44, 0.06, 0.55, 0.48 } },
                },
                App("calc.exe", K("계산기", "Calculator"), RoutinePlacementMode.Saved, new[] { 0.44, 0.56, 0.31, 0.3 }),
            };
            Window? w = null;
            try
            {
                if (f == 2) w = RoutineEditorWindow.ForScreen(_services, items, false); // 지금 화면 저장 = 편집 창과 같은 화면 (10-23)
                else
                {
                    var def = new RoutineDef { Id = "site-example", Name = Loc.T("업무 시작"), Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "blue", Glyph = "" } };
                    def.Items.AddRange(items);
                    w = new RoutineEditorWindow(_services, def, def, false);
                }
                var img = Snap((FrameworkElement)w.Content);
                Put(g, img, (W - img.Width) / 2, Math.Max(40, (H - 100 - img.Height) / 2));
            }
            catch (Exception ex) { _problems?.Add($"루틴 저장 카드 실패: {ex.GetType().Name} {ex.Message}"); }
            finally { w?.Close(); }
        }
    }
}
