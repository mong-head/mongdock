using System.Windows;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Native;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>코치마크 카드 한 장 (앵커 말풍선 또는 화면 가운데 카드).</summary>
internal sealed class CoachPage
{
    public required string Title { get; init; }
    public string Body { get; init; } = "";
    public CoachAnchor Anchor { get; init; } = CoachAnchor.Center;
    /// <summary>버전(또는 묶음) 머리글 + 항목들 — "그 밖에 바뀐 것" 같은 목록 카드.</summary>
    public List<(string Header, List<string> Items)>? Groups { get; init; }
    /// <summary>목록에 다 못 넣은 항목 수 ("외 N개").</summary>
    public int More { get; init; }
    /// <summary>"변경 내역 보기" 링크 표시 (설정 → 변경 내역).</summary>
    public bool ChangelogLink { get; init; }
    /// <summary>링크 문구 (예 "그 밖에 개선·수정 5개 — 변경 내역 보기 ›").</summary>
    public string LinkText { get; init; } = "변경 내역 보기 ›";
    /// <summary>본문 아래 강조색 한 줄 (예 "시계를 눌러 보세요") — 앵커를 직접 눌러 보라는 안내.</summary>
    public string? Hint { get; init; }
    /// <summary>[다음 →] 대신 쓸 글자 (예 "좋아요").</summary>
    public string? NextText { get; init; }
    /// <summary>[다음] 왼쪽의 두 번째 버튼 — 누르면 실행하고 다음 단계로 (예 "작업 표시줄 다시 보이기", "컴퓨터 켜면 몽독도 켜기").</summary>
    public (string Label, Action Run)? Action { get; init; }
    /// <summary>가리키는 기능을 직접 써 보면(Spotlight 를 열었다 닫으면) 저절로 다음 단계로.</summary>
    public bool AdvanceOnUse { get; init; }
}

/// <summary>둘러보기가 끝난 이유 (설정 창이 다시 나타날 때 포커스를 가져갈지 정하는 데 씀).</summary>
internal enum CoachEndReason
{
    /// <summary>끝까지 봄·건너뛰기·변경 내역 링크.</summary>
    Completed,
    /// <summary>전체 화면 앱이 켜져 닫힘.</summary>
    Fullscreen,
    /// <summary>일시 정지되어 닫힘.</summary>
    Paused,
    /// <summary>다른 안내로 바뀜·앱 종료.</summary>
    Replaced,
}

/// <summary>
/// 버전 업데이트 후 "새로운 기능" 코치마크와 첫 설치 둘러보기의 흐름.
/// - 시작 2.5초 뒤(독·상단바가 자리 잡은 뒤) LastSeenVersion 과 현재 버전을 비교해 한 번 보여 줌.
///   새 설치(settings.json 을 이번에 만듦) → 둘러보기, 기존 설정인데 LastSeenVersion 없음 → 0.2.0 에서 올라온 것으로 봄.
/// - 최신 버전 단계(Changelog.json 의 coach)는 앵커 말풍선으로 하나씩(최대 6), 마지막 가운데 카드 "그 밖에 바뀐 것"에
///   놓친 버전들의 나머지 변경(코치 없는 새 기능 > 고친 문제 > 개선 순, 넘치면 "외 N개" + 변경 내역 링크). 코치 단계가 없으면 이 카드 한 장만.
/// - 2개 버전 이상 건너뛰고 업데이트했으면(오랜만에) 놓친 버전들의 주요 업데이트(major)만: coach 있는 것은 말풍선(최대 6),
///   나머지는 마지막 카드 "그동안 바뀐 주요 기능"에 버전 headline 아래 한 줄씩, 사소한 것은 "그 밖에 개선·수정 N개" 링크 한 줄.
/// - 둘러보기는 최대 8장, 마지막 카드에 넘친 단계 + 둘러보기에서 다루지 않은 주요 기능 목록.
/// - 설정 창(변경 내역·정보)에서 버전별 둘러보기·주요 기능 둘러보기를 다시 재생 (<see cref="Play"/>, 꺼진 기능은 "지금 꺼져 있어요" 카드).
/// - 말풍선이 가리키는 요소를 사용자가 직접 누르면 평소처럼 열리고, 말풍선은 숨었다가 열린 것이 닫히면 다시 나타남 (CoachSession).
/// - 끝까지 보거나 건너뛰면 LastSeenVersion = 현재. 일시 정지·전체 화면이면 미루고, 보는 중 일시 정지되면 저장 없이 닫음(다음 실행에 다시).
/// </summary>
internal static class CoachMarks
{
    private const int MaxWhatsNewSteps = 6;
    /// <summary>"그 밖에 바뀐 것" 카드: 최신 버전 줄 수 / 그 이전 버전마다 줄 수.</summary>
    private const int MaxLatestLines = 5;
    private const int MaxOlderLines = 3;
    private const int MaxTourSteps = 8;
    /// <summary>설정 창에서 재생하는 버전별·주요 기능 둘러보기 최대 장수.</summary>
    private const int MaxReplaySteps = 8;

    private static AppServices? _services;
    private static Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?>? _resolve;
    private static Func<bool>? _popupOpen;
    private static DispatcherTimer? _startupTimer;
    private static CoachSession? _session;

