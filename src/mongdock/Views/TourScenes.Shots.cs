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
/// 홈페이지 스크린샷 (site-draft/shots.md 8장, report-test --shots): 둘러보기 장면과 같은 코드로 실제 화면처럼 그림.
/// 1280×800 화면을 1.25배(1600×1000)로, 몽독 기본 바탕(그라데이션) 위에 실제 크기의 상단바·독·판·창.
/// 사용자 앱·이름은 없음 — 예시 앱(기호 아이콘), 윈도우 기본 앱(메모장·계산기·Edge 링크), 예시 폴더·예시 알림(알람 및 시계·PowerShell)만.
/// </summary>
internal static partial class TourScenes
{
    public static readonly string[] ShotNames =
    {
        "01-routine-open", "02-routine-editor", "03-routine-ask", "04-all-apps",
        "05-sleeping-apps", "06-dock-folder", "07-menu-bar", "08-calendar",
    };

    /// <summary>홈페이지 그림 한 장. exampleDir = 예시 폴더를 만들 곳 (시험 폴더). 문제는 problems 에.</summary>
    public static BitmapSource? Shot(AppServices services, UiPalette p, string name, string exampleDir, List<string> problems)
        => new Scene(services, p, 1280, 800, 1.25).BuildShot(name, exampleDir, problems);

    private sealed partial class Scene
    {
        private static bool Ko => Loc.Code == "ko";

        public BitmapSource? BuildShot(string name, string exampleDir, List<string> problems)
        {
            _problems = problems;
            var (g, _) = NewScene(null);
            switch (name)
            {
                case "01-routine-open": ShotRoutineOpen(g); break;
                case "02-routine-editor": ShotRoutineEditor(g); break;
                case "03-routine-ask": ShotRoutineAsk(g); break;
                case "04-all-apps": ShotPanel(g, "all"); break;
                case "05-sleeping-apps": ShotPanel(g, "unused"); break;
                case "06-dock-folder": ShotDockFolder(g, exampleDir); break;
                case "07-menu-bar": ShotMenuBar(g); break;
                case "08-calendar": ShotCalendar(g); break;
                default: return null;
            }
            return Render(g);
        }

        // ── 공통 ──

        /// <summary>실제 크기 상단바. pill = 가운데 데스크톱 알약 글자 (루틴 데스크톱).</summary>
        private Grid ShotBar(bool menus = true, string? pill = null)
        {
            var bar = Bar(1, menus);
            if (pill is not null)
                bar.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(11),
                    Padding = new Thickness(12, 3, 12, 4),
                    Background = new SolidColorBrush(_dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0, 0, 0)),
                    Child = new TextBlock { Text = pill, FontSize = 13, Foreground = Ink },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            return bar;
        }

        /// <summary>실제 크기 독 (화면 아래 가운데, 항상 보이기).</summary>
        private DockParts ShotDock(Grid g, bool withStacks = false, string[]? running = null, string? routine = null, string? folderName = null, string? notify = null)
        {
            var d = MakeDock(withStacks, false, running, routine, folderName, notify);
            d.Panel.Margin = new Thickness(0, 0, 0, 8);
            g.Children.Add(d.Panel);
            return d;
        }

        private void Put(Grid g, FrameworkElement el, double x, double y)
        {
            el.HorizontalAlignment = HorizontalAlignment.Left;
            el.VerticalAlignment = VerticalAlignment.Top;
            el.Margin = new Thickness(x, y, 0, 0);
            g.Children.Add(el);
        }

