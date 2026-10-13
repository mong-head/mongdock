namespace Mongdock.ViewModels;

/// <summary>
/// 몽독 공통 글자 크기 (DIP) — 설정 창·알림 배너·신고·루틴 편집·앱 모음 판이 같은 값을 씀 (2026-10-13 사용자: 판 글씨가 다른 화면보다 큼).
/// 설정 창이 실제로 쓰던 값 그대로: 본문 13, 보조 11.5, 구역 제목 12(굵게), 버튼 12.5, 창 제목 20.
/// </summary>
public static class UiText
{
    public const double Body = 13;
    public const double Sub = 11.5;
    public const double Section = 12;
    public const double Button = 12.5;
    public const double Title = 20;
    /// <summary>카드·폴더 이름 같은 작은 제목.</summary>
    public const double CardTitle = 15;
}