    /// <summary>코치마크(둘러보기·새 기능)가 떠 있는지 — 다른 안내 카드가 겹치지 않게 (CrashPrompt).</summary>
    internal static bool IsShowing => _session is not null;

    /// <summary>
    /// App 이 독·상단바를 만든 뒤 한 번. resolve = 앵커 → 화면 사각형(모니터 기준 DIP) + 모니터 (안 보이면 null).
    /// popupOpen = 상단바 패널·메뉴가 열려 있는지 (앵커를 눌러 연 것이 닫히면 말풍선을 다시 보여 주려고).
    /// </summary>
    public static void Init(AppServices services, Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> resolve, Func<bool>? popupOpen = null)
    {
        _services = services;
        _resolve = resolve;
        _popupOpen = popupOpen;
    }

    /// <summary>
    /// 설정 창에서 둘러보기 재생. 만든 카드가 없으면 false. onEnded = 끝나거나 건너뛰거나 다른 안내로 바뀌면 (설정 창 다시 표시).
    /// onEnded 는 Begin 전에 구독하므로 시작하자마자 끝나도(앵커가 모두 사라짐) 불린다.
    /// </summary>
    public static bool Play(Func<List<CoachPage>> build, Action<CoachEndReason>? onEnded, bool isTour = false)
    {
        if (_services is null) return false;
        try
        {
            return Start(build(), onEnded, isTour) is not null;
        }
        catch (Exception ex)
        {
            Log.Error("둘러보기 재생 실패", ex);
            return false;
        }
    }

