using System.IO;
using System.Windows;
using System.Windows.Threading;
using Mongdock.Services;
using Mongdock.ViewModels;
using Mongdock.Views;

namespace Mongdock;

public partial class App : Application
{
    private const string MyDockFinderIni = @"C:\Tweaks\My Dock\MyDock\ico.ini";

    private Mutex? _singleInstance;
    private AppServices? _services;
    private DockWindow? _dock;
    /// <summary>모니터 장치 이름 → 그 모니터의 상단바 (ShowOnAllMonitors 면 모든 모니터, 아니면 주 모니터만).</summary>
    private readonly Dictionary<string, TopBarWindow> _topBars = new(StringComparer.OrdinalIgnoreCase);
    private bool _exiting;
    /// <summary>이 프로세스가 세션 표시(CrashReporter)를 썼는지 — --exit·두 번째 실행처럼 곧 끝나는 프로세스가 지우지 않게.</summary>
    private bool _sessionStarted;
    /// <summary>지난 실행의 비정상 종료·오류 (다음 실행 때 물어봄, Views/CrashPrompt).</summary>
    private CrashReporter.Pending? _crashPending;
    private TrayController? _tray;
    private SpotlightHotkeyController? _spotlightHotkey;
    private IDisposable? _banners;
    private UpdateService? _updates;
    private IDisposable? _updateUi;
    private IDisposable? _lowBattery;
    private NativeToastSuppressor? _toastSuppressor;
    private readonly WindowNudger _windowNudger = new();
    private const string ResumeEventName = @"Local\mongdock.Resume";
    private EventWaitHandle? _resumeEvent;
    private RegisteredWaitHandle? _resumeWait;
    // 설치 프로그램/스크립트용 정상 종료 요청 (mongdock.exe --exit). 트레이 "종료" 와 같은 경로로 끝나 작업 표시줄·AppBar 가 복원된다.
    private const string ExitEventName = @"Local\mongdock.Exit";
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 회사 인증 프록시(NTLM/Kerberos): 업데이트·캘린더·메뉴 규칙 요청이 로그인한 윈도우 계정으로 인증하게
        try { System.Net.Http.HttpClient.DefaultProxy.Credentials = System.Net.CredentialCache.DefaultCredentials; }
        catch (Exception ex) { Log.Warn($"프록시 자격 증명 설정 실패: {ex.Message}"); }

        if (e.Args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
                using (exit) exit.Set();
            Shutdown();
            return;
        }

