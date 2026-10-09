using System.IO;
using System.Text.RegularExpressions;

namespace Mongdock.Services;

/// <summary>
/// "문제 신고하기"로 보내는 진단 정보에서 개인정보를 가린다 (보내기 전 미리 보기에도 가린 결과를 보여 줌).
/// 가리는 것: 이메일 주소, URL(iCal 구독 주소 등), 사용자 폴더 경로의 사용자 이름(C:\Users\&lt;이름&gt;),
/// 사용자 이름·기기 이름, 지금 열린 창 제목. 로그는 추가로 작은따옴표 안 문구(창 제목·핀 이름이 들어가는 자리)도 가린다.
/// WPF 의존성 없음 — tools/report-test 가 이 파일만 링크해서 시험한다.
/// </summary>
public static class ReportRedactor
{
    public const string UrlMark = "<주소>";
    public const string EmailMark = "<이메일>";
    public const string UserMark = "<사용자>";
    public const string MachineMark = "<기기>";
    public const string TitleMark = "<창 제목>";
    public const string QuotedMark = "<가림>";

    /// <summary>가릴 이름들. 창 제목은 3자 이상만 (짧은 제목은 흔한 낱말과 겹쳐 진단 내용을 망가뜨림).</summary>
    public sealed record Context(string? UserName, string? MachineName, string? UserProfile, IReadOnlyCollection<string> WindowTitles)
    {
        public static Context Current(IEnumerable<string> windowTitles) => new(
            Environment.UserName,
            Environment.MachineName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            windowTitles.ToList());
    }

    private static readonly Regex Url = new(
        @"(?i)\b(?:https?|webcals?|wss?|ftp|file)://[^\s'""<>]+|\bwww\.[^\s'""<>]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Email = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // C:\Users\이름, C:\\Users\\이름 (JSON 이스케이프), /Users/이름 → 이름만 가림. 이름 칸: 경로 구분자·공백·따옴표 전까지
    private static readonly Regex UsersFolder = new(
        @"(?i)(\\\\Users\\\\|\\Users\\|/Users/)[^\\/\s'""<>:|?*]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Quoted = new(
        @"'[^'\r\n]{1,300}'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>진단 정보(설정 요약·모니터 등)용.</summary>
    public static string Redact(string text, Context ctx)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 창 제목: 긴 것부터 (짧은 제목이 긴 제목의 일부를 먼저 바꾸지 않게)
        foreach (string title in ctx.WindowTitles
                     .Select(t => t?.Trim() ?? "")
                     .Where(t => t.Length >= 3)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(t => t.Length))
        {
            text = text.Replace(title, TitleMark, StringComparison.OrdinalIgnoreCase);
        }

        text = Url.Replace(text, UrlMark);
        text = Email.Replace(text, EmailMark);

        if (!string.IsNullOrEmpty(ctx.UserProfile) && ctx.UserProfile.Length > 3)
        {
            string parent = Path.GetDirectoryName(ctx.UserProfile) ?? "";
            if (parent.Length > 0)
                text = text.Replace(ctx.UserProfile, Path.Combine(parent, UserMark), StringComparison.OrdinalIgnoreCase);
        }
        text = UsersFolder.Replace(text, m => m.Groups[1].Value + UserMark);

        text = ReplaceToken(text, ctx.MachineName, MachineMark);
        text = ReplaceToken(text, ctx.UserName, UserMark);
        return text;
    }

    /// <summary>로그용: <see cref="Redact"/> + 작은따옴표 안 문구('창 제목', '핀 이름')도 가림.</summary>
    public static string RedactLog(string text, Context ctx)
    {
        text = Redact(text, ctx);
        return Quoted.Replace(text, "'" + QuotedMark + "'");
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
