namespace MyDock.Models;

/// <summary>상단바 앱 메뉴 하나 (예: "파일").</summary>
public sealed class AppMenuDef
{
    public string Title { get; set; } = "";
    public List<AppMenuItemDef> Items { get; set; } = new();
}

/// <summary>앱 메뉴 항목. Keys 가 있으면 그 단축키를 앱에 보냄. Text 가 "-" 이면 구분선.</summary>
public sealed class AppMenuItemDef
{
    public string Text { get; set; } = "";
    /// <summary>예: "Ctrl+Shift+T", "F5", "Alt+Left". 메뉴 오른쪽에 회색으로도 표시.</summary>
    public string? Keys { get; set; }
    /// <summary>하위 메뉴 (선택).</summary>
    public List<AppMenuItemDef>? Items { get; set; }
}