        // 제거 프로그램용: 강제 종료로 남은 작업 표시줄 숨김·자동 숨김을 원래대로 (mongdock 이 실행 중이 아닐 때만)
        if (e.Args.Any(a => string.Equals(a, "--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            if (Mutex.TryOpenExisting(@"Local\mongdock.SingleInstance", out var running))
            {
                running.Dispose();
                Log.Info("--restore-taskbar: mongdock 실행 중 → 건너뜀");
            }
            else
            {
                try { DesktopWindowService.RestoreTaskbarFromRecord(); }
                catch (Exception ex) { Log.Error("--restore-taskbar 실패", ex); }
            }
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\mongdock.SingleInstance", out bool isFirst);
        if (isFirst)
            AppInfo.MigrateLegacyInstall();
        if (!isFirst)
        {
            // 이미 실행 중이면 그 mongdock 을 깨운다 (일시 정지 해제 / 다 꺼져 있으면 독 켜기).
            if (EventWaitHandle.TryOpenExisting(ResumeEventName, out var resume))
                using (resume) resume.Set();
            Shutdown();
            return;
        }
        // 오류 자동 신고: 지난 실행이 정상 종료됐는지 보고 이번 세션 표시를 씀 (가능한 한 일찍)
        _crashPending = CrashReporter.BeginSession();
        _sessionStarted = true;
        _resumeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ResumeEventName);
        _resumeWait = ThreadPool.RegisterWaitForSingleObject(_resumeEvent,
            (_, _) => Dispatcher.BeginInvoke(ResumeFromSecondLaunch), null, Timeout.Infinite, executeOnlyOnce: false);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                Log.Info("종료 요청 받음 (--exit)");
                // 설치 프로그램·업데이트가 기다리다(10초) 강제 종료해도 "갑자기 꺼졌어요" 가 뜨지 않게 미리 정상 종료 표시
                if (_sessionStarted) CrashReporter.EndSession();
                Shutdown();
            }), null, Timeout.Infinite, executeOnlyOnce: true);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error("처리되지 않은 예외", args.ExceptionObject as Exception);
            CrashReporter.Record("다른 스레드(종료)", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("관찰되지 않은 작업 예외", args.Exception);
            CrashReporter.Record("작업(Task)", args.Exception);
            args.SetObserved();
        };

        var settings = new SettingsService();
        var tracker = new WindowTracker();
        var launcher = new AppLauncher(tracker);
        // 스토어판은 시작 앱(StartupTask), 일반판은 Run 키
        IStartupService startup = AppInfo.IsPackaged ? new PackagedStartupService() : new StartupService();
        _services = new AppServices(
            settings,
            tracker,
            launcher,
            new IconService(),
            new DesktopWindowService(),
            new VirtualDesktopService(),
            new ShellActions(),
            new ImeService(),
            new StatusService(),
            new MediaService(),
            new AppMenuService(settings),
            startup,
            new NotificationService(tracker, launcher),
            new TrayIconService(),
            new CalendarFeedService(settings));

        InitializePinsOnce(settings);
        if (startup is PackagedStartupService packaged)
        {
            // 일반판에서 옮겨 왔으면 Run 키를 이어받고, 아니고 새 설치면 첫 둘러보기 뒤에 자동 실행을 물어봄 (CoachMarks)
            bool adopted = packaged.Adopt(settings.Current);
            bool ask = !adopted && settings.CreatedThisRun && !settings.Current.StartWithWindows;
            if (ask) settings.Current.StartupPromptPending = true;
            if (adopted || ask) settings.Save();
        }

        tracker.Start();
        _services.Status.Start();
        _services.Media.Start();
        if (!AppState.Paused) _services.Calendars.Start();
        _dock = new DockWindow(_services);
        _dock.Show();
        // 상단바 창들보다 먼저 구독 → 모니터가 분리되면 그 상단바가 이벤트를 처리하기 전에 닫힘
        _services.DesktopWindows.DisplayChanged += OnDisplayChanged;
        _services.Settings.SettingsChanged += OnSettingsChanged;
        // 새 버전 확인 (상단바 로고 배지가 UpdateService.Instance 를 쓰므로 상단바보다 먼저).
        // 스토어판은 스토어가 업데이트 → 만들지 않음 (Instance 가 null 이면 배지·메뉴 항목·정보 페이지 업데이트 영역이 모두 숨음)
        if (!AppInfo.IsPackaged) _updates = new UpdateService(settings, () => AppState.Paused);
        SyncTopBars();
        _tray = new TrayController(_services);
        _spotlightHotkey = new SpotlightHotkeyController(_services);
        _banners = NotificationBannerWindow.Attach(_services);
        if (_updates is not null) _updateUi = UpdateUi.Attach(_services, _updates);
        _lowBattery = LowBatteryBanner.Attach(_services); // 배터리 20·10·5% 알림 (배너 호스트 다음)
        _updates?.Start(); // 1분 뒤 첫 확인, 이후 12시간마다
        _services.Notifications.Start();
        _toastSuppressor = new NativeToastSuppressor(_services.Notifications as NotificationService);
        AppState.Changed += OnPausedChanged;
        SyncToastSuppressor();
        SyncTrayIcons();
        SyncWindowNudger();
        Log.Info($"{AppInfo.Name} 시작");
        // 버전 업데이트 후 "새로운 기능" / 첫 설치 둘러보기 (독·상단바가 자리 잡은 뒤)
        CoachMarks.Init(_services, ResolveCoachAnchor,
            () => !_exiting && _topBars.TryGetValue("", out var bar) && bar.CoachPopupOpen());
        // 지난 실행이 갑자기 꺼졌거나 오류가 있었으면 조용한 때에 "문제를 보낼까요?" (Views/CrashPrompt)
        CrashPrompt.Schedule(_services, _crashPending);
        CrashReporter.ScheduleTestCrash(Dispatcher); // MONGDOCK_TEST_CRASH 가 있을 때만 (개발 시험)
        // 메모리 진단 로그 (2분 뒤, 30분마다)
        var icons = _services.Icons as IconService;
        MemoryReport.Start(() => $"{icons?.CacheStats()}, 검색 아이콘 {SpotlightWindow.CachedIconCount}개");
        // --tour: 첫 설치 둘러보기를 지금 설정 그대로 다시 보기 (확인·시연용)
        bool tour = Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--tour", StringComparison.OrdinalIgnoreCase));
        CoachMarks.ScheduleStartup(settings.CreatedThisRun, forceTour: tour);
    }

    /// <summary>코치마크 앵커 위치: 독은 독 창, 나머지는 주 모니터 상단바 (안 보이면 null).</summary>
    private (Rect Rect, MonitorInfo Monitor)? ResolveCoachAnchor(CoachAnchor anchor)
    {
        if (_exiting) return null;
        if (anchor == CoachAnchor.Dock) return _dock?.GetAnchorRect(anchor);
        return _topBars.TryGetValue("", out var bar) ? bar.GetAnchorRect(anchor) : null;
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => SyncTopBars();
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        SyncTopBars();
        SyncToastSuppressor();
        SyncTrayIcons();
        SyncWindowNudger();
    }

    private void OnPausedChanged(object? sender, EventArgs e)
    {
        SyncToastSuppressor();
        SyncTrayIcons();
        SyncWindowNudger();
        // 일시 정지 중엔 알림 DB 감시·폴링, 구독 캘린더 주기 새로고침도 멈춤 (재개하면 그 사이 알림은 배너 없이 목록에만)
        if (_services is null || _exiting) return;
        if (AppState.Paused) { _services.Notifications.Stop(); _services.Calendars.Stop(); }
        else { _services.Notifications.Start(); _services.Calendars.Start(); }
    }

    /// <summary>윈도우 기본 알림 팝업 숨기기: 설정이 켜져 있고, 몽독 배너도 켜져 있고, 일시 정지가 아닐 때만.</summary>
    private void SyncToastSuppressor()
    {
        if (_services is null || _toastSuppressor is null || _exiting) return;
        var n = _services.Settings.Current.Notifications;
        _toastSuppressor.SetEnabled(n.HideWindowsToastPopups && n.ShowNotificationBanners && !AppState.Paused);
    }

    /// <summary>
    /// 상단바 밑으로 들어간 창 내리기: 상단바가 켜져 있고 공간 예약 중이고 설정이 켜져 있고 일시 정지가 아닐 때만.
    /// (상단바가 실제로 AppBar 로 등록돼 있지 않으면 WindowNudger 가 알아서 아무것도 안 함)
    /// </summary>
    private void SyncWindowNudger()
    {
        if (_services is null || _exiting) return;
        var t = _services.Settings.Current.TopBar;
        try { _windowNudger.SetEnabled(t.Enabled && t.ReserveSpace && t.KeepWindowsBelowBar && !AppState.Paused); }
        catch (Exception ex) { Log.Error("창 내리기 전환 실패", ex); }
    }

    /// <summary>
    /// 앱 트레이 아이콘 가로채기: 상단바가 켜져 있고, "앱 트레이 아이콘" 이 켜져 있고, 일시 정지가 아닐 때만.
    /// 끄면 몽독의 Shell_TrayWnd 창이 사라져 앱들은 다시 explorer 로만 보낸다.
    /// </summary>
    private void SyncTrayIcons()
    {
        if (_services is null || _exiting) return;
        var t = _services.Settings.Current.TopBar;
        try { _services.TrayIcons.SetEnabled(t.Enabled && t.ShowTrayIcons && !AppState.Paused); }
        catch (Exception ex) { Log.Error("트레이 아이콘 서비스 전환 실패", ex); }
    }

    /// <summary>
    /// 상단바 창 수를 모니터 구성에 맞춤: ShowOnAllMonitors 면 연결된 모든 모니터에 하나씩, 아니면 주 모니터에만.
    /// 사라진 모니터·더 이상 원하지 않는 모니터의 상단바는 닫고(AppBar 해제), 새 모니터에는 새로 만든다.
    /// 단일 모니터에서는 주 모니터 상단바 하나 (예전과 같음).
    /// </summary>
    private void SyncTopBars()
    {
        if (_services is null || _exiting) return;
        try
        {
            var monitors = _services.DesktopWindows.GetMonitors();
            bool all = _services.Settings.Current.TopBar.ShowOnAllMonitors;
            // 키 "" = 주 모니터 상단바 (장치 이름 없이 등록 → 주 모니터가 바뀌어도 그 창이 따라감, 단일 모니터는 예전과 똑같음).
            // 나머지 모니터는 장치 이름으로.
            var wanted = new List<string> { "" };
            if (all)
                wanted.AddRange(monitors.Where(m => !m.IsPrimary && m.DeviceName.Length > 0).Select(m => m.DeviceName));

            foreach (var key in _topBars.Keys.Where(k => !wanted.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                var bar = _topBars[key];
                _topBars.Remove(key);
                Log.Info($"상단바 닫음: {(key.Length > 0 ? key : "주 모니터")}");
                try { bar.Close(); }
                catch (Exception ex) { Log.Error("상단바 닫기 실패", ex); }
            }
            foreach (var device in wanted)
            {
                if (_topBars.ContainsKey(device)) continue;
                var bar = new TopBarWindow(_services, device);
                _topBars[device] = bar;
                Log.Info($"상단바 만듦: {(device.Length > 0 ? device : "주 모니터")}");
                bar.Show();
            }
        }
        catch (Exception ex)
        {
            Log.Error("상단바 모니터 동기화 실패", ex);
        }
    }

    /// <summary>
    /// 첫 실행에만 핀을 채운다: MyDockFinder 의 ico.ini 가 있으면 그것을, 없으면 기본 핀(Finder·Launchpad·브라우저·설정).
    /// ImportedFromMyDockFinder 를 "초기 핀 설정 완료" 표시로 써서, 사용자가 핀을 다 지워도 다시 채우지 않는다.
    /// </summary>
    private static void InitializePinsOnce(SettingsService settings)
    {
        var current = settings.Current;
        if (current.ImportedFromMyDockFinder || current.Pins.Count > 0)
            return;

        try
        {
            if (File.Exists(MyDockFinderIni))
            {
                current.Pins.AddRange(new MyDockFinderImporter(settings).Import(MyDockFinderIni));
                Log.Info($"MyDockFinder 핀 {current.Pins.Count}개 가져옴");
                // 그 뒤 작업 표시줄 고정 앱 중 없는 것만
                TaskbarPins.AddMissingTo(current, settings);
            }
            else
            {
                // Finder·Launchpad + 작업 표시줄 고정 앱 (없으면 기존 기본 핀)
                current.Pins.AddRange(DefaultPins.CreateInitial(settings));
                Log.Info($"기본 핀 {current.Pins.Count}개 설정");
            }
            current.ImportedFromMyDockFinder = true;
            current.TaskbarPinsImported = true;
            settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("초기 핀 설정 실패", ex);
        }
    }

    /// <summary>mongdock 을 한 번 더 실행했을 때: 트레이가 숨김 아이콘 영역에 있어도 다시 켤 수 있게.</summary>
    private void ResumeFromSecondLaunch()
    {
        if (_services is null) return;
        Log.Info("다시 실행됨 → 일시 정지 해제");
        AppState.Paused = false;
        var current = _services.Settings.Current;
        if (!current.Dock.Enabled && !current.TopBar.Enabled)
        {
            current.Dock.Enabled = true;
            _services.Settings.Save();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 독이 예외 하나로 사라지면 AppBar 영역만 남으므로, 기록하고 계속 실행한다.
        Log.Error("UI 스레드 예외", e.Exception);
        CrashReporter.Record("UI 스레드", e.Exception);
        e.Handled = true;
    }

    /// <summary>윈도우 로그아웃·종료·재시작: 그 뒤 윈도우가 프로세스를 바로 끝낼 수 있어 정상 종료 표시를 먼저 지움.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info($"윈도우 세션 끝 ({e.ReasonSessionEnding})");
        if (_sessionStarted) CrashReporter.EndSession();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 정상 종료 표시는 정리 작업 전에 (정리가 오래 걸려 강제 종료돼도 비정상 종료로 보지 않게)
        if (_sessionStarted) CrashReporter.EndSession();
        if (_services is not null) Log.Info($"{AppInfo.Name} 종료");
        CoachMarks.CloseAll();
        // 트레이 아이콘을 내리고, 숨겨 둔 작업 표시줄을 복원한다.
        _tray?.Dispose();
        // 전역 키보드 훅 해제
        _spotlightHotkey?.Dispose();
        // 트레이 가로채기 창을 먼저 없앤다 → 이후 AppBar 해제(SHAppBarMessage)·트레이 아이콘 삭제는 explorer 로 바로 감
        if (_services?.TrayIcons is IDisposable trayIcons)
        {
            try { trayIcons.Dispose(); }
            catch (Exception ex) { Log.Error("트레이 아이콘 서비스 정리 실패", ex); }
        }
        // 숨기던 윈도우 알림 팝업 훅 해제 (떠 있던 팝업은 제자리로)
        AppState.Changed -= OnPausedChanged;
        _toastSuppressor?.Dispose();
        _windowNudger.Dispose();
        // 창을 닫아야 AppBar 가 해제된다.
        _exiting = true;
        if (_services is not null)
        {
            _services.DesktopWindows.DisplayChanged -= OnDisplayChanged;
            _services.Settings.SettingsChanged -= OnSettingsChanged;
        }
        foreach (var bar in _topBars.Values.ToList())
        {
            try { bar.Close(); }
            catch (Exception ex) { Log.Error("상단바 닫기 실패", ex); }
        }
        _topBars.Clear();
        _dock?.Close();
        _banners?.Dispose();
        _updateUi?.Dispose();
        _lowBattery?.Dispose();
        _updates?.Dispose();
        if (_services is not null)
        {
            _services.Notifications.Stop();
            _services.Calendars.Stop();
            _services.Windows.Stop();
            _services.Status.Stop();
            _services.Media.Stop();
            object[] all = [_services.Settings, _services.Windows, _services.DesktopWindows, _services.VirtualDesktops, _services.Ime, _services.Status, _services.Media, _services.Notifications, _services.Calendars, _services.AppMenus];
            foreach (var disposable in all.OfType<IDisposable>())
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { Log.Error("종료 정리 실패", ex); }
            }
        }
        MemoryReport.Stop();
        _resumeWait?.Unregister(null);
        _resumeEvent?.Dispose();
        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
