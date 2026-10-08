using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using Mongdock.Models;
using Mongdock.Services.Calendar;

namespace Mongdock.Services;

/// <summary>
/// iCal(ICS) 구독 캘린더 가져오기·저장·펼치기.
/// - 구독 목록: %APPDATA%\mongdock\calendars.json. 주소(비공개 링크 = 비밀번호와 같음)는 DPAPI(현재 사용자 + 엔트로피)로 암호화해
///   "dpapi:BASE64" 로 저장 — 다른 계정·다른 PC 로 파일을 옮기면 풀리지 않는다(그때는 "주소를 다시 추가" 안내).
///   settings.json 에는 주소를 두지 않는다 (설정 파일을 공유·백업해도 링크가 새지 않게).
/// - 캐시: %APPDATA%\mongdock\cache\calendars\{id}.ics.dat (받은 ICS, 역시 DPAPI) + {id}.json (ETag·Last-Modified·마지막 동기화).
///   재시작 직후 네트워크 전에 캐시로 바로 표시.
/// - 가져오기: HttpClient, 15초 시간 초과, webcal:// → https://, If-None-Match/If-Modified-Since(304 면 그대로),
///   실패하면 이전 데이터 유지 + Log (주소 없이 호스트 이름만).
/// - 주기: Settings.Calendar.RefreshMinutes (1분마다 확인). 일시 정지(Stop) 중엔 멈춤. 절전 복귀·네트워크 복귀 시 한 번.
/// 모든 공개 멤버·이벤트는 UI 스레드.
/// </summary>
public sealed class CalendarFeedService : ICalendarFeedService, IDisposable
{
    private const long MaxBytes = 20 * 1024 * 1024;
    private const string ProtectedPrefix = "dpapi:";
    private static readonly byte[] Entropy = "mongdock.calendar.v1"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ISettingsService _settings;
    private readonly Dispatcher _dispatcher;
    private readonly string _storePath;
    private readonly string _cacheDir;
    private readonly List<FeedState> _feeds = new();
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<(DateTime, DateTime), IReadOnlyList<CalendarOccurrence>> _occurrenceCache = new();
    /// <summary>캐시 파일 쓰기·삭제 직렬화 (백그라운드 쓰기 ↔ UI 스레드의 Remove).</summary>
    private readonly object _cacheIoGate = new();
    private readonly HashSet<string> _removedIds = new(StringComparer.Ordinal); // _cacheIoGate
    private DispatcherTimer? _kick;
    private bool _running;
    private bool _cacheLoaded;
    private bool _disposed;

    public CalendarFeedService(ISettingsService settings)
        : this(settings, AppInfo.DataDirectory)
    {
    }

