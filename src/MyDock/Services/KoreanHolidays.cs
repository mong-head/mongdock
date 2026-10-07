using System.Collections.Concurrent;
using System.Globalization;

namespace MyDock.Services;

/// <summary>
/// 한국 법정 공휴일(관공서의 공휴일에 관한 규정) 계산. 외부 데이터 없이 연도별로 계산해 캐시.
/// - 양력: 신정, 삼일절, 어린이날, 현충일, 광복절, 개천절, 한글날(2013~), 성탄절
/// - 음력: 설날(음 12/말일·1/1·1/2), 부처님오신날(음 4/8), 추석(음 8/14·15·16) — KoreanLunisolarCalendar 로 변환
/// - 대체공휴일(현행 규정):
///   설날·추석 연휴가 일요일 또는 다른 공휴일과 겹치면 연휴 다음 첫 비공휴일 (2014~)
///   어린이날이 토·일 또는 다른 공휴일과 겹치면 다음 첫 비공휴일 (2014~)
///   삼일절·광복절·개천절·한글날 (2021~), 부처님오신날·성탄절 (2023~) 도 같은 방식
///   신정·현충일은 대체 없음
/// 선거일·임시공휴일은 미리 알 수 없어 포함하지 않는다.
/// </summary>
public static class KoreanHolidays
{
    public const string SubstituteName = "대체공휴일";

    /// <summary>대체공휴일 규칙 종류.</summary>
    private enum Rule
    {
        /// <summary>대체 없음 (신정·현충일).</summary>
        None,
        /// <summary>설날·추석: 일요일 또는 다른 공휴일과 겹치면 대체.</summary>
        LunarBreak,
        /// <summary>국경일·어린이날·부처님오신날·성탄절: 토·일 또는 다른 공휴일과 겹치면 대체.</summary>
        Weekend,
    }

    private readonly record struct Base(DateTime Date, string Name, Rule Rule);

    private static readonly ConcurrentDictionary<int, IReadOnlyDictionary<DateTime, string>> Cache = new();
    private static readonly KoreanLunisolarCalendar Lunar = new();

    /// <summary>해당 날짜의 공휴일 이름 (여러 개면 " · " 로 연결). 공휴일이 아니면 null. 일요일 자체는 공휴일로 치지 않는다.</summary>
    public static string? NameOf(DateTime date)
        => ForYear(date.Year).TryGetValue(date.Date, out var name) ? name : null;

    /// <summary>한 해의 공휴일 (날짜 → 이름). 대체공휴일 포함.</summary>
    public static IReadOnlyDictionary<DateTime, string> ForYear(int year)
        => Cache.GetOrAdd(year, Compute);

    /// <summary>해당 월의 공휴일 목록 (날짜순).</summary>
    public static IReadOnlyList<KeyValuePair<DateTime, string>> ForMonth(int year, int month)
        => ForYear(year).Where(kv => kv.Key.Month == month).OrderBy(kv => kv.Key).ToList();

    private static IReadOnlyDictionary<DateTime, string> Compute(int year)
    {
        var list = new List<Base>();
        void Solar(int m, int d, string name, Rule rule) => list.Add(new Base(new DateTime(year, m, d), name, rule));

        bool weekendFrom2021 = year >= 2021;
        bool weekendFrom2023 = year >= 2023;
        bool from2014 = year >= 2014;

        Solar(1, 1, "신정", Rule.None);
        Solar(3, 1, "삼일절", weekendFrom2021 ? Rule.Weekend : Rule.None);
        Solar(5, 5, "어린이날", from2014 ? Rule.Weekend : Rule.None);
        Solar(6, 6, "현충일", Rule.None);
        Solar(8, 15, "광복절", weekendFrom2021 ? Rule.Weekend : Rule.None);
        Solar(10, 3, "개천절", weekendFrom2021 ? Rule.Weekend : Rule.None);
        if (year >= 2013) Solar(10, 9, "한글날", weekendFrom2021 ? Rule.Weekend : Rule.None);
        Solar(12, 25, "성탄절", weekendFrom2023 ? Rule.Weekend : Rule.None);

        // 음력 공휴일 (지원 범위 밖 연도는 양력만)
        var seollal = FromLunar(year, 1, 1);
        if (seollal is DateTime s)
        {
            // 음 12월 말일 = 설날 전날 (12월이 29일/30일인지 따질 필요 없음)
            var rule = from2014 ? Rule.LunarBreak : Rule.None;
            list.Add(new Base(s.AddDays(-1), "설날", rule));
            list.Add(new Base(s, "설날", rule));
            list.Add(new Base(s.AddDays(1), "설날", rule));
        }
        if (FromLunar(year, 4, 8) is DateTime buddha)
            list.Add(new Base(buddha, "부처님오신날", weekendFrom2023 ? Rule.Weekend : Rule.None));
        if (FromLunar(year, 8, 15) is DateTime c)
        {
            var rule = from2014 ? Rule.LunarBreak : Rule.None;
            list.Add(new Base(c.AddDays(-1), "추석", rule));
            list.Add(new Base(c, "추석", rule));
            list.Add(new Base(c.AddDays(1), "추석", rule));
        }

        var result = new Dictionary<DateTime, string>();
        foreach (var g in list.GroupBy(b => b.Date))
            result[g.Key] = string.Join(" · ", g.Select(b => b.Name).Distinct());

        // 대체공휴일: 날짜별로 "겹친 횟수"만큼 그 날 이후 첫 비공휴일을 하루씩 지정 (날짜순 처리)
        foreach (var g in list.GroupBy(b => b.Date).OrderBy(g => g.Key))
        {
            var day = g.Key;
            var items = g.ToList();
            bool anyWeekendRule = items.Any(b => b.Rule == Rule.Weekend);
            bool anyRule = items.Any(b => b.Rule != Rule.None);
            int count = 0;
            // 다른 공휴일과 겹침 (대체 규칙이 있는 공휴일이 끼어 있을 때만)
            if (anyRule) count += items.Count - 1;
            if (day.DayOfWeek == DayOfWeek.Sunday && anyRule) count++;
            else if (day.DayOfWeek == DayOfWeek.Saturday && anyWeekendRule) count++;

            var cursor = day;
            for (int i = 0; i < count; i++)
            {
                do cursor = cursor.AddDays(1);
                while (result.ContainsKey(cursor) || cursor.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
                if (cursor.Year != year) break; // 연말 넘어가면 다음 해 계산에 맡기지 않고 생략 (성탄절이 최대 12/28 이라 실제로는 없음)
                result[cursor] = SubstituteName;
            }
        }
        return result;
    }

    /// <summary>음력(평달) year/month/day → 양력. 윤달이 있는 해는 윤달 뒤의 달 인덱스를 1 올려 보정.</summary>
    private static DateTime? FromLunar(int year, int month, int day)
    {
        try
        {
            if (year < Lunar.MinSupportedDateTime.Year + 1 || year > Lunar.MaxSupportedDateTime.Year - 1) return null;
            int leap = Lunar.GetLeapMonth(year); // 0 = 윤달 없음, 그 외 = 윤달의 월 인덱스(예: 윤4월이면 5)
            int index = leap > 0 && month >= leap ? month + 1 : month;
            return Lunar.ToDateTime(year, index, day, 0, 0, 0, 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
