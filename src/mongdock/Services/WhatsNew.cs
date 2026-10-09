using System.Reflection;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>코치마크 말풍선이 가리킬 대상. Center = 앵커 없이 화면 가운데 카드.</summary>
public enum CoachAnchor
{
    Center,
    /// <summary>독 패널 전체.</summary>
    Dock,
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
    /// 첫 설치 둘러보기 (#22: 5장 + 마무리) — ① 시스템 변경 안내 ② 독(다음으로 넘어갈 때 숨는 시연) ③ 검색 ④ 로고 메뉴 ⑤ 마무리.
    /// 빠진 기능은 마무리 카드 목록 + 처음 그 아이콘을 썼을 때 한 번 <see cref="Hints"/>. 마지막 단계(settings)는 항상 마지막 카드.
    /// </summary>
    public static readonly IReadOnlyList<CoachStep> Tour =
    [
        new("", IntroKey, "작업 표시줄은 숨겨 뒀어요",
            "윈도우 작업 표시줄 대신 위쪽 상단바와 독을 써요. 윈도우 알림도 오른쪽 위 몽독 배너로 떠요. 되돌리려면 설정 → 일반 → '윈도우 작업 표시줄 숨기기'를 끄면 돼요.",
            CoachAnchor.Center, s => s.HideWindowsTaskbar),
        new("", "dock", "여기가 독이에요",
            "자주 쓰는 앱은 오른쪽 클릭 → 독에 고정, 끌어서 순서를 바꿔요.{dockhide}",
            CoachAnchor.Dock, s => s.Dock.Enabled),
        new("", SearchKey, "검색",
            "{hotkey} 앱·파일·윈도우 설정·계산기까지 찾아요. 지금 눌러 보세요.",
            CoachAnchor.Search, s => TopBarOn(s) && s.TopBar.ShowQuickButtons && s.TopBar.SearchMode == SearchMode.Spotlight),
        new("", "logo", "로고 메뉴",
            "몽독 설정·이 PC 정보·잠자기·다시 시작은 여기서 해요.",
            CoachAnchor.Logo, s => TopBarOn(s) && s.TopBar.ShowLogo),
        new("", "settings", "이 밖에도 있어요",
            "설정은 로고 메뉴 → mongdock → 설정…에서 모두 클릭으로 바꿔요. 아래 기능은 처음 써 볼 때 짧게 알려 드릴게요.",
            CoachAnchor.Center),
    ];

    /// <summary>
    /// 둘러보기에서 뺀 기능 — 처음 그 요소를 쓰고(패널·메뉴를 닫은 뒤) 한 번만 짧은 카드 (Views/FirstUseHints).
    /// 마무리 카드의 "이 밖에도" 목록에도 제목이 들어감.
    /// </summary>
    public static readonly IReadOnlyList<CoachStep> Hints =
    [
        new("", "appmenu", "지금 앱의 메뉴",
            "앱 이름과 그 옆 메뉴(파일·편집·보기…)를 클릭으로 열어요. 단축키 없이도 돼요.",
            CoachAnchor.AppName, s => TopBarOn(s) && s.TopBar.ShowActiveAppName),
        new("", "desktops", "가상 데스크톱",
            "‹ › 로 데스크톱을 넘기고, 가운데 숫자를 누르면 작업 보기가 열려요.",
            CoachAnchor.Desktops, s => TopBarOn(s) && s.TopBar.ShowDesktopButtons),
        new("", "calendar", "시계와 달력",
            "날짜를 누르면 음력·공휴일과 일정이 보여요. 설정 → 캘린더에서 구글 일정도 연결할 수 있어요.",
            CoachAnchor.Clock, TopBarOn),
        new("", "tray", "다른 앱 트레이 아이콘",
            "카카오톡 같은 앱 아이콘이 여기 있어요. 끌어서 ⌃ 안팎으로 옮기고, 오른쪽 클릭하면 앱 메뉴가 떠요.",
            CoachAnchor.Tray, s => TopBarOn(s) && s.TopBar.ShowTrayIcons),
    ];

    /// <summary>마무리 카드 "이 밖에도" 목록 (힌트 제목 + 알림).</summary>
    public static IReadOnlyList<string> ExtraFeatureLines(Settings s) =>
        Hints.Where(h => h.IsAvailable(s)).Select(h => h.Title)
            .Concat(s.Notifications.ShowNotificationBanners ? new[] { "알림은 오른쪽 위 배너로" } : Array.Empty<string>())
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
