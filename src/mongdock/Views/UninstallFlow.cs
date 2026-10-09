using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 설정 → 정보 → "몽독 제거하기…": 확인 카드 → 알림 소리 원래대로 → 윈도우 앱 제거 화면 열기 → 몽독 종료.
/// 종료 경로(App.OnExit)가 작업 표시줄 숨김·자동 숨김·트레이 가로채기·AppBar 를 원래대로 돌린다.
/// 스토어판(MSIX)은 제거될 때 몽독이 정리할 기회가 없어서(제거 훅 없음) 이 길로 지우면 깨끗하게 끝난다.
/// 일반판은 앱 제거 화면 → 설치 프로그램 제거(installer/mongdock.iss)로 이어진다 (거기서도 같은 정리를 함).
/// </summary>
internal static class UninstallFlow
{
    private const string AppsSettingsUri = "ms-settings:appsfeatures";

    public static async Task RunAsync(AppServices services)
    {
        bool ok = await ConfirmCardWindow.AskAsync(services,
            Loc.T("몽독을 제거할까요?"),
            Loc.T("작업 표시줄과 알림 소리를 원래대로 돌리고 몽독을 끈 뒤, 윈도우의 앱 제거 화면을 열어요. 거기서 mongdock 을 제거해 주세요."),
            Loc.T("제거 준비"));
        if (!ok) return;
        Log.Info("몽독 제거하기: 원래대로 돌리고 종료");

        // 몽독이 바꾼 윈도우 알림 소리 → 원래 값 (종료해도 남는 유일한 시스템 변경. 작업 표시줄은 종료 때 복원)
        try
        {
            var n = services.Settings.Current.Notifications;
            if (n.Sound is not null && NotificationSoundService.Restore(n)) services.Settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("제거 준비: 알림 소리 되돌리기 실패", ex);
        }

        services.Launcher.OpenFile(AppsSettingsTarget());
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>스토어판은 그 앱의 "고급 옵션"(제거 버튼 있음)으로 바로, 아니면 설치된 앱 목록.</summary>
    private static string AppsSettingsTarget()
    {
        if (!AppInfo.IsPackaged) return AppsSettingsUri;
        try { return AppsSettingsUri + "-app?" + Windows.ApplicationModel.Package.Current.Id.FamilyName; }
        catch (Exception ex)
        {
            Log.Warn($"패키지 이름 조회 실패: {ex.Message}");
            return AppsSettingsUri;
        }
    }
}
