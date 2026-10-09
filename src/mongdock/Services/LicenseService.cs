using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Windows.Services.Store;

namespace Mongdock.Services;

public enum LicenseState
{
    /// <summary>구매함 — 또는 일반판(깃허브·설치 프로그램), 확인 실패(잠그지 않음).</summary>
    Full,
    /// <summary>스토어 체험 중.</summary>
    Trial,
    /// <summary>체험이 끝났고 구매하지 않음 → 독·상단바 멈춤 (Views/LicenseUi).</summary>
    Expired,
}

/// <summary>지금 라이선스. DaysLeft = 체험 남은 날(올림, 0 이상). Source = 로그용 ("store"·"cache"·"fake"·"general").</summary>
public sealed record LicenseInfo(LicenseState State, int DaysLeft, DateTimeOffset? Expiration, string Source)
{
    public bool IsTrial => State == LicenseState.Trial;
}

/// <summary>
/// 스토어 체험판·구매 (#5). 스토어판(패키지)만 Windows.Services.Store 로 확인하고, 일반판은 늘 정식(테스터·기존 사용자).
/// - 확인: 시작 5초 뒤, OfflineLicensesChanged(구매·환불 직후 스토어가 알림), 6시간마다, 체험 만료 시각 1분 뒤.
///   남은 날은 1시간마다 저장된 만료 시각으로 다시 셈 (스토어 호출 없이).
/// - 확인 실패(오프라인·스토어 오류): 마지막으로 확인한 상태(license.json)를 씀. 단 그 캐시로는 절대 '만료' 로 잠그지 않음 —
///   캐시가 만료였거나 체험 만료 시각이 지났는데 확인을 못 하면 정식 취급, 로그만 (만료 오판으로 잠그는 것보다 낫다).
/// - 시험: MONGDOCK_FAKE_LICENSE=trial:N | expired | full (패키지 밖에서도 흐름 시험. 구매하면 바로 정식으로 바뀜 — 스토어 창 없음).
/// - 스토어 창(구매)은 창 핸들이 있어야 뜸 → IInitializeWithWindow (WinRT.Interop.InitializeWithWindow).
/// </summary>
public sealed class LicenseService : IDisposable
{
    /// <summary>파트너 센터의 스토어 ID (https://apps.microsoft.com/detail/9NQ3NKW93F3M). 구매 창을 못 띄울 때 스토어 페이지 링크에 씀.</summary>
    public const string StoreId = "9NQ3NKW93F3M";

    private static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(6);
    private readonly string _cachePath = Path.Combine(AppInfo.DataDirectory, "license.json");
    private readonly Func<IntPtr> _ownerWindow;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _recheck;
    private readonly DispatcherTimer _hourly;
    private readonly DispatcherTimer _atExpiry;
    private readonly (LicenseState State, DateTimeOffset? Expiration)? _fake;
    private StoreContext? _store;
    private bool _checking;

    public LicenseService(Func<IntPtr> ownerWindow)
    {
        _ownerWindow = ownerWindow;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _fake = ParseFake(Environment.GetEnvironmentVariable("MONGDOCK_FAKE_LICENSE"));
        Current = _fake is { } f ? Make(f.State, f.Expiration, "fake")
            : !AppInfo.IsPackaged ? new LicenseInfo(LicenseState.Full, 0, null, "general")
            : FromCache() ?? new LicenseInfo(LicenseState.Full, 0, null, "default");
        _recheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _recheck.Tick += (_, _) => { _recheck.Interval = RecheckInterval; _ = CheckAsync("주기"); };
        _hourly = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _hourly.Tick += (_, _) => Recount();
        _atExpiry = new DispatcherTimer();
        _atExpiry.Tick += (_, _) =>
        {
            _atExpiry.Stop();
            if (_fake is not null) Set(Make(LicenseState.Expired, null, "fake")); // 시험: 시각이 지나면 만료
            else _ = CheckAsync("체험 만료 시각");
        };
    }

    /// <summary>지금 상태 (UI 스레드에서 읽음).</summary>
    public LicenseInfo Current { get; private set; }

    /// <summary>상태·남은 날이 바뀜 (UI 스레드).</summary>
    public event EventHandler? Changed;

    /// <summary>스토어로 확인하는 판인지 (스토어판 또는 시험 스위치). 일반판이면 체험·구매 화면을 아예 안 보임.</summary>
    public bool Managed => _fake is not null || AppInfo.IsPackaged;