        /// <summary>창 내용(띄우지 않은 창)을 그대로 그림으로 — 창의 글꼴·색을 그대로 물려받음. region = 그 안의 일부.</summary>
        private Image Snap(FrameworkElement content, Rect? region = null)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            content.UpdateLayout();
            var size = content.DesiredSize; // 바깥 여백(그림자 자리) 포함
            if (size.Width < 4 || size.Height < 4) { _problems?.Add("창 내용이 비어 있음"); size = new Size(4, 4); }
            // 통째로 그림 (여백 자리도 그대로 — VisualBrush 는 여백만큼 밀려 오른쪽·아래가 잘렸음)
            var whole = new RenderTargetBitmap((int)Math.Ceiling(size.Width * Scale), (int)Math.Ceiling(size.Height * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
            whole.Render(content);
            BitmapSource src = whole;
            var r = new Rect(size);
            if (region is { } want)
            {
                want.Intersect(r);
                if (!want.IsEmpty && want.Width >= 4 && want.Height >= 4)
                {
                    r = want;
                    src = new CroppedBitmap(whole, new Int32Rect((int)(r.X * Scale), (int)(r.Y * Scale), Math.Min(whole.PixelWidth - (int)(r.X * Scale), (int)Math.Ceiling(r.Width * Scale)), Math.Min(whole.PixelHeight - (int)(r.Y * Scale), (int)Math.Ceiling(r.Height * Scale))));
                }
            }
            src.Freeze();
            return new Image { Source = src, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
        }


        // ── 1. 루틴이 열린 모습 ──

        private void ShotRoutineOpen(Grid g) => RoutineDesktop(g, 3, time: true);

        // ── 2. 루틴 편집 창 ──

        private void ShotRoutineEditor(Grid g)
        {
            g.Children.Add(ShotBar(false));
            ShotDock(g, running: new[] { "browser", "notes" }, routine: Loc.T("업무 시작"));
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            RoutineItem App(string exe, string name, RoutinePlacementMode place) => new()
            {
                Kind = RoutineItemKind.App, Target = Path.Combine(sys, exe), Name = name,
                Monitor = new RoutineMonitor { Mode = RoutineMonitorMode.Index, Index = 1 },
                Placement = new RoutinePlacement { Mode = place },
            };
            var def = new RoutineDef
            {
                Id = "site-example",
                Name = Loc.T("업무 시작"),
                Icon = new PinIcon { Mode = PinIconMode.Glyph, Color = "blue", Glyph = "" },
                Items =
                {
                    App("notepad.exe", K("메모장", "Notepad"), RoutinePlacementMode.Left),
                    new RoutineItem
                    {
                        Kind = RoutineItemKind.Url, Target = "https://learn.microsoft.com",
                        Tabs = new List<string> { "https://www.wikipedia.org", "https://github.com/mong-head/mongdock" },
                        Monitor = new RoutineMonitor { Mode = RoutineMonitorMode.Index, Index = 1 },
                        Placement = new RoutinePlacement { Mode = RoutinePlacementMode.Right },
                    },
                    App("calc.exe", K("계산기", "Calculator"), RoutinePlacementMode.Saved),
                },
            };
            def.Items[2].Placement!.Rect = new[] { 0.55, 0.5, 0.4, 0.45 };
            RoutineEditorWindow? w = null;
            try
            {
                w = new RoutineEditorWindow(_services, def, def, false);
                var img = Snap((FrameworkElement)w.Content);
                double maxH = H - 32 - 96;
                if (img.Height > maxH) { _problems?.Add($"편집 창이 화면보다 큼 ({img.Height:0} > {maxH:0})"); }
                Put(g, img, (W - img.Width) / 2, 44);
            }
            catch (Exception ex) { _problems?.Add($"편집 창 그리기 실패: {ex.GetType().Name} {ex.Message}"); }
            finally { w?.Close(); }
        }

        // ── 3. "열까요?" 묻기 배너 ──

        private void ShotRoutineAsk(Grid g)
        {
            g.Children.Add(ShotBar(true));
            Put(g, Window("notes", 560, 470), 150, 110);
            ShotDock(g, running: new[] { "notes" }, routine: Loc.T("업무 시작"));
            var item = new NotificationItem(0, "", AppInfo.Name, Loc.F($"{Loc.T("업무 시작")} 루틴을 열까요?"), new[] { Loc.F($"{Loc.T("헤드셋")}를 연결하면") }, DateTime.Now, null, null, false, null);
            var buttons = new List<NotificationBannerWindow.BannerButton>
            {
                new(Loc.T("열기"), () => { }, Primary: true),
                new(Loc.T("오늘은 안 함"), () => { }),
                new(Loc.T("다시 묻지 않기"), () => { }, Small: true),
            };
            Window? w = null;
            try
            {
                (w, var content) = NotificationBannerWindow.ForScene(_services, _p, item, MacIconRenderer.Symbol("blue", "", null), buttons);
                var img = Snap(content);
                Put(g, img, W - img.Width - 4, 34);
            }
            catch (Exception ex) { _problems?.Add($"묻기 배너 그리기 실패: {ex.GetType().Name} {ex.Message}"); }
            finally { w?.Close(); }
        }

        // ── 4·5. 앱 모음 판 / 잠자는 앱 ──

        private void ShotPanel(Grid g, string mode)
        {
            g.Children.Add(ShotBar(false));
            var d = ShotDock(g, running: new[] { "browser", "notes" });
            var (panel, content) = MakePanel(mode, site: true, size: (720, 640));
            try
            {
                if (mode == "all") Need(panel.SceneFavRow, "즐겨찾기 줄");
                else
                {
                    // 잠자는 앱 목록이 보이게 판 안을 스크롤 (맨 위 루틴·즐겨찾기 대신)
                    content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    content.Arrange(new Rect(content.DesiredSize));
                    content.UpdateLayout();
                    var first = panel.SceneUnusedRows().FirstOrDefault(r => r.ActualWidth > 0);
                    var sv = Descendants(content).OfType<ScrollViewer>().OrderByDescending(x => x.ActualHeight).FirstOrDefault();
                    if (first is null || sv is null) _problems?.Add("잠자는 앱 줄을 못 찾음");
                    else
                    {
                        double y = first.TranslatePoint(new Point(0, 0), sv).Y + sv.VerticalOffset;
                        // 폴더 줄부터 보이게 (위 칸이 반쯤 걸리지 않게), 단 잠자는 앱 줄이 판 아래로 넘지 않게
                        var tile = panel.SceneFolderTiles().FirstOrDefault(t => t.ActualWidth > 0);
                        double folders = tile is null ? y - 120 : tile.TranslatePoint(new Point(0, 0), sv).Y + sv.VerticalOffset - 44;
                        sv.ScrollToVerticalOffset(Math.Max(0, Math.Max(folders, y - sv.ViewportHeight + 260)));
                        content.UpdateLayout();
                    }
                }
                var img = Snap(content);
                img.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.22 };
                Put(g, img, (W - img.Width) / 2, Math.Max(40, (H - 32 - 90 - img.Height) / 2 + 32));
            }
            finally { panel.Close(); }
            GC.KeepAlive(d);
        }

        // ── 6. 독 폴더와 휴지통 ──

        private void ShotDockFolder(Grid g, string exampleDir)
        {
            g.Children.Add(ShotBar(false));
            string folderName = K("받은 파일", "Received files");
            string dir = Path.Combine(exampleDir, folderName);
            MakeExampleFolder(dir);
            var d = ShotDock(g, withStacks: true, running: new[] { "files" }, folderName: folderName);
            Layout(g);
            var folder = Need(d.Views[Index(d, "downloads")], "독 예시 폴더")!;
            var fb = Bounds(folder, g);
            var pin = new PinItem { Kind = PinKind.Folder, Target = dir, Name = folderName, Id = "site-example", Folder = new FolderOptions { Sort = FolderSort.Name } };
            FolderStackPanel? w = null;
            try
            {
                DockStackPanel.ThumbsNow = true;
                w = new FolderStackPanel(_services, _p, pin, fb, DockEdge.Bottom, _services.DesktopWindows.ResolveMonitor(""));
                var c0 = (FrameworkElement)w.Content;
                var img = Snap(c0);
                Put(g, img, Math.Clamp(fb.Left + fb.Width / 2 - img.Width / 2, 8, W - img.Width - 8), fb.Top - img.Height - 6);
            }
            catch (Exception ex) { _problems?.Add($"독 폴더 판 그리기 실패: {ex.GetType().Name} {ex.Message}"); }
            finally { DockStackPanel.ThumbsNow = false; w?.Close(); }
        }

        /// <summary>예시 폴더 (시험 폴더 안): 예시 이름 파일 6개 — 그림은 진짜 PNG, 나머지는 짧은 글.</summary>
        private static void MakeExampleFolder(string dir)
        {
            Directory.CreateDirectory(dir);
            var names = new[] { K("회의록.txt", "Meeting notes.txt"), K("사진1.png", "Photo1.png"), K("발표자료.pdf", "Slides.pdf"), K("영수증.pdf", "Receipt.pdf"), K("메모.txt", "Notes.txt"), K("지도.png", "Map.png") };
            var colors = new[] { Color.FromRgb(0x6F, 0xA8, 0xF2), Color.FromRgb(0x7C, 0xC8, 0x95) };
            int c = 0;
            foreach (var n in names)
            {
                string path = Path.Combine(dir, n);
                if (File.Exists(path)) continue;
                if (n.EndsWith(".png", StringComparison.Ordinal))
                {
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new LinearGradientBrush(colors[c % 2], Colors.White, 60), null, new Rect(0, 0, 96, 72));
                        dc.DrawEllipse(Brushes.White, null, new Point(70, 22), 10, 10);
                    }
                    c++;
                    var rtb = new RenderTargetBitmap(96, 72, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using var fs = File.Create(path);
                    enc.Save(fs);
                }
                else if (n.EndsWith(".pdf", StringComparison.Ordinal)) File.WriteAllText(path, "%PDF-1.4\n%%EOF\n");
                else File.WriteAllText(path, K("예시 파일", "Example file"));
            }
        }

