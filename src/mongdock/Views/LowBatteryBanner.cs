using System.Globalization;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 배터리 부족 알림: IStatusService.Changed 마다 배터리를 <see cref="LowBatteryTracker"/> 에 넘기고,
/// 단계(20·10·5%)에 닿으면 몽독 배너 한 장 (설정 Notifications.LowBatteryAlerts). 클릭 = 윈도우 배터리 설정.
/// 확인: MONGDOCK_FAKE_BATTERY="9" 로 실행하면 시작 직후 10% 단계 배너가 뜬다.
/// </summary>
internal static class LowBatteryBanner
{
    /// <summary>배너 카드 Id (업데이트 배너 -7300 과 겹치지 않는 음수, 단계마다 다르게).</summary>
    private const long BannerIdBase = -7400;

    public static IDisposable Attach(AppServices services) => new Hook(services);

    private sealed class Hook : IDisposable
    {
        private readonly AppServices _services;
        private readonly LowBatteryTracker _tracker = new();

        public Hook(AppServices services)
        {
            _services = services;
            _services.Status.Changed += OnChanged;
            OnChanged(null, EventArgs.Empty);
        }

        private void OnChanged(object? sender, EventArgs e)
        {
            try
            {
                var b = _services.Status.Battery;
                if (_tracker.Update(b) is not int level || b is null) return;
                if (!_services.Settings.Current.Notifications.LowBatteryAlerts || AppState.Paused) return;
                string pct = b.Percent.ToString(CultureInfo.InvariantCulture);
                string hint = level <= 5
                    ? Loc.T("곧 꺼질 수 있어요. 전원을 연결해 주세요.")
                    : b.Saver ? Loc.T("전원을 연결해 주세요.") : Loc.T("전원을 연결하거나 절전 모드를 켜 보세요.");
                var item = new NotificationItem(BannerIdBase - level, "", AppInfo.Name, Loc.F($"배터리가 {pct}% 남았어요"),
                    new[] { hint }, DateTime.Now, null, null, false, null);
                bool shown = NotificationBannerWindow.ShowCustom(item, AppIcon(_services), () => _services.Status.OpenBatterySettings());
                Log.Info($"배터리 부족 알림 {level}% (현재 {pct}%){(shown ? "" : " — 배너 꺼짐/전체 화면이라 표시 안 함")}");
            }
            catch (Exception ex) { Log.Error("배터리 부족 알림 실패", ex); }
        }

        public void Dispose() => _services.Status.Changed -= OnChanged;
    }

    private static ImageSource? AppIcon(AppServices services)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null) return null;
            return services.Icons.GetIcon(new PinItem { Name = AppInfo.Name, Kind = PinKind.Exe, Target = exe }, services.Settings.Current.Dock.IconStyle);
        }
        catch (Exception ex)
        {
            Log.Warn($"몽독 아이콘 읽기 실패: {ex.Message}");
            return null;
        }
    }
}
