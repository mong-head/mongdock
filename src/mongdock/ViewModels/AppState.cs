namespace Mongdock.ViewModels;

/// <summary>저장하지 않는 런타임 상태 (트레이 "일시 정지", 체험 끝남 잠금).</summary>
public static class AppState
{
    private static bool _paused;
    private static bool _locked;

    /// <summary>
    /// true 면 독·상단바를 숨기고 AppBar/예약 해제, 폴링 정지, 작업 표시줄 숨김 복원.
    /// 체험이 끝나 잠겼으면(<see cref="Locked"/>) 사용자가 일시 정지를 풀어도 계속 true.
    /// </summary>
    public static bool Paused
    {
        get => _paused || _locked;
        set
        {
            bool before = Paused;
            _paused = value;
            if (Paused != before) Changed?.Invoke(null, EventArgs.Empty);
            else if (!value && _locked) LockedPoke?.Invoke(null, EventArgs.Empty); // 잠긴 채 다시 켜려 함 → 안내
        }
    }

    /// <summary>스토어 체험이 끝나고 구매하지 않음 (#5). 일시 정지와 같은 효과 + 풀 수 없음. Views/LicenseUi 가 정함.</summary>
    public static bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value) return;
            bool before = Paused;
            _locked = value;
            if (Paused != before) Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Paused 가 바뀜 (UI 스레드).</summary>
    public static event EventHandler? Changed;

    /// <summary>잠긴 동안 사용자가 다시 켜려 함 (트레이 클릭·일시 정지 해제·다시 실행) → "체험이 끝났어요" 창.</summary>
    public static event EventHandler? LockedPoke;

    public static void TogglePaused() => Paused = !Paused;
}
