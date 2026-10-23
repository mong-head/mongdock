using System.Diagnostics;
using System.Windows.Threading;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 설정 파일 문제 한 줄 알림 (#33, 사용자 승인 2026-10-18): 시작할 때 마지막 정상 설정으로 되돌렸거나 일부 항목을 기본값으로 바꿨을 때,
/// 켜져 있는 동안 직접 고친 settings.json 을 못 읽었을 때 — 몽독 배너 + [파일 열기](메모장). 같은 문구는 1분에 한 번만.
/// </summary>
internal static class SettingsFileNotice
{
    private static AppServices? _services;
    private static string? _lastText;
    private static DateTime _lastAt;

    public static void Attach(AppServices services)
    {
        _services = services;
        if (services.Settings is SettingsService s) s.Problem += text => Show(text);
        if (SettingsService.StartupNotice is { } startup)
        {
            SettingsService.StartupNotice = null;
            bool restored = SettingsService.StartupRestored;
            // 독·상단바가 뜬 뒤에 (시작 직후 배너는 놓치기 쉬움)
            // 전체 화면·일시 정지라 못 띄우면 30초 뒤 다시 (최대 10번)
            int tries = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                t.Interval = TimeSpan.FromSeconds(30);
                if (Show(startup, restored) || ++tries >= 10) t.Stop();
            };
            t.Start();
        }
    }

    /// <summary>띄웠으면 true (같은 문구 1분 안 다시는 true 로 봄).</summary>
    private static bool Show(string text, bool offerReset = false)
    {
        if (_services is not { } services) return true;
        if (text == _lastText && DateTime.Now - _lastAt < TimeSpan.FromMinutes(1)) return true;
        try
        {
            var item = new NotificationItem(0, "", AppInfo.Name, Loc.T("설정"), new[] { text }, DateTime.Now, null, null, false, null);
            var buttons = new List<NotificationBannerWindow.BannerButton> { new(Loc.T("파일 열기"), () => OpenFile(services), Primary: true) };
            // 되돌렸을 때만: 일부러 지웠거나 처음부터 하고 싶으면 (사용자 10-23)
            if (offerReset) buttons.Add(new(Loc.T("처음 설정으로"), () => _ = AskResetAsync(services)));
            if (!NotificationBannerWindow.ShowQuestion(item, null, buttons, TimeSpan.FromSeconds(12), () => { })) return false;
            _lastText = text;
            _lastAt = DateTime.Now;
            return true;
        }
        catch (Exception ex) { Log.Warn($"설정 파일 알림 실패: {ex.GetType().Name}"); return true; }
    }

    private static async Task AskResetAsync(AppServices services)
    {
        if (!await ConfirmCardWindow.AskAsync(services, Loc.T("처음 설정으로 되돌릴까요?"),
                Loc.T("독·루틴·설정이 새로 설치한 것처럼 돼요. 지금 설정은 백업 파일로 남겨 두고 몽독이 다시 시작해요."), Loc.T("처음 설정으로")))
            return;
        if (services.Settings is SettingsService s && s.ResetToFirstRun()) App.Relaunch();
    }

    private static void OpenFile(AppServices services)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + services.Settings.SettingsPath + "\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"설정 파일 열기 실패: {ex.GetType().Name}"); }
    }
}
