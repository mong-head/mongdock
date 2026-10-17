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
    public string LinkText { get; init; } = Loc.T("변경 내역 보기 ›");
    /// <summary>본문 아래 강조색 한 줄 (예 "시계를 눌러 보세요") — 앵커를 직접 눌러 보라는 안내.</summary>
    public string? Hint { get; init; }
    /// <summary>[다음 →] 대신 쓸 글자 (예 "좋아요").</summary>
    public string? NextText { get; init; }
    /// <summary>[다음] 왼쪽의 두 번째 버튼 — 누르면 실행하고 다음 단계로 (예 "작업 표시줄 다시 보이기", "컴퓨터 켜면 몽독도 켜기").</summary>
    public (string Label, Action Run)? Action { get; init; }
    /// <summary>가리키는 기능을 직접 써 보면(Spotlight 를 열었다 닫으면) 저절로 다음 단계로.</summary>
    public bool AdvanceOnUse { get; init; }
    /// <summary>카드 맨 아래 회색 작은 글씨 한 줄 (예: 마무리 카드의 사용 통계 안내).</summary>
    public string? Footnote { get; init; }
    /// <summary>둘러보기 카드 그림 키 (Assets/Tour/tour-{key}-…). null 이면 그림 없음.</summary>
    public string? Pictures { get; init; }
    /// <summary>그림 없는 목록 카드에서도 위에 몽독 아이콘 (둘러보기 정리 카드 "그 밖에 알아 두면 좋은 것").</summary>
    public bool ShowLogo { get; init; }
    /// <summary>설명 아래 버튼 (누르면 실행만, 다음 단계로 안 넘어감 — 예 업데이트 "독 폴더·휴지통" 카드 [다운로드·휴지통 넣기]).</summary>
    public InlineAction? Inline { get; init; }
}

/// <summary>
/// 카드 설명 아래 버튼 (설정을 바꾸는 버튼은 모두 이 방식 — 2026-10-14 PM: 조용히 바꾸고 넘어가지 않기).
/// Visible = 지금 보일지(이미 해 둔 상태면 안 보임). Run = 실행 → 결과(null 이면 아무것도 안 바뀜).
/// 실행하면 버튼 자리에 결과 한 줄 + [되돌리기](Undo 가 있으면), 다음 단계로는 안 넘어감.
/// LongestResult = 카드 높이를 잴 때 쓸 가장 긴 결과 문구.
/// </summary>
internal sealed record InlineAction(string Label, Func<bool> Visible, Func<Task<InlineResult?>> Run, string LongestResult);

