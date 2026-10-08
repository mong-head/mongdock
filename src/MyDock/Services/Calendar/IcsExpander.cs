using System.Globalization;

namespace MyDock.Services.Calendar;

/// <summary>펼친 일정 한 회차 (로컬 시각). 종일이면 Start = 그 날 0시, End = 끝 다음 날 0시(배타).</summary>
internal readonly record struct IcsOccurrence(IcsEvent Event, DateTime Start, DateTime End, bool AllDay);

/// <summary>
/// VEVENT 들을 [from, to) 로컬 범위의 회차로 펼친다.
/// - RRULE: FREQ=DAILY/WEEKLY/MONTHLY/YEARLY, INTERVAL, COUNT, UNTIL, BYDAY(1MO·-1FR 같은 순번 포함), BYMONTHDAY(음수 = 끝에서),
///   BYMONTH, BYSETPOS, WKST. 그 밖의 BYxxx(BYHOUR·BYWEEKNO·BYYEARDAY 등)는 무시, 다른 FREQ 는 첫 회차만.
/// - 회차 계산은 그 일정의 시간대 벽시계 기준(서머타임이 있어도 "매주 10시" 유지) → 로컬로 변환.
/// - EXDATE 제외, RDATE 추가, RECURRENCE-ID 가 있는 같은 UID 일정은 그 회차를 대체(취소면 그 회차 삭제), STATUS:CANCELLED 제외.
/// - COUNT 가 없으면 범위 근처까지 주기를 건너뛰고, 계산은 범위 끝에서 멈춤 (긴 반복도 가볍게). 안전 상한 있음.
/// - 한 번 펼칠 때 전체 회차·시간 상한(<see cref="ExpandBudget"/>) — 넘으면 나머지 생략 (budget.Exhausted).
/// - 일정 하나의 날짜 계산이 넘치면(거대 DURATION·이상한 UNTIL 등) 그 일정만 건너뜀 (budget.FailedEvents).
/// </summary>
internal static class IcsExpander
{
    public static List<IcsOccurrence> Expand(IReadOnlyList<IcsEvent> events, DateTime from, DateTime to, ExpandBudget? budget = null)
    {
        budget ??= ExpandBudget.ForUi();
        var result = new List<IcsOccurrence>();

        // 같은 UID + RECURRENCE-ID 가 여러 번이면 SEQUENCE 가 큰 것만. 수정된 회차는 원래 회차(순간)로 찾음
        var overrides = new Dictionary<string, Dictionary<DateTime, IcsEvent>>(StringComparer.Ordinal);
        var masters = new List<IcsEvent>();
        foreach (var ev in events)
        {
            if (ev.RecurrenceId is IcsTime rid && ev.Uid.Length > 0)
            {
                DateTime key;
                try { key = Key(rid); }
                catch (Exception ex) when (ex is ArgumentException or OverflowException)
                {
                    budget.FailedEvents++; // 범위 밖 RECURRENCE-ID → 이 수정 회차만 건너뜀
                    continue;
                }
                if (!overrides.TryGetValue(ev.Uid, out var map)) overrides[ev.Uid] = map = new Dictionary<DateTime, IcsEvent>();
                if (!map.TryGetValue(key, out var old) || old.Sequence <= ev.Sequence) map[key] = ev;
            }
            else masters.Add(ev);
        }

        foreach (var ev in masters)
        {
            if (ev.Cancelled) continue;
            if (budget.ShouldStop(result.Count)) return result;
            overrides.TryGetValue(ev.Uid, out var map);
            Guarded(result, budget, () => ExpandOne(ev, from, to, map, result, budget));
        }
        // 수정된 회차는 그 자체로 한 번 (원래 날짜와 다른 날로 옮겨졌어도 새 날짜에)
        foreach (var map in overrides.Values)
            foreach (var ev in map.Values)
            {
                if (ev.Cancelled) continue;
                if (budget.ShouldStop(result.Count)) return result;
                Guarded(result, budget, () => AddSingle(ev, ev.Start!.Value, from, to, result));
            }

        return result;
    }

