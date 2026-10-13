using System.Reflection;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>코치마크 말풍선이 가리킬 대상. Center = 앵커 없이 화면 가운데 카드.</summary>
public enum CoachAnchor
{
    Center,
    /// <summary>독 패널 전체.</summary>
    Dock,
    /// <summary>독의 (가장 최근에 넣은) 독 폴더 아이콘.</summary>
    DockFolder,
    /// <summary>상단바 로고 버튼.</summary>
    Logo,
    /// <summary>상단바 앱 이름(앱 메뉴).</summary>
    AppName,
    /// <summary>상단바 가상 데스크톱 ‹ 1 / 2 › 묶음.</summary>
    Desktops,
    /// <summary>상단바 트레이 아이콘 영역.</summary>
    Tray,
    /// <summary>상단바 검색 버튼.</summary>
    Search,
    /// <summary>상단바 시계.</summary>
    Clock,
    /// <summary>상단바 전체 (둘러보기 새 방식).</summary>
    TopBar,
    /// <summary>독의 앱 모음 아이콘.</summary>
    AllApps,
    /// <summary>독의 폴더·휴지통 (있는 것만 묶어서).</summary>
    DockStacks,
}

/// <summary>
/// 코치마크 한 단계. Key 는 같은 기능이 여러 버전에 걸쳐 소개될 때 최신 것만 남기는 데 씀.
/// Body 의 "{hotkey}" 는 실제 Spotlight 단축키 문구로 바뀐다 (Views/CoachMarks).
/// When 이 false 면(그 기능이 꺼져 있으면) 이 단계는 건너뜀.
/// </summary>
public sealed record CoachStep(string Version, string Key, string Title, string Body, CoachAnchor Anchor, Func<Settings, bool>? When = null)
{
    public bool IsAvailable(Settings s)
    {
        try { return When?.Invoke(s) ?? true; }
        catch { return false; }
    }
}

/// <summary>
/// 버전별 "새로운 기능"(Changelog.json 에서 읽음)과 첫 설치 둘러보기 내용(코드 안 데이터). 새 버전을 낼 때는 src/mongdock/Changelog.json 에 항목을 추가한다.
/// 버전 문자열은 "v" 접두·"-test" 같은 접미를 무시하고 System.Version(주.부.빌드)으로 비교한다.
/// </summary>
public static class WhatsNew
{
    /// <summary>LastSeenVersion 이 없는데 설정 파일은 있던 사용자(= 0.2.0 이하에서 업데이트)의 이전 버전.</summary>
    public const string LegacyVersion = "0.2.0";

    private static bool TopBarOn(Settings s) => s.TopBar.Enabled;

    /// <summary>
    /// 버전별 새 기능 — Changelog.json 의 coach 가 있는 feature 항목 (순서 상관없음, 정렬해서 씀).
    /// condition 이름 → 그 기능이 켜져 있는지 (<see cref="Condition"/>).
    /// </summary>
    public static IReadOnlyList<CoachStep> Releases => _releases ??= Changelog.Releases
        .SelectMany(r => r.Entries.Where(e => e.Coach is not null)
            .Select(e => new CoachStep(r.VersionText, e.Coach!.Key, e.Coach.Title, e.Coach.Body, e.Coach.Anchor, Condition(e.Coach.Condition))))
        .ToList();

    private static IReadOnlyList<CoachStep>? _releases;

    /// <summary>Changelog.json coach.condition → 조건. 없거나 모르는 이름이면 항상.</summary>
    private static Func<Settings, bool>? Condition(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "topbar" => TopBarOn,
        "trayicons" => s => TopBarOn(s) && s.TopBar.ShowTrayIcons,
        "appmenus" => s => TopBarOn(s) && s.TopBar.ShowActiveAppName && s.TopBar.ShowAppMenus,
        "spotlight" => s => TopBarOn(s) && s.TopBar.ShowQuickButtons && s.TopBar.SearchMode == SearchMode.Spotlight,
        "banners" => s => s.Notifications.ShowNotificationBanners,
        "dock" => s => s.Dock.Enabled,
        _ => null,
    };

    /// <summary>첫 둘러보기 첫 카드(시스템 변경 안내) — CoachMarks 가 [작업 표시줄 다시 보이기] 버튼을 붙임.</summary>
    public const string IntroKey = "intro";
    /// <summary>검색 카드 — Spotlight 를 직접 열었다 닫으면 다음으로.</summary>
    public const string SearchKey = "search";

