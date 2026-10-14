using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 홈페이지 그림 보강 (2026-10-14 사용자: "너무 대강, 홍보 느낌이 없다"):
/// 창 안을 그럴듯한 예시 내용으로(메모·브라우저·계산기·음악·사진·메일), 기능마다 움직이는 장면용 프레임(둘러보기 순서), 맨 위 대표 그림.
/// 글은 한국어·영어 두 벌, 실제 회사·개인 이름 없음.
/// </summary>
internal static partial class TourScenes
{
    /// <summary>움직이는 장면 (폴더 이름 → 장 수).</summary>
    public static readonly (string Key, int Count)[] AnimSets =
    {
        ("routine", 6), ("allapps", 5), ("stacks", 5), ("topbar", 4), ("notify", 5), ("search", 5),
    };

    public static BitmapSource? AnimFrame(AppServices services, UiPalette p, string key, int i, string exampleDir, List<string> problems)
        => new Scene(services, p, 1280, 800, 1.25).BuildAnim(key, i, exampleDir, problems);

    /// <summary>대표 그림: 1280×800 을 1.875배 = 2400×1500.</summary>
    public static BitmapSource? Hero(AppServices services, UiPalette p, string exampleDir, List<string> problems)
        => new Scene(services, p, 1280, 800, 1.875).BuildHero(problems);

    private sealed partial class Scene
    {
        /// <summary>예시 글: 한국어 원문, 다른 언어는 번역 파일(i18n)의 "site:" 키에서 — 없으면 영어.</summary>
        private static string K(string ko, string en)
        {
            if (Ko || ko.Length == 0) return ko;
            string key = "site:" + ko; // 그림 전용 키 (앱 문구 번역과 섞이지 않게)
            string t = Loc.T(key);
            return t != key ? t : en;
        }

        // ───────────────────────── 그럴듯한 창 ─────────────────────────

        /// <summary>창 틀: 제목 줄(앱 아이콘·제목·— □ ✕) + 내용.</summary>
        private Border Chrome(string app, string title, double w, double h, UIElement body, Brush? bodyBg = null)
        {
            var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            var bar = new DockPanel { Height = 34, LastChildFill = true };
            var caps = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var g in new[] { "", "", "" })
                caps.Children.Add(new TextBlock { Text = g, FontFamily = icons, FontSize = 10, Foreground = _p.SubText, Width = 40, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            DockPanel.SetDock(caps, System.Windows.Controls.Dock.Right);
            bar.Children.Add(caps);
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(IconImage(Icon(app), 16));
            left.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = _p.Text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            bar.Children.Add(left);
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bar, System.Windows.Controls.Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(new Border { Background = bodyBg ?? Brushes.Transparent, Child = body });
            return new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(_dark ? Color.FromRgb(0x2E, 0x2E, 0x34) : Color.FromRgb(0xF7, 0xF7, 0xF9)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Child = dock,
                ClipToBounds = true,
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.22 },
            };
        }