    /// <summary>일정 하나 펼치기 — 날짜 계산이 넘치면 그 일정이 넣은 회차를 되돌리고 건너뜀.</summary>
    private static void Guarded(List<IcsOccurrence> result, ExpandBudget budget, Action expand)
    {
        int before = result.Count;
        try { expand(); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or InvalidOperationException)
        {
            // ArgumentOutOfRangeException 포함 (DateTime 범위 밖)
            if (result.Count > before) result.RemoveRange(before, result.Count - before);
            budget.FailedEvents++;
        }
    }

    /// <summary>EXDATE/RECURRENCE-ID 비교 키: 종일이면 날짜, 아니면 UTC 순간.</summary>
    private static DateTime Key(IcsTime t) => t.DateOnly ? t.Value.Date : t.ToUtc();

    private static bool Matches(HashSet<DateTime> keys, HashSet<DateTime> dates, IcsTime occ)
        => keys.Contains(Key(occ)) || dates.Contains(occ.Value.Date);

    private static void ExpandOne(IcsEvent ev, DateTime from, DateTime to, Dictionary<DateTime, IcsEvent>? overrides, List<IcsOccurrence> result,
        ExpandBudget budget)
    {
        var start = ev.Start!.Value;
        var length = Length(ev);

        // 제외할 회차: EXDATE + 수정된 회차의 원래 시각. 종일 EXDATE 가 시각 일정에 붙은 경우(일부 생성기)는 날짜로 비교
        var skip = new HashSet<DateTime>();
        var skipDates = new HashSet<DateTime>();
        foreach (var ex in ev.ExDates)
        {
            if (ex.DateOnly && !start.DateOnly) skipDates.Add(ex.Value.Date);
            else skip.Add(Key(ex));
        }
        if (overrides != null) foreach (var k in overrides.Keys) skip.Add(k);

        var rule = ev.RRule != null ? RRule.TryParse(ev.RRule) : null;
        if (rule == null)
        {
            if (!Matches(skip, skipDates, start)) Add(ev, start, length, from, to, result);
        }
        else
        {
            // 벽시계 기준 범위 (시간대 차이·일정 길이만큼 여유)
            var slack = TimeSpan.FromDays(2) + (length > TimeSpan.Zero ? length : TimeSpan.Zero);
            var wallFrom = from - slack;
            var wallTo = to + TimeSpan.FromDays(2);
            DateTime? until = rule.Until is IcsTime u ? UntilWall(u, start) : null;
            foreach (var wall in rule.Occurrences(start.Value, wallFrom, wallTo, until, budget))
            {
                if (budget.ShouldStop(result.Count)) return;
                var occ = start with { Value = wall };
                if (Matches(skip, skipDates, occ)) continue;
                Add(ev, occ, length, from, to, result);
            }
        }
        foreach (var r in ev.RDates)
        {
            // RDATE 가 DTSTART 와 같은 형식이 아니면(시간대 다름) 그 값 그대로
            if (budget.ShouldStop(result.Count)) return;
            if (Matches(skip, skipDates, r)) continue;
            Add(ev, r, length, from, to, result);
        }
    }

