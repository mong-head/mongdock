using MyDock.Models;

namespace MyDock.Services;

/// <summary>
/// 내장 기본 앱 메뉴 (한국어). settings.json 의 appMenus 와 같은 형식이라 사용자가 같은 키로 덮어쓸 수 있다.
/// 단축키는 각 앱의 공식 Windows 단축키 중 확실한 것만.
/// </summary>
internal static class BuiltinMenus
{
    private static AppMenuItemDef I(string text, string? keys = null) => new() { Text = text, Keys = keys };
    private static AppMenuItemDef Sep => new() { Text = "-" };
    private static AppMenuDef M(string title, params AppMenuItemDef[] items) => new() { Title = title, Items = items.ToList() };

    /// <summary>kind: chrome.exe / msedge.exe / whale.exe / firefox.exe / explorer / notion / electron / generic</summary>
    public static List<AppMenuDef> For(string kind) => kind switch
    {
        "chrome.exe" or "msedge.exe" or "whale.exe" => Chromium(kind),
        "firefox.exe" => Firefox(),
        "explorer" => Explorer(),
        "notion" => Notion(),
        "electron" => Electron(),
        _ => Generic(),
    };

    private static List<AppMenuDef> Chromium(string exe)
    {
        string privateWindow = exe == "msedge.exe" ? "새 InPrivate 창" : "새 시크릿 창";
        return new()
        {
            M("파일",
                I("새 탭", "Ctrl+T"), I("새 창", "Ctrl+N"), I(privateWindow, "Ctrl+Shift+N"), I("닫은 탭 다시 열기", "Ctrl+Shift+T"),
                Sep, I("탭 닫기", "Ctrl+W"), I("창 닫기", "Ctrl+Shift+W"),
                Sep, I("인쇄…", "Ctrl+P")),
            M("편집",
                I("실행 취소", "Ctrl+Z"), I("다시 실행", "Ctrl+Y"),
                Sep, I("잘라내기", "Ctrl+X"), I("복사", "Ctrl+C"), I("붙여넣기", "Ctrl+V"), I("모두 선택", "Ctrl+A"),
                Sep, I("찾기…", "Ctrl+F")),
            M("보기",
                I("새로고침", "Ctrl+R"), I("강력 새로고침", "Ctrl+Shift+R"),
                Sep, I("확대", "Ctrl+="), I("축소", "Ctrl+-"), I("실제 크기", "Ctrl+0"),
                Sep, I("전체 화면", "F11"), I("개발자 도구", "F12")),
            M("방문 기록",
                I("뒤로", "Alt+Left"), I("앞으로", "Alt+Right"),
                Sep, I("방문 기록 보기", "Ctrl+H")),
            M("북마크",
                I("이 페이지 북마크", "Ctrl+D"), I("북마크바 표시", "Ctrl+Shift+B"), I("북마크 관리자", "Ctrl+Shift+O")),
            M("탭",
                I("다음 탭", "Ctrl+Tab"), I("이전 탭", "Ctrl+Shift+Tab"),
                Sep, I("주소창으로 이동", "Ctrl+L")),
            M("다운로드",
                I("다운로드", "Ctrl+J")),
        };
    }

