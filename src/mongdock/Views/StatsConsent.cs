using System.Windows.Threading;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 사용 통계 동의 (#d20, opt-in). SendUsageStats 가 null(아직 안 물음)이면 "사용 통계를 보낼까요? [안 보내기] [보내기]" 카드를 한 번.
/// - 새 설치: 첫 안내(둘러보기)가 끝난 뒤. 기존 사용자: 이번 업데이트 뒤 (settingsVersion 5 이관이 null 로 바꿈).
/// - 둘러보기·새 기능 카드·체험 끝 잠금·일시 정지 중에는 미룸 (30초마다 다시 봄). 시작 직후 20초는 기다림.
/// - 바깥을 눌러 닫으면(고르지 않음) 이번 실행에서는 다시 안 묻고 다음 실행 때 다시. 답하기 전엔 보내지 않음(모아만 둠).
/// - [안 보내기] → 모아 둔 것도 지움. 설정 → 정보 토글로 언제든 바꿈.
/// </summary>
public static class StatsConsent
{
    private static AppServices? _services;
    private static DispatcherTimer? _timer;

    public static void Attach(AppServices services)
    {
        _services = services;
        if (services.Settings.Current.SendUsageStats is not null) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimeSpan.FromSeconds(30);
            var s = services.Settings.Current;
            if (s.SendUsageStats is not null) { _timer.Stop(); return; }
            if (s.FirstRunTourPending || CoachMarks.IsShowing || AppState.Paused) return;
            _timer.Stop();
            _ = AskAsync();
        };
        _timer.Start();
    }

    private static async Task AskAsync()
    {
        if (_services is null) return;
        bool? answer = await ConfirmCardWindow.AskChoiceAsync(_services,
            Loc.T("사용 통계를 보낼까요?"),
            Loc.T("몽독을 다듬는 데 쓰도록 하루 한 번 작은 신호를 보내요: 앱·윈도우 버전, 언어, 노트북 여부, 모니터 수·배율, 주요 기능 켜짐/꺼짐, 오류 수. PC 를 알아볼 수 있는 정보는 보내지 않고, 설정 → 정보에서 언제든 바꿀 수 있어요."),
            Loc.T("보내기"), Loc.T("안 보내기"));
        if (answer is null)
        {
            Log.Info("사용 통계 묻기: 고르지 않음 → 다음 실행 때 다시");
            return;
        }
        _services.Settings.Current.SendUsageStats = answer;
        _services.Settings.Save();
        Log.Info($"사용 통계 묻기: {(answer == true ? "보내기" : "안 보내기")}");
        UsageStatsService.Instance?.Poke();
    }
}