    /// <summary>시작 후 2.5초 뒤 확인. 일시 정지·전체 화면이면 5초마다 다시 확인.</summary>
    public static void ScheduleStartup(bool firstInstall, bool forceTour = false)
    {
        if (_services is null) return;
        _startupTimer?.Stop();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            if (Busy())
            {
                timer.Interval = TimeSpan.FromSeconds(5);
                return;
            }
            timer.Stop();
            _startupTimer = null;
            try { if (forceTour) ShowTour(); else RunStartup(firstInstall); }
            catch (Exception ex) { Log.Error("새로운 기능 안내 실패", ex); }
        };
        _startupTimer = timer;
        timer.Start();
    }

    /// <summary>
    /// 처음 쓰기 힌트 (#22): 새 설치(FirstUseHintsPending)에서 둘러보기에서 뺀 기능(WhatsNew.Hints)을 처음 쓰면,
    /// 연 패널·메뉴가 닫히고 다른 안내가 없을 때 그 요소를 가리키는 카드 한 장. 키마다 한 번만 (Settings.SeenHints).
    /// </summary>
    public static void HintUsed(string key)
    {
        try
        {
            var services = _services;
            if (services is null) return;
            var s = services.Settings.Current;
            if (!s.FirstUseHintsPending || s.FirstRunTourPending || s.SeenHints.Contains(key, StringComparer.OrdinalIgnoreCase)) return;
            var step = WhatsNew.Hints.FirstOrDefault(h => h.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (step is null || !step.IsAvailable(s)) return;
            s.SeenHints.Add(key);
            if (WhatsNew.Hints.All(h => s.SeenHints.Contains(h.Key, StringComparer.OrdinalIgnoreCase))) s.FirstUseHintsPending = false;
            services.Settings.Save();

            // 연 것이 닫히고(달력·메뉴·작업 보기 등) 다른 안내가 없을 때 — 최대 2분 기다림
            long until = Environment.TickCount64 + 120_000;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                if (Environment.TickCount64 > until) { timer.Stop(); return; }
                bool popup;
                try { popup = SpotlightWindow.IsOpen || _popupOpen?.Invoke() == true; }
                catch { popup = false; }
                if (popup || Busy() || _session is not null || !AnchorVisible(step.Anchor)) return;
                timer.Stop();
                Log.Info($"처음 쓰기 힌트: {key}");
                Start(new List<CoachPage> { new() { Title = step.Title, Body = step.Body, Anchor = step.Anchor, NextText = "알겠어요" } });
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error("처음 쓰기 힌트 실패", ex);
        }
    }

    /// <summary>트레이·정보 페이지 "새로운 기능 보기": 현재 버전의 새 기능.</summary>
    public static void ShowWhatsNew()
    {
        if (_services is null) return;
        try
        {
            // 현재 버전(없으면 그 이하 가장 최근 버전) 하나만: 그 바로 아래 버전 이후 ~ 현재
            var upTo = Changelog.Releases.FirstOrDefault(r => r.Version <= WhatsNew.Current);
            var pages = new List<CoachPage>();
            if (upTo is not null)
            {
                var below = Changelog.Releases.FirstOrDefault(r => r.Version < upTo.Version)?.Version ?? new Version(0, 0, 0);
                pages = BuildWhatsNew(below, upTo.Version);
            }
            if (pages.Count == 0)
            {
                pages.Add(new CoachPage { Title = "새로운 기능이 없어요", Body = $"버전 {WhatsNew.CurrentText} 에는 따로 소개할 기능이 없어요.", ChangelogLink = true });
            }
            Start(pages);
        }
        catch (Exception ex) { Log.Error("새로운 기능 보기 실패", ex); }
    }

    /// <summary>정보 페이지 "둘러보기 다시 보기".</summary>
    public static void ShowTour()
    {
        if (_services is null) return;
        try { Start(BuildTour(), isTour: true); }
        catch (Exception ex) { Log.Error("둘러보기 실패", ex); }
    }

    /// <summary>앱 종료 시.</summary>
    public static void CloseAll()
    {
        _startupTimer?.Stop();
        _startupTimer = null;
        _session?.Close(markSeen: false, CoachEndReason.Replaced);
        _session = null;
    }

    // ───────────────────────── 시작 판정 ─────────────────────────

    private static bool Busy()
    {
        if (_services is null) return true;
        if (AppState.Paused) return true;
        try { return _services.DesktopWindows.IsFullscreenOn(""); }
        catch { return false; }
    }

    private static void RunStartup(bool firstInstall)
    {
        var s = _services!.Settings.Current;
        var current = WhatsNew.Current;
        // 첫 설치이거나, 첫 설치 둘러보기를 끝까지 못 봄(일시 정지·전체 화면·종료로 닫힘) → 이번에도 둘러보기
        if (s.LastSeenVersion is null && firstInstall || s.FirstRunTourPending)
        {
            Log.Info($"첫 설치 → 둘러보기 (v{WhatsNew.CurrentText})");
            // 둘러보기를 끝까지 보거나 건너뛰면 (스토어판 새 설치) 자동 실행을 물어봄
            // 자동 실행 질문은 마무리 카드 안 버튼 — 끝까지 보면 따로 묻지 않음 (못 띄우면 카드로)
            if (Start(BuildTour(), reason => { if (reason == CoachEndReason.Completed) ClearStartupPrompt(); }, isTour: true) is null)
            {
                ClearTourPending();
                AskStartupIfPending();
            }
            return;
        }
        // 지난번에 카드를 고르지 않고 닫았음 → 이번에 다시
        if (s.StartupPromptPending)
        {
            AskStartupIfPending();
            return;
        }
        var last = WhatsNew.Parse(s.LastSeenVersion) ?? WhatsNew.Parse(WhatsNew.LegacyVersion)!;
        if (current <= last) return;

        var pages = BuildWhatsNew(last, current);
        Log.Info($"업데이트 v{last.ToString(3)} → v{WhatsNew.CurrentText}: 새로운 기능 {pages.Count}장");
        if (pages.Count == 0)
        {
            MarkSeen();
            return;
        }
        Start(pages);
    }

    /// <summary>
    /// 스토어판 새 설치: "컴퓨터를 켜면 몽독도 같이 켤까요?" [켜기]/[나중에]. 일반판은 설치 프로그램 체크박스로 정하므로 StartupPromptPending 이 켜지지 않음.
    /// [켜기] → 자동 실행 켬, [나중에] → 꺼진 채. 바깥 클릭 등으로 고르지 않고 닫히면 다음 실행에 다시 물음.
    /// </summary>
    private static void ClearStartupPrompt()
    {
        if (_services is null || !_services.Settings.Current.StartupPromptPending) return;
        _services.Settings.Current.StartupPromptPending = false;
        _services.Settings.Save();
    }

    private static async void AskStartupIfPending()
    {
        var services = _services;
        if (services is null || !services.Settings.Current.StartupPromptPending) return;
        try
        {
            bool? choice = await ConfirmCardWindow.AskChoiceAsync(services,
                "컴퓨터를 켜면 몽독도 같이 켤까요?",
                "로그인하면 독과 상단바가 바로 나타나요. 설정 → 일반에서 언제든 바꿀 수 있어요.",
                "켜기", "나중에");
            if (choice is null) return; // 고르지 않음 → 다음 실행에 다시
            var s = services.Settings.Current;
            s.StartupPromptPending = false;
            if (choice == true)
            {
                services.Startup.SetEnabled(true);
                s.StartWithWindows = true;
            }
            services.Settings.Save();
            Log.Info($"첫 실행 자동 실행 질문: {(choice == true ? "켜기" : "나중에")}");
        }
        catch (Exception ex)
        {
            Log.Error("자동 실행 질문 카드 실패", ex);
        }
    }

    /// <summary>첫 설치 둘러보기를 다 봄(또는 건너뜀) → 다음 실행에 다시 띄우지 않음.</summary>
    internal static void ClearTourPending()
    {
        if (_services is null) return;
        var s = _services.Settings.Current;
        if (!s.FirstRunTourPending) return;
        s.FirstRunTourPending = false;
        _services.Settings.Save();
    }

    /// <summary>LastSeenVersion = 현재 (더 새 버전이 적어 둔 값이면 그대로).</summary>
    internal static void MarkSeen()
    {
        if (_services is null) return;
        var s = _services.Settings.Current;
        var seen = WhatsNew.Parse(s.LastSeenVersion);
        if (seen is not null && seen >= WhatsNew.Current) return;
        s.LastSeenVersion = WhatsNew.CurrentText;
        _services.Settings.Save();
    }

    // ───────────────────────── 카드 만들기 ─────────────────────────

    private static bool AnchorVisible(CoachAnchor a)
    {
        if (a == CoachAnchor.Center) return true;
        // 자동 숨김 독은 지금 숨어 있어도 둘러보기가 고정해 보이게 하므로 보이는 것으로 봄 (CoachSession)
        if (a == CoachAnchor.Dock && _services?.Settings.Current.Dock is { Enabled: true, Mode: DockMode.AutoHide } && !AppState.Paused) return true;
        return _resolve?.Invoke(a) is not null;
    }

    private static CoachPage ToPage(CoachStep step, Settings s) => new()
    {
        Title = step.Title,
        Body = step.Body.Replace("{hotkey}", HotkeyPhrase(s)).Replace("{dockedge}", DockEdgePhrase(s))
            .Replace("{dockhide}", s.Dock.Mode == DockMode.AutoHide
                ? $" 평소엔 숨어 있다가 마우스를 {DockEdgePhrase(s)} 끝에 대면 나타나요 — [다음]을 누르면 숨는 걸 보여 드려요."
                : ""),
        Anchor = step.Anchor,
        NextText = step.Key == WhatsNew.IntroKey ? "좋아요" : null,
        Action = step.Key == WhatsNew.IntroKey ? ("작업 표시줄 다시 보이기", ShowWindowsTaskbarAgain) : null,
        AdvanceOnUse = step.Key == WhatsNew.SearchKey,
        Hint = step.Title.Contains("눌러 보세요") || step.Body.Contains("눌러 보세요") ? null : PressHint(step.Anchor),
    };

    /// <summary>둘러보기 첫 카드 [작업 표시줄 다시 보이기]: "윈도우 작업 표시줄 숨기기" 끄기 (알림 숨김은 그대로).</summary>
    private static void ShowWindowsTaskbarAgain()
    {
        if (_services is null) return;
        _services.Settings.Current.SetHideWindowsTaskbar(false);
        _services.Settings.Save();
        Log.Info("둘러보기: 작업 표시줄 다시 보이기");
    }

    /// <summary>독 위치 쪽 화면 가장자리 ("화면 아래" / "화면 왼쪽" …).</summary>
    private static string DockEdgePhrase(Settings s) => s.Dock.Edge switch
    {
        DockEdge.Left => "화면 왼쪽",
        DockEdge.Right => "화면 오른쪽",
        DockEdge.Top => "화면 위",
        _ => "화면 아래",
    };

    /// <summary>앵커를 직접 눌러 보라는 짧은 안내 (상단바 요소만 — 독은 누르면 앱이 열려서 안내하지 않음).</summary>
    private static string? PressHint(CoachAnchor anchor) => anchor switch
    {
        CoachAnchor.Logo => "로고를 눌러 보세요",
        CoachAnchor.AppName => "앱 이름을 눌러 보세요",
        CoachAnchor.Desktops => "가운데 숫자를 눌러 보세요",
        CoachAnchor.Search => "검색 버튼을 눌러 보세요",
        CoachAnchor.Clock => "시계를 눌러 보세요",
        CoachAnchor.Tray => "⌃ 를 눌러 보세요",
        _ => null,
    };

    /// <summary>꺼져 있거나 지금 안 보이는 기능 → 화면 가운데 한 줄 카드 (설정 창 둘러보기 재생용).</summary>
    private static CoachPage OffPage(CoachStep step) => new()
    {
        Title = step.Title,
        Body = "이 기능은 지금 꺼져 있어요. 설정에서 켤 수 있어요.",
    };

    private static CoachPage ReplayPage(CoachStep step, Settings s) =>
        step.IsAvailable(s) && AnchorVisible(step.Anchor) ? ToPage(step, s) : OffPage(step);

    /// <summary>이 버전에 둘러볼 coach 단계가 있는지 (변경 내역·정보 페이지의 "둘러보기 ▶" 표시).</summary>
    public static bool HasTour(ChangeRelease release) => WhatsNew.Releases.Any(x => x.Version == release.VersionText);

    /// <summary>설정 창 "둘러보기 ▶": 그 버전의 coach 단계 (꺼진 기능은 한 줄 카드로), 최대 8장.</summary>
    public static List<CoachPage> BuildReleaseTour(ChangeRelease release)
    {
        if (_services is null) return new List<CoachPage>();
        var s = _services.Settings.Current;
        return WhatsNew.Releases.Where(x => x.Version == release.VersionText)
            .Take(MaxReplaySteps).Select(x => ReplayPage(x, s)).ToList();
    }

    /// <summary>"주요 기능 둘러보기 ▶": 모든 버전의 major + coach 단계, 최신 순, 같은 Key 는 최신 것만, 최대 8장.</summary>
    public static List<CoachPage> BuildMajorTour()
    {
        if (_services is null) return new List<CoachPage>();
        var s = _services.Settings.Current;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<CoachPage>();
        foreach (var release in Changelog.Releases)
        {
            foreach (var e in release.Majors)
            {
                if (pages.Count >= MaxReplaySteps) return pages;
                if (e.Coach is null || !keys.Add(e.Coach.Key)) continue;
                var step = WhatsNew.Releases.FirstOrDefault(x => x.Version == release.VersionText && string.Equals(x.Key, e.Coach.Key, StringComparison.OrdinalIgnoreCase));
                if (step is not null) pages.Add(ReplayPage(step, s));
            }
        }
        return pages;
    }

    /// <summary>정보 페이지 "처음 사용 둘러보기".</summary>
    public static List<CoachPage> BuildTourPages() => _services is null ? new List<CoachPage>() : BuildTour();

    /// <summary>"Win+Space(또는 검색 버튼)로" — 실제 Spotlight 단축키 설정에 맞춤.</summary>
    private static string HotkeyPhrase(Settings s)
    {
        SpotlightHotkey key;
        try { key = SpotlightHotkeyController.Resolve(s.TopBar.SpotlightHotkey); }
        catch { key = s.TopBar.SpotlightHotkey; }
        string? text = key switch
        {
            SpotlightHotkey.WinSpace => "Win+Space",
            SpotlightHotkey.AltSpace => "Alt+Space",
            SpotlightHotkey.CtrlSpace => "Ctrl+Space",
            _ => null,
        };
        return text is null ? "검색 버튼을 눌러" : $"{text}(또는 검색 버튼)로";
    }

    /// <summary>
    /// (after, upTo] 버전들의 변경 내역 → 카드.
    /// 가장 새 버전의 coach 단계 중 켜져 있고 앵커가 보이는 것은 말풍선으로 최대 6장,
    /// 마지막 가운데 카드에 나머지(말풍선 못 띄운 새 기능·코치 없는 새 기능 > 고친 문제 > 개선, 이전 버전은 새 기능 먼저)를
    /// 버전 머리글별로 — 최신 버전 5줄, 이전 버전마다 3줄까지, 넘치면 "외 N개". 같은 Key 의 기능은 최신 버전 것만.
    /// </summary>
    private static List<CoachPage> BuildWhatsNew(Version after, Version upTo)
    {
        var s = _services!.Settings.Current;
        var pages = new List<CoachPage>();
        var releases = Changelog.Between(after, upTo); // 최신 먼저
        if (releases.Count == 0) return pages;
        if (releases.Count >= 2) return BuildCatchUp(releases, s);
        var latest = releases[0];

        var latestSteps = WhatsNew.Releases.Where(x => WhatsNew.Parse(x.Version) == latest.Version).ToList();
        var coach = latestSteps.Where(x => x.IsAvailable(s) && AnchorVisible(x.Anchor)).Take(MaxWhatsNewSteps).ToList();
        pages.AddRange(coach.Select(x => ToPage(x, s)));

        // 말풍선으로 본 기능(Key)은 목록에서 빼고, 이전 버전에 같은 Key 가 있어도 한 번만
        var seenKeys = new HashSet<string>(coach.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        var groups = new List<(string Header, List<string> Items)>();
        int more = 0;
        foreach (var release in releases)
        {
            bool isLatest = release == latest;
            var candidates = release.Entries
                .Where(e => e.Coach is null || seenKeys.Add(e.Coach.Key))
                .Select((e, i) => (Entry: e, Order: i))
                .OrderBy(x => Rank(x.Entry.Kind))
                .ThenBy(x => x.Order)
                .Select(x => x.Entry.Text)
                .ToList();
            int max = isLatest ? MaxLatestLines : MaxOlderLines;
            var items = candidates.Take(max).ToList();
            more += candidates.Count - items.Count;
            if (items.Count > 0) groups.Add(($"v{release.VersionText}", items));
        }
        if (groups.Count > 0 || more > 0)
        {
            bool older = releases.Count > 1;
            pages.Add(new CoachPage
            {
                Title = coach.Count > 0 ? "그 밖에 바뀐 것" : (older ? "그동안 바뀐 것" : $"v{latest.VersionText} 에서 바뀐 것"),
                Body = older ? "업데이트하지 않은 사이에 바뀐 점이에요." : "",
                Groups = groups,
                More = more,
                ChangelogLink = true,
            });
        }
        return pages;
    }

    /// <summary>
    /// 오랜만에 업데이트(2개 버전 이상): 놓친 버전들의 주요 업데이트(major)만.
    /// 같은 Key 는 최신 버전 것만, coach 가 있고 켜져 있고 앵커가 보이면 말풍선(최신 버전 먼저, 최대 6),
    /// 나머지 주요 기능은 마지막 카드에 "v0.3.0 · headline" 머리글 아래 한 줄씩, 사소한 것(major 아닌 모든 항목)은 개수만 링크로.
    /// </summary>
    private static List<CoachPage> BuildCatchUp(List<ChangeRelease> releases, Settings s)
    {
        var pages = new List<CoachPage>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var majors = new List<(ChangeRelease Release, ChangeEntry Entry)>();
        foreach (var release in releases) // 최신 먼저 → 같은 Key 는 최신 것만 남음
            foreach (var e in release.Majors)
                if (e.Coach is null || keys.Add(e.Coach.Key)) majors.Add((release, e));

        var shown = new HashSet<ChangeEntry>();
        foreach (var (release, e) in majors)
        {
            if (pages.Count >= MaxWhatsNewSteps) break;
            if (e.Coach is null) continue;
            var step = WhatsNew.Releases.FirstOrDefault(x => x.Version == release.VersionText && string.Equals(x.Key, e.Coach.Key, StringComparison.OrdinalIgnoreCase));
            if (step is null || !step.IsAvailable(s) || !AnchorVisible(step.Anchor)) continue;
            pages.Add(ToPage(step, s));
            shown.Add(e);
        }

        var groups = new List<(string Header, List<string> Items)>();
        foreach (var release in releases)
        {
            var items = majors.Where(x => x.Release == release && !shown.Contains(x.Entry)).Select(x => x.Entry.Text).ToList();
            if (items.Count == 0) continue;
            string header = string.IsNullOrEmpty(release.Headline) ? $"v{release.VersionText}" : $"v{release.VersionText} · {release.Headline}";
            groups.Add((header, items));
        }
        int minor = releases.Sum(r => r.Entries.Count(e => !e.Major));
        if (groups.Count > 0 || minor > 0)
        {
            pages.Add(new CoachPage
            {
                Title = groups.Count > 0 ? "그동안 바뀐 주요 기능" : "그 밖에 바뀐 것",
                Body = $"v{releases[^1].VersionText} 부터 v{releases[0].VersionText} 까지 {releases.Count}개 버전이 나왔어요.",
                Groups = groups,
                ChangelogLink = true,
                LinkText = minor > 0 ? $"그 밖에 개선·수정 {minor}개 — 변경 내역 보기 ›" : "변경 내역 보기 ›",
            });
        }
        return pages;
    }

    /// <summary>목록 우선순위: 새 기능 > 고친 문제 > 개선.</summary>
    private static int Rank(ChangeKind kind) => kind switch
    {
        ChangeKind.Feature => 0,
        ChangeKind.Fix => 1,
        _ => 2,
    };

    /// <summary>
    /// 첫 둘러보기 (#22): 켜져 있고 보이는 단계만 (시스템 변경 안내·독·검색·로고) + 마무리 카드.
    /// 마무리 카드: 둘러보기에서 뺀 기능 짧은 목록, 스토어판 새 설치면 [컴퓨터 켜면 몽독도 켜기] 버튼 (따로 카드 안 띄움).
    /// </summary>
    private static List<CoachPage> BuildTour()
    {
        var services = _services!;
        var s = services.Settings.Current;
        var all = WhatsNew.Tour;
        if (all.Count == 0) return new List<CoachPage>();
        var last = all[^1];
        var pages = all.Take(all.Count - 1).Where(x => x.IsAvailable(s) && AnchorVisible(x.Anchor))
            .Take(MaxTourSteps - 1).Select(x => ToPage(x, s)).ToList();
        var final = ToPage(last, s);
        var extras = WhatsNew.ExtraFeatureLines(s);
        (string, Action)? startup = s.StartupPromptPending
            ? ("컴퓨터 켜면 몽독도 켜기", () =>
            {
                services.Startup.SetEnabled(true);
                services.Settings.Current.StartWithWindows = true;
                services.Settings.Save();
                Log.Info("둘러보기 마무리: 로그인 시 자동 실행 켬");
            })
            : null;
        pages.Add(new CoachPage
        {
            Title = final.Title,
            Body = final.Body,
            Anchor = final.Anchor,
            Groups = extras.Count > 0 ? new() { ("이 밖에도", extras.ToList()) } : null,
            Action = startup,
        });
        return pages;
    }

    // ───────────────────────── 세션 ─────────────────────────

    /// <summary>
    /// 세션 생성 → Ended 구독(onEnded 포함) → Begin 순서 — 시작하자마자 끝나도 onEnded 가 불림.
    /// isTour = 첫 설치 둘러보기 (다 보거나 건너뛰면 FirstRunTourPending 해제).
    /// </summary>
    private static CoachSession? Start(List<CoachPage> pages, Action<CoachEndReason>? onEnded = null, bool isTour = false)
    {
        _session?.Close(markSeen: false, CoachEndReason.Replaced);
        _session = null;
        if (pages.Count == 0 || _services is null || _resolve is null) return null;
        var session = new CoachSession(_services, _resolve, _popupOpen, pages);
        session.Ended += reason =>
        {
            if (_session == session) _session = null;
            if (isTour && reason == CoachEndReason.Completed)
            {
                try { ClearTourPending(); }
                catch (Exception ex) { Log.Error("첫 둘러보기 상태 저장 실패", ex); }
            }
        };
        if (onEnded is not null) session.Ended += onEnded;
        _session = session;
        session.Begin();
        return session;
    }
}

/// <summary>카드 창 하나를 재사용하며 단계를 넘기고, 앵커마다 강조 링 창을 새로 띄운다.</summary>
internal sealed class CoachSession
{
    private readonly AppServices _services;
    private readonly Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> _resolve;
    private readonly List<CoachPage> _pages;
    private readonly Func<bool>? _popupOpen;
    private readonly DispatcherTimer _reposition;
    /// <summary>지금 카드가 가리키는 앵커 (그 위 클릭 = 사용자가 직접 눌러 봄). 카드가 숨었거나 가운데 카드면 null.</summary>
    private Rect? _anchorRect;
    /// <summary>앵커를 눌러 카드를 숨긴 동안: 연 것이 닫혔는지 확인.</summary>
    private readonly DispatcherTimer _awayPoll = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _away;
    private long _awayStarted;
    private bool _awaySeenOpen;
    private OutsideClickWatcher? _awayClicks;
    private CoachMarkWindow? _card;
    private CoachRingWindow? _ring;
    private int _index;
    private bool _closed;
    private bool _dockShown;
    /// <summary>"써 보면 다음으로"(검색) 카드: Spotlight 가 열리면 카드를 숨기고, 닫히면 다음 단계.</summary>
    private readonly DispatcherTimer _usePoll = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _usedOpen;

    public event Action<CoachEndReason>? Ended;

    public CoachSession(AppServices services, Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> resolve, Func<bool>? popupOpen, List<CoachPage> pages)
    {
        _services = services;
        _resolve = resolve;
        _popupOpen = popupOpen;
        _pages = pages;
        _awayPoll.Tick += (_, _) => PollAway();
        _usePoll.Tick += (_, _) => PollUse();
        _reposition = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reposition.Tick += (_, _) =>
        {
            _reposition.Stop();
            if (!_closed && !_away) ShowCurrent(animate: false);
        };
    }

    /// <summary>
    /// 맨 위 유지: 다른 앱을 켜거나 다른 Topmost 창(상단바·독·다른 앱)이 올라와도 말풍선·링이 가려지지 않게
    /// 포그라운드 창이 바뀔 때 + 2초마다 Topmost 맨 앞으로 다시 올림 (포커스는 건드리지 않음).
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>포그라운드 창이 바뀜 (WindowTracker, EVENT_SYSTEM_FOREGROUND) → 새 창이 올라온 뒤 맨 앞으로.</summary>
    private void OnWindowActivated(object? sender, IntPtr hwnd)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_closed && !_away) BringToFront();
        });
    }

    private void BringToFront()
    {
        const uint flags = 0x0001 | 0x0002 | User32.SWP_NOACTIVATE; // NOSIZE | NOMOVE | NOACTIVATE
        foreach (System.Windows.Window? w in new System.Windows.Window?[] { _ring, _card })
        {
            if (w is null) continue;
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero) User32.SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, flags);
        }
    }

    public void Begin()
    {
        _keepOnTop.Tick += (_, _) => { if (!_closed && !_away) BringToFront(); };
        _keepOnTop.Start();
        _services.Windows.WindowActivated += OnWindowActivated;
        UiFonts.Apply(_services.Settings.Current);
        AppState.Changed += OnPausedChanged;
        _services.DesktopWindows.DisplayChanged += OnDisplayChanged;
        _services.DesktopWindows.FullscreenAppChanged += OnFullscreenChanged;
        _services.DesktopWindows.GlobalMouseDown += OnGlobalMouseDown;
        _card = new CoachMarkWindow(_services, UiTheme.Palette(_services.Settings.Current));
        _card.NextClicked += Next;
        _card.SkipClicked += () => Close(markSeen: true, CoachEndReason.Completed);
        _card.LinkClicked += () =>
        {
            // 설정 → 변경 내역. 마지막 카드에서 누르면 안내는 본 것으로 끝냄 (설정 창이 카드에 가리지 않게)
            try { SettingsWindow.OpenChangelogPage(_services); }
            catch (Exception ex) { Log.Error("변경 내역 열기 실패", ex); }
            if (_index >= _pages.Count - 1) Close(markSeen: true, CoachEndReason.Completed);
        };
        _index = 0;
        // 독을 가리키는 단계가 있으면 자동 숨김 독을 보이게 고정 (설정값은 그대로, Close 에서 반드시 풂)
        if (_pages.Any(p => p.Anchor == CoachAnchor.Dock)) DockState.SetCoachPinned(true);
        ShowCurrent(animate: true);
    }

    private void OnPausedChanged(object? sender, EventArgs e)
    {
        if (AppState.Paused) Close(markSeen: false, CoachEndReason.Paused);
    }

    private void OnFullscreenChanged(object? sender, bool any)
    {
        if (any && _services.DesktopWindows.IsFullscreenOn("")) Close(markSeen: false, CoachEndReason.Fullscreen);
    }

    /// <summary>배율·모니터 구성이 바뀌면 자리 잡은 뒤 다시 배치.</summary>
    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        _reposition.Stop();
        _reposition.Start();
    }

    private void Next()
    {
        if (_closed) return;
        if (_index >= _pages.Count - 1)
        {
            Close(markSeen: true, CoachEndReason.Completed);
            return;
        }
        _index++;
        ShowCurrent(animate: true);
    }

    /// <summary>현재 단계 표시. 그 사이 앵커가 사라졌으면(기능을 끔) 그 단계는 빼고 다음으로.</summary>
    private void ShowCurrent(bool animate)
    {
        if (_closed || _card is null) return;
        (Rect Rect, MonitorInfo Monitor)? anchor = null;
        while (_index < _pages.Count)
        {
            var page = _pages[_index];
            if (page.Anchor == CoachAnchor.Center) break;
            anchor = SafeResolve(page.Anchor);
            if (anchor is not null) break;
            _pages.RemoveAt(_index);
        }
        if (_index >= _pages.Count)
        {
            Close(markSeen: true, CoachEndReason.Completed);
            return;
        }

        var current = _pages[_index];
        // 독 카드를 지나면 고정을 풂 → 자동 숨김 독이 다음 카드로 넘어가며 미끄러져 숨는 걸 보여 줌
        if (current.Anchor == CoachAnchor.Dock) _dockShown = true;
        else if (_dockShown) DockState.SetCoachPinned(false);
        if (current.AdvanceOnUse) _usePoll.Start(); else _usePoll.Stop();
        _ring?.Close();
        _ring = null;
        if (anchor is { } a)
        {
            double radius = current.Anchor == CoachAnchor.Dock
                ? Math.Max(8, _services.Settings.Current.Dock.CornerRadius + 4)
                : Math.Min(a.Rect.Height / 2 + 3, 10);
            var p = UiTheme.Palette(_services.Settings.Current);
            _ring = new CoachRingWindow(_services, a.Rect, a.Monitor, radius, p.Accent);
            _ring.Show();
        }
        var monitor = anchor?.Monitor ?? _services.DesktopWindows.ResolveMonitor("");
        _anchorRect = anchor?.Rect;
        _card.ShowPage(current, _index, _pages.Count, anchor?.Rect, monitor, animate);
    }

    // ───────────────────────── 앵커를 직접 눌러 봄 ─────────────────────────

    /// <summary>
    /// 말풍선이 가리키는 요소를 사용자가 누름 (강조 링은 클릭 통과라 실제 버튼이 평소처럼 동작) →
    /// 카드·링을 숨겨 열린 패널·검색창·메뉴와 겹치지 않게 하고, 그것이 닫히면 같은 카드를 다시 보여 줘 "다음 →"으로 이어감.
    /// </summary>
    private void OnGlobalMouseDown(object? sender, Point? position)
    {
        if (_closed || _away || _anchorRect is not Rect r || position is not Point p) return;
        r.Inflate(2, 2);
        if (!r.Contains(p)) return;
        _away = true;
        _awayStarted = Environment.TickCount64;
        _awaySeenOpen = false;
        _anchorRect = null;
        _ring?.Close();
        _ring = null;
        _card?.HideForAway();
        _awayPoll.Start();
    }

    private bool PopupOpen()
    {
        try { return SpotlightWindow.IsOpen || _popupOpen?.Invoke() == true; }
        catch { return false; }
    }

    /// <summary>
    /// 열린 것(패널·메뉴·검색창)이 보이면 닫힐 때까지 기다림. 1.5초 안에 아무것도 안 열리면(작업 보기·윈도우 검색·데스크톱 넘김 등
    /// 닫힘을 알 수 없는 것) 클릭 시점에서 10초 뒤 또는 아무 데나 클릭하면 다시. 3분이 지나면 무조건 다시 (숨은 채 남지 않게).
    /// </summary>
    private void PollAway()
    {
        if (!_away || _closed)
        {
            _awayPoll.Stop();
            return;
        }
        long elapsed = Environment.TickCount64 - _awayStarted;
        if (elapsed > 180_000)
        {
            Return();
            return;
        }
        if (_awayClicks is not null)
        {
            if (elapsed >= 10_000) Return();
            return;
        }
        if (PopupOpen())
        {
            _awaySeenOpen = true;
            return;
        }
        if (_awaySeenOpen)
        {
            Return();
            return;
        }
        if (elapsed > 1500)
        {
            _awayClicks = new OutsideClickWatcher(_services, Array.Empty<Rect>, Return) { CloseOnActivation = false };
            _awayClicks.Start();
        }
    }

    private void PollUse()
    {
        if (_closed || _index >= _pages.Count || !_pages[_index].AdvanceOnUse)
        {
            _usePoll.Stop();
            return;
        }
        bool open = SpotlightWindow.IsOpen;
        if (open && !_usedOpen)
        {
            _usedOpen = true;
            if (!_away)
            {
                _ring?.Close();
                _ring = null;
                _anchorRect = null;
                _card?.HideForAway();
            }
        }
        else if (!open && _usedOpen)
        {
            _usedOpen = false;
            _usePoll.Stop();
            StopAway();
            Next();
        }
    }

    private void Return()
    {
        if (!_away) return;
        StopAway();
        if (!_closed) ShowCurrent(animate: true);
    }

    private void StopAway()
    {
        _away = false;
        _awayPoll.Stop();
        _awayClicks?.Stop();
        _awayClicks = null;
    }

    private (Rect Rect, MonitorInfo Monitor)? SafeResolve(CoachAnchor a)
    {
        try { return _resolve(a); }
        catch (Exception ex)
        {
            Log.Warn($"코치마크 앵커 {a} 위치 조회 실패: {ex.Message}");
            return null;
        }
    }

    public void Close(bool markSeen, CoachEndReason reason)
    {
        if (_closed) return;
        _closed = true;
        _usePoll.Stop();
        DockState.SetCoachPinned(false);
        _keepOnTop.Stop();
        _services.Windows.WindowActivated -= OnWindowActivated;
        _reposition.Stop();
        StopAway();
        _anchorRect = null;
        _services.DesktopWindows.GlobalMouseDown -= OnGlobalMouseDown;
        AppState.Changed -= OnPausedChanged;
        _services.DesktopWindows.DisplayChanged -= OnDisplayChanged;
        _services.DesktopWindows.FullscreenAppChanged -= OnFullscreenChanged;
        _ring?.Close();
        _ring = null;
        _card?.FadeClose();
        _card = null;
        if (markSeen)
        {
            try { CoachMarks.MarkSeen(); }
            catch (Exception ex) { Log.Error("LastSeenVersion 저장 실패", ex); }
        }
        Ended?.Invoke(reason);
    }
}
