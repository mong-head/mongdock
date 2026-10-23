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
        if (services.Settings is SettingsService s) s.Problem += Show;
        if (SettingsService.StartupNotice is { } startup)
        {
            SettingsService.StartupNotice = null;
            // 독·상단바가 뜬 뒤에 (시작 직후 배너는 놓치기 쉬움)
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) => { t.Stop(); Show(startup); };
            t.Start();
        }
    }

    private static void Show(string text)
    {
        if (_services is not { } services) return;
        if (text == _lastText && DateTime.Now - _lastAt < TimeSpan.FromMinutes(1)) return;
        _lastText = text;
        _lastAt = DateTime.Now;
        try
        {
            var item = new NotificationItem(0, "", AppInfo.Name, Loc.T("설정"), new[] { text }, DateTime.Now, null, null, false, null);
            var buttons = new[] { new NotificationBannerWindow.BannerButton(Loc.T("파일 열기"), () => OpenFile(services), Primary: true) };
            NotificationBannerWindow.ShowQuestion(item, null, buttons, TimeSpan.FromSeconds(12), () => { });
        }
        catch (Exception ex) { Log.Warn($"설정 파일 알림 실패: {ex.GetType().Name}"); }
    }

    private static void OpenFile(AppServices services)
    {
        try { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + services.Settings.SettingsPath + "\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn($"설정 파일 열기 실패: {ex.GetType().Name}"); }
    }
}
