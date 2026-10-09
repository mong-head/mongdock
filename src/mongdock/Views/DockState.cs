using System.Windows;

namespace Mongdock.Views;

/// <summary>창 사이에 공유하는 독 상태 (상단바 자동 색 샘플링에서 독 영역을 빼기 위함).</summary>
internal static class DockState
{
    /// <summary>현재 화면에 보이는 독 패널 영역 (<see cref="Monitor"/> 기준 DIP). 숨김이면 Rect.Empty.</summary>
    public static Rect VisiblePanel { get; set; } = Rect.Empty;

    /// <summary>독이 있는 모니터의 장치 이름 (VisiblePanel 의 DIP 기준). 모르면 "".</summary>
    public static string Monitor { get; set; } = "";

    /// <summary>
    /// 둘러보기(코치마크)가 독을 가리키는 동안 자동 숨김을 멈추고 보이게 고정 (설정값은 그대로). CoachSession 이 켜고 끈다 —
    /// 세션이 어떻게 끝나든(다 봄·건너뜀·일시 정지·전체 화면·교체) Close 에서 반드시 끔.
    /// </summary>
    public static bool CoachPinned { get; private set; }

    public static event Action? CoachPinChanged;

    public static void SetCoachPinned(bool pinned)
    {
        if (CoachPinned == pinned) return;
        CoachPinned = pinned;
        CoachPinChanged?.Invoke();
    }
}
