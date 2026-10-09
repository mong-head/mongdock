using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Mongdock.Services;

/// <summary>
/// 윈도우 공식 알림 읽기 API (UserNotificationListener) — 스토어판(패키지)에서만 (#7 결정 15~17).
/// - 허용되면 NotificationService 가 목록·새 알림을 이것으로 읽고, 보낸 사람 사진·태그 같은 세부는 알림 DB 로 보강.
/// - 거부·실패면 지금처럼 알림 DB 방식. 일반판(설치 프로그램·zip)은 패키지 ID 가 없어 이 API 를 쓸 수 없음 → 늘 DB 방식.
/// - 몽독에서 알림을 지우면 윈도우 알림 센터에서도 지움 (RemoveNotification).
/// 매니페스트에 uap3:Capability userNotificationListener 가 있어야 함 (tools/msix/AppxManifest.xml).
/// </summary>
internal static class NotificationListener
{
    /// <summary>쓸 수 있는 환경인지 (패키지 + API 있음). 허용 여부는 <see cref="Status"/>.</summary>
    public static bool Supported { get; } = AppInfo.IsPackaged && TypePresent();

    private static bool TypePresent()
    {
        try { return ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"); }
        catch { return false; }
    }

    public static UserNotificationListenerAccessStatus Status()
    {
        if (!Supported) return UserNotificationListenerAccessStatus.Denied;
        try { return UserNotificationListener.Current.GetAccessStatus(); }
        catch (Exception ex)
        {
            Log.Warn($"알림 접근 상태 확인 실패: {ex.GetType().Name} {ex.Message}");
            return UserNotificationListenerAccessStatus.Denied;
        }
    }

    public static bool IsAllowed => Status() == UserNotificationListenerAccessStatus.Allowed;

    /// <summary>아직 묻지 않았으면 윈도우 허락 창을 띄움 (UI 스레드에서). 결과가 허용이면 true.</summary>
    public static async Task<bool> RequestAccessAsync()
    {
        if (!Supported) return false;
        try
        {
            var status = await UserNotificationListener.Current.RequestAccessAsync();
            Log.Info($"알림 접근 요청 결과: {status}");
            return status == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Warn($"알림 접근 요청 실패: {ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>읽은 토스트 하나 (글자만 — 사진·태그는 DB 로 보강).</summary>
    internal sealed record Toast(uint Id, string Aumid, DateTime ArrivalUtc, string? Title, IReadOnlyList<string> Lines);

    /// <summary>지금 알림 센터의 토스트 (백그라운드 스레드에서). 접근이 없거나 실패하면 예외.</summary>
    public static List<Toast> Read()
    {
        var list = new List<Toast>();
        var items = UserNotificationListener.Current.GetNotificationsAsync(NotificationKinds.Toast).AsTask().GetAwaiter().GetResult();
        foreach (var n in items)
        {
            try
            {
                string aumid = "";
                try { aumid = n.AppInfo?.AppUserModelId ?? ""; }
                catch { /* 일부 시스템 알림은 앱 정보가 없음 */ }
                if (aumid.Length == 0) continue;
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                var lines = texts?.Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList() ?? new List<string>();
                string? title = lines.Count > 0 ? lines[0] : null;
                list.Add(new Toast(n.Id, aumid, n.CreationTime.UtcDateTime, title, lines.Skip(1).ToList()));
            }
            catch (Exception ex)
            {
                Log.Warn($"알림 하나 읽기 실패 (건너뜀): {ex.GetType().Name}");
            }
        }
        return list;
    }

    /// <summary>윈도우 알림 센터에서 지움. 실패는 로그만 (이미 사라졌을 수 있음).</summary>
    public static void Remove(IEnumerable<long> ids)
    {
        var list = ids.Where(id => id is > 0 and <= uint.MaxValue).Select(id => (uint)id).ToList();
        if (list.Count == 0) return;
        Task.Run(() =>
        {
            int failed = 0;
            foreach (uint id in list)
            {
                try { UserNotificationListener.Current.RemoveNotification(id); }
                catch { failed++; }
            }
            Log.Info($"윈도우 알림 센터에서 지움: {list.Count - failed}개" + (failed > 0 ? $" (실패 {failed})" : ""));
        });
    }
}
