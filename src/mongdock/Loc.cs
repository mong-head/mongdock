using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Mongdock;

/// <summary>
/// 화면 문구 번역 (#8, 다국어 #23). 한국어 원문이 곧 키 — 코드에서 <c>Loc.T("설정…")</c>, 보간 문자열은 <c>Loc.F($"{n}개 추가했어요.")</c>
/// (서식 문자열 "{0}개 추가했어요." 를 키로 찾고 인자를 끼움).
/// 사전: 임베디드 리소스 i18n/&lt;코드&gt;.json { "한국어": "번역" }. 찾는 순서: 그 언어 → 영어 → 한국어 원문 (빠진 번역은 tools/i18n/extract.py).
/// 언어: settings.json "language" — "" = 윈도우 표시 언어(지원 언어면 그 언어, 아니면 영어), 또는 지원 코드. 모르는 코드는 자동. 시작할 때 한 번 정함 (바꾸면 다시 시작).
/// 로그 메시지는 번역하지 않는다.
/// </summary>
public static class Loc
{
    /// <summary>지원 언어 (설정 목록 순서). 이름은 그 언어로.</summary>
    public static readonly IReadOnlyList<(string Code, string NativeName)> Languages = new[]
    {
        ("ko", "한국어"), ("en", "English"), ("ja", "日本語"), ("zh-Hans", "简体中文"), ("zh-Hant", "繁體中文"),
        ("de", "Deutsch"), ("fr", "Français"), ("es", "Español"),
    };

    private static Dictionary<string, string>? _table;   // 지금 언어 (한국어면 null)
    private static Dictionary<string, string>? _english; // 대체용 (영어·한국어가 아니면)
    private static Dictionary<string, string>? _reverse; // 번역 → 한국어 원문 (SourceOf)

    /// <summary>지금 화면 언어 코드: ko, en, ja, zh-Hans, zh-Hant, de, fr, es.</summary>
    public static string Code { get; private set; } = "ko";

    public static bool IsKorean => Code == "ko";

    /// <summary>영어인지 (영어 전용 어순·문구에만 — 그 밖의 "한국어가 아님" 은 !IsKorean).</summary>
    public static bool IsEnglish => Code == "en";

    /// <summary>날짜·시간 형식에 쓸 문화권: 그 언어의 윈도우 사용자 문화권이면 그것(지역 형식 존중), 아니면 그 언어의 대표 문화권.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>그 언어 이름 (그 언어로). 모르면 코드.</summary>
    public static string NativeName(string code) => Languages.FirstOrDefault(l => l.Code == code).NativeName ?? code;

    /// <summary>윈도우 표시 언어로 고른 코드 (자동일 때 쓸 언어).</summary>
    public static string SystemCode => FromCulture(CultureInfo.CurrentUICulture) ?? "en";

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

    /// <summary>설정값("" = 자동, 또는 지원 코드)으로 언어를 정함. 모르는 코드는 자동.</summary>
    public static void Init(string? setting)
    {
        string s = (setting ?? "").Trim();
        Code = Normalize(s) ?? SystemCode;
        Culture = CultureFor(Code);
        _table = Code == "ko" ? null : Load(Code);
        _english = Code is "ko" or "en" ? null : Load("en");
        _reverse = null;
    }

