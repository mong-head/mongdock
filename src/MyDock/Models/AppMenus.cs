namespace MyDock.Models;

/// <summary>상단바 앱 메뉴 하나 (예: "파일"). 제목이 "@app" 이면 상단바 제목 대신 앱 이름(굵게) 메뉴 맨 위에 붙는다.</summary>
public sealed class AppMenuDef
{
    public string Title { get; set; } = "";
    public List<AppMenuItemDef> Items { get; set; } = new();
}

/// <summary>앱 메뉴 항목. Keys 가 있으면 그 단축키를 앱에 보냄. Text 가 "-" 이면 구분선.</summary>
public sealed class AppMenuItemDef
{
    public string Text { get; set; } = "";
    /// <summary>예: "Ctrl+Shift+T", "F5", "Alt+Left", 연속 입력 "Ctrl+K Ctrl+S"(공백으로 구분, 최대 4개). 메뉴 오른쪽에 회색으로도 표시.</summary>
    public string? Keys { get; set; }
    /// <summary>하위 메뉴 (선택).</summary>
    public List<AppMenuItemDef>? Items { get; set; }
}
