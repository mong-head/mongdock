namespace MyDock.ViewModels;

/// <summary>저장하지 않는 런타임 상태 (트레이 "일시 정지").</summary>
public static class AppState
{
    private static bool _paused;

    /// <summary>true 면 독·상단바를 숨기고 AppBar/예약 해제, 폴링 정지, 작업 표시줄 숨김 복원.</summary>
    public static bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Paused 가 바뀜 (UI 스레드).</summary>
    public static event EventHandler? Changed;

    public static void TogglePaused() => Paused = !Paused;
}
