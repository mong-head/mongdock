using System.IO;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>처음 실행 때의 기본 고정 앱.</summary>
public static class DefaultPins
{
    private const string SettingsAumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    /// <summary>독 첫 칸의 파일 탐색기·앱 모음 이름 (#d19 — 애플 이름 Finder·Launchpad 를 쓰지 않음. 코드 식별자 "launchpad" 는 그대로).</summary>
    public static string ExplorerName => Loc.T("파일 탐색기");
    public static string AllAppsName => Loc.T("앱 모음");

    /// <summary>독에 처음 넣을 앱 수 상한 (파일 탐색기·앱 모음 제외).</summary>
    private const int MaxApps = 8;

    /// <summary>
    /// 새 설치의 첫 핀: Finder, Launchpad → 윈도우 작업 표시줄 고정 앱(작업 표시줄 순서, <see cref="TaskbarPins"/>) →
    /// 그래도 앱이 8개보다 적으면 설치된 자주 쓰는 앱(브라우저 하나·카카오톡·오피스·메모장·디스코드·노션·설정)을 시작 메뉴 바로 가기에서 찾아 채움 (#22).
    /// </summary>
    public static List<PinItem> CreateInitial(ISettingsService settings)
    {
        var pins = Base();
        int fromTaskbar = 0, popular = 0;
        try { fromTaskbar = TaskbarPins.AddMissing(pins, TaskbarPins.Read(settings)); }
        catch (Exception ex) { Log.Error("작업 표시줄 고정 앱 읽기 실패", ex); }
        try { popular = AddPopular(pins, settings); }
        catch (Exception ex) { Log.Error("자주 쓰는 앱 찾기 실패", ex); }
        if (pins.Count <= Base().Count) return AddDownloads(Create()); // 아무것도 못 찾으면 예전 기본값
        Log.Info($"기본 고정 앱: 파일 탐색기·앱 모음 + 작업 표시줄 {fromTaskbar}개 + 자주 쓰는 앱 {popular}개");
        return AddDownloads(pins);
    }

    /// <summary>새 설치: 오른쪽 끝에 다운로드 독 폴더 (#24-B). 다운로드 폴더를 못 찾으면 그냥 둠.</summary>
    private static List<PinItem> AddDownloads(List<PinItem> pins)
    {
        try
        {
            if (DockFolderService.DownloadsFolder() is { } dl && !pins.Any(p => p.Kind == PinKind.Folder))
                pins.Add(new PinItem { Kind = PinKind.Folder, Target = dl, Name = Loc.T("다운로드"), Id = Guid.NewGuid().ToString("N"), Folder = new FolderOptions() });
        }
        catch (Exception ex) { Log.Error("다운로드 독 폴더 추가 실패", ex); }
        return pins;
    }

    /// <summary>찾을 앱: 시작 메뉴 바로 가기 이름(대소문자 무시, 여러 개면 처음 찾은 것). browser = 브라우저 묶음(하나만).</summary>
    private static readonly (string[] Shortcuts, bool Browser)[] Popular =
    {
        (new[] { "Google Chrome", "Naver Whale", "네이버 웨일", "Whale", "Microsoft Edge", "Firefox" }, true),
        (new[] { "카카오톡", "KakaoTalk" }, false),
        (new[] { "Word" }, false),
        (new[] { "Excel" }, false),
        (new[] { "PowerPoint" }, false),
        (new[] { "Discord" }, false),
        (new[] { "Notion" }, false),
    };

    private static readonly string[] BrowserExes = { "chrome.exe", "whale.exe", "msedge.exe", "firefox.exe" };

    private static int AddPopular(List<PinItem> pins, ISettingsService settings)
    {
        int room = MaxApps - (pins.Count - Base().Count);
        if (room <= 0) return 0;
        var shortcuts = StartMenuShortcuts();
        int added = 0;
        foreach (var (names, browser) in Popular)
        {
            if (added >= room) break;
            if (browser && pins.Any(IsBrowser)) continue;
            string? lnk = names.Select(n => shortcuts.GetValueOrDefault(n)).FirstOrDefault(x => x is not null);
            if (lnk is null) continue;
            var pin = PinFactory.CreatePin(lnk, settings);
            if (pin is null || pins.Any(p => SameApp(p, pin))) continue;
            pins.Add(pin);
            added++;
        }
        // 메모장 (윈도우 11 은 스토어 앱) → 설정
        foreach (string aumid in new[] { "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", SettingsAumid })
        {
            if (added >= room) break;
            string? real = AppsFolder.RestoreAumidCase(aumid);
            if (real is null || pins.Any(p => p.Kind == PinKind.Aumid && p.Target.Equals(real, StringComparison.OrdinalIgnoreCase))) continue;
            pins.Add(new PinItem { Name = AppsFolder.GetAppDisplayName(real) ?? (aumid == SettingsAumid ? Loc.T("설정") : Loc.T("메모장")), Kind = PinKind.Aumid, Target = real });
            added++;
        }
        return added;
    }

    private static bool IsBrowser(PinItem p) =>
        p.Kind == PinKind.Exe && BrowserExes.Contains(Path.GetFileName(p.Target), StringComparer.OrdinalIgnoreCase);

    private static bool SameApp(PinItem a, PinItem b) =>
        a.Kind == b.Kind && (a.Kind == PinKind.Aumid
            ? a.Target.Equals(b.Target, StringComparison.OrdinalIgnoreCase)
            : Path.GetFileName(a.Target).Equals(Path.GetFileName(b.Target), StringComparison.OrdinalIgnoreCase));

    /// <summary>모든 사용자·내 시작 메뉴의 바로 가기: 파일 이름(확장자 없이) → 경로. 같은 이름이면 처음 것.</summary>
    private static Dictionary<string, string> StartMenuShortcuts()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            string dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (string lnk in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    map.TryAdd(Path.GetFileNameWithoutExtension(lnk), lnk);
            }
            catch (Exception ex) { Log.Warn($"시작 메뉴 읽기 실패: {ex.Message}"); }
        }
        return map;
    }

    private static List<PinItem> Base() => new()
    {
        new PinItem { Name = ExplorerName, Kind = PinKind.Exe, Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") },
        new PinItem { Name = AllAppsName, Kind = PinKind.Special, Target = "launchpad" },
    };

    /// <summary>Finder(탐색기), Launchpad, 브라우저(크롬 우선, 없으면 엣지), 설정 — 설치 확인된 것만.</summary>
    public static List<PinItem> Create()
    {
        var pins = new List<PinItem>();
        try
        {
            pins.AddRange(Base());

            string? browser = FirstExisting(
                @"%ProgramFiles%\Google\Chrome\Application\chrome.exe",
                @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe",
                @"%LocalAppData%\Google\Chrome\Application\chrome.exe");
            string browserName = "Google Chrome";
            if (browser is null)
            {
                browser = FirstExisting(
                    @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                    @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe");
                browserName = "Microsoft Edge";
            }
            if (browser is not null) pins.Add(new PinItem { Name = browserName, Kind = PinKind.Exe, Target = browser });

            string? settings = AppsFolder.RestoreAumidCase(SettingsAumid);
            if (settings is not null) pins.Add(new PinItem { Name = AppsFolder.GetAppDisplayName(settings) ?? Loc.T("설정"), Kind = PinKind.Aumid, Target = settings });
        }
        catch (Exception ex)
        {
            Log.Error("기본 고정 앱 만들기 실패", ex);
        }
        Log.Info($"기본 고정 앱: '{string.Join(", ", pins.Select(p => p.Name))}'");
        return pins;
    }

    private static string? FirstExisting(params string[] paths) =>
        paths.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
}
