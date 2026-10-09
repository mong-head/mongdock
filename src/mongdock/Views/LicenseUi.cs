using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 체험판·구매 화면 (#5). LicenseService 상태에 맞춰:
/// - 체험 중: 남은 날이 3일·1일이면 하루 한 번 카드 "체험이 N일 남았어요 [구매하기] [나중에]". 설정 → 정보에 "체험판 · N일 남음 [구매하기]".
/// - 체험 끝: AppState.Locked → 일시 정지와 같은 효과(작업 표시줄·자동 숨김·알림 팝업 원래대로, 독·상단바 숨김)이면서 풀 수 없음.
///   "체험이 끝났어요 [구매하기] [종료]" 카드 하나. 트레이 아이콘은 남아서 누르면 이 카드를 다시 띄움(다시 켜는 길).
///   구매하면 스토어가 OfflineLicensesChanged 를 보내 → 정식 → 잠금 풀림 (앱을 다시 켤 필요 없음).
/// 일반판(깃허브)은 늘 정식이라 아무것도 보이지 않음.
/// </summary>
public static class LicenseUi
{
    private static AppServices? _services;
    private static LicenseService? _license;
    private static bool _expiredCardOpen;
    private static bool _trialCardOpen;
    private static LicenseState _lastState;
    private const string TrialCardTag = "license-trial";

    public static LicenseService? Service => _license;

    public static LicenseService Attach(AppServices services, Func<Window?> fallbackOwner)
    {
        _services = services;
        _license = new LicenseService(() => OwnerHandle(fallbackOwner()));
        _lastState = _license.Current.State;
        _license.Changed += (_, _) => Apply();
        AppState.LockedPoke += (_, _) => ShowExpiredCard();
        _license.Start();
        Apply(initial: true);
        return _license;
    }

    public static void Detach()
    {
        _license?.Dispose();
        _license = null;
    }

    private static void Apply(bool initial = false)
    {
        if (_license is null) return;
        var info = _license.Current;
        AppState.Locked = info.State == LicenseState.Expired;
        if (info.State == LicenseState.Expired && (initial || _lastState != LicenseState.Expired))
        {
            Log.Info("체험 끝남 → 독·상단바 멈춤, 작업 표시줄·알림 원래대로");
            // 시작 때는 조금 뒤에 (다른 창이 뜨며 활성화가 바뀌면 카드가 바로 닫히므로)
            Later(initial ? TimeSpan.FromSeconds(3) : TimeSpan.Zero, ShowExpiredCard);
        }
        else if (info.State == LicenseState.Full && _lastState != LicenseState.Full && !initial)
        {
            Log.Info("정식으로 바뀜 → 다시 켜짐");
        }
        _lastState = info.State;
        if (info.State == LicenseState.Trial) Later(initial ? TimeSpan.FromSeconds(8) : TimeSpan.Zero, () => MaybeShowTrialCard());
    }

    /// <summary>delay 뒤 UI 스레드에서. 둘러보기·새 기능 카드가 떠 있으면 30초씩 미룸 (카드끼리 겹치지 않게).</summary>
    private static void Later(TimeSpan delay, Action show)
    {
        var timer = new DispatcherTimer { Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1) };
        timer.Tick += (_, _) =>
        {
            if (CoachMarks.IsShowing)
            {
                timer.Interval = TimeSpan.FromSeconds(30);
                return;
            }
            timer.Stop();
            show();
        };
        timer.Start();
    }

    // ───────────────────────── 카드 ─────────────────────────

    /// <summary>체험 3일·1일 남았을 때 하루 한 번 (번쩍임 없이 확인 카드 하나).</summary>
    private static async void MaybeShowTrialCard()
    {
        if (_services is null || _license is null || _trialCardOpen) return;
        var info = _license.Current;
        if (info.State != LicenseState.Trial || info.DaysLeft is not (3 or 1) || AppState.Paused) return;
        var s = _services.Settings.Current;
        string today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (s.TrialNoticeDay == today) return;
        s.TrialNoticeDay = today;
        _services.Settings.Save();
        _trialCardOpen = true;
        try
        {
            bool? buy = await ConfirmCardWindow.AskChoiceAsync(_services,
                Loc.F($"체험이 {info.DaysLeft}일 남았어요"),
                Loc.T("체험이 끝나면 독과 상단바가 멈춰요. 지금 구매하면 그대로 계속 쓸 수 있어요."),
                Loc.T("구매하기"), Loc.T("나중에"), TrialCardTag);
            if (buy == true) await PurchaseAsync();
        }
        finally
        {
            _trialCardOpen = false;
        }
    }

    /// <summary>"체험이 끝났어요 [구매하기] [종료]". 바깥을 눌러 닫으면 그대로 두고, 트레이를 누르면 다시 뜸.</summary>
    public static async void ShowExpiredCard()
    {
        if (_services is null || _expiredCardOpen || !AppState.Locked) return;
        ConfirmCardWindow.CloseTagged(TrialCardTag); // 실행 중에 만료되면 "N일 남았어요" 카드가 겹쳐 남지 않게 (QA)
        _expiredCardOpen = true;
        try
        {
            bool? choice = await ConfirmCardWindow.AskChoiceAsync(_services,
                Loc.T("체험이 끝났어요"),
                Loc.T("몽독을 계속 쓰려면 구매해 주세요. 작업 표시줄과 알림은 원래대로 돌려 두었어요. 트레이 아이콘을 누르면 이 창을 다시 열 수 있어요."),
                Loc.T("구매하기"), Loc.T("몽독 종료")); // "종료" 는 영어 사전에서 Shut Down — PC 끄기로 읽혀서 따로 (QA)
            if (choice == true) await PurchaseAsync();
            else if (choice == false)
            {
                Log.Info("체험 끝남 카드: 종료");
                Application.Current.Shutdown();
            }
        }
        finally
        {
            _expiredCardOpen = false;
        }
    }

    /// <summary>[구매하기] (설정 → 정보·카드). 실패해도 그대로 (로그).</summary>
    public static async Task PurchaseAsync()
    {
        if (_license is null) return;
        bool ok = await _license.RequestPurchaseAsync();
        if (!ok && AppState.Locked) Log.Info("구매하지 않음 → 잠금 유지");
    }

    // ───────────────────────── 창 핸들 (스토어 구매 창의 주인) ─────────────────────────

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>지금 앞에 있는 몽독 창(카드·설정 창), 없으면 넘겨받은 창(독).</summary>
    private static IntPtr OwnerHandle(Window? fallback)
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && GetWindowThreadProcessId(fg, out uint pid) != 0 && pid == (uint)Environment.ProcessId) return fg;
            return fallback is null ? IntPtr.Zero : new WindowInteropHelper(fallback).EnsureHandle();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
