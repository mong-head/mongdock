using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.Views;

namespace ReportTest;

/// <summary>
/// report-test            → 가리기 단위 시험 (실패하면 종료 코드 1)
/// report-test --png DIR  → 가리기 시험 + 신고 창을 라이트/다크로 렌더링해 DIR 에 PNG 저장
/// </summary>
internal static class Program
{
    private static int _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        RedactionTests();
        Console.WriteLine(_failed == 0 ? "가리기 시험: 모두 통과" : $"가리기 시험: {_failed}개 실패");

        int png = Array.FindIndex(args, a => a == "--png");
        if (png >= 0 && png + 1 < args.Length) RenderPngs(args[png + 1]);
        return _failed == 0 ? 0 : 1;
    }

    // ───────────────────────── 가리기 ─────────────────────────

    private static void RedactionTests()
    {
        var ctx = new ReportRedactor.Context("melon", "DESKTOP-AB12CD", @"C:\Users\melon",
            new[] { "연봉 협상안.xlsx - Excel", "카카오톡", "ab" });

        // 이메일
        Check("이메일", ReportRedactor.Redact("답장: hong.gil-dong+x@example.co.kr 로", ctx), "답장: <이메일> 로");
        Check("이메일 여러 개", ReportRedactor.Redact("a@b.com, c_d@e.org", ctx), "<이메일>, <이메일>");

        // URL (iCal 비밀 주소 포함)
        Check("iCal https", ReportRedactor.Redact("캘린더 https://calendar.google.com/calendar/ical/abc%40group/private-123/basic.ics 실패", ctx),
            "캘린더 <주소> 실패");
        Check("webcal", ReportRedactor.Redact("webcal://p01-caldav.icloud.com/published/2/MTIz", ctx), "<주소>");
        Check("http 와 따옴표", ReportRedactor.Redact("열기 \"http://example.com/a?b=c\" 끝", ctx), "열기 \"<주소>\" 끝");
        Check("www", ReportRedactor.Redact("www.naver.com 열림", ctx), "<주소> 열림");
        Check("ms-settings 는 그대로", ReportRedactor.Redact("ms-settings:privacy-microphone", ctx), "ms-settings:privacy-microphone");

        // 경로의 사용자 이름
        Check("내 프로필 경로", ReportRedactor.Redact(@"파일 C:\Users\melon\Desktop\a.txt", ctx), @"파일 C:\Users\<사용자>\Desktop\a.txt");
        Check("다른 사용자 경로", ReportRedactor.Redact(@"D:\Users\홍길동\AppData\x", ctx), @"D:\Users\<사용자>\AppData\x");
        Check("JSON 이스케이프 경로", ReportRedactor.Redact(@"""C:\\Users\\someone\\x""", ctx), @"""C:\\Users\\<사용자>\\x""");
        Check("슬래시 경로", ReportRedactor.Redact("C:/Users/someone/x", ctx), "C:/Users/<사용자>/x");
        Check("Users 폴더 자체는 그대로", ReportRedactor.Redact(@"C:\Program Files\mongdock", ctx), @"C:\Program Files\mongdock");

        // 사용자 이름·기기 이름 (낱말 단위)
        Check("기기 이름", ReportRedactor.Redact("기기 DESKTOP-AB12CD 에서", ctx), "기기 <기기> 에서");
        Check("사용자 이름 낱말", ReportRedactor.Redact("user melon logged", ctx), "user <사용자> logged");
        Check("다른 낱말 일부는 그대로", ReportRedactor.Redact("watermelons", ctx), "watermelons");

        // 창 제목 (3자 이상만, 긴 것부터)
        Check("창 제목", ReportRedactor.Redact("포그라운드: 연봉 협상안.xlsx - Excel", ctx), "포그라운드: <창 제목>");
        Check("창 제목 짧은 것", ReportRedactor.Redact("카카오톡 열림", ctx), "<창 제목> 열림");
        Check("2자 제목은 무시", ReportRedactor.Redact("tab bar", ctx), "tab bar");

        // 로그: 작은따옴표 안 문구도 가림
        Check("로그 따옴표", ReportRedactor.RedactLog("WARN 프로세스 경로를 알 수 없는 창 → 실행 불가 핀 (이름만): '비밀 문서 - 메모장'", ctx),
            "WARN 프로세스 경로를 알 수 없는 창 → 실행 불가 핀 (이름만): '<가림>'");
        Check("로그 경로+URL", ReportRedactor.RedactLog(@"업데이트 다운로드 완료: https://github.com/x/y.exe → C:\Users\melon\AppData\Local\Temp\y.exe", ctx),
            @"업데이트 다운로드 완료: <주소> → C:\Users\<사용자>\AppData\Local\Temp\y.exe");
        Check("빈 문자열", ReportRedactor.Redact("", ctx), "");
    }

    private static void Check(string name, string actual, string expected)
    {
        if (actual == expected) { Console.WriteLine($"  통과  {name}"); return; }
        _failed++;
        Console.WriteLine($"  실패  {name}\n        기대: {expected}\n        실제: {actual}");
    }

    // ───────────────────────── PNG ─────────────────────────

    private static void RenderPngs(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Mongdock.App();
        app.InitializeComponent(); // Themes/Controls.xaml·Menus.xaml (CardButton, IconFont)
        var ctx = new ReportRedactor.Context("melon", "DESKTOP-AB12CD", @"C:\Users\melon", new[] { "연봉 협상안.xlsx - Excel" });
        string sample = ReportRedactor.Redact(
            "앱: mongdock 0.4.2 (설치 프로그램)\n윈도우: Windows 11 Home 24H2 (26200.6584)\n언어: ko-KR\n모니터 1: 2560×1440, 배율 125%, 주 모니터\n배터리: 없음\n터치: 없음\n\n[설정 요약]\n독: 켜짐, 위치 Bottom, 모드 Reserve, 테마 System, 아이콘 52\n\n[최근 로그]\n", ctx)
            + ReportRedactor.RedactLog(@"2026-10-09 10:00:00.000 INFO  [1] 파일 열기 실패: C:\Users\melon\Desktop\연봉 협상안.xlsx - Excel", ctx) + "\n"
            + ReportRedactor.RedactLog("2026-10-09 10:00:01.000 WARN  [7] 캘린더 'Work' 가져오기 실패: https://calendar.google.com/x/private-1/basic.ics", ctx);

        foreach (var theme in new[] { DockTheme.Light, DockTheme.Dark })
        {
            var settings = new Settings();
            settings.Dock.Theme = theme;
            foreach (bool open in new[] { false, true })
            {
                var w = new ReportWindow(settings, () => "test")
                {
                    Left = -20000,
                    Top = -20000,
                    ShowActivated = false,
                };
                w.Show();
                w.SetDiagnostics(sample);
                w.SetPreviewState(ReportKind.Bug, "독에서 카카오톡 아이콘을 누르면 창이 안 떠요.\n다시 누르면 떠요.", open ? "me@example.com" : "", open);
                w.UpdateLayout();
                string name = $"report-{theme.ToString().ToLowerInvariant()}{(open ? "-details" : "")}.png";
                Save(w, Path.Combine(dir, name));
                w.Close();
                Console.WriteLine($"  저장  {name}");
            }
        }
    }

    private static void Save(Window w, string path)
    {
        var content = (FrameworkElement)w.Content;
        double width = content.ActualWidth, height = content.ActualHeight;
        const double scale = 1.5;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            // 여백까지 그대로 (VisualBrush 기본값은 내용 경계에 맞춰 늘림)
            var brush = new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.None };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }
        var rtb = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
