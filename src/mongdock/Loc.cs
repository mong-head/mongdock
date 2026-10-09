using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Mongdock;

/// <summary>
/// 화면 문구 번역 (#8). 한국어 원문이 곧 키 — 코드에서 <c>Loc.T("설정…")</c>, 보간 문자열은 <c>Loc.F($"{n}개 추가했어요.")</c>
/// (서식 문자열 "{0}개 추가했어요." 를 키로 찾고 인자를 끼움).
/// 사전: 임베디드 리소스 Resources/en.json { "한국어": "English" }. 사전에 없으면 한국어 그대로 (빠진 번역은 tools/i18n-check 로 찾음).
/// 언어: settings.json "language" — "" = 윈도우 표시 언어(한국어면 한국어, 그 밖은 영어), "ko", "en". 시작할 때 한 번 정함 (바꾸면 다시 시작).
/// 로그 메시지는 번역하지 않는다.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string>? _en;

    /// <summary>지금 화면 언어가 영어인지 (시작할 때 <see cref="Init"/> 로 정함).</summary>
    public static bool IsEnglish { get; private set; }

    /// <summary>날짜·시간 형식에 쓸 문화권 (한국어 = ko-KR, 영어 = 윈도우 사용자 문화권이 영어면 그것, 아니면 en-US).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>"ko" / "en" (신고 진단·Changelog 언어 고르기).</summary>
    public static string Code => IsEnglish ? "en" : "ko";

    /// <summary>
    /// 앱을 만들 때(App 생성자 — 어떤 창·정적 목록보다 먼저) settings.json 의 "language" 만 읽어 언어를 정함.
    /// 파일이 없거나 읽지 못하면 자동(윈도우 표시 언어).
    /// </summary>
    public static void InitFromSettingsFile()
    {
        string? language = null;
        try
        {
            string path = Path.Combine(AppInfo.DataDirectory, "settings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals("language", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        language = p.Value.GetString();
            }
        }
        catch { /* 손상된 설정은 SettingsService 가 따로 처리 */ }
        Init(language);
    }

    /// <summary>설정값("", "ko", "en")으로 언어를 정함.</summary>
    public static void Init(string? setting)
    {
        string s = (setting ?? "").Trim().ToLowerInvariant();
        IsEnglish = s switch
        {
            "ko" => false,
            "en" => true,
            _ => !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ko", StringComparison.OrdinalIgnoreCase),
        };
        Culture = !IsEnglish ? CultureInfo.GetCultureInfo("ko-KR")
            : CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "en" ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo("en-US");
        if (IsEnglish) _en ??= LoadEnglish();
    }

    /// <summary>문구 번역 (영어 사전에 있으면 영어, 없으면 원문).</summary>
    public static string T(string korean)
    {
        if (!IsEnglish || string.IsNullOrEmpty(korean) || _en is null) return korean;
        return _en.TryGetValue(korean, out var en) && en.Length > 0 ? en : korean;
    }

    /// <summary>보간 문자열 번역: 서식("{0}개 추가했어요.")을 키로 번역한 뒤 인자를 끼움.</summary>
    public static string F(FormattableString text)
    {
        string format = T(text.Format);
        try { return string.Format(Culture, format, text.GetArguments()); }
        catch (FormatException) { return text.ToString(Culture); } // 번역 서식이 틀리면 원문
    }

    private static Dictionary<string, string> LoadEnglish()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("mongdock.i18n.en.json");
            if (stream is null) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"영어 사전 읽기 실패: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    // ── 날짜·시간 (한국어 어순 / 영어는 문화권 형식) ──

    /// <summary>"10월 9일 목요일" / "Thursday, October 9".</summary>
    public static string DateLong(DateTime d) =>
        IsEnglish ? d.ToString("dddd, MMMM d", Culture) : d.ToString("M월 d일 dddd", Culture);

    /// <summary>"2026년 10월" / "October 2026".</summary>
    public static string MonthYear(DateTime d) =>
        IsEnglish ? d.ToString("MMMM yyyy", Culture) : d.ToString("yyyy년 M월", Culture);

    /// <summary>"2026년 10월 8일" / "October 8, 2026".</summary>
    public static string DateFull(DateTime d) =>
        IsEnglish ? d.ToString("MMMM d, yyyy", Culture) : d.ToString("yyyy년 M월 d일", Culture);

    /// <summary>"10월 9일" / "Oct 9".</summary>
    public static string MonthDay(DateTime d) =>
        IsEnglish ? d.ToString("MMM d", Culture) : d.ToString("M월 d일", Culture);

    /// <summary>"오후 7:50" / "7:50 PM" (영어는 윈도우 시간 형식).</summary>
    public static string ShortTime(DateTime d) =>
        IsEnglish ? d.ToString("t", Culture) : d.ToString("tt h:mm", Culture);

    /// <summary>달력 요일 머리 (일~토 / S M T W T F S).</summary>
    public static string[] WeekdayLetters =>
        IsEnglish ? new[] { "S", "M", "T", "W", "T", "F", "S" } : new[] { "일", "월", "화", "수", "목", "금", "토" };

    /// <summary>시험 도구용: 사전 전체 (tools/i18n-check).</summary>
    public static IReadOnlyDictionary<string, string> EnglishTable => _en ??= LoadEnglish();
}
