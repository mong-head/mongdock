using System.Windows;
using System.Windows.Threading;
using MyDock.Models;
using MyDock.Native;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

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
}

/// <summary>
/// 버전 업데이트 후 "새로운 기능" 코치마크와 첫 설치 둘러보기의 흐름.
/// - 시작 2.5초 뒤(독·상단바가 자리 잡은 뒤) LastSeenVersion 과 현재 버전을 비교해 한 번 보여 줌.
///   새 설치(settings.json 을 이번에 만듦) → 둘러보기, 기존 설정인데 LastSeenVersion 없음 → 0.2.0 에서 올라온 것으로 봄.
/// - 최신 버전 단계(Changelog.json 의 coach)는 앵커 말풍선으로 하나씩(최대 6), 마지막 가운데 카드 "그 밖에 바뀐 것"에
///   놓친 버전들의 나머지 변경(코치 없는 새 기능 > 고친 문제 > 개선 순, 넘치면 "외 N개" + 변경 내역 링크). 코치 단계가 없으면 이 카드 한 장만.
/// - 둘러보기는 최대 8장, 넘치면 마지막 카드에 나머지 목록.
/// - 끝까지 보거나 건너뛰면 LastSeenVersion = 현재. 일시 정지·전체 화면이면 미루고, 보는 중 일시 정지되면 저장 없이 닫음(다음 실행에 다시).
/// </summary>
internal static class CoachMarks
{
    private const int MaxWhatsNewSteps = 6;
    /// <summary>"그 밖에 바뀐 것" 카드: 최신 버전 줄 수 / 그 이전 버전마다 줄 수.</summary>
    private const int MaxLatestLines = 5;
    private const int MaxOlderLines = 3;
    private const int MaxTourSteps = 8;

    private static AppServices? _services;
    private static Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?>? _resolve;
    private static DispatcherTimer? _startupTimer;
    private static CoachSession? _session;

    /// <summary>App 이 독·상단바를 만든 뒤 한 번. resolve = 앵커 → 화면 사각형(모니터 기준 DIP) + 모니터 (안 보이면 null).</summary>
    public static void Init(AppServices services, Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> resolve)
    {
        _services = services;
        _resolve = resolve;
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
        try { Start(BuildTour()); }
        catch (Exception ex) { Log.Error("둘러보기 실패", ex); }
    }