    /// <summary>UNTIL 을 DTSTART 시간대의 벽시계 시각으로 (UTC 면 변환, 날짜만이면 그 날 끝까지).</summary>
    private static DateTime UntilWall(IcsTime until, IcsTime start)
    {
        if (until.DateOnly) return until.Value.Date.AddDays(1).AddTicks(-1);
        if (until.Zone == TimeZoneInfo.Utc && !start.DateOnly)
        {
            var zone = start.Zone ?? TimeZoneInfo.Local;
            if (zone == TimeZoneInfo.Utc) return until.Value;
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(until.Value, DateTimeKind.Utc), zone), DateTimeKind.Unspecified);
        }
        if (until.Zone == TimeZoneInfo.Utc && start.DateOnly)
        {
            // 종일 반복 + UTC UNTIL (구글: UNTIL=20261031T000000Z 등) → 로컬 날짜 기준
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(until.Value, DateTimeKind.Utc), TimeZoneInfo.Local), DateTimeKind.Unspecified);
        }
        return until.Value;
    }

    /// <summary>일정 길이: DTEND - DTSTART (시간대가 달라도 순간 기준), 없으면 DURATION, 둘 다 없으면 종일 1일·시각 0.</summary>
    private static TimeSpan Length(IcsEvent ev)
    {
        var s = ev.Start!.Value;
        if (ev.End is IcsTime e)
        {
            var len = s.DateOnly && e.DateOnly ? e.Value.Date - s.Value.Date : e.ToUtc() - s.ToUtc();
            if (len >= TimeSpan.Zero) return len;
        }
        if (ev.Duration is TimeSpan d && d >= TimeSpan.Zero) return d;
        return s.DateOnly ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    private static void AddSingle(IcsEvent ev, IcsTime start, DateTime from, DateTime to, List<IcsOccurrence> result)
        => Add(ev, start, Length(ev), from, to, result);

    private static void Add(IcsEvent ev, IcsTime start, TimeSpan length, DateTime from, DateTime to, List<IcsOccurrence> result)
    {
        DateTime s, e;
        if (start.DateOnly)
        {
            s = start.Value.Date;
            int days = Math.Max(1, (int)Math.Round(length.TotalDays));
            e = s.AddDays(days);
        }
        else
        {
            s = start.ToLocal();
            // 길이는 순간 기준 → 끝도 순간으로 더한 뒤 로컬로 (서머타임 경계가 있어도 맞음)
            var su = start.ToUtc();
            e = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(su + length, TimeZoneInfo.Local), DateTimeKind.Unspecified);
        }
        bool overlaps = e > s ? s < to && e > from : s >= from && s < to;
        if (overlaps) result.Add(new IcsOccurrence(ev, s, e, start.DateOnly));
    }
}

/// <summary>
/// 한 번 펼칠 때의 상한: 결과 회차 수 + 경과 시간. 넘으면 <see cref="Exhausted"/> 가 true 가 되고 나머지는 생략.
/// 피드 하나에 하나씩 (상한은 피드당).
/// </summary>
internal sealed class ExpandBudget
{
    public const int DefaultMaxOccurrences = 5000;

    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private readonly TimeSpan _maxTime;
    private readonly int _maxOccurrences;

    public ExpandBudget(int maxOccurrences, TimeSpan maxTime)
    {
        _maxOccurrences = maxOccurrences;
        _maxTime = maxTime;
    }

    /// <summary>UI 스레드에서 바로 펼칠 때: 5000 회차, 50ms.</summary>
    public static ExpandBudget ForUi() => new(DefaultMaxOccurrences, TimeSpan.FromMilliseconds(50));

    /// <summary>백그라운드에서 미리 펼칠 때: 5000 회차, 2초.</summary>
    public static ExpandBudget ForBackground() => new(DefaultMaxOccurrences, TimeSpan.FromSeconds(2));

    /// <summary>상한에 걸려 일부를 생략함.</summary>
    public bool Exhausted { get; private set; }
    /// <summary>"회차 수" 또는 "시간" (Exhausted 일 때).</summary>
    public string? Reason { get; private set; }
    /// <summary>날짜 계산이 넘쳐 건너뛴 일정 수.</summary>
    public int FailedEvents { get; set; }
    public TimeSpan Elapsed => _sw.Elapsed;

    public bool TimeUp()
    {
        if (Exhausted) return true;
        if (_sw.Elapsed <= _maxTime) return false;
        Exhausted = true;
        Reason = $"시간 {_maxTime.TotalMilliseconds:0}ms";
        return true;
    }

