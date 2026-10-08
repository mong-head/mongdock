using System.IO;
using Mongdock.Services;

namespace Mongdock.ViewModels;

/// <summary>Spotlight 검색 대상 앱 (shell:AppsFolder 항목).</summary>
public sealed record SpotlightApp(string ParsingName, string Name);

/// <summary>
/// Spotlight 검색어 매칭. 대소문자 무시, 순위 = 이름 시작 일치(0) &gt; 단어 시작 일치(1) &gt; 포함(2).
/// 한글 초성 검색 지원: 검색어의 자음(ㄱ~ㅎ)은 그 초성을 가진 음절과 일치 (예 "ㅋㅋㅇㅌ" → 카카오톡, "카ㅋ" 도 가능).
/// 같은 순위 안에서는 최근 실행한 앱 → 짧은 이름 → 가나다순.
/// </summary>
public static class SpotlightMatcher
{
    public const int MaxResults = 8;

    // 호환 자모(ㄱ U+3131 ~ ㅎ U+314E) → 초성 인덱스 (겹받침 전용 자모는 -1)
    private static readonly int[] JamoToChoseong = BuildJamoMap();

    private static int[] BuildJamoMap()
    {
        const string choseong = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
        var map = new int[0x314E - 0x3131 + 1];
        Array.Fill(map, -1);
        for (int i = 0; i < choseong.Length; i++) map[choseong[i] - 0x3131] = i;
        return map;
    }

    /// <summary>검색 결과 (최대 max 개, 기본 <see cref="MaxResults"/>). 검색어가 비면 최근 실행한 앱.</summary>
    public static IReadOnlyList<SpotlightApp> Search(IReadOnlyList<SpotlightApp> apps, string query, IReadOnlyList<string> recents, int max = MaxResults)
    {
        var recentRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < recents.Count; i++) recentRank.TryAdd(recents[i], i);

        string q = query.Trim();
        if (q.Length == 0)
        {
            return apps.Where(a => recentRank.ContainsKey(a.ParsingName))
                       .GroupBy(a => a.ParsingName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                       .OrderBy(a => recentRank[a.ParsingName])
                       .Take(max).ToList();
        }

        var hits = new List<(SpotlightApp App, int Tier, int Recent)>();
        foreach (var app in apps)
        {
            int tier = Match(app.Name, q);
            if (tier < 0) continue;
            hits.Add((app, tier, recentRank.TryGetValue(app.ParsingName, out int r) ? r : int.MaxValue));
        }
        return hits.OrderBy(h => h.Tier)
                   .ThenBy(h => h.Recent)
                   .ThenBy(h => h.App.Name.Length)
                   .ThenBy(h => h.App.Name, StringComparer.CurrentCultureIgnoreCase)
                   .Select(h => h.App)
                   .Take(max).ToList();
    }

    /// <summary>순위 0(이름 시작)/1(단어 시작)/2(포함), 일치하지 않으면 -1.</summary>
    public static int Match(string name, string query)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(query)) return -1;
        int best = BestTier(name, query);
        if (best >= 0) return best;
        // 띄어쓰기 무시 ("ㅁㅁㅈ" → "메모 장" 같은 경우, "visualstudio" → "Visual Studio")
        string compactName = name.Replace(" ", "");
        string compactQuery = query.Replace(" ", "");
        if (compactQuery.Length == 0) return -1;
        best = BestTier(compactName, compactQuery);
        return best < 0 ? -1 : Math.Max(best, 1);
    }

    private static int BestTier(string name, string q)
    {
        int best = -1;
        for (int start = 0; start + q.Length <= name.Length; start++)
        {
            if (!MatchAt(name, q, start)) continue;
            int tier = start == 0 ? 0 : IsWordStart(name, start) ? 1 : 2;
            if (best < 0 || tier < best) best = tier;
            if (best == 0) break;
        }
        return best;
    }

    private static bool MatchAt(string name, string q, int start)
    {
        for (int i = 0; i < q.Length; i++)
            if (!CharMatches(name[start + i], q[i])) return false;
        return true;
    }

    private static bool CharMatches(char n, char q)
    {
        if (n == q || char.ToLowerInvariant(n) == char.ToLowerInvariant(q)) return true;
        // 초성: 검색어 자음 vs 이름의 완성형 음절 (가 U+AC00 ~ 힣 U+D7A3, 초성 = (c - 가) / 588)
        if (q >= 0x3131 && q <= 0x314E && n >= 0xAC00 && n <= 0xD7A3)
        {
            int cho = JamoToChoseong[q - 0x3131];
            return cho >= 0 && (n - 0xAC00) / 588 == cho;
        }
        return false;
    }

    private static bool IsWordStart(string name, int i)
    {
        if (i <= 0) return true;
        char prev = name[i - 1], cur = name[i];
        if (char.IsWhiteSpace(prev) || prev is '-' or '_' or '.' or '(' or '[' or '/' or '\\' or ':' or '&' or '+') return true;
        return char.IsUpper(cur) && char.IsLower(prev); // OneDrive 의 "D"
    }
}

/// <summary>
/// Spotlight 에서 최근 실행한 앱 (파싱 이름, 최신 순 최대 20개).
/// %APPDATA%\mongdock\spotlight-recent.txt 에 한 줄씩 저장 — 실패해도 메모리 기록으로 계속 동작.
/// </summary>
public static class SpotlightRecents
{
    private const int Limit = 20;
    private static readonly string FilePath = Path.Combine(AppInfo.DataDirectory, "spotlight-recent.txt");
    private static List<string>? _items;

    public static IReadOnlyList<string> Items => Load();

    public static void Add(string parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName)) return;
        var list = Load();
        list.RemoveAll(s => s.Equals(parsingName, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, parsingName);
        if (list.Count > Limit) list.RemoveRange(Limit, list.Count - Limit);
        try
        {
            AtomicFile.WriteAllLines(FilePath, list); // 임시 파일 → 바꿔 끼움 (중간에 꺼져도 잘린 파일이 남지 않음)
        }
        catch (Exception ex)
        {
            Log.Warn($"Spotlight 최근 항목 저장 실패: {ex.Message}");
        }
    }

    private static List<string> Load()
    {
        if (_items is not null) return _items;
        _items = new List<string>();
        try
        {
            if (File.Exists(FilePath))
                _items.AddRange(File.ReadAllLines(FilePath).Select(l => l.Trim()).Where(l => l.Length > 0).Take(Limit));
        }
        catch (Exception ex)
        {
            Log.Warn($"Spotlight 최근 항목 읽기 실패: {ex.Message}");
        }
        return _items;
    }
}
