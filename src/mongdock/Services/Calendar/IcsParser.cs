using System.Globalization;
using System.Text;

namespace Mongdock.Services.Calendar;

/// <summary>
/// ICS 의 날짜/시각 값 하나.
/// - DateOnly: VALUE=DATE (종일). Value 는 그 날짜 0시.
/// - Zone == null 이고 DateOnly 가 아니면 "떠 있는 시각"(floating) — 로컬 시각으로 본다.
/// - Zone == TimeZoneInfo.Utc 면 'Z' 로 끝난 UTC.
/// Value 는 항상 Kind=Unspecified (그 시간대의 벽시계 시각).
/// </summary>
internal readonly record struct IcsTime(DateTime Value, bool DateOnly, TimeZoneInfo? Zone)
{
    /// <summary>로컬 시각 (종일·floating 이면 값 그대로). 서머타임으로 없는 시각이면 1시간 뒤로.</summary>
    public DateTime ToLocal() => DateOnly || Zone == null ? Value : IcsZones.ToLocal(Value, Zone);

    /// <summary>비교용 UTC 순간 (종일·floating 은 로컬 기준).</summary>
    public DateTime ToUtc() => IcsZones.ToUtc(Value, DateOnly || Zone == null ? TimeZoneInfo.Local : Zone);
}

/// <summary>VEVENT 하나 (필요한 속성만).</summary>
internal sealed class IcsEvent
{
    public string Uid = "";
    public string Summary = "";
    public string? Location;
    public bool Cancelled;
    public int Sequence;
    public IcsTime? Start;
    public IcsTime? End;
    public TimeSpan? Duration;
    /// <summary>RRULE 원문 (예 "FREQ=WEEKLY;BYDAY=MO,WE").</summary>
    public string? RRule;
    public readonly List<IcsTime> ExDates = new();
    public readonly List<IcsTime> RDates = new();
    public IcsTime? RecurrenceId;
}

internal sealed class IcsCalendar
{
    /// <summary>X-WR-CALNAME (구글·아웃룩이 넣어 줌). 없으면 null.</summary>
    public string? Name;
    public readonly List<IcsEvent> Events = new();
}

/// <summary>
/// RFC 5545 iCalendar 의 VEVENT 만 읽는 작은 파서 (외부 라이브러리 없이).
/// - 줄 이어붙이기(CRLF/LF + 공백·탭), 속성 매개변수(따옴표 안의 ':' ';' 포함), 텍스트 이스케이프(\n \, \; \\)
/// - DTSTART/DTEND/DURATION/EXDATE/RDATE/RECURRENCE-ID: DATE, UTC 'Z', TZID(윈도우·IANA 이름, 실패하면 VTIMEZONE 고정 오프셋, 그래도 안 되면 로컬)
/// - 알 수 없는 속성·컴포넌트(VALARM, VTODO 등)는 무시. 깨진 줄은 건너뜀 (예외 없이 최대한 읽음).
/// 구글(PRODID:-//Google Inc//Google Calendar), 아웃룩(Microsoft Exchange Server / TZID:Korea Standard Time),
/// 애플·네이버웍스 형태를 기준으로 맞춤.
/// </summary>
internal static class IcsParser
{
    public static IcsCalendar Parse(string text)
    {
        var cal = new IcsCalendar();
        var zones = new IcsZones();
        var lines = Unfold(text);

        // 1차: VTIMEZONE 만 모아 둠 (TZID 를 해석할 때 고정 오프셋 대체용 — VEVENT 보다 뒤에 나올 수도 있음)
        CollectTimeZones(lines, zones);

        IcsEvent? ev = null;
        int depth = 0; // VEVENT 안의 하위 컴포넌트(VALARM) 깊이
        foreach (var line in lines)
        {
            if (!TrySplit(line, out var name, out var prms, out var value)) continue;
            if (name == "BEGIN")
            {
                if (ev != null) depth++;
                else if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) { ev = new IcsEvent(); depth = 0; }
                continue;
            }
            if (name == "END")
            {
                if (ev == null) continue;
                if (depth > 0) { depth--; continue; }
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (ev.Start != null) cal.Events.Add(ev);
                    ev = null;
                }
                continue;
            }
            if (ev == null)
            {
                if (name == "X-WR-CALNAME" && cal.Name == null)
                {
                    string n = Unescape(value).Trim();
                    if (n.Length > 0) cal.Name = n;
                }
                continue;
            }
            if (depth > 0) continue; // VALARM 의 속성 무시

