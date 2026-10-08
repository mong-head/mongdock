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
    private static AppMenuItemDef Sub(string text, params AppMenuItemDef[] items) => new() { Text = text, Items = items.ToList() };

    /// <summary>
    /// 앱 이름(굵게) 메뉴에 덧붙일 항목을 담는 특별한 메뉴 제목. 상단바 제목으로는 나오지 않는다.
    /// settings.json 의 appMenus 에서도 같은 제목으로 쓸 수 있다.
    /// </summary>
    public const string AppNameMenuTitle = "@app";

    /// <summary>
    /// 앱 전용 내장 메뉴가 있는 종류인지. 전용 메뉴는 UI 자동화 메뉴 막대보다 먼저 쓴다
    /// (electron·generic 은 범용 대체 메뉴라 UI 자동화 쪽이 우선).
    /// </summary>
    public static bool IsAppSpecific(string kind) => kind is not ("electron" or "generic");

    /// <summary>exe 파일 이름(소문자)·창 클래스 → 내장 메뉴 종류. 전용 메뉴가 없으면 null.</summary>
    public static string? KindOf(string exe, string windowClass) => exe switch
    {
        "chrome.exe" or "msedge.exe" or "whale.exe" => exe,
        "firefox.exe" => "firefox.exe",
        "explorer.exe" when windowClass == "CabinetWClass" => "explorer",
        "notion.exe" => "notion",
        "code.exe" or "code - insiders.exe" or "cursor.exe" => "vscode",
        "kakaotalk.exe" => "kakaotalk",
        "discord.exe" or "discordptb.exe" or "discordcanary.exe" => "discord",
        _ => null,
    };

    /// <summary>kind: chrome.exe / msedge.exe / whale.exe / firefox.exe / explorer / notion / vscode / kakaotalk / discord / electron / generic</summary>
    public static List<AppMenuDef> For(string kind) => kind switch
    {
        "chrome.exe" or "msedge.exe" or "whale.exe" => Chromium(kind),
        "firefox.exe" => Firefox(),
        "explorer" => Explorer(),
        "notion" => Notion(),
        "vscode" => VsCode(),
        "kakaotalk" => KakaoTalk(),
        "discord" => Discord(),
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

    /// <summary>
    /// 파일 탐색기 — Microsoft "Windows 바로 가기 키" 문서의 파일 탐색기 항목
    /// (https://support.microsoft.com/windows/keyboard-shortcuts-in-windows-dcc61a57-8ff0-cffe-9796-cb9706c75eec).
    /// "숨긴 항목 보기" 는 단축키가 없어 넣지 않음.
    /// </summary>
    private static List<AppMenuDef> Explorer() => new()
    {
        M("파일",
            I("새 창", "Ctrl+N"), I("새 탭", "Ctrl+T"), I("새 폴더", "Ctrl+Shift+N"),
            Sep, I("속성", "Alt+Enter"),
            Sep, I("탭 닫기", "Ctrl+W")),
        M("편집",
            I("실행 취소", "Ctrl+Z"), I("다시 실행", "Ctrl+Y"),
            Sep, I("잘라내기", "Ctrl+X"), I("복사", "Ctrl+C"), I("붙여넣기", "Ctrl+V"), I("모두 선택", "Ctrl+A"),
            Sep, I("이름 바꾸기", "F2"), I("삭제", "Delete")),
        M("보기",
            I("새로 고침", "F5"),
            Sep, I("미리 보기 창", "Alt+P"), I("세부 정보 창", "Alt+Shift+P"),
            Sep, I("전체 화면", "F11")),
        M("이동",
            I("뒤로", "Alt+Left"), I("앞으로", "Alt+Right"), I("상위 폴더", "Alt+Up"),
            Sep, I("주소 표시줄", "Alt+D"), I("검색", "Ctrl+E")),
    };

    /// <summary>
    /// VS Code (Cursor·Insiders 도 같은 메뉴). 단축키는 VS Code 기본 키 바인딩(Windows):
    /// https://code.visualstudio.com/shortcuts/keyboard-shortcuts-windows.pdf ,
    /// https://code.visualstudio.com/docs/reference/default-keybindings , 터미널·디버그 문서.
    /// "Ctrl+K Ctrl+O" 처럼 공백으로 나눈 것은 연속 입력(코드 단축키).
    /// </summary>
    private static List<AppMenuDef> VsCode() => new()
    {
        M(AppNameMenuTitle,
            I("설정…", "Ctrl+,"), I("바로 가기 키", "Ctrl+K Ctrl+S")),
        M("파일",
            I("새 텍스트 파일", "Ctrl+N"), I("새 창", "Ctrl+Shift+N"),
            Sep, I("파일 열기…", "Ctrl+O"), I("폴더 열기…", "Ctrl+K Ctrl+O"),
            Sep, I("저장", "Ctrl+S"), I("다른 이름으로 저장…", "Ctrl+Shift+S"), I("모두 저장", "Ctrl+K S"),
            Sep, I("닫은 편집기 다시 열기", "Ctrl+Shift+T"),
            Sep, I("편집기 닫기", "Ctrl+F4"), I("폴더 닫기", "Ctrl+K F"), I("창 닫기", "Alt+F4")),
        M("편집",
            I("실행 취소", "Ctrl+Z"), I("다시 실행", "Ctrl+Y"),
            Sep, I("잘라내기", "Ctrl+X"), I("복사", "Ctrl+C"), I("붙여넣기", "Ctrl+V"),
            Sep, I("찾기", "Ctrl+F"), I("바꾸기", "Ctrl+H"),
            Sep, I("파일에서 찾기", "Ctrl+Shift+F"), I("파일에서 바꾸기", "Ctrl+Shift+H"),
            Sep, I("줄 주석 설정/해제", "Ctrl+/"), I("블록 주석 설정/해제", "Shift+Alt+A")),
        M("선택",
            I("모두 선택", "Ctrl+A"), I("선택 영역 확장", "Shift+Alt+Right"), I("선택 영역 축소", "Shift+Alt+Left"),
            Sep, I("위에 줄 복사", "Shift+Alt+Up"), I("아래에 줄 복사", "Shift+Alt+Down"),
            I("줄 위로 이동", "Alt+Up"), I("줄 아래로 이동", "Alt+Down"),
            Sep, I("위에 커서 추가", "Ctrl+Alt+Up"), I("아래에 커서 추가", "Ctrl+Alt+Down"), I("줄 끝에 커서 추가", "Shift+Alt+I"),
            I("다음 항목 추가", "Ctrl+D"), I("모든 항목 선택", "Ctrl+Shift+L")),
        M("보기",
            I("명령 팔레트…", "Ctrl+Shift+P"),
            Sep, I("탐색기", "Ctrl+Shift+E"), I("검색", "Ctrl+Shift+F"), I("소스 제어", "Ctrl+Shift+G"),
            I("실행 및 디버그", "Ctrl+Shift+D"), I("확장", "Ctrl+Shift+X"),
            Sep, I("문제", "Ctrl+Shift+M"), I("출력", "Ctrl+Shift+U"), I("디버그 콘솔", "Ctrl+Shift+Y"), I("터미널", "Ctrl+`"),
            Sep, I("기본 사이드바 표시", "Ctrl+B"), I("패널 표시", "Ctrl+J"),
            Sep, I("자동 줄 바꿈", "Alt+Z"), I("전체 화면", "F11"), I("Zen 모드", "Ctrl+K Z"),
            Sep, Sub("확대/축소", I("확대", "Ctrl+="), I("축소", "Ctrl+-"), I("확대/축소 다시 설정", "Ctrl+NumPad0"))),
        M("이동",
            I("뒤로", "Alt+Left"), I("앞으로", "Alt+Right"),
            Sep, I("파일로 이동…", "Ctrl+P"), I("작업 영역의 기호로 이동…", "Ctrl+T"), I("기호로 이동…", "Ctrl+Shift+O"),
            I("줄로 이동…", "Ctrl+G"), I("대괄호로 이동", "Ctrl+Shift+\\"),
            Sep, I("정의로 이동", "F12"), I("구현으로 이동", "Ctrl+F12"), I("참조로 이동", "Shift+F12"),
            Sep, I("다음 문제", "F8"), I("이전 문제", "Shift+F8")),
        M("실행",
            I("디버깅 시작", "F5"), I("디버깅 없이 실행", "Ctrl+F5"), I("디버깅 중지", "Shift+F5"), I("디버깅 다시 시작", "Ctrl+Shift+F5"),
            Sep, I("프로시저 단위 실행", "F10"), I("한 단계씩 코드 실행", "F11"), I("프로시저 나가기", "Shift+F11"),
            Sep, I("중단점 설정/해제", "F9")),
        M("터미널",
            I("새 터미널", "Ctrl+Shift+`"), I("터미널 분할", "Ctrl+Shift+5"),
            Sep, I("빌드 작업 실행…", "Ctrl+Shift+B")),
        M("도움말",
            I("명령 팔레트", "Ctrl+Shift+P"), I("키보드 단축키 참조", "Ctrl+K Ctrl+R")),
    };

    /// <summary>
    /// 카카오톡 PC — 카카오 고객센터 "Windows 버전 단축키가 궁금해요." (https://cs.kakao.com/helps_html/1073209659?locale=ko).
    /// Ctrl+A 가 "친구 추가" 라 일반 편집 메뉴는 넣지 않음. 채팅방 검색 단축키는 공식 목록에 없어 제외.
    /// </summary>
    private static List<AppMenuDef> KakaoTalk() => new()
    {
        M(AppNameMenuTitle,
            I("잠금 모드", "Ctrl+L"), I("로그아웃", "Alt+N")),
        M("파일",
            I("새로운 채팅", "Ctrl+N"), I("친구 추가", "Ctrl+A"),
            Sep, I("메인 창 열기", "Ctrl+Shift+M"), I("톡캘린더 열기", "Ctrl+D"), I("서랍 > 파일", "Ctrl+J"),
            Sep, I("모든 창 닫기", "Ctrl+Shift+W")),
        M("채팅방",
            I("초대하기", "Ctrl+I"), I("파일 보내기", "Ctrl+T"), I("이모티콘", "Ctrl+E"), I("캡처하기", "Ctrl+Shift+C"),
            Sep, I("대화 내용 내보내기", "Ctrl+S"), I("알림 끄기/켜기", "Ctrl+Shift+B"), I("카나나 검색 열기", "Ctrl+K"),
            Sep, I("이전 채팅방", "Ctrl+PageUp"), I("다음 채팅방", "Ctrl+PageDown"),
            Sep, I("채팅방 닫기", "Esc")),
    };

    /// <summary>
    /// Discord — 공식 "Discord Commands, Shortcuts, and Navigation Guide"
    /// (https://support.discord.com/hc/en-us/articles/31232432266647).
    /// </summary>
    private static List<AppMenuDef> Discord() => new()
    {
        M(AppNameMenuTitle,
            I("사용자 설정…", "Ctrl+,"), I("키보드 단축키", "Ctrl+/")),
        M("서버",
            I("빠른 전환…", "Ctrl+K"), I("서버 만들기/참가", "Ctrl+Shift+N"),
            Sep, I("이전 서버", "Ctrl+Alt+Left"), I("다음 서버", "Ctrl+Alt+Right"),
            I("이전 채널", "Alt+Up"), I("다음 채널", "Alt+Down"), I("뒤로", "Alt+Left")),
        M("편집",
            I("실행 취소", "Ctrl+Z"),
            Sep, I("잘라내기", "Ctrl+X"), I("복사", "Ctrl+C"), I("붙여넣기", "Ctrl+V"), I("모두 선택", "Ctrl+A"),
            Sep, I("채널에서 검색", "Ctrl+F"), I("모든 채널에서 검색", "Ctrl+Shift+F")),
        M("메시지",
            I("이모지", "Ctrl+E"), I("GIF", "Ctrl+G"), I("파일 업로드", "Ctrl+Shift+U"),
            Sep, I("고정 메시지", "Ctrl+P"), I("멤버 목록", "Ctrl+U")),
        M("음성",
            I("마이크 음소거/해제", "Ctrl+Shift+M"), I("헤드셋 음소거/해제", "Ctrl+Shift+D")),
        M("도움말",
            I("도움말", "Ctrl+Shift+H"), I("키보드 단축키", "Ctrl+/")),
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
