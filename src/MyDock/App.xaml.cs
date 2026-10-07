using System.IO;
using System.Windows;
using System.Windows.Threading;
using MyDock.Services;
using MyDock.ViewModels;
using MyDock.Views;

namespace MyDock;

public partial class App : Application
{
    private const string MyDockFinderIni = @"C:\Tweaks\My Dock\MyDock\ico.ini";

    private Mutex? _singleInstance;
    private AppServices? _services;
    private DockWindow? _dock;
    private TopBarWindow? _topBar;
    private TrayController? _tray;
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

        if (e.Args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
                using (exit) exit.Set();
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\mongdock.SingleInstance", out bool isFirst);
        if (isFirst)
            AppInfo.MigrateLegacyInstall();
        if (!isFirst)
        {
            // 이미 실행 중이면 그 MyDock 을 깨운다 (일시 정지 해제 / 다 꺼져 있으면 독 켜기).
            if (EventWaitHandle.TryOpenExisting(ResumeEventName, out var resume))
                using (resume) resume.Set();
            Shutdown();
            return;
        }
        _resumeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ResumeEventName);
        _resumeWait = ThreadPool.RegisterWaitForSingleObject(_resumeEvent,
            (_, _) => Dispatcher.BeginInvoke(ResumeFromSecondLaunch), null, Timeout.Infinite, executeOnlyOnce: false);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => Dispatcher.BeginInvoke(() =>
            {
                Log.Info("종료 요청 받음 (--exit)");
                Shutdown();
            }), null, Timeout.Infinite, executeOnlyOnce: true);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("처리되지 않은 예외", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("관찰되지 않은 작업 예외", args.Exception);
            args.SetObserved();
        };

        var settings = new SettingsService();
        var tracker = new WindowTracker();
        _services = new AppServices(
            settings,
            tracker,
            new AppLauncher(tracker),
            new IconService(),
            new DesktopWindowService(),
            new VirtualDesktopService(),
            new ShellActions(),
            new ImeService(),
            new StatusService(),
            new MediaService(),
            new AppMenuService(settings),
            new StartupService());

        InitializePinsOnce(settings);

        tracker.Start();
        _services.Status.Start();
        _services.Media.Start();
        _dock = new DockWindow(_services);
        _dock.Show();
        _topBar = new TopBarWindow(_services);
        _topBar.Show();
        _tray = new TrayController(_services);
        Log.Info($"{AppInfo.Name} 시작");
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
            }
            else
            {
                current.Pins.AddRange(DefaultPins.Create());
                Log.Info($"기본 핀 {current.Pins.Count}개 설정");
            }
            current.ImportedFromMyDockFinder = true;
            settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("초기 핀 설정 실패", ex);
        }
    }

    /// <summary>MyDock 을 한 번 더 실행했을 때: 트레이가 숨김 아이콘 영역에 있어도 다시 켤 수 있게.</summary>
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
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null) Log.Info($"{AppInfo.Name} 종료");
        // 트레이 아이콘을 내리고, 숨겨 둔 작업 표시줄을 복원한다.
        _tray?.Dispose();
        // 창을 닫아야 AppBar 가 해제된다.
        _topBar?.Close();
        _dock?.Close();
        if (_services is not null)
        {
            _services.Windows.Stop();
            _services.Status.Stop();
            _services.Media.Stop();
            object[] all = [_services.Settings, _services.Windows, _services.DesktopWindows, _services.VirtualDesktops, _services.Ime, _services.Status, _services.Media];
            foreach (var disposable in all.OfType<IDisposable>())
            {
                try { disposable.Dispose(); }
                catch (Exception ex) { Log.Error("종료 정리 실패", ex); }
            }
        }
        _resumeWait?.Unregister(null);
        _resumeEvent?.Dispose();
        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