/// <summary>버튼 결과: 결과 한 줄, 되돌리기(되돌린 뒤 보일 문구를 돌려줌 — null 이면 되돌리기 없음).</summary>
internal sealed record InlineResult(string Message, Func<string>? Undo = null);

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
    private const int MaxLatestLines = 6;
    private const int MaxOlderLines = 3;
    private const int MaxTourSteps = 8;
    /// <summary>설정 창에서 재생하는 버전별·주요 기능 둘러보기 최대 장수.</summary>
    private const int MaxReplaySteps = 8;

    private static AppServices? _services;
    private static Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?>? _resolve;
    private static Func<bool>? _popupOpen;
    private static DispatcherTimer? _startupTimer;
    private static ICoachRun? _session;

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
                Start(new List<CoachPage> { new() { Title = step.Title, Body = step.Body.Replace("{hotkey}", HotkeyPhrase(s)), Anchor = step.Anchor, NextText = Loc.T("알겠어요") } });
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
                pages.Add(new CoachPage { Title = Loc.T("새로운 기능이 없어요"), Body = Loc.F($"버전 {WhatsNew.CurrentText} 에는 따로 소개할 기능이 없어요."), ChangelogLink = true });
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
                Loc.T("컴퓨터를 켜면 몽독도 같이 켤까요?"),
                Loc.T("로그인하면 독과 상단바가 바로 나타나요. 설정 → 일반에서 언제든 바꿀 수 있어요."),
                Loc.T("켜기"), Loc.T("나중에"));
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
            if (choice == true)
                ConfirmCardWindow.ShowResult(services, Loc.T("자동 실행을 켰어요"), Loc.T("컴퓨터를 켜면 몽독도 켜져요. 설정 → 일반에서도 바꿀 수 있어요."), () =>
                {
                    services.Startup.SetEnabled(false);
                    services.Settings.Current.StartWithWindows = false;
                    services.Settings.Save();
                    Log.Info("첫 실행 자동 실행 질문: 되돌림");
                    return Loc.T("되돌렸어요 — 자동 실행 꺼짐");
                });
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
        // 둘러보기에서 검색 단계를 봤으면 처음 쓰기 힌트 "검색"은 띄우지 않음 (0.5.1)
        var search = WhatsNew.Tour.FirstOrDefault(t => t.Key == WhatsNew.TourSearchKey);
        if (search is not null && search.IsAvailable(s) && !s.SeenHints.Contains(WhatsNew.SearchKey, StringComparer.OrdinalIgnoreCase))
        {
            s.SeenHints.Add(WhatsNew.SearchKey);
            if (WhatsNew.Hints.All(h => s.SeenHints.Contains(h.Key, StringComparer.OrdinalIgnoreCase))) s.FirstUseHintsPending = false;
        }
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
        if (a is CoachAnchor.Dock or CoachAnchor.DockFolder && _services?.Settings.Current.Dock is { Enabled: true, Mode: DockMode.AutoHide } && !AppState.Paused) return true;
        return _resolve?.Invoke(a) is not null;
    }

    /// <summary>
    /// 업데이트 사용자용 "독 폴더·휴지통" 카드 (첫 설치는 이미 있음): [다운로드·휴지통 넣기] — 독에 없는 것만, 새 설치와 같은 자리.
    /// 둘 다 이미 있으면 버튼 없음. 넣어도 다음 단계로 넘어가지 않음 (강조 구멍이 새 칸으로 옮겨 감).
    /// </summary>
    private static CoachPage WithStacksButton(CoachPage page) => new()
    {
        Title = page.Title,
        Body = page.Body,
        Anchor = page.Anchor,
        Hint = page.Hint,
        Pictures = page.Pictures,
        Inline = new InlineAction(Loc.T("다운로드·휴지통 넣기"),
            () => _services is { } sv && DefaultPins.DockStacksMissing(sv.Settings.Current.Pins),
            () => Task.FromResult(AddDockStacks()),
            Loc.T("독 끝에 넣었어요 — 눌러서 펼쳐 보세요")),
    };

    /// <summary>카드에 설명 아래 버튼을 붙인 복사본 (WithStacksButton 과 같은 항목만 옮김).</summary>
    private static CoachPage WithInline(CoachPage page, InlineAction inline) => new()
    {
        Title = page.Title,
        Body = page.Body,
        Anchor = page.Anchor,
        Hint = page.Hint,
        Pictures = page.Pictures,
        Inline = inline,
    };

    /// <summary>
    /// 0.8 창 최소화 효과 카드 [켜기] (#20): 「살짝 내려앉기」로 켬 → 결과 줄
    /// "켰어요 — 윈도우 최소화·최대화 효과는 껐어요" + [되돌리기](끔 → 윈도우 효과 되돌림). 이미 켜져 있으면 버튼 없음.
    /// </summary>
    private static InlineAction MinimizeInline() => new(Loc.T("켜기"),
        () => _services is { } sv && sv.Settings.Current.Dock.MinimizeEffect == MinimizeEffect.Off,
        () =>
        {
            if (_services is not { } sv) return Task.FromResult<InlineResult?>(null);
            sv.Settings.Current.Dock.MinimizeEffect = MinimizeEffect.Settle;
            sv.Settings.Save(); // MinimizeEffectController.Sync → 윈도우 효과 끄기
            bool off = WindowAnimationSetting.TurnedOffByMongdock;
            Log.Info($"둘러보기: 창 최소화 효과 켬 (윈도우 효과 {(off ? "껐음" : "원래 꺼져 있었음")})");
            string msg = off ? Loc.T("켰어요 — 윈도우 최소화·최대화 효과는 껐어요") : Loc.T("켰어요 — 설정 → 독에서 바꿀 수 있어요");
            return Task.FromResult<InlineResult?>(new InlineResult(msg, () =>
            {
                sv.Settings.Current.Dock.MinimizeEffect = MinimizeEffect.Off;
                sv.Settings.Save(); // 윈도우 효과 되돌림
                Log.Info("둘러보기: 창 최소화 효과 되돌림 (끔)");
                return Loc.T("되돌렸어요 — 윈도우 효과 그대로예요");
            }));
        },
        Loc.T("켰어요 — 윈도우 최소화·최대화 효과는 껐어요"));

    /// <summary>[다운로드·휴지통 넣기] 실행: 넣은 것만 기억해 [되돌리기]는 그것만 뺌.</summary>
    private static InlineResult? AddDockStacks()
    {
        if (_services is not { } sv) return null;
        var pins = sv.Settings.Current.Pins;
        var before = pins.ToList();
        var (dl, bin) = DefaultPins.AddDockStacks(pins);
        if (!dl && !bin) return null;
        var added = pins.Where(p => !before.Contains(p)).ToList();
        sv.Settings.Save(); // 독이 새 칸을 그림
        Log.Info($"둘러보기: 독에 넣음 (다운로드 {(dl ? "넣음" : "그대로")}, 휴지통 {(bin ? "넣음" : "그대로")})");
        return new InlineResult(Loc.T("독 끝에 넣었어요 — 눌러서 펼쳐 보세요"), () =>
        {
            sv.Settings.Current.Pins.RemoveAll(added.Contains);
            sv.Settings.Save();
            Log.Info("둘러보기: 다운로드·휴지통 되돌림 (독에서 뺌)");
            return Loc.T("되돌렸어요 — 독에서 뺐어요");
        });
    }

    /// <summary>둘러보기 [컴퓨터 켜면 몽독도 켜기]: 켜고 결과 + [되돌리기] (예전엔 켜고 바로 끝났음).</summary>
    private static InlineAction StartupInline(AppServices services) => new(Loc.T("컴퓨터 켜면 몽독도 켜기"),
        () => !services.Settings.Current.StartWithWindows,
        () =>
        {
            var s = services.Settings.Current;
            bool wasOn = s.StartWithWindows;
            services.Startup.SetEnabled(true);
            s.StartWithWindows = true;
            services.Settings.Save();
            Log.Info("둘러보기: 로그인 시 자동 실행 켬");
            return Task.FromResult<InlineResult?>(new InlineResult(Loc.T("켰어요 — 컴퓨터를 켜면 몽독도 켜져요. 설정 → 일반에서도 바꿀 수 있어요"), () =>
            {
                services.Startup.SetEnabled(wasOn);
                services.Settings.Current.StartWithWindows = wasOn;
                services.Settings.Save();
                Log.Info("둘러보기: 자동 실행 되돌림");
                return Loc.T("되돌렸어요 — 자동 실행 꺼짐");
            }));
        },
        Loc.T("켰어요 — 컴퓨터를 켜면 몽독도 켜져요. 설정 → 일반에서도 바꿀 수 있어요"));

    /// <summary>둘러보기 [허용하기]: 윈도우 허락 창 → 결과를 다시 읽어 "허용됐어요"/"아직 허용 안 됨" (윈도우 권한이라 되돌리기 없음).</summary>
    private static InlineAction AllowInline(AppServices services) => new(Loc.T("허용하기"),
        () => NotificationListener.Status() != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed,
        async () =>
        {
            try { await NotificationListener.RequestAccessAsync(); }
            catch (Exception ex) { Log.Warn($"알림 읽기 허락 요청 실패: {ex.GetType().Name}"); }
            bool allowed = NotificationListener.Status() == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed;
            if (allowed) (services.Notifications as NotificationService)?.ReconsiderSource();
            Log.Info($"둘러보기: 알림 읽기 허락 → {(allowed ? "허용됨" : "아직 허용 안 됨")}");
            return new InlineResult(allowed
                ? Loc.T("허용됐어요 — 새 알림이 몽독 배너로 떠요")
                : Loc.T("아직 허용 안 됨 — 나중에 설정 → 알림에서 허용할 수 있어요"));
        },
        Loc.T("아직 허용 안 됨 — 나중에 설정 → 알림에서 허용할 수 있어요"));

    private static CoachPage ToPage(CoachStep step, Settings s) => new()
    {
        Title = step.Title,
        Body = step.Body.Replace("{hotkey}", HotkeyPhrase(s)).Replace("{dockedge}", DockEdgePhrase(s))
            .Replace("{dockhide}", s.Dock.Mode == DockMode.AutoHide
                ? Loc.F($" 평소엔 숨어 있다가 마우스를 {DockEdgePhrase(s)} 끝에 대면 나타나요 — [다음]을 누르면 숨는 걸 보여 드려요.")
                : ""),
        Anchor = step.Anchor,
        NextText = step.Key == WhatsNew.IntroKey ? Loc.T("좋아요") : null,
        AdvanceOnUse = step.Key == WhatsNew.SearchKey,
        Hint = HasPressPrompt(step.Title) || HasPressPrompt(step.Body) ? null : PressHint(step.Anchor),
        Pictures = WhatsNew.TourPictures.Contains(step.Key) ? step.Key : null,
    };

    /// <summary>카드 글에 이미 "눌러 보세요"(영어 "Click …"/"Try it") 가 있으면 아래 힌트 줄을 또 붙이지 않음.</summary>
    private static bool HasPressPrompt(string text) =>
        (Loc.SourceOf(text) ?? text).Contains("눌러 보세요", StringComparison.Ordinal) // 번역된 문구는 한국어 원문으로 판단
        || text.StartsWith("Click ", StringComparison.Ordinal) || text.Contains("Try it", StringComparison.Ordinal); // 사전에 없는 영어(변경 내역 _en)

    /// <summary>둘러보기 [허용하기]: 윈도우 허락 창 → 허용되면 알림을 공식 API 로 다시 읽기 시작.</summary>

    /// <summary>독 위치 쪽 화면 가장자리 ("화면 아래" / "화면 왼쪽" …).</summary>
    private static string DockEdgePhrase(Settings s) => s.Dock.Edge switch
    {
        DockEdge.Left => Loc.T("화면 왼쪽"),
        DockEdge.Right => Loc.T("화면 오른쪽"),
        DockEdge.Top => Loc.T("화면 위"),
        _ => Loc.T("화면 아래"),
    };

    /// <summary>앵커를 직접 눌러 보라는 짧은 안내 (상단바 요소만 — 독은 누르면 앱이 열려서 안내하지 않음).</summary>
    private static string? PressHint(CoachAnchor anchor) => anchor switch
    {
        CoachAnchor.Logo => Loc.T("로고를 눌러 보세요"),
        CoachAnchor.AppName => Loc.T("앱 이름을 눌러 보세요"),
        CoachAnchor.Desktops => Loc.T("가운데 숫자를 눌러 보세요"),
        CoachAnchor.Search => Loc.T("검색 버튼을 눌러 보세요"),
        CoachAnchor.Clock => Loc.T("시계를 눌러 보세요"),
        CoachAnchor.Tray => Loc.T("⌃ 를 눌러 보세요"),
        _ => null,
    };

    /// <summary>꺼져 있거나 지금 안 보이는 기능 → 화면 가운데 한 줄 카드 (설정 창 둘러보기 재생용).</summary>
    private static CoachPage OffPage(CoachStep step) => new()
    {
        Title = step.Title,
        Body = Loc.T("이 기능은 지금 꺼져 있어요. 설정에서 켤 수 있어요."),
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

    /// <summary>설정 → 변경 내역 "v0.5.0 새 기능 다시 보기": 업데이트 때 보는 것과 같은 카드(새 기능 장면 + 그 밖에 바뀐 것).</summary>
    public static List<CoachPage> BuildCurrentWhatsNew()
    {
        if (_services is null) return new List<CoachPage>();
        var current = WhatsNew.Current;
        var older = Changelog.Releases.Select(r => r.Version).Where(v => v < current).OrderByDescending(v => v).FirstOrDefault();
        return BuildWhatsNew(older ?? new Version(0, 0, 0), current);
    }

    /// <summary>정보 페이지 "처음 사용 둘러보기".</summary>
    public static List<CoachPage> BuildTourPages() => _services is null ? new List<CoachPage>() : BuildTour();

    /// <summary>"Win+Space(또는 검색 버튼)로" — 실제 Spotlight 단축키 설정에 맞춤.</summary>
    private static string HotkeyPhrase(Settings s)
    {
        HotkeyChord? key;
        try { key = HotkeyController.SearchChord(s); }
        catch { key = null; }
        return key is { } k ? Loc.F($"{string.Join("+", k.Labels)}(또는 검색 버튼)로") : Loc.T("검색 버튼을 눌러");
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
        var latest = releases[0];
        // 바로 앞 부 버전(0.4.x → 0.5.0)에서 올라왔으면 패치 몇 개를 건너뛰었어도 이번 버전 새 기능 장면 + 목록 (2026-10-14).
        // 그보다 오래된 버전에서 올라오면 주요 기능만 (오랜만에 업데이트)
        bool previousMinor = after.Major == latest.Version.Major && after.Minor == latest.Version.Minor - 1;
        if (releases.Count >= 2 && !previousMinor) return BuildCatchUp(releases, s);

        var latestSteps = WhatsNew.Releases.Where(x => WhatsNew.Parse(x.Version) == latest.Version).ToList();
        // 그림 장면이 있는 새 기능은 가리킬 자리가 지금 없어도 넣음 (카드만 가운데 — 장면이 설명함)
        var coach = latestSteps.Where(x => x.IsAvailable(s) && (WhatsNew.TourPictures.Contains(x.Key) || AnchorVisible(x.Anchor))).Take(MaxWhatsNewSteps).ToList();
        pages.AddRange(coach.Select(x => x.Key switch
        {
            "stacks" => WithStacksButton(ToPage(x, s)),
            "minimize" => WithInline(ToPage(x, s), MinimizeInline()), // 0.8 창 최소화 효과 [켜기] (#20)
            _ => ToPage(x, s),
        }));

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
                Title = coach.Count > 0 ? Loc.T("그 밖에 바뀐 것") : (older ? Loc.T("그동안 바뀐 것") : Loc.F($"v{latest.VersionText} 에서 바뀐 것")),
                Body = older ? Loc.T("업데이트하지 않은 사이에 바뀐 점이에요.") : "",
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
                Title = groups.Count > 0 ? Loc.T("그동안 바뀐 주요 기능") : Loc.T("그 밖에 바뀐 것"),
                Body = Loc.F($"v{releases[^1].VersionText} 부터 v{releases[0].VersionText} 까지 {releases.Count}개 버전이 나왔어요."),
                Groups = groups,
                ChangelogLink = true,
                LinkText = minor > 0 ? Loc.F($"그 밖에 개선·수정 {minor}개 — 변경 내역 보기 ›") : Loc.T("변경 내역 보기 ›"),
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
        // 그림 단계는 가리킬 자리가 지금 없어도 넣음 (카드만 가운데 — 그림이 설명함)
        var pages = all.Take(all.Count - 1).Where(x => x.IsAvailable(s) && (WhatsNew.TourPictures.Contains(x.Key) || AnchorVisible(x.Anchor)))
            .Take(MaxTourSteps - 1).Select(x => ToPage(x, s)).ToList();
        // 스토어판: 첫 카드(시스템 변경 안내) 다음에 윈도우 알림 접근 허락 카드 (#7 결정 16). 일반판·이미 정했으면 없음
        if (NotificationListener.Supported && NotificationListener.Status() == Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Unspecified)
        {
            int at = pages.Count > 0 && all[0].Key == WhatsNew.IntroKey ? 1 : 0;
            pages.Insert(at, new CoachPage
            {
                Title = Loc.T("알림을 몽독에서 보려면"),
                Body = Loc.T("윈도우가 알림 읽기 허락을 물어봐요. 허용하면 새 알림을 상단바 아래 배너와 알림 목록으로 보여 주고, 몽독에서 지운 알림은 윈도우 알림 센터에서도 지워져요."),
                Anchor = CoachAnchor.Center,
                Inline = AllowInline(services),
            });
        }
        var final = ToPage(last, s);
        var extras = WhatsNew.ExtraFeatureLines(s);
        InlineAction? startup = s.StartupPromptPending ? StartupInline(services) : null;
        // 설정 카드는 설명만, "이 밖에도" 목록은 마지막 정리 카드 한 장으로 따로 (2026-10-14 PM: 설정 카드에서 목록이 잘림)
        pages.Add(new CoachPage
        {
            Title = final.Title,
            Body = final.Body,
            Anchor = final.Anchor,
            Pictures = final.Pictures,
            Inline = extras.Count > 0 ? null : startup,
        });
        if (extras.Count > 0)
            pages.Add(new CoachPage
            {
                Title = Loc.T("그 밖에 알아 두면 좋은 것"),
                Body = Loc.T("처음 써 볼 때 짧게 알려 드려요."),
                Groups = new() { ("", extras.ToList()) },
                ShowLogo = true,
                Inline = startup,
            });
        return pages;
    }

    // ───────────────────────── 세션 ─────────────────────────

    /// <summary>
    /// 세션 생성 → Ended 구독(onEnded 포함) → Begin 순서 — 시작하자마자 끝나도 onEnded 가 불림.
    /// isTour = 첫 설치 둘러보기 (다 보거나 건너뛰면 FirstRunTourPending 해제).
    /// </summary>
    private static ICoachRun? Start(List<CoachPage> pages, Action<CoachEndReason>? onEnded = null, bool isTour = false)
    {
        _session?.Close(markSeen: false, CoachEndReason.Replaced);
        _session = null;
        if (pages.Count == 0 || _services is null || _resolve is null) return null;
        // 첫 설치 둘러보기·다시 보기 = 새 방식 카드(그림 + 강조), 새 기능·힌트 = 말풍선 (spec-coach-tour.md)
        // 장면 그림이 있는 안내(업데이트 "새 기능" 포함)도 새 카드 — 처음 쓰기 힌트처럼 그림 없는 한 장은 말풍선
        // 장면 없이 "바뀐 것" 목록 카드만 있는 업데이트(0.6.2 처럼 작은 기능)도 새 카드 — 옛 말풍선으로 빠지지 않게
        ICoachRun session = isTour || pages.Any(p => p.Pictures is not null || p.Groups is not null) ? new TourSession(_services, _resolve, pages) : new CoachSession(_services, _resolve, _popupOpen, pages);
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
internal sealed class CoachSession : ICoachRun
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
        // 가리키는 버튼의 크기·자리가 바뀌면(데스크톱 알약에 루틴 이름이 붙음 등) 링·말풍선을 다시 맞춤 (QA: 링이 알약과 어긋남)
        var anchorWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        anchorWatch.Tick += (_, _) =>
        {
            if (_closed) { anchorWatch.Stop(); return; }
            if (_away || _anchorRect is not Rect old || _index >= _pages.Count) return;
            if (SafeResolve(_pages[_index].Anchor) is not { } now) return;
            if (Math.Abs(now.Rect.Left - old.Left) > 1 || Math.Abs(now.Rect.Width - old.Width) > 1 || Math.Abs(now.Rect.Top - old.Top) > 1 || Math.Abs(now.Rect.Height - old.Height) > 1)
                ShowCurrent(animate: false);
        };
        anchorWatch.Start();
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
        if (_pages.Any(p => p.Anchor is CoachAnchor.Dock or CoachAnchor.DockFolder)) DockState.SetCoachPinned(true);
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
        if (current.Anchor is CoachAnchor.Dock or CoachAnchor.DockFolder) _dockShown = true;
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
            _ring = new CoachRingWindow(_services, a.Rect, a.Monitor, radius, p.AccentInk);
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
