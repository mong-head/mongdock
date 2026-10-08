namespace Mongdock.Models;

/// <summary>
/// iCal(ICS) 구독 캘린더 하나. %APPDATA%\mongdock\calendars.json 에 저장 (Url 은 DPAPI 로 암호화 — CalendarFeedService).
/// Url 은 비공개 주소일 수 있는 비밀 링크라 로그·예외 메시지에 절대 남기지 않는다 (호스트 이름만).
/// </summary>
public sealed class CalendarFeed
{
    /// <summary>고정 식별자 (캐시 파일 이름). 처음 추가할 때 만든 GUID.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>https:// 주소 (webcal:// 은 추가할 때 https:// 로 바꿔 둠). 복호화 실패 시 "".</summary>
    public string Url { get; set; } = "";
    /// <summary>"#RRGGBB" — 달력 점·일정 막대 색.</summary>
    public string Color { get; set; } = Palette[0];
    public bool Enabled { get; set; } = true;

    /// <summary>설정 창 색 점을 눌렀을 때 고르는 8색 (맥 캘린더 색).</summary>
    public static readonly string[] Palette =
    {
        "#007AFF", // 파랑
        "#34C759", // 초록
        "#FF3B30", // 빨강
        "#FF9500", // 주황
        "#FFCC00", // 노랑
        "#AF52DE", // 보라
        "#FF2D55", // 분홍
        "#A2845E", // 갈색
    };

    public static readonly string[] PaletteNames = { "파랑", "초록", "빨강", "주황", "노랑", "보라", "분홍", "갈색" };

    public CalendarFeed Clone() => new() { Id = Id, Name = Name, Url = Url, Color = Color, Enabled = Enabled };
}
