using MyDock.Models;

namespace MyDock.Services;

/// <summary>
/// 코드에 남겨 둔 범용 대체 메뉴(Electron·기본). 앱 전용 메뉴(브라우저·탐색기·VS Code·카카오톡·디스코드·노션 등)는
/// 저장소의 menus/app-menus.json 으로 옮겼다 (<see cref="MenuRules"/>, 내장 리소스 + GitHub 에서 하루 한 번 갱신).
/// 범용 메뉴는 규칙 파일이 깨지거나 비어도 항상 있어야 하므로 코드에 둔다.
/// </summary>
internal static class BuiltinMenus
{
    private static AppMenuItemDef I(string text, string? keys = null) => new() { Text = text, Keys = keys };
    private static AppMenuItemDef Sep => new() { Text = "-" };
    private static AppMenuDef M(string title, params AppMenuItemDef[] items) => new() { Title = title, Items = items.ToList() };

    /// <summary>
    /// 앱 이름(굵게) 메뉴에 덧붙일 항목을 담는 특별한 메뉴 제목. 상단바 제목으로는 나오지 않는다.
    /// settings.json 의 appMenus 에서도 같은 제목으로 쓸 수 있다 (규칙 파일에서는 "appMenu").
    /// </summary>
    public const string AppNameMenuTitle = "@app";

    /// <summary>kind: "electron" / 그 밖(= generic).</summary>
    public static List<AppMenuDef> For(string kind) => kind == "electron" ? Electron() : Generic();

    /// <summary>Electron 앱(Claude 등): 확대/축소/새로고침은 Electron 기본 메뉴 역할 단축키.</summary>
    private static List<AppMenuDef> Electron() => new()
    {
        M("편집",
            I("실행 취소", "Ctrl+Z"), I("다시 실행", "Ctrl+Y"),
            Sep, I("잘라내기", "Ctrl+X"), I("복사", "Ctrl+C"), I("붙여넣기", "Ctrl+V"), I("모두 선택", "Ctrl+A"),
            Sep, I("찾기", "Ctrl+F")),
        M("보기", I("확대", "Ctrl+="), I("축소", "Ctrl+-"), I("실제 크기", "Ctrl+0"), Sep, I("새로고침", "Ctrl+R")),
    };

    /// <summary>알 수 없는 앱: 보기(확대/축소/실제 크기)만 — 편집 단축키는 앱마다 의미가 달라 넣지 않음.</summary>
    private static List<AppMenuDef> Generic() => new()
    {
        M("보기", I("확대", "Ctrl+="), I("축소", "Ctrl+-"), I("실제 크기", "Ctrl+0")),
    };
}