    /// <summary>
    /// 첫 설치 둘러보기 (2026-10-14 새 방식 — spec-coach-tour.md): ① 시스템 변경 안내(그림 없음) + 그림 6단계
    /// (독·상단바·앱 모음 판·루틴·독 폴더와 휴지통·설정). 그림은 Assets/Tour/tour-{key}-{n}-{light|dark}.png (report-test --tour).
    /// 마지막 단계(settings)는 늘 마지막 카드 (자동 실행 버튼). 검색·로고 메뉴는 처음 써 볼 때 <see cref="Hints"/>.
    /// </summary>
    public static readonly IReadOnlyList<CoachStep> Tour =
    [
        new("", IntroKey, Loc.T("작업 표시줄은 숨겨 뒀어요"),
            Loc.T("윈도우 작업 표시줄 대신 위쪽 상단바와 독을 써요. 윈도우 알림도 오른쪽 위 몽독 배너로 떠요. 되돌리려면 설정 → 일반 → '윈도우 작업 표시줄 숨기기'를 끄면 돼요."),
            CoachAnchor.Center, s => s.HideWindowsTaskbar),
        new("", "dock", Loc.T("독 — 자주 쓰는 앱을 한 줄에"),
            Loc.T("아이콘을 누르면 앱이 열리고, 마우스를 올리면 커져요. 자주 쓰는 앱은 오른쪽 클릭 → 독에 고정. 끌어서 순서를 바꿔요."),
            CoachAnchor.Dock, s => s.Dock.Enabled),
        new("", "topbar", Loc.T("상단바 — 시계·소리·알림을 한눈에"),
            Loc.T("와이파이·소리를 누르면 제어 센터, 시계를 누르면 달력과 일정이 열려요. 왼쪽에는 지금 앱의 메뉴가 나와요."),
            CoachAnchor.TopBar, TopBarOn),
        new("", "allapps", Loc.T("앱 모음 판 — 모든 앱을 한곳에"),
            Loc.T("독의 앱 모음을 누르면 열려요. 자주 쓰는 앱은 즐겨찾기에, 비슷한 앱은 폴더에 모여 있어요. 끌어서 순서를 바꾸고, 안 쓰는 앱은 정리해요."),
            CoachAnchor.AllApps, s => s.Dock.Enabled),
        new("", "routines", Loc.T("루틴 — 일하는 앱을 한 번에"),
            Loc.T("자주 같이 여는 앱들을 루틴으로 만들어 두면, 누를 때 새 데스크톱에 정해 둔 자리로 한꺼번에 열려요. 헤드셋을 연결하면 열지 물어보게 할 수도 있어요."),
            CoachAnchor.AllApps, s => s.Dock.Enabled),
        new("", "stacks", Loc.T("독의 폴더와 휴지통"),
            Loc.T("폴더를 독에 두면 눌렀을 때 안의 파일이 펼쳐져요. 파일을 휴지통으로 끌면 지워지고, 휴지통을 누르면 비울 수 있어요."),
            CoachAnchor.DockStacks, s => s.Dock.Enabled),
        new("", "settings", Loc.T("설정 — 내 손에 맞게"),
            Loc.T("독 위치·자동 숨기기·크기와 라이트·다크 테마를 바꿔요. 독이나 상단바를 오른쪽 클릭하면 설정이 열려요."),
            CoachAnchor.Center),
    ];

    /// <summary>둘러보기 그림이 있는 단계 키.</summary>
    public static readonly IReadOnlySet<string> TourPictures = new HashSet<string> { "dock", "topbar", "allapps", "routines", "stacks", "settings" };

    /// <summary>
    /// 둘러보기에서 뺀 기능 — 처음 그 요소를 쓰고(패널·메뉴를 닫은 뒤) 한 번만 짧은 카드 (Views/FirstUseHints).
    /// 마무리 카드의 "이 밖에도" 목록에도 제목이 들어감.
    /// </summary>
    public static readonly IReadOnlyList<CoachStep> Hints =
    [
        new("", SearchKey, Loc.T("검색"),
            Loc.T("{hotkey} 앱·파일·윈도우 설정·계산기까지 찾아요."),
            CoachAnchor.Search, s => TopBarOn(s) && s.TopBar.ShowQuickButtons && s.TopBar.SearchMode == SearchMode.Spotlight),
        new("", "logo", Loc.T("로고 메뉴"),
            Loc.T("몽독 설정·이 PC 정보·잠자기·다시 시작은 여기서 해요."),
            CoachAnchor.Logo, s => TopBarOn(s) && s.TopBar.ShowLogo),
        new("", "appmenu", Loc.T("지금 앱의 메뉴"),
            Loc.T("앱 이름과 그 옆 메뉴(파일·편집·보기…)를 클릭으로 열어요. 단축키 없이도 돼요."),
            CoachAnchor.AppName, s => TopBarOn(s) && s.TopBar.ShowActiveAppName),
        new("", "desktops", Loc.T("가상 데스크톱"),
            Loc.T("‹ › 로 데스크톱을 넘기고, 가운데 숫자를 누르면 작업 보기가 열려요."),
            CoachAnchor.Desktops, s => TopBarOn(s) && s.TopBar.ShowDesktopButtons),
        new("", "calendar", Loc.T("시계와 달력"),
            Loc.T("날짜를 누르면 음력·공휴일과 일정이 보여요. 설정 → 캘린더에서 구글 일정도 연결할 수 있어요."),
            CoachAnchor.Clock, TopBarOn),
        new("", "tray", Loc.T("다른 앱 트레이 아이콘"),
            Loc.T("카카오톡 같은 앱 아이콘이 여기 있어요. 끌어서 ⌃ 안팎으로 옮기고, 오른쪽 클릭하면 앱 메뉴가 떠요."),
            CoachAnchor.Tray, s => TopBarOn(s) && s.TopBar.ShowTrayIcons),
    ];

    /// <summary>마무리 카드 "이 밖에도" 목록 (힌트 제목 + 알림).</summary>
    public static IReadOnlyList<string> ExtraFeatureLines(Settings s) =>
        Hints.Where(h => h.IsAvailable(s)).Select(h => h.Title)
            .Concat(s.Notifications.ShowNotificationBanners ? new[] { Loc.T("알림은 오른쪽 위 배너로") } : Array.Empty<string>())
            .ToList();

    /// <summary>"v0.3.0", "0.3.0-test", "0.3" → 0.3.0. 못 읽으면 null.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        int cut = t.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>실행 중인 몽독 버전 (어셈블리 정보 버전, 접미 제거 — 예 0.3.0).</summary>
    public static Version Current { get; } = ReadCurrent();

    public static string CurrentText => Current.ToString(3);

    private static Version ReadCurrent()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(WhatsNew).Assembly;
        string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Parse(info) ?? Parse(asm.GetName().Version?.ToString()) ?? new Version(0, 0, 0);
    }
}
