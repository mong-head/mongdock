using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 윈도우 작업 표시줄이 자동 숨김 + 독이 아래 + 몽독 "작업 표시줄 숨기기" 꺼짐이면, 화면 아래 끝에서 올라온 작업 표시줄이 독을 덮음.
/// 이 조합을 처음 만나면 한 번만 몽독 톤 카드로 묻고(자동으로 바꾸지 않음): [작업 표시줄 숨기기] [독 위로 옮기기] [그대로 두기].
/// [그대로 두기]·다른 선택은 다시 묻지 않음. 바깥을 눌러 그냥 닫으면 다음 시작 때 다시. 일시 정지·전체 화면 중엔 안 물음.
/// </summary>
internal static class TaskbarOverlapHint
{
    private static AppServices? _services;
    private static DispatcherTimer? _timer;
    private static bool _asking;

    public static void Init(AppServices services)
    {
        _services = services;
        // 시작하고 20초 뒤 (둘러보기·다른 첫 안내와 겹치지 않게), 설정이 바뀌면 다시 (독 위치·작업 표시줄 숨기기를 바꿨을 때)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => { _timer.Stop(); Check(); };
        _timer.Start();
        services.Settings.SettingsChanged += (_, _) =>
        {
            if (_timer is null || _asking) return;
            _timer.Interval = TimeSpan.FromSeconds(3);
            _timer.Stop();
            _timer.Start();
        };
    }

    private static bool Applies(Settings s) =>
        !s.TaskbarOverlapAsked && s.Dock.Enabled && s.Dock.Edge == DockEdge.Bottom && !s.HideWindowsTaskbar
        && (string.IsNullOrEmpty(s.Dock.Monitor) || Monitors.Find(s.Dock.Monitor)?.IsPrimary != false) // 작업 표시줄이 있는 주 모니터의 독
        && TaskbarAutoHide.WindowsAutoHideOn == true;

    private static async void Check()
    {
        var services = _services;
        if (services is null || _asking || AppState.Paused) return;
        var s = services.Settings.Current;
        try
        {
            if (!Applies(s)) return;
            if (Monitors.GetAll().Any(m => services.DesktopWindows.IsFullscreenOn(m.IsPrimary ? "" : m.DeviceName))) return;
            _asking = true;
            Log.Info("작업 표시줄이 독을 가릴 수 있는 조합 — 한 번 물음");
            var choice = await ConfirmCardWindow.AskWithExtraAsync(services,
                Loc.T("작업 표시줄이 독을 가릴 수 있어요"),
                Loc.T("윈도우 작업 표시줄이 자동 숨김이라, 화면 아래 끝에서 올라오면 독을 덮어요."),
                Loc.T("작업 표시줄 숨기기"), Loc.T("독 위로 옮기기"), Loc.T("그대로 두기"));
            s = services.Settings.Current;
            switch (choice)
            {
                case ConfirmCardWindow.Choice.Confirm:
                    s.HideWindowsTaskbar = true; // 몽독이 숨기는 동안 작업 표시줄이 올라오지 않음
                    break;
                case ConfirmCardWindow.Choice.Cancel:
                    s.Dock.Edge = DockEdge.Top;
                    break;
                case ConfirmCardWindow.Choice.Dismissed:
                    Log.Info("작업 표시줄 겹침 안내: 고르지 않고 닫음 — 다음 시작 때 다시");
                    return;
            }
            s.TaskbarOverlapAsked = true;
            Log.Info($"작업 표시줄 겹침 안내: {choice}");
            services.Settings.Save();
        }
        catch (Exception ex) { Log.Error("작업 표시줄 겹침 안내 실패", ex); }
        finally { _asking = false; }
    }
}
