using System.Windows;

namespace Mongdock.Views;

/// <summary>
/// 상단바 아래로 뜨는 카드(StatusPanelWindow)를 화면 안에 맞추는 계산 (UI 요소 없이 숫자만 — 검증하기 쉽게 분리).
/// 모든 값은 패널이 뜨는 모니터 기준 DIP.
/// </summary>
internal static class PanelFit
{
    /// <summary>카드 위 여백 (상단바 아래 ~ 카드 위).</summary>
    public const double TopGap = 6;
    /// <summary>카드 아래 ~ 작업 영역 아래 여백.</summary>
    public const double BottomGap = 12;
    /// <summary>아무리 낮은 화면이어도 이보다 작게는 줄이지 않음 (그 이하는 그냥 스크롤).</summary>
    public const double MinCardHeight = 160;

    /// <summary>
    /// 카드 최대 높이 = 작업 영역 아래 - 아래 여백 - 카드 위쪽.
    /// <paramref name="cardTop"/> = 상단바 아래 + <see cref="TopGap"/>.
    /// </summary>
    public static double MaxCardHeight(Rect workArea, double cardTop)
    {
        if (workArea.IsEmpty || double.IsNaN(cardTop)) return double.PositiveInfinity;
        return Math.Max(MinCardHeight, Math.Floor(workArea.Bottom - BottomGap - cardTop));
    }

    /// <summary>
    /// 달력 카드가 넘칠 때 줄이는 순서: ① 알림 목록 최대 높이(최소 <paramref name="notifMin"/>)
    /// ② 일정 목록 최대 높이(최소 <paramref name="eventsMin"/>). 그래도 남는 초과분은 카드 전체 스크롤이 맡는다.
    /// </summary>
    /// <param name="spare">카드 안 내용에 쓸 수 있는 높이에서 줄일 수 없는 부분(달력 격자·정보 줄 등)을 뺀 값.</param>
    /// <param name="notifHeight">알림 목록의 원래 높이 (기본 최대 높이로 잰 값).</param>
    /// <param name="eventsHeight">일정 목록의 원래 높이 (기본 최소~최대 사이로 맞춘 값).</param>
    /// <returns>알림 목록 최대 높이, 일정 목록 최대 높이, 그래도 넘치는 높이(0 이면 전체 스크롤 불필요).</returns>
    public static (double NotifMax, double EventsMax, double Overflow) ShrinkCalendar(
        double spare, double notifHeight, double eventsHeight,
        double notifDefaultMax, double notifMin, double eventsDefaultMax, double eventsMin)
    {
        double notifMax = notifDefaultMax;
        double eventsMax = eventsDefaultMax;
        double over = notifHeight + eventsHeight - spare;
        if (double.IsNaN(over) || over <= 0.5) return (notifMax, eventsMax, 0);

        if (notifHeight > notifMin)
        {
            double cut = Math.Min(over, notifHeight - notifMin);
            notifMax = Math.Floor(notifHeight - cut);
            over -= cut;
        }
        if (over > 0.5 && eventsHeight > eventsMin)
        {
            double cut = Math.Min(over, eventsHeight - eventsMin);
            eventsMax = Math.Floor(eventsHeight - cut);
            over -= cut;
        }
        return (notifMax, eventsMax, Math.Max(0, over));
    }
}