    /// <summary>설정 값 → 지원 코드 (대소문자·zh-TW 같은 지역 이름 허용). 빈 값·모르는 값은 null.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var (code, _) in Languages)
            if (string.Equals(code, value.Trim(), StringComparison.OrdinalIgnoreCase)) return code;
        try { return FromCulture(CultureInfo.GetCultureInfo(value.Trim())); }
        catch (CultureNotFoundException) { return null; }
    }

    /// <summary>문화권 → 지원 코드. zh-TW/HK/MO·Hant → 번체, 그 밖 zh → 간체. 지원하지 않으면 null.</summary>
    private static string? FromCulture(CultureInfo c)
    {
        string two = c.TwoLetterISOLanguageName.ToLowerInvariant();
        if (two == "zh")
        {
            string name = c.Name;
            bool hant = name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase);
            return hant ? "zh-Hant" : "zh-Hans";
        }
        foreach (var (code, _) in Languages)
            if (code == two) return code;
        return null;
    }

    private static CultureInfo CultureFor(string code)
    {
        var current = CultureInfo.CurrentCulture;
        if (FromCulture(current) == code) return current; // 지역 형식(날짜 순서·24시간) 존중
        return CultureInfo.GetCultureInfo(code switch
        {
            "ko" => "ko-KR",
            "ja" => "ja-JP",
            "zh-Hans" => "zh-CN",
            "zh-Hant" => "zh-TW",
            "de" => "de-DE",
            "fr" => "fr-FR",
            "es" => "es-ES",
            _ => "en-US",
        });
    }

    /// <summary>문구 번역: 그 언어 사전 → 영어 사전 → 한국어 원문.</summary>
    public static string T(string korean)
    {
        if (string.IsNullOrEmpty(korean) || _table is null) return korean;
        if (_table.TryGetValue(korean, out var t) && t.Length > 0) return t;
        if (_english is not null && _english.TryGetValue(korean, out var en) && en.Length > 0) return en;
        return korean;
    }

    /// <summary>보간 문자열 번역: 서식("{0}개 추가했어요.")을 키로 번역한 뒤 인자를 끼움.</summary>
    public static string F(FormattableString text)
    {
        string format = T(text.Format);
        try { return string.Format(Culture, format, text.GetArguments()); }
        catch (FormatException) { return text.ToString(Culture); } // 번역 서식이 틀리면 원문
    }

    /// <summary>번역된 문구 → 한국어 원문 (모르면 null). 문구에 따라 화면을 바꿀 때 (예: 둘러보기 "눌러 보세요" 판단).</summary>
    public static string? SourceOf(string translated)
    {
        if (_table is null) return translated;
        if (_reverse is null)
        {
            _reverse = new Dictionary<string, string>();
            foreach (var d in new[] { _english, _table })
                if (d is not null)
                    foreach (var (k, v) in d)
                        if (v.Length > 0) _reverse[v] = k;
        }
        return _reverse.TryGetValue(translated, out var k2) ? k2 : null;
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"mongdock.i18n.{code}.json");
            if (stream is null) return new Dictionary<string, string>();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            Services.Log.Warn($"{code} 사전 읽기 실패: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    // ── 날짜·시간 (언어별 어순, 이름은 문화권) ──

    private static bool Cjk => Code is "ja" or "zh-Hans" or "zh-Hant";

    /// <summary>"10월 9일 목요일" / "Thursday, October 9" / "10月9日 木曜日" / "Donnerstag, 9. Oktober".</summary>
    public static string DateLong(DateTime d) => d.ToString(Code switch
    {
        "ko" => "M월 d일 dddd",
        "en" => "dddd, MMMM d",
        "ja" or "zh-Hans" or "zh-Hant" => "M月d日 dddd",
        "de" => "dddd, d. MMMM",
        "es" => "dddd, d 'de' MMMM",
        _ => "dddd d MMMM",
    }, Culture);

    /// <summary>"2026년 10월" / "October 2026" / "2026年10月".</summary>
    public static string MonthYear(DateTime d) => d.ToString(Code switch
    {
        "ko" => "yyyy년 M월",
        "ja" or "zh-Hans" or "zh-Hant" => "yyyy年M月",
        "es" => "MMMM 'de' yyyy",
        _ => "MMMM yyyy",
    }, Culture);

    /// <summary>"2026년 10월 8일" / "October 8, 2026" / "2026年10月8日" / "8. Oktober 2026".</summary>
    public static string DateFull(DateTime d) => d.ToString(Code switch
    {
        "ko" => "yyyy년 M월 d일",
        "en" => "MMMM d, yyyy",
        "ja" or "zh-Hans" or "zh-Hant" => "yyyy年M月d日",
        "de" => "d. MMMM yyyy",
        "es" => "d 'de' MMMM 'de' yyyy",
        _ => "d MMMM yyyy",
    }, Culture);

    /// <summary>"10월 9일" / "Oct 9" / "10月9日" / "9. Okt.".</summary>
    public static string MonthDay(DateTime d) => d.ToString(Code switch
    {
        "ko" => "M월 d일",
        "en" => "MMM d",
        "ja" or "zh-Hans" or "zh-Hant" => "M月d日",
        "de" => "d. MMM",
        _ => "d MMM",
    }, Culture);

    /// <summary>"오후 7:50" / 그 밖은 문화권의 짧은 시간 형식.</summary>
    public static string ShortTime(DateTime d) => IsKorean ? d.ToString("tt h:mm", Culture) : d.ToString("t", Culture);

    /// <summary>상단바 시계 앞 날짜 서식 (뒤에 시각이 붙음).</summary>
    public static string ClockDatePrefix => Code switch
    {
        "ko" => "M월 d일 (ddd) ",
        "en" => "ddd MMM d  ",
        "ja" or "zh-Hans" or "zh-Hant" => "M月d日 (ddd) ",
        "de" => "ddd d. MMM  ",
        _ => "ddd d MMM  ",
    };

    /// <summary>오전/오후 표시가 시각 앞인지 (한국어·일본어·중국어 어순).</summary>
    public static bool AmPmFirst => IsKorean || Cjk;

    /// <summary>달력 요일 머리 (일~토).</summary>
    public static string[] WeekdayLetters => Code switch
    {
        "ko" => new[] { "일", "월", "화", "수", "목", "금", "토" },
        "en" => new[] { "S", "M", "T", "W", "T", "F", "S" },
        _ => Culture.DateTimeFormat.ShortestDayNames,
    };

    /// <summary>시험 도구용: 지금 언어 사전 (한국어면 비어 있음).</summary>
    public static IReadOnlyDictionary<string, string> Table => _table ?? new Dictionary<string, string>();
}