            try
            {
                switch (name)
                {
                    case "UID": ev.Uid = value.Trim(); break;
                    case "SUMMARY": ev.Summary = Unescape(value).Trim(); break;
                    case "LOCATION":
                        string loc = Unescape(value).Trim();
                        ev.Location = loc.Length > 0 ? loc : null;
                        break;
                    case "STATUS": ev.Cancelled = value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
                    case "SEQUENCE": int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ev.Sequence); break;
                    case "DTSTART": ev.Start = ParseTime(value, prms, zones); break;
                    case "DTEND": ev.End = ParseTime(value, prms, zones); break;
                    case "DURATION": ev.Duration = ParseDuration(value); break;
                    case "RRULE": if (ev.RRule == null) ev.RRule = value.Trim(); break; // 여러 개면 첫 번째만 (RFC 5545 에서 권장 안 함)
                    case "EXDATE": AddTimes(ev.ExDates, value, prms, zones); break;
                    case "RDATE":
                        if (!Param(prms, "VALUE").Equals("PERIOD", StringComparison.OrdinalIgnoreCase)) AddTimes(ev.RDates, value, prms, zones);
                        break;
                    case "RECURRENCE-ID": ev.RecurrenceId = ParseTime(value, prms, zones); break;
                }
            }
            catch (Exception)
            {
                // 값이 깨진 속성 하나만 무시 (형식 오류·범위 밖 날짜·잘못된 시간대 오프셋 등 무엇이든)
            }
        }
        return cal;
    }

    // ───────────────────────── 줄 ─────────────────────────

    /// <summary>줄 이어붙이기: 줄바꿈 뒤 공백/탭으로 시작하는 줄은 앞 줄에 (그 공백 한 글자 제거). CRLF·LF 모두.</summary>
    internal static List<string> Unfold(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool has = false;
        int i = 0;
        while (i <= text.Length)
        {
            int nl = text.IndexOf('\n', i);
            int end = nl < 0 ? text.Length : nl;
            int len = end - i;
            if (len > 0 && text[end - 1] == '\r') len--;
            string raw = text.Substring(i, len);
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                if (has) sb.Append(raw, 1, raw.Length - 1);
            }
            else
            {
                if (has && sb.Length > 0) result.Add(sb.ToString());
                sb.Clear();
                sb.Append(raw);
                has = true;
            }
            if (nl < 0) break;
            i = nl + 1;
        }
        if (has && sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>"NAME;P1=a;P2="x:y":VALUE" → 이름(대문자), 매개변수, 값. 따옴표 안의 ':' ';' 는 구분자가 아님.</summary>
    internal static bool TrySplit(string line, out string name, out Dictionary<string, string> prms, out string value)
    {
        name = "";
        value = "";
        prms = EmptyParams;
        int colon = -1;
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') quoted = !quoted;
            else if (c == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return false;
        value = line[(colon + 1)..];
        string head = line[..colon];
        var parts = SplitOutsideQuotes(head, ';');
        name = parts[0].Trim().ToUpperInvariant();
        if (parts.Count > 1)
        {
            prms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int p = 1; p < parts.Count; p++)
            {
                int eq = parts[p].IndexOf('=');
                if (eq <= 0) continue;
                string v = parts[p][(eq + 1)..].Trim();
                if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
                prms[parts[p][..eq].Trim()] = v;
            }
        }
        return name.Length > 0;
    }

    private static readonly Dictionary<string, string> EmptyParams = new(StringComparer.OrdinalIgnoreCase);

    private static List<string> SplitOutsideQuotes(string s, char sep)
    {
        var list = new List<string>();
        bool quoted = false;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') quoted = !quoted;
            else if (s[i] == sep && !quoted)
            {
                list.Add(s[start..i]);
                start = i + 1;
            }
        }
        list.Add(s[start..]);
        return list;
    }

    private static string Param(Dictionary<string, string> prms, string key) => prms.TryGetValue(key, out var v) ? v : "";

    /// <summary>TEXT 값 이스케이프 풀기: \n \N → 줄바꿈, \, \; \\ → 그 글자.</summary>
    internal static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n is 'n' or 'N' ? '\n' : n);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ───────────────────────── 날짜·시각 ─────────────────────────

    private static void AddTimes(List<IcsTime> list, string value, Dictionary<string, string> prms, IcsZones zones)
    {
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { list.Add(ParseTime(part, prms, zones)); }
            catch (Exception) { /* 깨진 값 하나만 건너뜀 */ }
        }
    }

    /// <summary>"20261009" / "20261009T140000" / "20261009T050000Z" + TZID·VALUE 매개변수.</summary>
    internal static IcsTime ParseTime(string value, Dictionary<string, string> prms, IcsZones zones)
    {
        value = value.Trim();
        bool dateOnly = Param(prms, "VALUE").Equals("DATE", StringComparison.OrdinalIgnoreCase) || value.Length == 8;
        if (dateOnly)
        {
            if (!DateTime.TryParseExact(value.Length >= 8 ? value[..8] : value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                throw new FormatException("bad date");
            return new IcsTime(d, true, null);
        }
        bool utc = value.EndsWith('Z') || value.EndsWith('z');
        string core = utc ? value[..^1] : value;
        // 초가 없는 변형(일부 생성기)도 허용
        if (!DateTime.TryParseExact(core, new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            throw new FormatException("bad date-time");
        t = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
        if (utc) return new IcsTime(t, false, TimeZoneInfo.Utc);
        string tzid = Param(prms, "TZID");
        return new IcsTime(t, false, tzid.Length > 0 ? zones.Resolve(tzid) : null);
    }

    /// <summary>일정 길이 상한 — 이보다 긴 DURATION 은 깨진 값으로 봄 (날짜 계산 넘침 방지).</summary>
    private const double MaxDurationSeconds = 3660 * 86400.0; // 약 10년

    /// <summary>ISO 8601 기간 "P1D" "PT1H30M" "P1W" "-PT15M". 깨졌거나 10년을 넘으면 null.</summary>
    internal static TimeSpan? ParseDuration(string value)
    {
        value = value.Trim().ToUpperInvariant();
        if (value.Length < 2) return null;
        int sign = 1;
        int i = 0;
        if (value[0] is '+' or '-')
        {
            if (value[0] == '-') sign = -1;
            i++;
        }
        if (i >= value.Length || value[i] != 'P') return null;
        i++;
        double total = 0; // 초
        bool inTime = false;
        int num = -1;
        for (; i < value.Length; i++)
        {
            char c = value[i];
            if (c == 'T') { inTime = true; continue; }
            if (char.IsAsciiDigit(c))
            {
                if (num > 100_000_000) return null; // 자릿수가 너무 많음 (int 넘침 방지)
                num = (num < 0 ? 0 : num * 10) + (c - '0');
                continue;
            }
            if (num < 0) return null;
            total += c switch
            {
                'W' => num * 7 * 86400.0,
                'D' => num * 86400.0,
                'H' when inTime => num * 3600.0,
                'M' when inTime => num * 60.0,
                'S' when inTime => num,
                _ => double.NaN,
            };
            if (double.IsNaN(total) || total > MaxDurationSeconds) return null;
            num = -1;
        }
        return TimeSpan.FromSeconds(sign * total);
    }

    // ───────────────────────── VTIMEZONE ─────────────────────────

    /// <summary>
    /// VTIMEZONE 에서 TZID 와 STANDARD/DAYLIGHT 의 TZOFFSETTO 를 모아 둔다.
    /// 시스템이 모르는 TZID(예 아웃룩의 "(UTC+09:00) Seoul" 처럼 표시 이름만 있는 경우)를 고정 오프셋으로 대신할 때 씀 —
    /// 서머타임(DAYLIGHT)이 있는 시간대는 고정 오프셋이 반년은 틀리므로 쓰지 않는다.
    /// </summary>
    private static void CollectTimeZones(List<string> lines, IcsZones zones)
    {
        string? tzid = null;
        TimeSpan? standard = null;
        bool hasDaylight = false;
        string? sub = null;
        bool inside = false;
        foreach (var line in lines)
        {
            if (!TrySplit(line, out var name, out _, out var value)) continue;
            string v = value.Trim().ToUpperInvariant();
            if (name == "BEGIN" && v == "VTIMEZONE") { inside = true; tzid = null; standard = null; hasDaylight = false; continue; }
            if (!inside) continue;
            if (name == "END" && v == "VTIMEZONE")
            {
                inside = false;
                if (tzid != null) zones.AddDefinition(tzid, hasDaylight ? null : standard);
                continue;
            }
            if (name == "BEGIN") { sub = v; if (v == "DAYLIGHT") hasDaylight = true; continue; }
            if (name == "END") { sub = null; continue; }
            if (name == "TZID" && sub == null) tzid = value.Trim().Trim('"');
            else if (name == "TZOFFSETTO" && sub == "STANDARD" && TryOffset(value.Trim(), out var off)) standard = off;
        }
    }

    /// <summary>"+0900" / "-0430" / "+090000". 시간대로 쓸 수 없는 값(±14시간 초과, 분 60 이상)은 false.</summary>
    private static bool TryOffset(string s, out TimeSpan offset)
    {
        offset = default;
        if (s.Length < 5 || s[0] is not ('+' or '-')) return false;
        if (!int.TryParse(s.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int h)) return false;
        if (!int.TryParse(s.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int m)) return false;
        if (m >= 60 || h > 14 || h == 14 && m > 0) return false; // TimeZoneInfo 가 받는 범위 (±14:00)
        offset = new TimeSpan(h, m, 0);
        if (s[0] == '-') offset = -offset;
        return true;
    }
}

/// <summary>
/// TZID → TimeZoneInfo. 순서: 시스템(윈도우 ID 또는 IANA — .NET 8 은 ICU 로 IANA 도 찾음) →
/// "/mozilla.org/.../Europe/Berlin" 같은 접두 제거 → 윈도우 표시 이름("(UTC+09:00) 서울") 일치 →
/// 같은 파일의 VTIMEZONE 이 서머타임 없는 고정 오프셋이면 그것 → 모르면 null(로컬로 취급).
/// </summary>
internal sealed class IcsZones
{
    private readonly Dictionary<string, TimeZoneInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeSpan?> _definitions = new(StringComparer.OrdinalIgnoreCase);

    public void AddDefinition(string tzid, TimeSpan? fixedOffset) => _definitions[tzid] = fixedOffset;

    public TimeZoneInfo? Resolve(string tzid)
    {
        tzid = tzid.Trim().Trim('"');
        if (_cache.TryGetValue(tzid, out var z)) return z;
        z = Find(tzid);
        if (z == null && _definitions.TryGetValue(tzid, out var off) && off is TimeSpan o)
        {
            try { z = TimeZoneInfo.CreateCustomTimeZone("ics:" + tzid, o, tzid, tzid); }
            catch (ArgumentException) { z = null; } // 쓸 수 없는 오프셋 → 로컬로 취급
        }
        _cache[tzid] = z;
        return z;
    }

    private static TimeZoneInfo? Find(string id)
    {
        if (id.Length == 0) return null;
        if (TryFind(id) is { } z) return z;
        // "/mozilla.org/20050126_1/America/New_York", "/citadel.org/20190101_1/Europe/Berlin" → 뒤에서 2·3 조각
        if (id.Contains('/'))
        {
            var seg = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int n = Math.Min(3, seg.Length); n >= 2; n--)
                if (TryFind(string.Join('/', seg[^n..])) is { } z2) return z2;
        }
        // 아웃룩 일부 버전: TZID 가 윈도우 표시 이름 그대로 ("(UTC+09:00) Seoul")
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            if (string.Equals(tz.DisplayName, id, StringComparison.OrdinalIgnoreCase) || string.Equals(tz.StandardName, id, StringComparison.OrdinalIgnoreCase))
                return tz;
        return null;
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) return TimeZoneInfo.FindSystemTimeZoneById(win);
        }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }
        return null;
    }

    /// <summary>zone 의 벽시계 시각 → 로컬 벽시계 시각 (Kind=Unspecified).</summary>
    public static DateTime ToLocal(DateTime wall, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(wall, zone), TimeZoneInfo.Local);
        return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
    }

    /// <summary>zone 의 벽시계 시각 → UTC. 서머타임으로 건너뛴(없는) 시각이면 1시간 뒤로.</summary>
    public static DateTime ToUtc(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone == TimeZoneInfo.Utc) return DateTime.SpecifyKind(wall, DateTimeKind.Utc);
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        try { return TimeZoneInfo.ConvertTimeToUtc(wall, zone); }
        catch (ArgumentException)
        {
            // 범위 끝 날짜(0001-01-01 등)에서 넘치면 그대로 — 호출 쪽(펼치기)이 그 일정만 건너뜀
            return DateTime.SpecifyKind(wall - zone.BaseUtcOffset, DateTimeKind.Utc);
        }
    }
}