    public bool ShouldStop(int resultCount)
    {
        if (Exhausted) return true;
        if (resultCount >= _maxOccurrences)
        {
            Exhausted = true;
            Reason = $"회차 {_maxOccurrences}개";
            return true;
        }
        return TimeUp();
    }
}

/// <summary>RRULE 한 줄 (지원하는 부분만).</summary>
internal sealed class RRule
{
    public enum Frequency { Daily, Weekly, Monthly, Yearly }

    public Frequency Freq;
    public int Interval = 1;
    public int? Count;
    public IcsTime? Until;
    /// <summary>(순번, 요일). 순번 0 = 모든 그 요일.</summary>
    public List<(int N, DayOfWeek Day)>? ByDay;
    public List<int>? ByMonthDay;
    public List<int>? ByMonth;
    public List<int>? BySetPos;
    public DayOfWeek WeekStart = DayOfWeek.Monday;

    public static RRule? TryParse(string text)
    {
        var r = new RRule();
        bool hasFreq = false;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim().ToUpperInvariant();
            string val = part[(eq + 1)..].Trim().ToUpperInvariant();
            switch (key)
            {
                case "FREQ":
                    switch (val)
                    {
                        case "DAILY": r.Freq = Frequency.Daily; break;
                        case "WEEKLY": r.Freq = Frequency.Weekly; break;
                        case "MONTHLY": r.Freq = Frequency.Monthly; break;
                        case "YEARLY": r.Freq = Frequency.Yearly; break;
                        default: return null; // HOURLY 등: 첫 회차만
                    }
                    hasFreq = true;
                    break;
                case "INTERVAL":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv) && iv > 0) r.Interval = iv;
                    break;
                case "COUNT":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) && c > 0) r.Count = c;
                    break;
                case "UNTIL":
                    try { r.Until = IcsParser.ParseTime(val, new Dictionary<string, string>(), new IcsZones()); }
                    catch (Exception) { /* 깨진 UNTIL 은 무시 */ }
                    break;
                case "BYDAY":
                    r.ByDay = new();
                    foreach (var d in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (d.Length < 2 || Day(d[^2..]) is not DayOfWeek wd) continue;
                        int n = 0;
                        if (d.Length > 2 && !int.TryParse(d[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) continue;
                        r.ByDay.Add((n, wd));
                    }
                    if (r.ByDay.Count == 0) r.ByDay = null;
                    break;
                case "BYMONTHDAY": r.ByMonthDay = Ints(val, -31, 31); break;
                case "BYMONTH": r.ByMonth = Ints(val, 1, 12); break;
                case "BYSETPOS": r.BySetPos = Ints(val, -366, 366); break;
                case "WKST":
                    if (Day(val) is DayOfWeek ws) r.WeekStart = ws;
                    break;
            }
        }
        return hasFreq ? r : null;
    }

    private static List<int>? Ints(string val, int min, int max)
    {
        var list = new List<int>();
        foreach (var p in val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(p, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) && n != 0 && n >= min && n <= max) list.Add(n);
        return list.Count > 0 ? list : null;
    }

    private static DayOfWeek? Day(string s) => s switch
    {
        "SU" => DayOfWeek.Sunday,
        "MO" => DayOfWeek.Monday,
        "TU" => DayOfWeek.Tuesday,
        "WE" => DayOfWeek.Wednesday,
        "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday,
        "SA" => DayOfWeek.Saturday,
        _ => null,
    };

    /// <summary>
    /// 회차 시작(벽시계)들을 시간 순으로. DTSTART 는 항상 첫 회차(COUNT 에 포함, RFC 5545).
    /// [wallFrom, wallTo] 밖은 돌려주지 않지만 COUNT 가 있으면 처음부터 셈. until 은 벽시계 기준 포함.
    /// </summary>
    public IEnumerable<DateTime> Occurrences(DateTime dtstart, DateTime wallFrom, DateTime wallTo, DateTime? until, ExpandBudget? budget = null)
    {
        int emitted = 0;
        if (until is DateTime u0 && dtstart > u0) yield break;
        emitted++;
        if (dtstart >= wallFrom && dtstart <= wallTo) yield return dtstart;
        if (Count is int c0 && emitted >= c0) yield break;

        var time = dtstart.TimeOfDay;
        long first = 0;
        // COUNT 가 없으면 범위 직전 주기까지 건너뜀
        if (Count == null && wallFrom > dtstart)
        {
            long units = Freq switch
            {
                Frequency.Daily => (long)(wallFrom.Date - dtstart.Date).TotalDays,
                Frequency.Weekly => (long)(wallFrom.Date - dtstart.Date).TotalDays / 7,
                Frequency.Monthly => (wallFrom.Year - dtstart.Year) * 12L + wallFrom.Month - dtstart.Month,
                _ => wallFrom.Year - dtstart.Year,
            };
            first = Math.Max(0, units / Interval - 1);
        }

        var weekAnchor = dtstart.Date.AddDays(-(((int)dtstart.DayOfWeek - (int)WeekStart + 7) % 7));
        var monthAnchor = new DateTime(dtstart.Year, dtstart.Month, 1);
        for (long p = first; p < first + MaxPeriodsGuard; p++)
        {
            if (budget?.TimeUp() == true) yield break; // COUNT 로 범위 앞을 세는 동안에도 시간 상한
            long step = p * Interval;
            DateTime periodStart;
            try
            {
                periodStart = Freq switch
                {
                    Frequency.Daily => dtstart.Date.AddDays(step),
                    Frequency.Weekly => weekAnchor.AddDays(step * 7),
                    Frequency.Monthly => monthAnchor.AddMonths((int)step),
                    _ => new DateTime(dtstart.Year + (int)step, 1, 1),
                };
            }
            catch (ArgumentOutOfRangeException) { yield break; }
            if (periodStart > wallTo) yield break;
            if (until is DateTime u1 && periodStart > u1) yield break;

            var days = Candidates(periodStart, dtstart);
            if (BySetPos != null && Freq != Frequency.Daily) days = SetPos(days);
            foreach (var day in days)
            {
                var occ = day + time;
                if (occ <= dtstart) continue;
                if (until is DateTime u && occ > u) yield break;
                emitted++;
                if (occ >= wallFrom && occ <= wallTo) yield return occ;
                if (Count is int c && emitted >= c) yield break;
            }
        }
    }

    private const int MaxPeriodsGuard = 20000;

    /// <summary>한 주기 안의 후보 날짜 (오름차순, 중복 없음).</summary>
    private List<DateTime> Candidates(DateTime period, DateTime dtstart)
    {
        var list = new List<DateTime>();
        switch (Freq)
        {
            case Frequency.Daily:
                if (PassesFilters(period, monthDayFilter: true, dayFilter: true)) list.Add(period);
                break;
            case Frequency.Weekly:
                if (ByDay != null)
                {
                    foreach (var (_, wd) in ByDay)
                        list.Add(period.AddDays(((int)wd - (int)WeekStart + 7) % 7));
                }
                else list.Add(period.AddDays(((int)dtstart.DayOfWeek - (int)WeekStart + 7) % 7));
                list.RemoveAll(d => ByMonth != null && !ByMonth.Contains(d.Month));
                break;
            case Frequency.Monthly:
                if (ByMonth == null || ByMonth.Contains(period.Month)) MonthDays(period, dtstart, list);
                break;
            default:
                if (ByMonth == null && ByMonthDay == null && ByDay != null)
                {
                    // 연 단위 BYDAY (예 FREQ=YEARLY;BYDAY=20MO) — 순번은 그 해 기준
                    YearByDay(period.Year, list);
                }
                else
                {
                    IEnumerable<int> months = ByMonth ?? (ByMonthDay != null ? Enumerable.Range(1, 12) : new[] { dtstart.Month });
                    foreach (int m in months.Distinct().OrderBy(x => x))
                        MonthDays(new DateTime(period.Year, m, 1), dtstart, list);
                }
                break;
        }
        list = list.Distinct().ToList();
        list.Sort();
        return list;
    }

    private bool PassesFilters(DateTime d, bool monthDayFilter, bool dayFilter)
    {
        if (ByMonth != null && !ByMonth.Contains(d.Month)) return false;
        if (monthDayFilter && ByMonthDay != null && !ByMonthDay.Any(n => MonthDay(d.Year, d.Month, n) == d.Day)) return false;
        if (dayFilter && ByDay != null && !ByDay.Any(x => x.Day == d.DayOfWeek)) return false;
        return true;
    }

    /// <summary>n(음수 = 끝에서) → 그 달의 날짜. 없으면 0.</summary>
    private static int MonthDay(int year, int month, int n)
    {
        int dim = DateTime.DaysInMonth(year, month);
        int d = n > 0 ? n : dim + n + 1;
        return d >= 1 && d <= dim ? d : 0;
    }

    private void MonthDays(DateTime month, DateTime dtstart, List<DateTime> list)
    {
        int dim = DateTime.DaysInMonth(month.Year, month.Month);
        if (ByMonthDay != null)
        {
            foreach (int n in ByMonthDay)
            {
                int d = MonthDay(month.Year, month.Month, n);
                if (d == 0) continue;
                var date = new DateTime(month.Year, month.Month, d);
                if (ByDay != null && !ByDay.Any(x => x.Day == date.DayOfWeek)) continue; // BYDAY 는 거르기만
                list.Add(date);
            }
            return;
        }
        if (ByDay != null)
        {
            foreach (var (n, wd) in ByDay)
            {
                var firstWd = month.AddDays(((int)wd - (int)month.DayOfWeek + 7) % 7);
                if (n == 0)
                {
                    for (var d = firstWd; d.Month == month.Month; d = d.AddDays(7)) list.Add(d);
                }
                else if (n > 0)
                {
                    var d = firstWd.AddDays(7 * (n - 1));
                    if (d.Month == month.Month) list.Add(d);
                }
                else
                {
                    var last = new DateTime(month.Year, month.Month, dim);
                    var lastWd = last.AddDays(-(((int)last.DayOfWeek - (int)wd + 7) % 7));
                    var d = lastWd.AddDays(7 * (n + 1));
                    if (d.Month == month.Month) list.Add(d);
                }
            }
            return;
        }
        // 기본: DTSTART 의 일. 그 달에 없는 날(31일 등)은 건너뜀 (RFC 5545)
        if (dtstart.Day <= dim) list.Add(new DateTime(month.Year, month.Month, dtstart.Day));
    }

    private void YearByDay(int year, List<DateTime> list)
    {
        var jan1 = new DateTime(year, 1, 1);
        var dec31 = new DateTime(year, 12, 31);
        foreach (var (n, wd) in ByDay!)
        {
            var firstWd = jan1.AddDays(((int)wd - (int)jan1.DayOfWeek + 7) % 7);
            if (n == 0)
            {
                for (var d = firstWd; d.Year == year; d = d.AddDays(7)) list.Add(d);
            }
            else if (n > 0)
            {
                var d = firstWd.AddDays(7 * (n - 1));
                if (d.Year == year) list.Add(d);
            }
            else
            {
                var lastWd = dec31.AddDays(-(((int)dec31.DayOfWeek - (int)wd + 7) % 7));
                var d = lastWd.AddDays(7 * (n + 1));
                if (d.Year == year) list.Add(d);
            }
        }
    }

    /// <summary>BYSETPOS: 주기 안 후보 중 n번째(음수 = 끝에서)만.</summary>
    private List<DateTime> SetPos(List<DateTime> days)
    {
        var picked = new List<DateTime>();
        foreach (int n in BySetPos!)
        {
            int i = n > 0 ? n - 1 : days.Count + n;
            if (i >= 0 && i < days.Count) picked.Add(days[i]);
        }
        picked = picked.Distinct().ToList();
        picked.Sort();
        return picked;
    }
}
