using System.Text.RegularExpressions;

namespace Mongdock.Services;

/// <summary>
/// "문제 신고하기"로 보내는 진단 정보에서 개인정보를 가린다 (보내기 전 미리 보기에도 가린 결과를 보여 줌).
/// 가리는 것: 이메일 주소, URL(iCal 구독 주소 등), 파일·폴더 경로(드라이브 문자·UNC 로 시작 → &lt;경로&gt;.확장자),
/// 사용자 폴더 이름(/Users/&lt;이름&gt;), 사용자 이름·기기 이름.
/// 로그는 추가로 지금 열린 창 제목과 작은따옴표 안 문구(창 제목·핀 이름·메뉴 이름 자리)도 가린다.
/// 순서가 중요하다: URL·이메일 → 경로·사용자 → 창 제목·기기 → 따옴표. (창 제목을 먼저 바꾸면 URL·경로 안에 &lt; 가 생겨 뒤 규칙이 거기서 멈춤)
/// WPF 의존성 없음 — tools/report-test 가 시험한다.
/// </summary>
public static class ReportRedactor
{
    public const string UrlMark = "<주소>";
    public const string EmailMark = "<이메일>";
    public const string PathMark = "<경로>";
    public const string UserMark = "<사용자>";
    public const string MachineMark = "<기기>";
    public const string TitleMark = "<창 제목>";
    public const string QuotedMark = "<가림>";
    public const string IdMark = "<번호>";

    /// <summary>가릴 이름들. 창 제목은 로그에만, 3자 이상만 (짧은 제목은 흔한 낱말과 겹쳐 진단 내용을 망가뜨림).</summary>
    public sealed record Context(string? UserName, string? MachineName, IReadOnlyCollection<string> WindowTitles)
    {
        public static Context Current(IEnumerable<string> windowTitles) =>
            new(Environment.UserName, Environment.MachineName, windowTitles.ToList());
    }

    private static readonly Regex Url = new(
        @"(?i)\b(?:https?|webcals?|wss?|ftp|file)://[^\s'""<>]+|\bwww\.[^\s'""<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Email = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 경로: C:\… C:/… C:\\… (JSON 이스케이프), \\서버\공유\… 로 시작해 따옴표·괄호·쉼표·콜론·" → "·줄 끝 전까지 (공백 포함 — 폴더 이름에 공백이 흔함).
    // \\.\DISPLAY1 같은 장치 이름은 제외 (서버 이름 첫 글자가 영숫자).
    private static readonly Regex FilePath = new(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\[A-Za-z0-9_\-][^\\\s]*\\)(?:(?!\s+→)[^\r\n""'<>|*?,;:()\[\]])*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // PC 를 오래 알아볼 수 있는 값: GUID(앱·장치·설치 식별자), 12자리 넘는 긴 숫자(트레이 아이콘 "윈도우 ID" 등)
    private static readonly Regex GuidPattern = new(
        @"(?i)\{?\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b\}?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LongNumber = new(@"(?<![\w.])\d{12,}(?![\w.])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Extension = new(@"\.([A-Za-z0-9]{1,8})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 드라이브 없는 사용자 폴더 (/Users/이름, \Users\이름) — 이름 칸: 경로 구분자·따옴표 전까지
    private static readonly Regex UsersFolder = new(
        @"(?i)(\\\\Users\\\\|\\Users\\|/Users/)[^\\/\r\n'""<>:|?*]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 줄 안 첫 ' 부터 마지막 ' 까지 통째로 (이름 속 아포스트로피 'Melon's AirPods' 도, 길이 제한 없이)
    private static readonly Regex Quoted = new(@"'[^\r\n]*'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>진단 정보 앞부분(시스템 정보·설정 요약)용. 창 제목은 적용하지 않음 ("Windows" 같은 흔한 제목이 진단 줄을 망침).</summary>
    public static string Redact(string text, Context ctx)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Common(text, ctx);
        return ReplaceToken(text, ctx.MachineName, MachineMark);
    }

    /// <summary>로그용: <see cref="Redact"/> + 지금 열린 창 제목 + 작은따옴표 안 문구.</summary>
    public static string RedactLog(string text, Context ctx)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Common(text, ctx);

        // 창 제목: 긴 것부터 (짧은 제목이 긴 제목의 일부를 먼저 바꾸지 않게)
        foreach (string title in ctx.WindowTitles
                     .Select(t => t?.Trim() ?? "")
                     .Where(t => t.Length >= 3)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(t => t.Length))
        {
            // 낱말 단위로만: claude.exe, Claude_pzs8sxrjxfjjc!Claude 처럼 exe·AUMID 안의 같은 글자는 그대로 (제목 "Claude")
            text = Regex.Replace(text, @"(?<![\w.!\\])" + Regex.Escape(title) + @"(?![\w.!\\])", TitleMark,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        text = ReplaceToken(text, ctx.MachineName, MachineMark);
        return Quoted.Replace(text, "'" + QuotedMark + "'");
    }

    /// <summary>URL·이메일 → 경로 → 사용자 폴더·사용자 이름.</summary>
    private static string Common(string text, Context ctx)
    {
        text = Url.Replace(text, UrlMark);
        text = Email.Replace(text, EmailMark);
        text = FilePath.Replace(text, m =>
        {
            string path = m.Value.TrimEnd();
            var ext = Extension.Match(path);
            string mark = ext.Success ? PathMark + "." + ext.Groups[1].Value.ToLowerInvariant() : PathMark;
            return mark + m.Value[path.Length..]; // 잘라 낸 뒤쪽 공백은 그대로
        });
        text = UsersFolder.Replace(text, m => m.Groups[1].Value + UserMark);
        text = GuidPattern.Replace(text, IdMark);
        text = LongNumber.Replace(text, IdMark);
        return ReplaceToken(text, ctx.UserName, UserMark);
    }

    /// <summary>낱말 단위로만 바꿈 (영숫자에 붙어 있으면 다른 낱말의 일부). 3자 미만 이름은 흔한 글자와 겹쳐 건너뜀.</summary>
    private static string ReplaceToken(string text, string? token, string mark)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 3) return text;
        var re = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(token) + @"(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return re.Replace(text, mark);
    }
}
