using System.Runtime.InteropServices;
using System.Windows.Threading;
using Windows.Services.Store;

namespace Mongdock.Services;

public enum StoreUpdateState
{
    /// <summary>아직 확인 안 함.</summary>
    Unknown,
    Checking,
    UpToDate,
    Available,
    /// <summary>스토어에 묻지 못함 (오프라인·스토어 오류).</summary>
    Failed,
    /// <summary>[업데이트]를 눌러 스토어가 내려받는·설치하는 중.</summary>
    Installing,
}

/// <summary>
/// 스토어판 업데이트 (스토어판에서만 만듦 — 일반판은 UpdateService 가 GitHub 릴리스로).
/// - 확인: 시작 1분 뒤, 그 뒤 하루 한 번(일시 정지·체험 끝 잠금 중엔 건너뜀), 설정 → 정보 [스토어에서 업데이트 확인].
///   StoreContext.GetAppAndOptionalStorePackageUpdatesAsync → 새 버전이 있으면 Pending (상단바 점·메뉴 줄·버전마다 배너 한 번 = GitHub판과 같음, Views/UpdateUi).
/// - [업데이트]: RequestDownloadAndInstallStorePackageUpdatesAsync (스토어 확인 창 + 진행). 설치 단계(Deploying)에 들어가면
///   일시 정지 = 작업 표시줄·자동 숨김을 먼저 원래대로 (윈도우가 설치하며 몽독을 끄면 작업 표시줄이 숨은 채 남지 않게).
///   취소·실패면 일시 정지를 풂. 설치 뒤 다시 켜지기 = RegisterApplicationRestart (업데이트로 꺼졌을 때만 — 오류·멈춤·재부팅 땐 다시 켜지 않음).
/// - 스토어가 앱 밖에서 자동으로 업데이트해도 RegisterApplicationRestart 로 다시 켜지고, 다음 실행이 작업 표시줄 기록대로 다시 숨김.
/// - 시험: DEBUG 빌드에서만 MONGDOCK_FAKE_STOREUPDATE=none | available[:0.7.1] | error (패키지 밖에서도 정보 화면·배너 흐름).
/// </summary>
public sealed class StoreUpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly Func<IntPtr> _ownerWindow;
    private readonly Func<bool> _isPaused;
    private readonly DispatcherTimer _timer;
    private readonly string? _fake;
    private StoreContext? _store;
    private IReadOnlyList<StorePackageUpdate>? _updates;
    private bool _pausedForInstall;

    public static StoreUpdateService? Instance { get; private set; }

    /// <summary>스토어판이거나 (DEBUG) 시험 스위치가 있으면 만들어야 함.</summary>
    public static bool Wanted => AppInfo.IsPackaged || FakeValue() is not null;

    public StoreUpdateService(Func<IntPtr> ownerWindow, Func<bool> isPaused)
    {
        _ownerWindow = ownerWindow;
        _isPaused = isPaused;
        _fake = FakeValue();
        _timer = new DispatcherTimer { Interval = FirstCheck };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (!_isPaused()) _ = CheckAsync(manual: false);
        };
        Instance = this;
    }

    public StoreUpdateState State { get; private set; }

    /// <summary>새 버전 (State == Available·Installing 일 때). 버전을 모르면 null 이어도 Available 일 수 있음.</summary>
    public Version? PendingVersion { get; private set; }

    public bool HasPending => State is StoreUpdateState.Available or StoreUpdateState.Installing;

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>상태가 바뀜 (UI 스레드).</summary>
    public event EventHandler? Changed;

    /// <summary>새 버전을 처음 찾음 (배너용, UI 스레드).</summary>
    public event EventHandler<Version?>? Available;

    public void Start()
    {
        RegisterRestart();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        if (Instance == this) Instance = null;
    }

    // ───────────────────────── 확인 ─────────────────────────

    public async Task CheckAsync(bool manual)
    {
        if (State is StoreUpdateState.Checking or StoreUpdateState.Installing) return;
        bool hadPending = HasPending;
        Set(StoreUpdateState.Checking);
        StoreUpdateState next;
        Version? version = null;
        try
        {
            if (_fake is not null)
            {
                await Task.Delay(600);
                (next, version) = FakeResult(_fake);
            }
            else
            {
                var store = Context();
                _updates = await store.GetAppAndOptionalStorePackageUpdatesAsync();
                version = MainVersion(_updates);
                next = _updates.Count > 0 ? StoreUpdateState.Available : StoreUpdateState.UpToDate;
            }
            LastChecked = DateTimeOffset.Now;
            Log.Info($"스토어 업데이트 확인({(manual ? "버튼" : "주기")}): {(next == StoreUpdateState.Available ? $"새 버전 {version?.ToString(3) ?? "?"}" : next == StoreUpdateState.UpToDate ? "최신" : "실패")}");
        }
        catch (Exception ex)
        {
            Log.Warn($"스토어 업데이트 확인 실패: {ex.GetType().Name} {ex.Message}");
            next = StoreUpdateState.Failed;
        }
        PendingVersion = next == StoreUpdateState.Available ? version : null;
        Set(next);
        if (next == StoreUpdateState.Available && !hadPending)
        {
            try { Available?.Invoke(this, version); }
            catch (Exception ex) { Log.Error("스토어 업데이트 Available 처리 실패", ex); }
        }
    }

    /// <summary>[업데이트]: 스토어 창으로 내려받기·설치. 설치 단계에서 작업 표시줄을 먼저 원래대로.</summary>
    public async Task InstallAsync()
    {
        if (State != StoreUpdateState.Available) return;
        Set(StoreUpdateState.Installing);
        try
        {
            if (_fake is not null)
            {
                Log.Info("스토어 업데이트(시험): 설치하는 척 → 최신");
                await Task.Delay(1200);
                PendingVersion = null;
                Set(StoreUpdateState.UpToDate);
                return;
            }
            var store = Context();
            if (_updates is not { Count: > 0 } updates)
                updates = await store.GetAppAndOptionalStorePackageUpdatesAsync();
            var op = store.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);
            op.Progress = (_, p) =>
            {
                if (p.PackageUpdateState == StorePackageUpdateState.Deploying)
                    Application().BeginInvoke(PauseForInstall);
            };
            var result = await op;
            Log.Info($"스토어 업데이트 결과: {result.OverallState}");
            if (result.OverallState == StorePackageUpdateState.Completed)
            {
                // 보통 여기 오기 전에 윈도우가 몽독을 끄고 새 버전을 켬. 그대로 살아 있으면 다시 확인
                ResumeAfterInstall();
                PendingVersion = null;
                _updates = null;
                Set(StoreUpdateState.UpToDate);
            }
            else
            {
                ResumeAfterInstall();
                Set(StoreUpdateState.Available);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"스토어 업데이트 실패: {ex.GetType().Name} {ex.Message}");
            ResumeAfterInstall();
            Set(StoreUpdateState.Available);
        }
    }

    /// <summary>스토어 앱의 몽독 페이지 (확인을 못 했을 때 직접 업데이트하는 길).</summary>
    public static void OpenStorePage()
    {
        try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-windows-store://pdp/?productid=" + LicenseService.StoreId) { UseShellExecute = true })) { } }
        catch (Exception ex) { Log.Warn($"스토어 페이지 열기 실패: {ex.Message}"); }
    }

    private void PauseForInstall()
    {
        if (_pausedForInstall || ViewModels.AppState.Paused) return;
        Log.Info("스토어 업데이트 설치 시작 → 작업 표시줄 원래대로 (일시 정지)");
        _pausedForInstall = true;
        ViewModels.AppState.Paused = true;
    }

    private void ResumeAfterInstall()
    {
        if (!_pausedForInstall) return;
        _pausedForInstall = false;
        ViewModels.AppState.Paused = false;
    }

    private StoreContext Context()
    {
        if (_store is null)
        {
            _store = StoreContext.GetDefault();
            WinRT.Interop.InitializeWithWindow.Initialize(_store, _ownerWindow());
        }
        return _store;
    }

    private static Dispatcher Application() => System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

    /// <summary>몽독 본 패키지의 새 버전 (선택 패키지는 빼고, 못 찾으면 첫 항목).</summary>
    private static Version? MainVersion(IReadOnlyList<StorePackageUpdate> updates)
    {
        try
        {
            string? own = null;
            try { own = Windows.ApplicationModel.Package.Current.Id.Name; } catch { }
            var main = updates.FirstOrDefault(u => u.Package.Id.Name == own) ?? updates.FirstOrDefault();
            if (main is null) return null;
            var v = main.Package.Id.Version;
            return new Version(v.Major, v.Minor, v.Build);
        }
        catch
        {
            return null;
        }
    }

    private void Set(StoreUpdateState state)
    {
        if (State == state) return;
        State = state;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("스토어 업데이트 Changed 처리 실패", ex); }
    }

    /// <summary>report-test 그림용: 상태를 바로 넣음 (리플렉션으로만 닿음).</summary>
    internal void SetForTest(StoreUpdateState state, Version? version, DateTimeOffset? lastChecked)
    {
        PendingVersion = version;
        LastChecked = lastChecked;
        State = state;
    }

    // ───────────────────────── 업데이트 뒤 다시 켜기 ─────────────────────────

    private const uint RESTART_NO_CRASH = 1, RESTART_NO_HANG = 2, RESTART_NO_REBOOT = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);

    /// <summary>업데이트(패치)로 꺼졌을 때만 윈도우가 다시 켜 줌 (실행 60초 뒤부터 유효). 스토어판만.</summary>
    private void RegisterRestart()
    {
        if (!AppInfo.IsPackaged) return;
        try
        {
            int hr = RegisterApplicationRestart(null, RESTART_NO_CRASH | RESTART_NO_HANG | RESTART_NO_REBOOT);
            if (hr != 0) Log.Warn($"업데이트 뒤 다시 켜기 등록 실패: 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Log.Warn($"업데이트 뒤 다시 켜기 등록 실패: {ex.Message}");
        }
    }

    // ───────────────────────── 시험 스위치 (DEBUG 전용) ─────────────────────────

    private static string? FakeValue()
    {
#if DEBUG
        string? v = Environment.GetEnvironmentVariable("MONGDOCK_FAKE_STOREUPDATE");
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim().ToLowerInvariant();
#else
        return null;
#endif
    }

    private static (StoreUpdateState, Version?) FakeResult(string fake)
    {
        if (fake.StartsWith("available", StringComparison.Ordinal))
        {
            string n = fake.Length > 9 ? fake[9..].TrimStart(':', '=') : "";
            return (StoreUpdateState.Available, Version.TryParse(n, out var v) ? v : new Version(WhatsNew.Current.Major, WhatsNew.Current.Minor, WhatsNew.Current.Build + 1));
        }
        if (fake == "error") throw new InvalidOperationException("시험: 스토어 오류");
        return (StoreUpdateState.UpToDate, null);
    }
}