    /// <summary>파이어폭스: 다시 실행 Ctrl+Shift+Z, 사생활 보호 창 Ctrl+Shift+P, 다운로드 Ctrl+Shift+Y, 라이브러리 Ctrl+Shift+O.</summary>
    private static List<AppMenuDef> Firefox() => new()
    {
        M("파일",
            I("새 탭", "Ctrl+T"), I("새 창", "Ctrl+N"), I("새 사생활 보호 창", "Ctrl+Shift+P"), I("닫은 탭 다시 열기", "Ctrl+Shift+T"),
            Sep, I("탭 닫기", "Ctrl+W"), I("창 닫기", "Ctrl+Shift+W"),
            Sep, I("인쇄…", "Ctrl+P")),
        M("편집",
            I("실행 취소", "Ctrl+Z"), I("다시 실행", "Ctrl+Shift+Z"),
            Sep, I("잘라내기", "Ctrl+X"), I("복사", "Ctrl+C"), I("붙여넣기", "Ctrl+V"), I("모두 선택", "Ctrl+A"),
            Sep, I("찾기…", "Ctrl+F")),
        M("보기",
            I("새로고침", "Ctrl+R"), I("강력 새로고침", "Ctrl+Shift+R"),
            Sep, I("확대", "Ctrl+="), I("축소", "Ctrl+-"), I("실제 크기", "Ctrl+0"),
            Sep, I("전체 화면", "F11"), I("개발자 도구", "F12")),
        M("방문 기록",
            I("뒤로", "Alt+Left"), I("앞으로", "Alt+Right"),
            Sep, I("방문 기록 보기", "Ctrl+H")),
        M("북마크",
            I("이 페이지 북마크", "Ctrl+D"), I("북마크 도구 모음 표시", "Ctrl+Shift+B"), I("북마크 관리", "Ctrl+Shift+O")),
        M("탭",
            I("다음 탭", "Ctrl+Tab"), I("이전 탭", "Ctrl+Shift+Tab"),
            Sep, I("주소창으로 이동", "Ctrl+L")),
        M("다운로드",
            I("다운로드", "Ctrl+Shift+Y")),
    };

    private static List<AppMenuDef> Explorer() => new()
    {
        M("파일",
            I("새 창", "Ctrl+N"), I("새 탭", "Ctrl+T"), I("새 폴더", "Ctrl+Shift+N"),
            Sep, I("탭 닫기", "Ctrl+W")),
        M("편집",
            I("실행 취소", "Ctrl+Z"),
            Sep, I("잘라내기", "Ctrl+X"), I("복사", "Ctrl+C"), I("붙여넣기", "Ctrl+V"), I("모두 선택", "Ctrl+A"),
            Sep, I("이름 바꾸기", "F2")),
        M("보기",
            I("새로고침", "F5")),
        M("이동",
            I("뒤로", "Alt+Left"), I("앞으로", "Alt+Right"), I("상위 폴더", "Alt+Up"),
            Sep, I("주소창", "Alt+D"), I("검색", "Ctrl+E")),
    };

    private static AppMenuDef EditMenu() => M("편집",
        I("실행 취소", "Ctrl+Z"), I("다시 실행", "Ctrl+Y"),
        Sep, I("잘라내기", "Ctrl+X"), I("복사", "Ctrl+C"), I("붙여넣기", "Ctrl+V"), I("모두 선택", "Ctrl+A"),
        Sep, I("찾기", "Ctrl+F"));

    /// <summary>Notion 데스크톱: 새 페이지 Ctrl+N, 새 창 Ctrl+Shift+N, 검색 Ctrl+P (공식 단축키).</summary>
    private static List<AppMenuDef> Notion() => new()
    {
        M("파일", I("새 페이지", "Ctrl+N"), I("새 창", "Ctrl+Shift+N"), Sep, I("검색", "Ctrl+P")),
        EditMenu(),
        M("보기", I("확대", "Ctrl+="), I("축소", "Ctrl+-"), I("실제 크기", "Ctrl+0"), Sep, I("새로고침", "Ctrl+R")),
    };

    /// <summary>Electron 앱(Claude 등): 확대/축소/새로고침은 Electron 기본 메뉴 역할 단축키.</summary>
    private static List<AppMenuDef> Electron() => new()
    {
        EditMenu(),
        M("보기", I("확대", "Ctrl+="), I("축소", "Ctrl+-"), I("실제 크기", "Ctrl+0"), Sep, I("새로고침", "Ctrl+R")),
    };

    /// <summary>알 수 없는 앱: 보기(확대/축소/실제 크기)만 — 편집 단축키는 앱마다 의미가 달라 넣지 않음.</summary>
    private static List<AppMenuDef> Generic() => new()
    {
        M("보기", I("확대", "Ctrl+="), I("축소", "Ctrl+-"), I("실제 크기", "Ctrl+0")),
    };
}