    /// <summary>테스트용: 다른 데이터 폴더.</summary>
    internal CalendarFeedService(ISettingsService settings, string dataDirectory)
    {
        _settings = settings;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _storePath = Path.Combine(dataDirectory, "calendars.json");
        _cacheDir = Path.Combine(dataDirectory, "cache", "calendars");

        LoadStore();

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => Tick();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        // 캐시는 백그라운드에서 읽고(복호화·파싱) 끝나면 UI 스레드에서 붙임
        var ids = _feeds.Select(f => f.Feed.Id).ToList();
        Task.Run(() => ids.Select(LoadCache).ToList()).ContinueWith(t =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    foreach (var c in t.Result)
                    {
                        if (c is null || Find(c.Id) is not { } st) continue;
                        st.ETag ??= c.ETag;
                        st.LastModified ??= c.LastModified;
                        st.LastSync ??= c.LastSync;
                        if (st.LastAttempt == default && c.LastSync is DateTime ls) st.LastAttempt = ls;
                        if (st.Parsed == null && c.Parsed != null)
                        {
                            st.Parsed = c.Parsed;
                            SchedulePrecompute(st);
                        }
                    }
                }
                else if (t.Exception is { } ex) Log.Warn($"캘린더 캐시 읽기 실패: {ex.InnerException?.GetType().Name}");
                _cacheLoaded = true;
                RaiseChanged();
                Tick();
            });
        }, TaskScheduler.Default);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CalendarFeed> Feeds => _feeds.Select(f => f.Feed.Clone()).ToList();

    public CalendarFeedStatus GetStatus(string feedId)
    {
        var st = Find(feedId);
        if (st == null) return new CalendarFeedStatus(null, null, false, 0);
        return new CalendarFeedStatus(st.LastSync, st.Error, st.Busy, st.Parsed?.Events.Count ?? 0);
    }

    // ───────────────────────── 시작·정지 ─────────────────────────

    public void Start()
    {
        if (_disposed || _running) return;
        _running = true;
        _timer.Start();
        Tick();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
        _kick?.Stop();
    }

    /// <summary>켜진 구독 중 마지막 시도가 주기보다 오래된 것만 새로고침.</summary>
    private void Tick()
    {
        if (!_running || !_cacheLoaded || _disposed) return;
        var interval = TimeSpan.FromMinutes(RefreshMinutes());
        var now = DateTime.Now;
        var window = DisplayWindow(now);
        foreach (var st in _feeds.ToList())
        {
            if (st.Feed.Enabled && !st.Busy && now - st.LastAttempt >= interval) _ = RefreshAsync(st);
            // 달이 바뀌어 미리 펼친 범위가 지났으면 새 범위로 (받은 데이터는 그대로)
            else if (st.Pre is { } pre && ReferenceEquals(pre.Source, st.Parsed) && (pre.From, pre.To) != window) SchedulePrecompute(st);
        }
    }

    private int RefreshMinutes() => Math.Clamp(_settings.Current.Calendar?.RefreshMinutes ?? 15, 5, 1440);

    /// <summary>절전 복귀·네트워크 복귀: 네트워크가 실제로 붙을 시간을 두고 한 번 전체 새로고침.</summary>
    private void KickSoon(TimeSpan delay)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (!_running || _disposed) return;
            _kick ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _kick.Stop();
            _kick.Interval = delay;
            _kick.Tick -= OnKick;
            _kick.Tick += OnKick;
            _kick.Start();
        });
    }

    private void OnKick(object? sender, EventArgs e)
    {
        _kick?.Stop();
        if (_running) RefreshAll();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) KickSoon(TimeSpan.FromSeconds(10));
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) KickSoon(TimeSpan.FromSeconds(5));
    }

    // ───────────────────────── 목록 변경 ─────────────────────────

    public void RefreshAll()
    {
        foreach (var st in _feeds.ToList())
            if (st.Feed.Enabled) _ = RefreshAsync(st);
    }

    public void Refresh(string feedId)
    {
        if (Find(feedId) is { } st) _ = RefreshAsync(st);
    }

    public void Remove(string feedId)
    {
        var st = Find(feedId);
        if (st == null) return;
        _feeds.Remove(st);
        Log.Info($"캘린더 구독 삭제: {MaskName(st.Feed.Name)} ({HostOf(st.Feed.Url)})");
        ForgetCache(feedId);
        SaveStore();
        RaiseChanged();
    }

    public void Update(string feedId, Action<CalendarFeed> change)
    {
        var st = Find(feedId);
        if (st == null) return;
        bool wasEnabled = st.Feed.Enabled;
        string url = st.Feed.Url;
        var copy = st.Feed.Clone();
        change(copy);
        st.Feed.Name = copy.Name.Trim().Length > 0 ? copy.Name.Trim() : st.Feed.Name;
        st.Feed.Color = NormalizeColor(copy.Color) ?? st.Feed.Color;
        st.Feed.Enabled = copy.Enabled;
        st.Feed.Url = url; // 주소는 Update 로 바꾸지 않음 (삭제 후 다시 추가)
        SaveStore();
        RaiseChanged();
        if (!wasEnabled && st.Feed.Enabled && (st.Parsed == null || DateTime.Now - st.LastAttempt >= TimeSpan.FromMinutes(RefreshMinutes())))
            _ = RefreshAsync(st);
    }

    public async Task<CalendarAddResult> AddAsync(string text)
    {
        if (!TryNormalizeUrl(text, out var uri))
            return new CalendarAddResult(false, "클립보드에 캘린더 주소(https:// 또는 webcal://)가 없어요.", null, CalendarAddError.NotUrl);
        if (!IsSecure(uri))
        {
            Log.Info($"캘린더 추가 거부: http 주소 ({uri.Host})");
            return new CalendarAddResult(false,
                "https 주소만 지원해요. 캘린더 앱에서 https:// 또는 webcal:// 로 시작하는 주소를 복사해 주세요.", null, CalendarAddError.InsecureUrl);
        }
        string url = uri.AbsoluteUri;
        if (_feeds.Any(f => string.Equals(f.Feed.Url, url, StringComparison.Ordinal)))
            return new CalendarAddResult(false, "이미 추가된 캘린더예요.", null, CalendarAddError.Duplicate);

        var result = await Task.Run(() => FetchAsync(url, null, null));
        if (_disposed) return new CalendarAddResult(false, "", null, CalendarAddError.Disposed);
        if (result.Kind == FetchKind.Invalid)
        {
            Log.Warn($"캘린더 추가 실패 ({uri.Host}): {result.LogDetail}");
            return new CalendarAddResult(false, result.UserMessage ?? "캘린더를 가져오지 못했어요.", null, CalendarAddError.Invalid);
        }
        // 그 사이 같은 주소가 추가됐으면 (버튼 연타)
        if (_feeds.Any(f => string.Equals(f.Feed.Url, url, StringComparison.Ordinal)))
            return new CalendarAddResult(false, "이미 추가된 캘린더예요.", null, CalendarAddError.Duplicate);

        var feed = new CalendarFeed
        {
            Name = result.Parsed?.Name ?? DefaultName(uri),
            Url = url,
            Color = NextColor(),
        };
        var st = new FeedState(feed) { LastAttempt = DateTime.Now };
        _feeds.Add(st);
        string message;
        if (result.Kind == FetchKind.Ok)
        {
            ApplyFetched(st, result);
            message = $"'{feed.Name}' 를 추가했어요.";
        }
        else
        {
            st.Error = result.UserMessage;
            Log.Warn($"캘린더 '{MaskName(feed.Name)}' 추가 — 지금은 가져오기 실패 ({uri.Host}): {result.LogDetail}");
            message = $"'{feed.Name}' 를 추가했지만 지금은 가져오지 못했어요. 나중에 다시 시도합니다.";
        }
        Log.Info($"캘린더 구독 추가: {MaskName(feed.Name)} ({uri.Host})");
        SaveStore();
        RaiseChanged();
        return new CalendarAddResult(true, message, feed.Clone());
    }

    private string NextColor()
    {
        var used = _feeds.Select(f => f.Feed.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CalendarFeed.Palette.FirstOrDefault(c => !used.Contains(c)) ?? CalendarFeed.Palette[_feeds.Count % CalendarFeed.Palette.Length];
    }

    private static string? NormalizeColor(string? c)
    {
        if (string.IsNullOrWhiteSpace(c)) return null;
        c = c.Trim();
        if (!c.StartsWith('#')) c = "#" + c;
        return c.Length == 7 && c.Skip(1).All(Uri.IsHexDigit) ? c.ToUpperInvariant() : null;
    }

    /// <summary>
    /// 텍스트(클립보드)가 캘린더 주소인지: 첫 줄, 앞뒤 공백·따옴표·꺾쇠 제거, webcal(s):// → https://, http/https 만.
    /// http:// 도 "주소" 로는 인정(설정 창이 "주소 없음" 대신 거부 이유를 보여 주게) — 추가·가져오기는 https 만 (<see cref="IsSecure"/>).
    /// </summary>
    public static bool TryNormalizeUrl(string? text, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        int nl = s.IndexOfAny(new[] { '\r', '\n' });
        if (nl >= 0) s = s[..nl];
        s = s.Trim().Trim('"', '\'', '<', '>', ' ');
        if (s.StartsWith("webcals://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s["webcals://".Length..];
        else if (s.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s["webcal://".Length..];
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
        if (string.IsNullOrEmpty(u.Host) || s.Any(char.IsWhiteSpace)) return false;
        uri = u;
        return true;
    }

    /// <summary>ICS 에 이름(X-WR-CALNAME)이 없을 때: 호스트에 따라 "Google 캘린더"/"네이버 캘린더"/"Outlook"/"캘린더".</summary>
    public static string DefaultName(Uri uri)
    {
        string h = uri.Host.ToLowerInvariant();
        if (h.EndsWith("google.com", StringComparison.Ordinal)) return "Google 캘린더";
        if (h.EndsWith("naver.com", StringComparison.Ordinal) || h.EndsWith("worksmobile.com", StringComparison.Ordinal)) return "네이버 캘린더";
        if (h.Contains("outlook", StringComparison.Ordinal) || h.EndsWith("office365.com", StringComparison.Ordinal)
            || h.EndsWith("office.com", StringComparison.Ordinal) || h.EndsWith("live.com", StringComparison.Ordinal)) return "Outlook";
        return "캘린더";
    }

    /// <summary>https 만 허용 (비공개 캘린더 링크·일정 내용이 평문으로 오가지 않게). webcal 은 TryNormalizeUrl 이 https 로 바꿈.</summary>
    public static bool IsSecure(Uri uri) => uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>로그용: 주소 대신 호스트만 (주소가 없거나 깨졌으면 "?").</summary>
    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

    private static readonly System.Text.RegularExpressions.Regex EmailLike = new(
        @"([A-Za-z0-9._%+\-]+)@([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// 로그용 캘린더 이름: 이메일 형태(구글 기본 캘린더 이름 등)는 "a***@gmail.com" 처럼 가리고, 30자에서 자름.
    /// </summary>
    internal static string MaskName(string? name)
    {
        string s = (name ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        s = EmailLike.Replace(s, m => m.Groups[1].Value[0] + "***@" + m.Groups[2].Value);
        if (s.Length > 30) s = s[..30] + "…";
        return s.Length > 0 ? s : "(이름 없음)";
    }

    // ───────────────────────── 일정 ─────────────────────────

    public IReadOnlyList<CalendarOccurrence> GetOccurrences(DateTime from, DateTime to)
    {
        if (to <= from) return Array.Empty<CalendarOccurrence>();
        var key = (from, to);
        if (_occurrenceCache.TryGetValue(key, out var cached)) return cached;

        var list = new List<CalendarOccurrence>();
        foreach (var st in _feeds)
        {
            if (!st.Feed.Enabled || st.Parsed is not { } cal) continue;
            try
            {
                IEnumerable<IcsOccurrence> occurrences;
                if (st.Pre is { } pre && ReferenceEquals(pre.Source, cal) && pre.From <= from && to <= pre.To)
                {
                    // 백그라운드에서 미리 펼친 표시 범위 안 → 거르기만
                    occurrences = pre.Items.Where(o => Overlaps(o, from, to));
                }
                else
                {
                    var budget = ExpandBudget.ForUi();
                    occurrences = IcsExpander.Expand(cal.Events, from, to, budget);
                    ReportLimits(st, cal, budget);
                }
                foreach (var o in occurrences)
                {
                    string title = o.Event.Summary.Length > 0 ? o.Event.Summary.Replace('\n', ' ') : "(제목 없음)";
                    list.Add(new CalendarOccurrence(st.Feed.Id, title, o.Event.Location?.Replace('\n', ' '), o.Start, o.End, o.AllDay, st.Feed.Color));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"캘린더 '{MaskName(st.Feed.Name)}' 일정 펼치기 실패: {ex.GetType().Name}");
            }
        }
        list.Sort((a, b) =>
        {
            int c = b.AllDay.CompareTo(a.AllDay);
            if (c == 0) c = a.Start.CompareTo(b.Start);
            if (c == 0) c = a.End.CompareTo(b.End);
            return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
        });
        if (_occurrenceCache.Count > 24) _occurrenceCache.Clear();
        _occurrenceCache[key] = list;
        return list;
    }

    /// <summary>IcsExpander 의 범위 판정과 같음 (길이 0 이면 시작이 범위 안).</summary>
    private static bool Overlaps(IcsOccurrence o, DateTime from, DateTime to)
        => o.End > o.Start ? o.Start < to && o.End > from : o.Start >= from && o.Start < to;

    /// <summary>미리 펼칠 표시 범위: 이번 달 앞뒤 2개월 ([두 달 전 1일, 석 달 뒤 1일)).</summary>
    private static (DateTime From, DateTime To) DisplayWindow(DateTime now)
    {
        var month = new DateTime(now.Year, now.Month, 1);
        return (month.AddMonths(-2), month.AddMonths(3));
    }

    /// <summary>
    /// 받은(또는 캐시에서 읽은) 일정을 백그라운드에서 표시 범위만큼 미리 펼침 → 끝나면 UI 스레드에서 붙이고 Changed.
    /// 큰 피드도 UI 스레드는 거르기만 하게. 그 사이 다시 받았거나 삭제됐으면 버림.
    /// </summary>
    private void SchedulePrecompute(FeedState st)
    {
        if (st.Parsed is not { } cal || _disposed) return;
        var (from, to) = DisplayWindow(DateTime.Now);
        Task.Run(() =>
        {
            var budget = ExpandBudget.ForBackground();
            var items = IcsExpander.Expand(cal.Events, from, to, budget);
            return (Pre: new Precomputed(cal, from, to, items), Budget: budget);
        }).ContinueWith(t =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                Log.Warn($"캘린더 일정 미리 펼치기 실패: {t.Exception?.InnerException?.GetType().Name}");
                return;
            }
            var (pre, budget) = t.Result;
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed || Find(st.Feed.Id) != st || !ReferenceEquals(st.Parsed, cal)) return;
                st.Pre = pre;
                ReportLimits(st, cal, budget);
                RaiseChanged();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>펼치기 상한·건너뛴 일정을 일정 데이터(받은 ICS)마다 한 번만 로그 (UI 스레드).</summary>
    private static void ReportLimits(FeedState st, IcsCalendar cal, ExpandBudget budget)
    {
        if (!budget.Exhausted && budget.FailedEvents == 0) return;
        if (ReferenceEquals(st.LimitLoggedFor, cal)) return;
        st.LimitLoggedFor = cal;
        var parts = new List<string>();
        if (budget.Exhausted) parts.Add($"상한({budget.Reason})에 걸려 나머지 회차 생략");
        if (budget.FailedEvents > 0) parts.Add($"날짜 계산이 넘친 일정 {budget.FailedEvents}개 건너뜀");
        Log.Warn($"캘린더 '{MaskName(st.Feed.Name)}' 일정 {cal.Events.Count}개 펼치기: {string.Join(", ", parts)}");
    }

    // ───────────────────────── 가져오기 ─────────────────────────

    private async Task RefreshAsync(FeedState st)
    {
        if (st.Busy || _disposed) return;
        if (st.Feed.Url.Length == 0)
        {
            st.Error = "주소를 읽을 수 없어요 — 삭제 후 다시 추가해 주세요.";
            RaiseChanged();
            return;
        }
        if (!Uri.TryCreate(st.Feed.Url, UriKind.Absolute, out var feedUri) || !IsSecure(feedUri))
        {
            // 예전 버전에서 추가한 http:// 구독 — 가져오지 않음 (캐시된 일정은 그대로 보임)
            if (st.Error == null) Log.Warn($"캘린더 '{MaskName(st.Feed.Name)}' http 주소 → 가져오지 않음 ({HostOf(st.Feed.Url)})");
            st.Error = "https 주소만 지원해요 — 삭제 후 https 주소로 다시 추가해 주세요.";
            st.LastAttempt = DateTime.Now;
            RaiseChanged();
            return;
        }
        st.Busy = true;
        st.LastAttempt = DateTime.Now;
        RaiseChanged();
        FetchResult result;
        try
        {
            string url = st.Feed.Url;
            string? etag = st.Parsed != null ? st.ETag : null;
            string? lastModified = st.Parsed != null ? st.LastModified : null;
            result = await Task.Run(() => FetchAsync(url, etag, lastModified));
        }
        catch (Exception ex)
        {
            result = FetchResult.Fail(FetchKind.Transient, "가져오기 실패", ex.GetType().Name);
        }
        st.Busy = false;
        if (_disposed) return;
        if (Find(st.Feed.Id) != st)
        {
            ForgetCache(st.Feed.Id); // 가져오는 사이 삭제됨
            return;
        }
        switch (result.Kind)
        {
            case FetchKind.Ok:
                ApplyFetched(st, result);
                break;
            case FetchKind.NotModified:
                st.LastSync = DateTime.Now;
                st.Error = null;
                WriteMeta(st);
                break;
            default:
                st.Error = result.UserMessage;
                Log.Warn($"캘린더 '{MaskName(st.Feed.Name)}' 가져오기 실패 ({HostOf(st.Feed.Url)}): {result.LogDetail}");
                break;
        }
        RaiseChanged();
    }

    private void ApplyFetched(FeedState st, FetchResult r)
    {
        st.Parsed = r.Parsed;
        st.Pre = null;
        st.ETag = r.ETag;
        st.LastModified = r.LastModified;
        st.LastSync = DateTime.Now;
        st.Error = null;
        SchedulePrecompute(st);
        // 캐시는 백그라운드에서 (암호화 + 쓰기)
        string id = st.Feed.Id;
        string text = r.Text!;
        var meta = new CacheMeta { ETag = st.ETag, LastModified = st.LastModified, LastSync = st.LastSync };
        Task.Run(() =>
        {
            try
            {
                byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy, DataProtectionScope.CurrentUser);
                // 쓰기와 삭제(Remove)를 한 잠금으로 — 삭제된 구독의 캐시를 늦게 써서 고아 파일이 남지 않게
                lock (_cacheIoGate)
                {
                    if (_removedIds.Contains(id)) return;
                    Directory.CreateDirectory(_cacheDir);
                    string path = CachePath(id);
                    string tmp = path + "." + Environment.ProcessId + ".tmp";
                    File.WriteAllBytes(tmp, data);
                    File.Move(tmp, path, overwrite: true);
                    AtomicFile.WriteAllText(MetaPath(id), JsonSerializer.Serialize(meta, JsonOptions));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"캘린더 캐시 저장 실패: {ex.GetType().Name}");
            }
        });
    }

    private void WriteMeta(FeedState st)
    {
        var meta = new CacheMeta { ETag = st.ETag, LastModified = st.LastModified, LastSync = st.LastSync };
        string id = st.Feed.Id;
        string path = MetaPath(id);
        Task.Run(() =>
        {
            try
            {
                lock (_cacheIoGate)
                {
                    if (_removedIds.Contains(id)) return;
                    AtomicFile.WriteAllText(path, JsonSerializer.Serialize(meta, JsonOptions));
                }
            }
            catch (Exception ex) { Log.Warn($"캘린더 캐시 정보 저장 실패: {ex.GetType().Name}"); }
        });
    }

    private enum FetchKind { Ok, NotModified, Transient, Invalid }

    private sealed record FetchResult(FetchKind Kind, string? Text, IcsCalendar? Parsed, string? ETag, string? LastModified, string? UserMessage, string LogDetail)
    {
        public static FetchResult Fail(FetchKind kind, string user, string log) => new(kind, null, null, null, null, user, log);
    }

    /// <summary>
    /// 백그라운드 스레드에서: 내려받기 + 파싱. 예외 없이 결과로. 로그용 문구(LogDetail)에도 주소는 넣지 않는다.
    /// Invalid = 주소가 틀렸거나 ICS 가 아님(추가 거부), Transient = 네트워크·서버 문제(나중에 다시).
    /// </summary>
    private static async Task<FetchResult> FetchAsync(string url, string? etag, string? lastModified)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("text/calendar, */*;q=0.5");
            if (etag != null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            if (lastModified != null) req.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (resp.StatusCode == HttpStatusCode.NotModified)
                return new FetchResult(FetchKind.NotModified, null, null, etag, lastModified, null, "304");
            if (code is 400 or 401 or 403 or 404 or 410)
                return FetchResult.Fail(FetchKind.Invalid, "가져오기 실패 — 주소를 확인하세요", $"HTTP {code}");
            if (!resp.IsSuccessStatusCode)
                return FetchResult.Fail(FetchKind.Transient, $"가져오기 실패 — 서버 오류 ({code})", $"HTTP {code}");
            if (resp.Content.Headers.ContentLength is long len && len > MaxBytes)
                return FetchResult.Fail(FetchKind.Invalid, "캘린더가 너무 커요 (20MB 초과)", $"Content-Length {len}");

            using var ms = new MemoryStream();
            await using (var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + n > MaxBytes)
                        return FetchResult.Fail(FetchKind.Invalid, "캘린더가 너무 커요 (20MB 초과)", "body > 20MB");
                    ms.Write(buffer, 0, n);
                }
            }
            ms.Position = 0;
            Encoding enc = Encoding.UTF8;
            try
            {
                string? charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"');
                if (!string.IsNullOrEmpty(charset)) enc = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException) { }
            string text;
            using (var reader = new StreamReader(ms, enc, detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();
            if (text.IndexOf("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase) < 0)
                return FetchResult.Fail(FetchKind.Invalid, "캘린더(ICS) 주소가 아니에요 — iCal 주소를 복사했는지 확인하세요", "본문이 ICS 아님");

            var parsed = IcsParser.Parse(text);
            string? newEtag = resp.Headers.ETag?.ToString();
            string? newLastModified = resp.Content.Headers.LastModified?.ToString("R");
            return new FetchResult(FetchKind.Ok, text, parsed, newEtag, newLastModified, null, "ok");
        }
        catch (TaskCanceledException)
        {
            return FetchResult.Fail(FetchKind.Transient, "가져오기 실패 — 시간 초과", "시간 초과 (15초)");
        }
        catch (HttpRequestException ex)
        {
            // 메시지에는 보통 "호스트:포트" 만 들어가지만 혹시 몰라 주소 조각을 지움
            return FetchResult.Fail(FetchKind.Transient, "가져오기 실패 — 네트워크를 확인하세요", $"{ex.HttpRequestError}: {Scrub(ex.Message, url)}");
        }
        catch (Exception ex)
        {
            return FetchResult.Fail(FetchKind.Transient, "가져오기 실패", ex.GetType().Name);
        }
    }

    /// <summary>예외 문구에서 주소·경로·쿼리를 지움 (호스트는 남김).</summary>
    private static string Scrub(string message, string url)
    {
        if (string.IsNullOrEmpty(message)) return "";
        string m = message.Replace(url, "<주소>", StringComparison.OrdinalIgnoreCase);
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.PathAndQuery.Length > 1)
            m = m.Replace(u.PathAndQuery, "/…", StringComparison.OrdinalIgnoreCase);
        return m;
    }

    private static HttpClient CreateHttpClient()
    {
        // charset=euc-kr 같은 코드 페이지 응답도 읽을 수 있게 (프레임워크 포함, NuGet 아님)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, "1.0"));
        return client;
    }

    // ───────────────────────── 저장 (calendars.json) ─────────────────────────

    private sealed class StoreDto
    {
        public int Version { get; set; } = 1;
        public List<FeedDto> Feeds { get; set; } = new();
    }

    private sealed class FeedDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>"dpapi:BASE64" (암호화). 직접 편집해 평문을 넣어도 읽고, 다음 저장 때 암호화.</summary>
        public string Url { get; set; } = "";
        public string Color { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    private sealed class CacheMeta
    {
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public DateTime? LastSync { get; set; }
    }

    private void LoadStore()
    {
        if (!File.Exists(_storePath)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(_storePath), JsonOptions) ?? new StoreDto();
            bool resave = false;
            foreach (var f in dto.Feeds ?? new List<FeedDto>())
            {
                if (string.IsNullOrWhiteSpace(f.Id) || _feeds.Any(x => x.Feed.Id == f.Id)) continue;
                string? url = Unprotect(f.Url ?? "");
                if (url == null) Log.Warn($"캘린더 '{MaskName(f.Name)}' 주소를 복호화하지 못함 (다른 사용자·PC 에서 옮긴 파일?)");
                else if (!(f.Url ?? "").StartsWith(ProtectedPrefix, StringComparison.Ordinal) && url.Length > 0) resave = true; // 평문 → 암호화
                if (url != null && TryNormalizeUrl(url, out var u)) url = u.AbsoluteUri;
                var feed = new CalendarFeed
                {
                    Id = f.Id,
                    Name = string.IsNullOrWhiteSpace(f.Name) ? "캘린더" : f.Name,
                    Url = url ?? "",
                    Color = NormalizeColor(f.Color) ?? CalendarFeed.Palette[0],
                    Enabled = f.Enabled,
                };
                var st = new FeedState(feed);
                if (url == null) st.Error = "주소를 읽을 수 없어요 — 삭제 후 다시 추가해 주세요.";
                _feeds.Add(st);
            }
            if (resave) SaveStore();
        }
        catch (Exception ex)
        {
            // 손상: 덮어쓰기 전에 백업 (주소가 들어 있어 내용은 로그에 남기지 않음)
            Log.Error($"calendars.json 읽기 실패 → 구독 없이 시작 (원본 백업): {ex.GetType().Name}");
            try { File.Copy(_storePath, _storePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false); }
            catch (Exception bex) { Log.Warn($"calendars.json 백업 실패: {bex.GetType().Name}"); }
        }
    }

    private void SaveStore()
    {
        try
        {
            var dto = new StoreDto
            {
                Feeds = _feeds.Select(f => new FeedDto
                {
                    Id = f.Feed.Id,
                    Name = f.Feed.Name,
                    Url = f.Feed.Url.Length > 0 ? Protect(f.Feed.Url) : "",
                    Color = f.Feed.Color,
                    Enabled = f.Feed.Enabled,
                }).ToList(),
            };
            AtomicFile.WriteAllText(_storePath, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"calendars.json 저장 실패: {ex.GetType().Name}");
        }
    }

    private static string Protect(string plain)
        => ProtectedPrefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>"dpapi:..." 면 복호화 (실패 null), 아니면 평문 그대로.</summary>
    private static string? Unprotect(string stored)
    {
        if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return stored;
        try
        {
            byte[] data = Convert.FromBase64String(stored[ProtectedPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    // ───────────────────────── 캐시 ─────────────────────────

    private string CachePath(string id) => Path.Combine(_cacheDir, id + ".ics.dat");
    private string MetaPath(string id) => Path.Combine(_cacheDir, id + ".json");

    private sealed record LoadedCache(string Id, IcsCalendar? Parsed, string? ETag, string? LastModified, DateTime? LastSync);

    private LoadedCache? LoadCache(string id)
    {
        try
        {
            CacheMeta? meta = null;
            if (File.Exists(MetaPath(id)))
            {
                try { meta = JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(MetaPath(id)), JsonOptions); }
                catch (JsonException) { }
            }
            IcsCalendar? parsed = null;
            if (File.Exists(CachePath(id)))
            {
                try
                {
                    byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(CachePath(id)), Entropy, DataProtectionScope.CurrentUser);
                    parsed = IcsParser.Parse(Encoding.UTF8.GetString(data));
                }
                catch (CryptographicException) { meta = null; } // 다른 사용자 캐시 → 새로 받음
            }
            // 일정 캐시가 없으면 ETag 도 쓰지 않음 (304 를 받으면 보여 줄 게 없음)
            if (parsed == null) return new LoadedCache(id, null, null, null, null);
            return new LoadedCache(id, parsed, meta?.ETag, meta?.LastModified, meta?.LastSync);
        }
        catch (Exception ex)
        {
            Log.Warn($"캘린더 캐시 읽기 실패: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>구독 삭제: 이후의 캐시 쓰기를 막고(제거된 Id 기록) 파일 삭제. 쓰기와 같은 잠금 안에서.</summary>
    private void ForgetCache(string id)
    {
        lock (_cacheIoGate)
        {
            _removedIds.Add(id);
            DeleteCache(id);
        }
    }

    private void DeleteCache(string id)
    {
        foreach (var path in new[] { CachePath(id), MetaPath(id) })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ───────────────────────── 기타 ─────────────────────────

    private FeedState? Find(string id) => _feeds.FirstOrDefault(f => f.Feed.Id == id);

    private void RaiseChanged()
    {
        _occurrenceCache.Clear();
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("캘린더 변경 알림 처리 실패", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }

    private sealed class FeedState
    {
        public FeedState(CalendarFeed feed) => Feed = feed;

        public CalendarFeed Feed { get; }
        public IcsCalendar? Parsed;
        public string? ETag;
        public string? LastModified;
        public DateTime? LastSync;
        /// <summary>마지막으로 가져오기를 시도한 시각 (주기 판단용, 실패 포함).</summary>
        public DateTime LastAttempt;
        public string? Error;
        public bool Busy;
        /// <summary>표시 범위를 미리 펼친 결과 (Source 가 지금 Parsed 와 같을 때만 씀).</summary>
        public Precomputed? Pre;
        /// <summary>펼치기 상한 로그를 이미 남긴 일정 데이터 (같은 데이터면 다시 안 남김).</summary>
        public IcsCalendar? LimitLoggedFor;
    }

    private sealed record Precomputed(IcsCalendar Source, DateTime From, DateTime To, List<IcsOccurrence> Items);
}
