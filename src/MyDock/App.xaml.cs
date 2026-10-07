using System.IO;
using System.Windows;
using System.Windows.Threading;
using MyDock.Services;
using MyDock.Views;

namespace MyDock;

public partial class App : Application
{
    private const string MyDockFinderIni = @"C:\Tweaks\My Dock\MyDock\ico.ini";

    private Mutex? _singleInstance;
    private AppServices? _services;
    private DockWindow? _dock;
    private TopBarWindow? _topBar;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\MyDock.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

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

        ImportMyDockFinderPinsOnce(settings);

        tracker.Start();
        _services.Status.Start();
        _services.Media.Start();
        _dock = new DockWindow(_services);
        _dock.Show();
        _topBar = new TopBarWindow(_services);
        _topBar.Show();
        Log.Info("MyDock 시작");
    }

    private static void ImportMyDockFinderPinsOnce(SettingsService settings)
    {
        var current = settings.Current;
        if (current.ImportedFromMyDockFinder || current.Pins.Count > 0 || !File.Exists(MyDockFinderIni))
            return;

        try
        {
            current.Pins.AddRange(new MyDockFinderImporter(settings).Import(MyDockFinderIni));
            current.ImportedFromMyDockFinder = true;
            settings.Save();
            Log.Info($"MyDockFinder 핀 {current.Pins.Count}개 가져옴");
        }
        catch (Exception ex)
        {
            Log.Error("MyDockFinder 핀 가져오기 실패", ex);
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
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