    public void Start()
    {
        Log.Info($"라이선스: {Describe(Current)}");
        if (_fake is not null || !AppInfo.IsPackaged) { _hourly.Start(); ScheduleExpiry(); return; }
        try
        {
            _store = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(_store, _ownerWindow());
            _store.OfflineLicensesChanged += OnOfflineLicensesChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"스토어 연결 실패 (마지막 상태 유지): {ex.GetType().Name} {ex.Message}");
        }
        _recheck.Start();
        _hourly.Start();
        ScheduleExpiry();
    }

    public void Dispose()
    {
        _recheck.Stop();
        _hourly.Stop();
        _atExpiry.Stop();
        if (_store is not null)
        {
            try { _store.OfflineLicensesChanged -= OnOfflineLicensesChanged; }
            catch { /* 종료 중 */ }
        }
    }

    private void OnOfflineLicensesChanged(StoreContext sender, object args) =>
        _dispatcher.BeginInvoke(() => _ = CheckAsync("스토어 라이선스 바뀜"));

    // ───────────────────────── 확인 ─────────────────────────

    /// <summary>스토어에 지금 라이선스를 물음 (UI 스레드). 실패하면 캐시 규칙대로.</summary>
    public async Task CheckAsync(string why)
    {
        if (_fake is not null || !AppInfo.IsPackaged || _checking) return;
        _checking = true;
        try
        {
            _store ??= StoreContext.GetDefault();
            var license = await _store.GetAppLicenseAsync();
            if (license is null) throw new InvalidOperationException("라이선스 없음");
            // IsActive=false: 체험이 끝났거나 소유하지 않음. IsTrial=true 이고 활성: 체험 중. 그 밖: 구매함
            LicenseState state = !license.IsActive ? LicenseState.Expired : license.IsTrial ? LicenseState.Trial : LicenseState.Full;
            DateTimeOffset? expiration = state == LicenseState.Trial ? license.ExpirationDate : null;
            Log.Info($"라이선스 확인({why}): 활성 {license.IsActive}, 체험 {license.IsTrial}, 이 사용자 체험 {license.IsTrialOwnedByThisUser}, 만료 {license.ExpirationDate:yyyy-MM-dd HH:mm}");
            SaveCache(state, expiration);
            Set(Make(state, expiration, "store"));
        }
        catch (Exception ex)
        {
            var fallback = FromCache() ?? new LicenseInfo(LicenseState.Full, 0, null, "default");
            Log.Warn($"라이선스 확인 실패({why}) → {Describe(fallback)}: {ex.GetType().Name} {ex.Message}");
            Set(fallback);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>스토어 구매 창. 구매·이미 구매면 다시 확인. 시험 스위치면 바로 정식.</summary>
    public async Task<bool> RequestPurchaseAsync()
    {
        if (_fake is not null)
        {
            Log.Info("라이선스(시험): 구매함 → 정식");
            Set(Make(LicenseState.Full, null, "fake"));
            return true;
        }
        if (!AppInfo.IsPackaged) return false;
        try
        {
            _store ??= StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(_store, _ownerWindow()); // 지금 앞에 있는 몽독 창에 붙여 띄움
            var product = await _store.GetStoreProductForCurrentAppAsync();
            if (product?.Product is null)
            {
                Log.Warn($"구매: 스토어 상품을 못 찾음 ({product?.ExtendedError?.Message})");
                OpenStorePage();
                return false;
            }
            var result = await product.Product.RequestPurchaseAsync();
            Log.Info($"구매 결과: {result.Status}");
            bool ok = result.Status is StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased;
            if (ok) await CheckAsync("구매");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"구매 창 실패: {ex.GetType().Name} {ex.Message}");
            OpenStorePage();
            return false;
        }
    }

    /// <summary>스토어 앱의 몽독 페이지 (StoreId 가 있을 때만).</summary>
    private static void OpenStorePage()
    {
        if (StoreId.Length == 0) return;
        try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-windows-store://pdp/?productid=" + StoreId) { UseShellExecute = true })) { } }
        catch (Exception ex) { Log.Warn($"스토어 페이지 열기 실패: {ex.Message}"); }
    }

    // ───────────────────────── 상태 ─────────────────────────

    private void Set(LicenseInfo next)
    {
        var prev = Current;
        Current = next;
        ScheduleExpiry();
        if (prev.State == next.State && prev.DaysLeft == next.DaysLeft) return;
        Log.Info($"라이선스 바뀜: {Describe(prev)} → {Describe(next)}");
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("라이선스 Changed 처리 실패", ex); }
    }

    /// <summary>1시간마다: 체험 남은 날을 만료 시각으로 다시 셈 (날이 바뀌면 Changed).</summary>
    private void Recount()
    {
        if (Current.State != LicenseState.Trial) return;
        Set(Make(LicenseState.Trial, Current.Expiration, Current.Source));
    }

    /// <summary>체험이면 만료 시각 1분 뒤에 다시 확인 (24시간 안일 때만 타이머 — 더 멀면 1시간 주기가 다가가면서 걸어 줌).</summary>
    private void ScheduleExpiry()
    {
        _atExpiry.Stop();
        if (Current is not { State: LicenseState.Trial, Expiration: { } exp }) return;
        var wait = exp - DateTimeOffset.Now + TimeSpan.FromMinutes(1);
        if (wait > TimeSpan.FromHours(24)) return;
        if (_fake is not null)
        {
            // 시험: 만료 시각이 지나면 만료로
            if (wait <= TimeSpan.Zero) { Set(Make(LicenseState.Expired, null, "fake")); return; }
        }
        _atExpiry.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(5);
        _atExpiry.Start();
    }

    private static LicenseInfo Make(LicenseState state, DateTimeOffset? expiration, string source)
    {
        int days = 0;
        if (state == LicenseState.Trial && expiration is { } exp)
            days = Math.Max(0, (int)Math.Ceiling((exp - DateTimeOffset.Now).TotalDays));
        return new LicenseInfo(state, days, expiration, source);
    }

    private static string Describe(LicenseInfo i) => i.State switch
    {
        LicenseState.Trial => $"체험 {i.DaysLeft}일 남음 ({i.Source})",
        LicenseState.Expired => $"체험 끝남 ({i.Source})",
        _ => $"정식 ({i.Source})",
    };

    /// <summary>MONGDOCK_FAKE_LICENSE: "trial:3" · "expired" · "full". 틀린 값이면 null(스위치 없음).</summary>
    private static (LicenseState, DateTimeOffset?)? ParseFake(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim().ToLowerInvariant();
        if (v == "full") return (LicenseState.Full, null);
        if (v == "expired") return (LicenseState.Expired, null);
        if (v.StartsWith("trial", StringComparison.Ordinal))
        {
            string n = v.Length > 5 ? v[5..].TrimStart(':', '=') : "14";
            if (double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 0)
                return (LicenseState.Trial, DateTimeOffset.Now.AddDays(d).AddMinutes(-1)); // "3" → 3일 남음으로 보이게
        }
        Log.Warn($"MONGDOCK_FAKE_LICENSE 값 '{value}' 를 모름 (trial:N / expired / full)");
        return null;
    }

    // ───────────────────────── 캐시 (license.json) ─────────────────────────

    private sealed class Cache
    {
        public LicenseState State { get; set; }
        public DateTimeOffset? Expiration { get; set; }
        public DateTimeOffset CheckedAt { get; set; }
    }

    private static readonly JsonSerializerOptions CacheJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>캐시로 정할 수 있는 상태 — 만료로 잠그지는 않음(정식 취급).</summary>
    private LicenseInfo? FromCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var c = JsonSerializer.Deserialize<Cache>(File.ReadAllText(_cachePath), CacheJson);
            if (c is null) return null;
            if (c.State == LicenseState.Trial && c.Expiration is { } exp && exp > DateTimeOffset.Now)
                return Make(LicenseState.Trial, exp, "cache");
            return new LicenseInfo(LicenseState.Full, 0, null, "cache"); // 정식 · 또는 확인 못 한 만료 → 잠그지 않음
        }
        catch (Exception ex)
        {
            Log.Warn($"license.json 읽기 실패: {ex.Message}");
            return null;
        }
    }

    private void SaveCache(LicenseState state, DateTimeOffset? expiration)
    {
        try
        {
            AtomicFile.WriteAllText(_cachePath, JsonSerializer.Serialize(new Cache { State = state, Expiration = expiration, CheckedAt = DateTimeOffset.Now }, CacheJson));
        }
        catch (Exception ex)
        {
            Log.Warn($"license.json 저장 실패: {ex.Message}");
        }
    }
}