    /// <summary>앱 종료 시.</summary>
    public static void CloseAll()
    {
        _startupTimer?.Stop();
        _startupTimer = null;
        _session?.Close(markSeen: false);
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
        if (s.LastSeenVersion is null && firstInstall)
        {
            Log.Info($"첫 설치 → 둘러보기 (v{WhatsNew.CurrentText})");
            Start(BuildTour());
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

    private static bool AnchorVisible(CoachAnchor a) => a == CoachAnchor.Center || _resolve?.Invoke(a) is not null;

    private static CoachPage ToPage(CoachStep step, Settings s) => new()
    {
        Title = step.Title,
        Body = step.Body.Replace("{hotkey}", HotkeyPhrase(s)),
        Anchor = step.Anchor,
    };

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

    /// <summary>목록 우선순위: 새 기능 > 고친 문제 > 개선.</summary>
    private static int Rank(ChangeKind kind) => kind switch
    {
        ChangeKind.Feature => 0,
        ChangeKind.Fix => 1,
        _ => 2,
    };

    /// <summary>첫 둘러보기: 켜져 있고 보이는 단계만, 최대 8장 (마지막 "설정" 카드 포함). 넘치면 마지막 카드에 목록.</summary>
    private static List<CoachPage> BuildTour()
    {
        var s = _services!.Settings.Current;
        var all = WhatsNew.Tour;
        if (all.Count == 0) return new List<CoachPage>();
        var last = all[^1];
        var middle = all.Take(all.Count - 1).Where(x => x.IsAvailable(s) && AnchorVisible(x.Anchor)).ToList();
        var pages = middle.Take(MaxTourSteps - 1).Select(x => ToPage(x, s)).ToList();
        var rest = middle.Skip(MaxTourSteps - 1).Select(x => x.Title).ToList();
        var final = ToPage(last, s);
        pages.Add(rest.Count == 0 ? final : new CoachPage
        {
            Title = final.Title,
            Body = final.Body,
            Anchor = final.Anchor,
            Groups = new() { ("이 밖에도", rest) },
        });
        return pages;
    }

    // ───────────────────────── 세션 ─────────────────────────

    private static void Start(List<CoachPage> pages)
    {
        _session?.Close(markSeen: false);
        _session = null;
        if (pages.Count == 0 || _services is null || _resolve is null) return;
        var session = new CoachSession(_services, _resolve, pages);
        session.Ended += () => { if (_session == session) _session = null; };
        _session = session;
        session.Begin();
    }
}

/// <summary>카드 창 하나를 재사용하며 단계를 넘기고, 앵커마다 강조 링 창을 새로 띄운다.</summary>
internal sealed class CoachSession
{
    private readonly AppServices _services;
    private readonly Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> _resolve;
    private readonly List<CoachPage> _pages;
    private readonly DispatcherTimer _reposition;
    private CoachMarkWindow? _card;
    private CoachRingWindow? _ring;
    private int _index;
    private bool _closed;

    public event Action? Ended;

    public CoachSession(AppServices services, Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> resolve, List<CoachPage> pages)
    {
        _services = services;
        _resolve = resolve;
        _pages = pages;
        _reposition = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reposition.Tick += (_, _) =>
        {
            _reposition.Stop();
            if (!_closed) ShowCurrent(animate: false);
        };
    }

    /// <summary>
    /// 맨 위 유지: 다른 앱을 켜거나 다른 Topmost 창(상단바·독·다른 앱)이 올라와도 말풍선·링이 가려지지 않게
    /// 주기적으로 Topmost 맨 앞으로 다시 올림 (포커스는 건드리지 않음).
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromMilliseconds(400) };

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
        _keepOnTop.Tick += (_, _) => { if (!_closed) BringToFront(); };
        _keepOnTop.Start();
        UiFonts.Apply(_services.Settings.Current);
        AppState.Changed += OnPausedChanged;
        _services.DesktopWindows.DisplayChanged += OnDisplayChanged;
        _services.DesktopWindows.FullscreenAppChanged += OnFullscreenChanged;
        _card = new CoachMarkWindow(_services, UiTheme.Palette(_services.Settings.Current));
        _card.NextClicked += Next;
        _card.SkipClicked += () => Close(markSeen: true);
        _card.LinkClicked += () =>
        {
            // 설정 → 변경 내역. 마지막 카드에서 누르면 안내는 본 것으로 끝냄 (설정 창이 카드에 가리지 않게)
            try { SettingsWindow.OpenChangelogPage(_services); }
            catch (Exception ex) { Log.Error("변경 내역 열기 실패", ex); }
            if (_index >= _pages.Count - 1) Close(markSeen: true);
        };
        _index = 0;
        ShowCurrent(animate: true);
    }

    private void OnPausedChanged(object? sender, EventArgs e)
    {
        if (AppState.Paused) Close(markSeen: false);
    }

    private void OnFullscreenChanged(object? sender, bool any)
    {
        if (any && _services.DesktopWindows.IsFullscreenOn("")) Close(markSeen: false);
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
            Close(markSeen: true);
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
            Close(markSeen: true);
            return;
        }

        var current = _pages[_index];
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
        _card.ShowPage(current, _index, _pages.Count, anchor?.Rect, monitor, animate);
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

    public void Close(bool markSeen)
    {
        if (_closed) return;
        _closed = true;
        _keepOnTop.Stop();
        _reposition.Stop();
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
        Ended?.Invoke();
    }
}