        // ── 7. 상단바와 제어 센터 ──

        private void ShotMenuBar(Grid g)
        {
            var bar = ShotBar(true);
            g.Children.Add(bar);
            Put(g, Window("notes", 600, 520), 160, 96);
            ShotDock(g, running: new[] { "notes", "browser" });
            var img = StatusPanel(StatusPanelKind.ControlCenter, out var size);
            if (img is null) return;
            var pic = new Border
            {
                Child = new Image { Source = img, Width = size.Width, Height = size.Height, Stretch = Stretch.Fill },
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
            };
            Put(g, pic, W - size.Width - 8, 36);
        }

        // ── 8. 달력·알림과 독 빨간 점 ──

        private void ShotCalendar(Grid g)
        {
            g.Children.Add(ShotBar(true));
            Put(g, Window("notes", 560, 460), 120, 110);
            ShotDock(g, running: new[] { "notes", "browser", "chat" }, notify: "chat");
            // 예시 알림만 (알람 및 시계 · PowerShell 예시) — 실제 알림은 읽지 않는 새 알림 서비스에
            var notes = new NotificationService(_services.Windows, _services.Launcher);
            const string clock = "Microsoft.WindowsAlarms_8wekyb3d8bbwe!App";
            const string ps = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";
            var now = DateTime.Now;
            notes.UseForScene(new List<NotificationItem>
            {
                new(3, clock, K("알람 및 시계", "Clock"), K("타이머", "Timer"), new[] { K("5분 타이머가 끝났어요.", "Your 5-minute timer is done.") }, now.AddMinutes(-2), null, null, false, null),
                new(2, ps, "Windows PowerShell", K("예시 알림", "Example notification"),
                    new[] { K("몽독 알림 목록 예시예요. 긴 알림은 눌러서 끝까지 펼쳐 볼 수 있어요.", "This is an example in the mongdock notification list. Click a long notification to read all of it.") }, now.AddMinutes(-9), null, null, false, null),
            });
            var sv2 = _services with { Notifications = notes };
            var img = StatusPanel(StatusPanelKind.Calendar, out var size, sv2);
            if (img is null) return;
            double maxH = H - 36 - 12; // 오른쪽 끝이라 가운데 독과 겹치지 않음
            var clip = new Border
            {
                Width = size.Width,
                Height = Math.Min(size.Height, maxH),
                ClipToBounds = true,
                CornerRadius = new CornerRadius(14),
                Child = new Image { Source = img, Width = size.Width, Height = size.Height, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Top },
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
            };
            if (size.Height > maxH) _problems?.Add($"달력 판이 화면보다 커서 아래가 잘림 ({size.Height:0} > {maxH:0})");
            Put(g, clip, W - size.Width - 8, 36);
        }
    }
}