        private TextBlock T(string text, double size = 13, bool bold = false, Brush? color = null, double top = 0) => new()
        {
            Text = text,
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = color ?? _p.Text,
            Margin = new Thickness(0, top, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        private Brush White => new SolidColorBrush(_dark ? Color.FromRgb(0x24, 0x24, 0x28) : Colors.White);

        /// <summary>예시 앱 창 — app 마다 그럴듯한 내용.</summary>
        private Border Window(string app, double w, double h) => app switch
        {
            "notes" => NotesWindow(w, h),
            "browser" => BrowserWindow(w, h),
            "calc" => CalcWindow(w, h),
            "music" => MusicWindow(w, h),
            "photos" => PhotosWindow(w, h),
            "mail" => MailWindow(w, h),
            _ => MockWindow(app, w, h),
        };

        private Border NotesWindow(double w, double h)
        {
            var menu = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 6) };
            foreach (var m in new[] { K("파일", "File"), K("편집", "Edit"), K("보기", "View") })
                menu.Children.Add(new TextBlock { Text = m, FontSize = 12, Foreground = _p.SubText, Margin = new Thickness(6, 0, 10, 0) });
            var text = new StackPanel { Margin = new Thickness(18, 12, 18, 12) };
            string[] lines = new[] { K("주간 회의 메모 — 10월 14일", "Weekly meeting notes — Oct 14"), K("", ""), K("• 홈페이지 시안 검토 (수요일까지)", "• Review the homepage draft (by Wed)"), K("• 새 기능 소개 영상 대본 정리", "• Write the new-features video script"), K("• 다음 배포: 금요일 오후", "• Next release: Friday afternoon"), K("", ""), K("할 일", "To do"), K("  - 스크린샷 다시 그리기", "  - Redraw the screenshots"), K("  - 변경 내역 문구 확인", "  - Check the release notes"), K("  - 설치 안내 페이지 다듬기", "  - Polish the install guide"), K("", ""), K("메모", "Note"), K("  루틴은 '업무 시작' 하나로 아침 앱을 한 번에 연다.", "  One 'Start work' routine opens the morning apps at once.") };
            foreach (var l in lines)
                text.Children.Add(new TextBlock { Text = l.Length == 0 ? " " : l, FontSize = 13.5, Foreground = _p.Text, Margin = new Thickness(0, 2, 0, 2), FontWeight = l == lines[0] ? FontWeights.SemiBold : FontWeights.Normal });
            var body = new DockPanel();
            DockPanel.SetDock(menu, System.Windows.Controls.Dock.Top);
            body.Children.Add(menu);
            body.Children.Add(new Border { Background = White, BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0)), BorderThickness = new Thickness(0, 1, 0, 0), Child = text });
            return Chrome("notes", K("회의 메모.txt - 메모", "Meeting notes.txt - Notes"), w, h, body);
        }

        private Border BrowserWindow(double w, double h)
        {
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            string[] titles = { K("시작하기 가이드", "Getting started"), K("백과사전", "Encyclopedia"), K("프로젝트 노트", "Project notes") };
            for (int i = 0; i < titles.Length; i++)
                tabs.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8, 8, 0, 0),
                    Background = i == 0 ? White : Brushes.Transparent,
                    Padding = new Thickness(12, 6, 26, 6),
                    Margin = new Thickness(0, 0, 2, 0),
                    Child = new TextBlock { Text = titles[i], FontSize = 12, Foreground = i == 0 ? _p.Text : _p.SubText },
                });
            var address = new Border
            {
                Margin = new Thickness(10, 6, 10, 6),
                Padding = new Thickness(12, 5, 12, 5),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(_dark ? Color.FromRgb(0x3A, 0x3A, 0x40) : Color.FromRgb(0xEF, 0xEF, 0xF3)),
                Child = new TextBlock { Text = "docs.example.com/guide", FontSize = 12, Foreground = _p.SubText },
            };
            var page = new StackPanel { Margin = new Thickness(28, 18, 28, 12) };
            page.Children.Add(T(K("시작하기 가이드", "Getting started"), 22, true));
            page.Children.Add(T(K("몇 단계만 따라 하면 작업 공간을 준비할 수 있어요. 자주 쓰는 도구를 한곳에 모으고, 날마다 같은 순서로 여는 일을 줄여 보세요.", "A few steps get your workspace ready. Keep the tools you use most in one place, and stop opening them one by one every day."), 13, false, _p.SubText, 8));
            page.Children.Add(new Border
            {
                Height = Math.Max(40, h * 0.28),
                Margin = new Thickness(0, 14, 0, 14),
                CornerRadius = new CornerRadius(10),
                Background = new LinearGradientBrush(Color.FromRgb(0xB9, 0xC9, 0xF5), Color.FromRgb(0xE3, 0xD2, 0xF3), 20),
                Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 34, Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            page.Children.Add(T(K("1. 자주 쓰는 앱 고르기", "1. Pick the apps you use most"), 15, true));
            page.Children.Add(T(K("처음 며칠 동안 연 앱을 보고 추천해 드려요. 마음에 드는 것만 남기면 돼요.", "Suggestions come from what you open in the first few days. Keep only the ones you like."), 13, false, _p.SubText, 4));
            var body = new DockPanel();
            var head = new StackPanel();
            head.Children.Add(tabs);
            head.Children.Add(new Border { Background = White, Child = address });
            DockPanel.SetDock(head, System.Windows.Controls.Dock.Top);
            body.Children.Add(head);
            body.Children.Add(new Border { Background = White, Child = page });
            return Chrome("browser", titles[0], w, h, body);
        }

        private Border CalcWindow(double w, double h)
        {
            var grid = new UniformGrid { Columns = 4, Rows = 5, Margin = new Thickness(8, 4, 8, 8) };
            string[] keys = { "%", "CE", "C", "⌫", "7", "8", "9", "×", "4", "5", "6", "−", "1", "2", "3", "+", "±", "0", ".", "=" };
            foreach (var k in keys)
            {
                bool eq = k == "=";
                bool num = char.IsDigit(k[0]) || k is "." or "±";
                grid.Children.Add(new Border
                {
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(6),
                    Background = eq ? _p.AccentFill : new SolidColorBrush(num ? (_dark ? Color.FromRgb(0x3C, 0x3C, 0x42) : Colors.White) : (_dark ? Color.FromRgb(0x33, 0x33, 0x38) : Color.FromRgb(0xEE, 0xEE, 0xF2))),
                    Child = new TextBlock { Text = k, FontSize = 16, Foreground = eq ? Brushes.White : _p.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = num || eq ? FontWeights.SemiBold : FontWeights.Normal },
                });
            }
            var display = new StackPanel { Margin = new Thickness(16, 6, 16, 8) };
            display.Children.Add(new TextBlock { Text = "1,250 × 12 =", FontSize = 13, Foreground = _p.SubText, HorizontalAlignment = HorizontalAlignment.Right });
            display.Children.Add(new TextBlock { Text = "15,000", FontSize = 40, FontWeight = FontWeights.SemiBold, Foreground = _p.Text, HorizontalAlignment = HorizontalAlignment.Right });
            var body = new DockPanel();
            DockPanel.SetDock(display, System.Windows.Controls.Dock.Top);
            body.Children.Add(display);
            body.Children.Add(grid);
            return Chrome("calc", K("계산기", "Calculator"), w, h, body);
        }

        private Border MusicWindow(double w, double h)
        {
            var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            var row = new DockPanel { Margin = new Thickness(18) };
            var art = new Border { Width = Math.Min(h - 110, 150), Height = Math.Min(h - 110, 150), CornerRadius = new CornerRadius(10), Background = new LinearGradientBrush(Color.FromRgb(0xF4, 0x9A, 0xC1), Color.FromRgb(0x8C, 0x9E, 0xF4), 45) };
            DockPanel.SetDock(art, System.Windows.Controls.Dock.Left);
            row.Children.Add(art);
            var info = new StackPanel { Margin = new Thickness(18, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(T(K("아침 산책", "Morning Walk"), 18, true));
            info.Children.Add(T(K("예시 아티스트 · 조용한 오후", "Example Artist · Quiet Afternoon"), 12.5, false, _p.SubText, 2));
            var prog = new Grid { Margin = new Thickness(0, 16, 0, 4), Height = 4 };
            prog.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = _p.Divider });
            prog.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = _p.AccentFill, HorizontalAlignment = HorizontalAlignment.Left, Width = 90 });
            info.Children.Add(prog);
            var times = new DockPanel();
            var end = T("3:42", 11, false, _p.SubText);
            DockPanel.SetDock(end, System.Windows.Controls.Dock.Right);
            times.Children.Add(end);
            times.Children.Add(T("1:18", 11, false, _p.SubText));
            info.Children.Add(times);
            var ctl = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var (g, size) in new[] { ("", 16.0), ("", 26.0), ("", 16.0) })
                ctl.Children.Add(new TextBlock { Text = g, FontFamily = icons, FontSize = size, Foreground = _p.Text, Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center });
            info.Children.Add(ctl);
            row.Children.Add(info);
            return Chrome("music", K("음악", "Music"), w, h, row, White);
        }

        private Border PhotosWindow(double w, double h)
        {
            var grid = new UniformGrid { Columns = 4, Margin = new Thickness(12) };
            var pairs = new[]
            {
                (0xA8D0F0, 0xF3E1C7), (0xB7E4C7, 0x7FB8A4), (0xF6C6B5, 0xE88E8E), (0xCDB8F2, 0x8FA2E8),
                (0xF9E3A1, 0xF2B66D), (0x9FD8E8, 0x5EA0C9), (0xE3C7E8, 0xB28FD1), (0xC9E6B5, 0x90C97A),
            };
            foreach (var (a, b) in pairs)
                grid.Children.Add(new Border
                {
                    Margin = new Thickness(4),
                    Height = (w - 40) / 4 * 0.72,
                    CornerRadius = new CornerRadius(6),
                    Background = new LinearGradientBrush(Color.FromRgb((byte)(a >> 16), (byte)(a >> 8), (byte)a), Color.FromRgb((byte)(b >> 16), (byte)(b >> 8), (byte)b), 50),
                });
            var body = new StackPanel();
            body.Children.Add(T(K("  최근 항목 · 10월", "  Recent · October"), 13, true, null, 10));
            body.Children.Add(grid);
            return Chrome("photos", K("사진", "Photos"), w, h, body, White);
        }

        private Border MailWindow(double w, double h)
        {
            var list = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            var rows = new[] { (K("디", "D"), K("디자인 팀", "Design team"), K("홈페이지 시안 2차", "Homepage draft v2"), K("수정한 시안을 올렸어요. 금요일까지 의견 주세요.", "The updated draft is up. Comments by Friday, please."), K("10:12", "10:12")), (K("주", "W"), K("주간 소식", "Weekly digest"), K("이번 주 업데이트 정리", "This week's updates"), K("새 기능 세 가지와 고친 문제를 모았어요.", "Three new features and the fixes, in one place."), K("9:40", "9:40")), (K("일", "R"), K("일정 알림", "Reminders"), K("내일 오전 10시 회의", "Meeting tomorrow at 10 AM"), K("회의실 2 · 30분", "Room 2 · 30 min"), K("어제", "Yesterday")), (K("도", "L"), K("도서관", "Library"), K("예약한 책이 도착했어요", "Your hold has arrived"), K("이번 주 안에 찾아가세요.", "Pick it up this week."), K("월", "Mon")) };
            var avatar = new[] { Color.FromRgb(0x8C, 0x7E, 0xEE), Color.FromRgb(0x4F, 0x9D, 0xF0), Color.FromRgb(0x2E, 0xB6, 0x7D), Color.FromRgb(0xF2, 0x9B, 0x38) };
            for (int i = 0; i < rows.Length; i++)
            {
                var (_, from, subj, prev, time) = rows[i];
                string ini = from.Length > 0 ? from[..1].ToUpperInvariant() : "?";
                var row = new DockPanel { Margin = new Thickness(14, 8, 14, 8) };
                var av = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(avatar[i]), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = ini, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                DockPanel.SetDock(av, System.Windows.Controls.Dock.Left);
                row.Children.Add(av);
                var t = T(time, 11, false, _p.SubText);
                DockPanel.SetDock(t, System.Windows.Controls.Dock.Right);
                row.Children.Add(t);
                var col = new StackPanel();
                col.Children.Add(T(from, 13, i == 0));
                col.Children.Add(T(subj, 12.5, i == 0, null, 1));
                col.Children.Add(new TextBlock { Text = prev, FontSize = 12, Foreground = _p.SubText, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                row.Children.Add(col);
                list.Children.Add(new Border { Background = i == 0 ? _p.PanelTint : Brushes.Transparent, CornerRadius = new CornerRadius(8), Margin = new Thickness(6, 1, 6, 1), Child = row });
            }
            var body = new StackPanel();
            body.Children.Add(T(K("  받은편지함", "  Inbox"), 15, true, null, 8));
            body.Children.Add(list);
            return Chrome("mail", K("메일", "Mail"), w, h, body, White);
        }

        // ───────────────────────── 움직이는 장면 ─────────────────────────

        public BitmapSource? BuildAnim(string key, int i, string exampleDir, List<string> problems)
        {
            _problems = problems;
            _cursorScale = 1.05;
            var (g, top) = NewScene(null);
            switch (key)
            {
                case "routine": AnimRoutine(g, top, i); break;
                case "allapps": AnimAllApps(g, top, i); break;
                case "stacks": AnimStacks(g, top, i, exampleDir); break;
                case "topbar": AnimTopBar(g, top, i); break;
                case "notify": AnimNotify(g, top, i); break;
                case "search": AnimSearch(g, top, i); break;
                default: return null;
            }
            return Render(g);
        }

        public BitmapSource? BuildHero(List<string> problems)
        {
            _problems = problems;
            var (g, _) = NewScene(null);
            RoutineDesktop(g, 3, time: true);
            return Render(g);
        }

        /// <summary>루틴이 연 데스크톱: 메모(왼쪽)·브라우저(오른쪽 위)·계산기와 메일(오른쪽 아래), opened 개까지.</summary>
        private DockParts RoutineDesktop(Grid g, int opened, bool time, bool pill = true)
        {
            g.Children.Add(ShotBar(true, pill ? $"‹   {Loc.T("업무 시작")} · 2/2{(time ? " · 0:12" : "")}   ›" : null));
            if (opened >= 1) Put(g, Window("notes", 560, 646), 18, 44);
            if (opened >= 2) Put(g, Window("browser", 682, 400), 590, 44);
            if (opened >= 3)
            {
                Put(g, Window("music", 360, 236), 590, 456);
                Put(g, Window("mail", 310, 236), 962, 456);
            }
            return ShotDock(g, running: opened >= 1 ? new[] { "browser", "notes" } : null, routine: Loc.T("업무 시작"));
        }

        private void AnimRoutine(Grid g, Canvas top, int f)
        {
            if (f <= 1)
            {
                // ① 빈 데스크톱, 커서가 독 루틴으로 ② 누름
                g.Children.Add(ShotBar(false));
                var d = ShotDock(g, routine: Loc.T("업무 시작"));
                int at = Index(d, "routine");
                if (f == 1) Magnify(d.Views, at);
                Layout(g);
                var icon = Need(d.Views[at], "독 루틴 아이콘")!;
                var c = Center(icon, g, 0.55, 0.55);
                Cursor(top, f == 0 ? new Point(c.X - 120, c.Y - 150) : c, pressed: f == 1);
                return;
            }
            // ③~⑥ 창이 차례로 제자리에, 마지막에 머문 시간
            var dock = RoutineDesktop(g, Math.Min(3, f - 1), time: f == 5);
            Layout(g);
            var r = Need(dock.Views[Index(dock, "routine")], "독 루틴 아이콘")!;
            Cursor(top, f == 5 ? new Point(W * 0.5, H * 0.62) : Center(r, g, 0.6, 0.2));
        }

        private void AnimAllApps(Grid g, Canvas top, int f)
        {
            g.Children.Add(ShotBar(false));
            var d = ShotDock(g, running: new[] { "browser", "notes" });
            int at = Index(d, "allapps");
            if (f == 0)
            {
                Magnify(d.Views, at);
                Layout(g);
                Cursor(top, Center(Need(d.Views[at], "독 앱 모음 아이콘")!, g, 0.55, 0.55), pressed: true);
                return;
            }
            string mode = f switch { 3 => "folder", 4 => "unused", _ => "all" };
            var (panel, content) = MakePanel(mode, site: true, size: (720, 640));
            try
            {
                if (mode == "unused") ScrollToUnused(panel, content);
                var img = Snap(content);
                img.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.22 };
                double x = (W - img.Width) / 2, y = Math.Max(40, (H - 32 - 90 - img.Height) / 2 + 32);
                Layout(g);
                var icon = Need(d.Views[at], "독 앱 모음 아이콘")!;
                var ic = Center(icon, g);
                if (f == 1)
                {
                    // 판이 아이콘 쪽에서 커지며 열림 (중간 모습)
                    img.RenderTransformOrigin = new Point((ic.X - x) / img.Width, 1);
                    img.RenderTransform = new ScaleTransform(0.55, 0.55);
                    img.Opacity = 0.75;
                }
                Put(g, img, x, y);
                Layout(g);
                if (f == 1) Cursor(top, new Point(ic.X + 10, ic.Y - 40));
                else if (f == 2) Cursor(top, new Point(x + img.Width * 0.62, y + img.Height * 0.4));
                else if (f == 3 && panel.SceneOpenFolderTile() is { } tile)
                {
                    var b = Bounds(tile, content);
                    Cursor(top, new Point(x + b.X + b.Width * 0.55, y + b.Y + b.Height * 0.5 - ScrollOffset(content)));
                }
                else if (f == 4 && panel.SceneSelectionBar is { } bar)
                {
                    var hide = Descendants(bar).OfType<Button>().FirstOrDefault();
                    if (hide is not null)
                    {
                        var b = Bounds(hide, content);
                        Cursor(top, new Point(x + b.X + b.Width * 0.5, y + b.Y + b.Height * 0.6));
                    }
                }
            }
            finally { panel.Close(); }
        }

        private static double ScrollOffset(FrameworkElement content) =>
            Descendants(content).OfType<ScrollViewer>().OrderByDescending(x => x.ActualHeight).FirstOrDefault()?.VerticalOffset ?? 0;

        /// <summary>잠자는 앱 목록이 보이게 판 안을 스크롤.</summary>
        private void ScrollToUnused(AllAppsPanel panel, FrameworkElement content)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();
            var first = panel.SceneUnusedRows().FirstOrDefault(r => r.ActualWidth > 0);
            var sv = Descendants(content).OfType<ScrollViewer>().OrderByDescending(x => x.ActualHeight).FirstOrDefault();
            if (first is null || sv is null) { _problems?.Add("잠자는 앱 줄을 못 찾음"); return; }
            double y = first.TranslatePoint(new Point(0, 0), sv).Y + sv.VerticalOffset;
            var tile = panel.SceneFolderTiles().FirstOrDefault(t => t.ActualWidth > 0);
            double folders = tile is null ? y - 120 : tile.TranslatePoint(new Point(0, 0), sv).Y + sv.VerticalOffset - 44;
            sv.ScrollToVerticalOffset(Math.Max(0, Math.Max(folders, y - sv.ViewportHeight + 260)));
            content.UpdateLayout();
        }

        private void AnimStacks(Grid g, Canvas top, int f, string exampleDir)
        {
            g.Children.Add(ShotBar(false));
            string folderName = K("받은 파일", "Received files");
            string dir = Path.Combine(exampleDir, folderName);
            MakeExampleFolder(dir);
            var d = MakeDock(withStacks: true, trashFull: f == 4, running: new[] { "files" }, folderName: folderName);
            d.Panel.Margin = new Thickness(0, 0, 0, 8);
            g.Children.Add(d.Panel);
            int fi = Index(d, "downloads"), ti = Index(d, "trash");
            if (f == 3) Magnify(d.Views, ti);
            Layout(g);
            var folder = Need(d.Views[fi], "독 예시 폴더")!;
            var trash = Need(d.Views[ti], "독 휴지통")!;
            var fb = Bounds(folder, g);
            Image FileImage() => IconImage(DockFolderService.Thumbnail(Path.Combine(dir, K("사진1.png", "Photo1.png")), 96) ?? Icon("photos"), 52);
            switch (f)
            {
                case 0:
                    Cursor(top, Center(folder, g, 0.55, 0.55), pressed: true);
                    break;
                case 1:
                    var pin = new PinItem { Kind = PinKind.Folder, Target = dir, Name = folderName, Id = "site-example", Folder = new FolderOptions { Sort = FolderSort.Name } };
                    FolderStackPanel? w = null;
                    try
                    {
                        DockStackPanel.ThumbsNow = true;
                        w = new FolderStackPanel(_services, _p, pin, fb, DockEdge.Bottom, _services.DesktopWindows.ResolveMonitor(""));
                        var img = Snap((FrameworkElement)w.Content);
                        double x = Math.Clamp(fb.Left + fb.Width / 2 - img.Width / 2, 8, W - img.Width - 8), y = fb.Top - img.Height - 6;
                        Put(g, img, x, y);
                        Cursor(top, new Point(x + img.Width * 0.52, y + img.Height * 0.42));
                    }
                    catch (Exception ex) { _problems?.Add($"독 폴더 판 실패: {ex.GetType().Name}"); }
                    finally { DockStackPanel.ThumbsNow = false; w?.Close(); }
                    break;
                case 2:
                    var tc = Center(trash, g);
                    Cursor(top, new Point((fb.Left + tc.X) / 2 - 40, fb.Top - 110), carry: FileImage());
                    break;
                case 3:
                    Cursor(top, Center(trash, g, 0.5, 0.35), carry: FileImage());
                    break;
                default:
                    Cursor(top, new Point(Center(trash, g).X + 70, fb.Top - 90));
                    break;
            }
        }

        private void AnimTopBar(Grid g, Canvas top, int f)
        {
            var bar = ShotBar(true);
            g.Children.Add(bar);
            Put(g, Window("notes", 600, 540), 120, 96);
            Put(g, Window("music", 470, 220), 640, 470);
            ShotDock(g, running: new[] { "notes", "browser" });
            Layout(g);
            if (f == 0)
            {
                var edit = Need(ByUid(bar, "menu:" + Loc.T("편집")), "상단바 편집 메뉴")!;
                Cursor(top, new Point(Center(edit, g).X + 60, 140));
                return;
            }
            if (f == 1)
            {
                var edit = Need(ByUid(bar, "menu:" + Loc.T("편집")), "상단바 편집 메뉴")!;
                var menu = Menu(new[] { Loc.T("실행 취소"), "-", Loc.T("잘라내기"), Loc.T("복사"), Loc.T("붙여넣기") }, Loc.T("복사"));
                var at = edit.TranslatePoint(new Point(0, edit.ActualHeight), g);
                Place(top, menu, at.X - 6, at.Y + 4);
                Layout(g);
                Cursor(top, Center(Need(MenuRow(menu, Loc.T("복사")), "복사 메뉴 줄")!, g, 0.4, 0.6));
                return;
            }
            var kind = f == 2 ? StatusPanelKind.ControlCenter : StatusPanelKind.Calendar;
            AppServices? sv = null;
            if (f == 3) sv = _services with { Notifications = SceneNotifications() };
            var img = StatusPanel(kind, out var size, sv);
            var anchor = Need(ByUid(bar, f == 2 ? "wifi" : "clock"), "상단바 아이콘")!;
            if (img is not null)
            {
                var pic = new Border
                {
                    Width = size.Width,
                    Height = Math.Min(size.Height, H - 48),
                    ClipToBounds = true,
                    Child = new Image { Source = img, Width = size.Width, Height = size.Height, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Top },
                    Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
                };
                Put(g, pic, W - size.Width - 8, 36);
            }
            Cursor(top, Center(anchor, g, 0.5, 0.55), pressed: true);
        }

        /// <summary>예시 알림 (알람 및 시계 · PowerShell 예시만).</summary>
        private NotificationService SceneNotifications()
        {
            var notes = new NotificationService(_services.Windows, _services.Launcher);
            const string clock = "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App";
            const string ps = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";
            var now = DateTime.Now;
            notes.UseForScene(new List<NotificationItem>
            {
                new(3, clock, K("알람 및 시계", "Clock"), K("타이머", "Timer"), new[] { K("5분 타이머가 끝났어요.", "Your 5-minute timer is done.") }, now.AddMinutes(-2), null, null, false, null),
                new(2, ps, "Windows PowerShell", K("예시 알림", "Example notification"), new[] { K("몽독 알림 목록 예시예요. 긴 알림은 눌러서 끝까지 펼쳐 볼 수 있어요.", "This is an example in the mongdock notification list. Click a long notification to read all of it.") }, now.AddMinutes(-9), null, null, false, null),
            });
            return notes;
        }

        private void AnimNotify(Grid g, Canvas top, int f)
        {
            g.Children.Add(ShotBar(true));
            Put(g, Window("notes", 600, 560), 140, 90);
            var d = MakeDock(running: new[] { "notes", "browser", "chat" }, notify: f >= 2 ? "chat" : null);
            d.Panel.Margin = new Thickness(0, 0, 0, 8);
            g.Children.Add(d.Panel);
            int at = Index(d, "chat");
            var icon = d.Views[at];
            if (f is 1 or 3) icon.RenderTransform = new TranslateTransform(0, -_layout.IconSize * (f == 1 ? 0.45 : 0.3)); // 두 번 튐 (두 번째는 조금 낮게)
            Layout(g);
            if (f <= 3)
            {
                // 배너: ① 들어오는 중 ②~④ 떠 있음
                var item = new NotificationItem(0, "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe", "Windows PowerShell", K("예시 알림", "Example notification"), new[] { K("작업이 끝났어요. 결과를 확인해 보세요.", "The task is done. Take a look at the result.") }, DateTime.Now, null, null, false, null);
                Window? w = null;
                try
                {
                    (w, var content) = NotificationBannerWindow.ForScene(_services, _p, item, null, Array.Empty<NotificationBannerWindow.BannerButton>());
                    var img = Snap(content);
                    if (f == 0) { img.Opacity = 0.55; img.RenderTransform = new TranslateTransform(img.Width * 0.45, 0); }
                    Put(g, img, W - img.Width - 4, 34);
                }
                catch (Exception ex) { _problems?.Add($"알림 배너 실패: {ex.GetType().Name}"); }
                finally { w?.Close(); }
            }
            if (f == 4) Cursor(top, new Point(Center(Need(icon, "독 메신저 아이콘")!, g).X - 60, H - 170));
        }

        /// <summary>
        /// 둘러보기 검색 단계 (0.5.1, 사용자 의견 반영): 바탕 화면 + 상단바 + 독 + 검색창만 (앱 창 없음).
        /// ① 지금 설정한 단축키(없으면 돋보기 키) ② 「사진」 몇 글자 → 사진 앱과 사진 폴더가 한 목록에.
        /// </summary>
        public BitmapSource? BuildTourSearch(int i, List<string>? problems, string[]? keys)
        {
            _problems = problems;
            _cursorScale = 1.05;
            var (g, _) = NewScene(null);
            var bar = ShotBar(false);
            // 창이 없는 바탕 화면 — 상단바 앱 이름은 실제처럼 파일 탐색기
            if (Descendants(bar).OfType<TextBlock>().FirstOrDefault(t => t.Uid == "app") is { } appName) appName.Text = DefaultPins.ExplorerName;
            g.Children.Add(bar);
            ShotDock(g);
            if (i == 0) { g.Children.Add(KeyPlate(keys ?? new[] { "" })); return Render(g); }
            // ② 한 검색어에 앱과 폴더가 같이 (실제 검색창처럼 종류가 섞인 목록): 「사진」 → 사진 앱 + 사진 폴더
            string name = Loc.T("사진");
            int part = Ko ? 2 : 4; // 몇 글자만 쳐도 찾음 (한국어는 「사진」, 다른 언어는 앞 4글자)
            string typed = name.Length <= part ? name : name[..part];
            g.Children.Add(SearchCard(typed, new[]
            {
                ("photos", name, K("앱 · Enter 로 열기", "App · press Enter to open")),
                ("folder", name, K("폴더", "Folder")),
            }, null, (1, MacIconRenderer.Folder(FolderGlyph.Pictures))));
            return Render(g);
        }

        /// <summary>지금 Spotlight 단축키를 누르는 키 이름 (Win·Space 등). 단축키 없음이면 null (상단바 검색 버튼을 누르는 그림).</summary>
        private static string[]? Hotkey(AppServices services)
        {
            SpotlightHotkey key;
            try { key = SpotlightHotkeyController.Resolve(services.Settings.Current.TopBar.SpotlightHotkey); }
            catch { key = services.Settings.Current.TopBar.SpotlightHotkey; }
            return key switch
            {
                SpotlightHotkey.WinSpace => new[] { "Win", "Space" },
                SpotlightHotkey.AltSpace => new[] { "Alt", "Space" },
                SpotlightHotkey.CtrlSpace => new[] { "Ctrl", "Space" },
                _ => null,
            };
        }

        /// <summary>키캡 판 (Win + Space 등, 글자 하나가 아이콘 글꼴 영역이면 돋보기 같은 기호 키).</summary>
        private Border KeyPlate(string[] parts)
        {
            // 단축키 (기본 Win + Space — 둘러보기는 지금 설정한 키). 단축키 없음 = 돋보기 키 하나 (상단바 검색 버튼)
            var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var keyInk = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30));
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) keys.Children.Add(new TextBlock { Text = "+", FontSize = 26, Foreground = Brushes.White, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
                bool glyph = parts[i].Length == 1 && parts[i][0] >= '';
                bool wide = i == parts.Length - 1 && parts.Length > 1; // Space 키는 넓게
                var label = new TextBlock { Text = parts[i], FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = keyInk };
                if (glyph) label.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
                keys.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(wide ? 54 : 22, 12, wide ? 54 : 22, 12), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), BorderThickness = new Thickness(1, 1, 1, 3), Child = label });
            }
            var plate = new Border { CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x37, 0x4C)), Padding = new Thickness(26, 18, 26, 18), Child = keys, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.25 } };
            return plate;
        }

        private void AnimSearch(Grid g, Canvas top, int f, string[]? hotkey = null)
        {
            g.Children.Add(ShotBar(true));
            Put(g, Window("notes", 600, 560), 120, 96);
            if (f == 4) Put(g, Window("calc", 320, 470), 760, 110);
            ShotDock(g, running: f == 4 ? new[] { "notes", "browser", "calc" } : new[] { "notes", "browser" });
            if (f == 0)
            {
                g.Children.Add(KeyPlate(hotkey ?? new[] { "Win", "Space" }));
                return;
            }
            if (f == 4) return;
            string typed = f switch { 1 => "", 2 => K("ㄱㅅ", "ca"), _ => K("ㄱㅅㄱ", "calc") };
            var results = f switch
            {
                1 => new (string Key, string Name, string Sub)[0],
                2 => new[] { ("calc", K("계산기", "Calculator"), K("앱", "App")), ("calendar", K("캘린더", "Calendar"), K("앱", "App")) },
                _ => new[] { ("calc", K("계산기", "Calculator"), K("앱 · Enter 로 열기", "App · press Enter to open")) },
            };
            g.Children.Add(SearchCard(typed, results));
        }

        /// <summary>Spotlight 같은 검색 창 모양 (예시 결과만 — 이 PC 앱 목록을 쓰지 않음).</summary>
        private Border SearchCard(string typed, (string Key, string Name, string Sub)[] results, ImageSource? firstIcon = null, (int Row, ImageSource Icon)? rowIcon = null)
        {
            var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            var field = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
            var mag = new TextBlock { Text = "", FontFamily = icons, FontSize = 20, Foreground = _p.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(mag, System.Windows.Controls.Dock.Left);
            field.Children.Add(mag);
            var input = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (typed.Length > 0) input.Children.Add(new TextBlock { Text = typed, FontSize = 22, Foreground = _p.Text });
            else input.Children.Add(new TextBlock { Text = K("앱·파일·설정 검색", "Search apps, files and settings"), FontSize = 22, Foreground = _p.SubText });
            input.Children.Insert(typed.Length > 0 ? 1 : 0, new Border { Width = 2, Height = 26, Background = _p.AccentFill, Margin = new Thickness(2, 0, 2, 0) });
            field.Children.Add(input);
            var stack = new StackPanel();
            stack.Children.Add(field);
            if (results.Length > 0)
            {
                stack.Children.Add(new Border { Height = 1, Background = _p.Divider });
                var list = new StackPanel { Margin = new Thickness(8) };
                for (int i = 0; i < results.Length; i++)
                {
                    var (key, name, sub) = results[i];
                    var row = new DockPanel { Margin = new Thickness(10, 7, 10, 7) };
                    var ic = IconImage(rowIcon is { } ri && ri.Row == i ? ri.Icon : i == 0 && firstIcon is not null ? firstIcon : Icon(key), 32);
                    ic.Margin = new Thickness(0, 0, 12, 0);
                    DockPanel.SetDock(ic, System.Windows.Controls.Dock.Left);
                    row.Children.Add(ic);
                    var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    col.Children.Add(T(name, 15, true));
                    col.Children.Add(T(sub, 12, false, _p.SubText));
                    row.Children.Add(col);
                    list.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = i == 0 ? _p.PanelTint : Brushes.Transparent, Child = row });
                }
                stack.Children.Add(list);
            }
            return new Border
            {
                Width = 620,
                CornerRadius = new CornerRadius(16),
                Background = _p.CardBackground,
                BorderBrush = _p.CardBorder,
                BorderThickness = new Thickness(0.5),
                Child = stack,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 170, 0, 0),
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.25 },
            };
        }
    }
}
