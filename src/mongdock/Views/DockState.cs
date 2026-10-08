using System.Windows;

namespace Mongdock.Views;

/// <summary>창 사이에 공유하는 독 상태 (상단바 자동 색 샘플링에서 독 영역을 빼기 위함).</summary>
internal static class DockState
{
    /// <summary>현재 화면에 보이는 독 패널 영역 (<see cref="Monitor"/> 기준 DIP). 숨김이면 Rect.Empty.</summary>
    public static Rect VisiblePanel { get; set; } = Rect.Empty;

    /// <summary>독이 있는 모니터의 장치 이름 (VisiblePanel 의 DIP 기준). 모르면 "".</summary>
    public static string Monitor { get; set; } = "";
}
