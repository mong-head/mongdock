using System.Windows.Threading;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 오류 자동 신고 (#14) — 화면 쪽. 지난 실행이 갑자기 꺼졌거나 처리되지 않은 오류가 있었으면(Services/CrashReporter),
/// 시작하고 조용해진 뒤(둘러보기·일시 정지·전체 화면이 아닐 때) 카드로 묻는다:
/// [보내기] → 문제 신고 창을 "버그"·제목·내용·오류 정보(가린 뒤)로 채워 연다 — 자동 전송 아님, 사용자가 보고 보냄.
/// [안 보내기] → 기록만 비움. "다시 묻지 않기" → 설정에 끔. 그냥 닫히면 기록을 남겨 다음 실행에 다시(같은 오류는 하루 한 번).
/// </summary>
internal static class CrashPrompt
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    public static void Schedule(AppServices services, CrashReporter.Pending? pending)
    {
        if (pending is null) return;
        if (services.Settings.Current.CrashPromptDisabled)
        {
            CrashReporter.Clear();
            return;
        }
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FirstDelay };
        timer.Tick += (_, _) =>
        {
            if (Busy(services))
            {
                timer.Interval = RetryDelay;
                return;
            }
            timer.Stop();
            _ = AskAsync(services, pending);
        };
        timer.Start();
    }

    private static bool Busy(AppServices services)
    {
        if (AppState.Paused || CoachMarks.IsShowing) return true;
        try { return services.DesktopWindows.IsFullscreenOn(""); }
        catch { return false; }
    }

    private static async Task AskAsync(AppServices services, CrashReporter.Pending pending)
    {
        try
        {
            if (CrashReporter.AlreadyAskedToday(pending.Signature))
            {
                Log.Info("오류 자동 신고: 같은 오류를 오늘 이미 물어봄 → 건너뜀");
                return;
            }
            bool crashed = pending.Crashed;
            var choice = await ConfirmCardWindow.AskWithExtraAsync(services,
                crashed ? "몽독이 지난번에 갑자기 꺼졌어요" : "몽독에서 오류가 있었어요",
                "문제를 몽독 지원으로 보내면 고치는 데 도움이 돼요. 보내기 전에 내용을 확인할 수 있어요.",
                "보내기", "안 보내기", "다시 묻지 않기");
            Log.Info($"오류 자동 신고 카드: {choice}");
            switch (choice)
            {
                case ConfirmCardWindow.Choice.Confirm:
                    ReportWindow.Open(services, new ReportWindow.Prefill(
                        ReportKind.Bug,
                        crashed ? "갑자기 꺼짐 (자동)" : "오류 (자동)",
                        (crashed ? "몽독이 갑자기 꺼졌어요." : "몽독에서 오류가 났어요.") + " 그때 하던 일을 적어 주시면 도움이 돼요.",
                        CrashReporter.Describe(pending)));
                    CrashReporter.Clear();
                    break;
                case ConfirmCardWindow.Choice.Cancel:
                    CrashReporter.Clear();
                    break;
                case ConfirmCardWindow.Choice.Extra:
                    services.Settings.Current.CrashPromptDisabled = true;
                    services.Settings.Save();
                    CrashReporter.Clear();
                    break;
                // Dismissed: 기록을 남겨 다음 실행에 다시 (같은 오류는 하루 한 번)
            }
        }
        catch (Exception ex)
        {
            Log.Error("오류 자동 신고 카드 실패", ex);
        }
    }
}
