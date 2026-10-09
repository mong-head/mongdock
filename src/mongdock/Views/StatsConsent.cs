using System.Globalization;
using System.Windows.Media;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 사용 통계 동의 (#d20, opt-in). SendUsageStats 가 null(아직 안 물음)이면 오른쪽 위 배너로 살짝 물음:
/// "사용 통계를 보낼까요? [안 보내기] [보내기]" — 10초 머물다(마우스를 올리면 멈춤) 흐려지며 지나감.
/// - 새 설치: 첫 안내(둘러보기)가 끝난 뒤. 기존 사용자: 업데이트 뒤. 둘러보기·새 기능 카드·일시 정지·전체 화면이면 미룸.
/// - 고르지 않고 지나가면 그날은 다시 안 묻고 다음 날(몽독이 켜진 날) 한 번 더. 3번 지나가면 "안 보냄"(false)으로 두고 더 묻지 않음.
/// - 답하기 전엔 보내지 않음(모아만 둠). [안 보내기] → 모아 둔 것도 지움. 설정 → 정보 토글로 언제든 바꿈.
/// </summary>
public static class StatsConsent
{
    private const int MaxAsks = 3;
    private const long BannerId = -7600;
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(10);

    private static AppServices? _services;
    private static DispatcherTimer? _timer;

    public static void Attach(AppServices services)
    {
        _services = services;
        if (services.Settings.Current.SendUsageStats is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromSeconds(60);
            var s = services.Settings.Current;
            if (s.SendUsageStats is not null || s.StatsAskDay == Today) { _timer.Stop(); return; }
            if (s.FirstRunTourPending || CoachMarks.IsShowing || AppState.Paused) return;
            if (Ask(s)) _timer.Stop(); // 못 띄웠으면(전체 화면 등) 1분 뒤 다시
        };
        _timer.Start();
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool Ask(Settings s)
    {
        if (_services is null) return true;
        var item = new NotificationItem(BannerId, "", AppInfo.Name, Loc.T("사용 통계를 보낼까요?"),
            new[] { Loc.T("PC 를 알아볼 수 없는 정보를 하루 한 번 보내요. 설정 → 정보에서 언제든 바꿀 수 있어요.") },
            DateTime.Now, null, null, false, null);
        var buttons = new[]
        {
            new NotificationBannerWindow.BannerButton(Loc.T("안 보내기"), () => Answer(false)),
            new NotificationBannerWindow.BannerButton(Loc.T("보내기"), () => Answer(true), Primary: true),
        };
        bool shown = NotificationBannerWindow.ShowQuestion(item, AppIcon(_services), buttons, Life, Ignored);
        if (!shown) return false;
        s.StatsAskDay = Today;
        _services.Settings.Save();
        Log.Info($"사용 통계 묻기: 배너 ({s.StatsAskCount + 1}/{MaxAsks}번째)");
        return true;
    }

    private static void Answer(bool send)
    {
        if (_services is null) return;
        _services.Settings.Current.SendUsageStats = send;
        _services.Settings.Save();
        Log.Info($"사용 통계 묻기: {(send ? "보내기" : "안 보내기")}");
        UsageStatsService.Instance?.Poke();
    }

    /// <summary>고르지 않고 지나감(시간 다 됨·×). 3번째면 "안 보냄"으로.</summary>
    private static void Ignored()
    {
        if (_services is null) return;
        var s = _services.Settings.Current;
        if (s.SendUsageStats is not null) return;
        s.StatsAskCount++;
        if (s.StatsAskCount >= MaxAsks)
        {
            s.SendUsageStats = false;
            Log.Info($"사용 통계 묻기: {MaxAsks}번 지나감 → 안 보냄으로 두고 더 묻지 않음");
            UsageStatsService.Instance?.Poke();
        }
        else
        {
            Log.Info($"사용 통계 묻기: 고르지 않고 지나감 ({s.StatsAskCount}/{MaxAsks}) → 다음 날 다시");
        }
        _services.Settings.Save();
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
