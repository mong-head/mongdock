using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>앱 모음 판에 보이는 폴더 하나.</summary>
internal sealed record AppFolder(string Id, string Name, List<AppEntry> Apps, bool Touched, bool Custom);

/// <summary>
/// 앱 모음 판 폴더 (#24, 2026-10-10 재기획):
/// - 자동 폴더 = 쓰는 앱만: 분류가 같은 앱 중 최근 30일 안에 실행한 앱 + 독 핀, 최대 9개, 2개 이상일 때만 폴더.
///   처음 채울 때만 많이 쓴 순. 그 뒤 하루 한 번(판이 닫혀 있을 때) 새로 쓰기 시작한 앱만 끝에 붙임 — 이미 있는 앱의 자리는 절대 안 바뀜.
/// - 사용자가 손댄 폴더(이름·넣기·빼기)와 새로 만든 폴더는 Apps 목록 그대로 (자동 변경 없음). 앱은 한 폴더에만.
/// - 안 쓰는 앱은 "모든 앱"에만.
/// </summary>
internal static class AppFolders
{
    public const int AutoMax = 9, UsedDays = 30;

    private static bool IsCustom(string id) => id.StartsWith("g-", StringComparison.Ordinal);

    /// <summary>폴더 정의 (없으면 만듦 — 기본 폴더는 손대기 전엔 설정에 없을 수 있음).</summary>
    private static AppGroupDef Def(AllAppsSettings s, string id)
    {
        var d = s.Groups.FirstOrDefault(g => g.Id == id);
        if (d is null) s.Groups.Add(d = new AppGroupDef { Id = id });
        return d;
    }

    /// <summary>화면에 보일 폴더들 (순서대로). 숨긴 앱·지운 폴더 제외.</summary>
    public static List<AppFolder> Visible(Settings settings, IReadOnlyList<AppEntry> apps)
    {
        var s = settings.AllApps;
        MigrateOverrides(settings, apps);
        var byKey = apps.ToDictionary(a => a.Key, StringComparer.OrdinalIgnoreCase);
        var hidden = s.Hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = s.Groups.Where(g => (g.Touched || IsCustom(g.Id)) && !g.Deleted && g.Apps is not null)
            .SelectMany(g => g.Apps!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<AppFolder>();
        foreach (var id in AllAppsCatalog.GroupOrder(s))
        {
            var d = s.Groups.FirstOrDefault(g => g.Id == id);
            if (d is { Deleted: true }) continue;
            bool custom = IsCustom(id), touched = custom || d is { Touched: true };
            List<AppEntry> members;
            if (touched)
                members = (d?.Apps ?? new List<string>()).Select(k => byKey.GetValueOrDefault(k)).OfType<AppEntry>().Where(a => !hidden.Contains(a.Key)).ToList();
            else
            {
                var auto = d?.AutoApps ?? AutoMembers(settings, apps, id);
                members = auto.Select(k => byKey.GetValueOrDefault(k)).OfType<AppEntry>()
                    .Where(a => !hidden.Contains(a.Key) && !claimed.Contains(a.Key)).ToList();
                if (members.Count < 2) continue; // 쓰는 앱이 2개 이상인 분류만 폴더로
            }
            list.Add(new AppFolder(id, AllAppsCatalog.GroupName(s, id), members, touched, custom));
        }
        return list;
    }

    /// <summary>기본 폴더 id 의 자동 내용: 그 분류 앱 중 최근 30일 실행했거나 독에 고정된 것, 실행 많은 순 최대 9개.</summary>
    public static List<string> AutoMembers(Settings settings, IReadOnlyList<AppEntry> apps, string id)
    {
        var docked = settings.Pins.Select(AllAppsCatalog.Identity).OfType<string>().ToHashSet();
        return apps.Where(a => AllAppsCatalog.Classify(a) == id)
            .Select(a => (App: a, Id: AllAppsCatalog.Identity(a)))
            .Select(x => (x.App, Count: AppUsage.Count(AllAppsCatalog.Identities(x.App), UsedDays), Docked: docked.Contains(x.Id)))
            .Where(x => x.Count > 0 || x.Docked)
            .OrderByDescending(x => x.Count).ThenByDescending(x => x.Docked).ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(AutoMax).Select(x => x.App.Key).ToList();
    }

    /// <summary>손대지 않은 기본 폴더: 처음이면 채우고, 아니면 새로 쓰기 시작한 앱만 끝에 (하루 한 번, 판이 닫혀 있을 때). 계산했으면 true.</summary>
    public static bool RefreshAuto(Settings settings, IReadOnlyList<AppEntry> apps, bool force = false)
    {
        var s = settings.AllApps;
        string today = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (!force && s.AutoFoldersDay == today) return false;
        var exists = apps.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = s.Hidden.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = s.Groups.Where(g => (g.Touched || IsCustom(g.Id)) && !g.Deleted && g.Apps is not null)
            .SelectMany(g => g.Apps!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in AllAppsCatalog.DefaultGroups)
        {
            var d = s.Groups.FirstOrDefault(g => g.Id == id);
            if (d is { Touched: true } or { Deleted: true }) continue;
            var def = Def(s, id);
            var now = AutoMembers(settings, apps, id);
            if (def.AutoApps is null) { def.AutoApps = now; continue; } // 처음 채울 때만 많이 쓴 순
            // 보이지 않는 앱(숨김·다른 폴더·지운 앱)은 칸만 차지하니 덜어 냄 — 남은 앱의 순서는 그대로
            def.AutoApps.RemoveAll(k => hidden.Contains(k) || claimed.Contains(k) || (exists.Count > 0 && !exists.Contains(k)));
            // 그 뒤로는 자리 고정: 이미 들어 있는 앱의 자리·순서는 그대로, 새로 쓰기 시작한 앱만 끝에 (9개 꽉 차면 안 붙음).
            // 빠지는 건 사용자가 빼거나 정리 카드에서 골랐을 때만 (그때는 손댄 폴더가 됨)
            foreach (var k in now)
            {
                if (def.AutoApps.Count >= AutoMax) break;
                if (!def.AutoApps.Contains(k, StringComparer.OrdinalIgnoreCase)) def.AutoApps.Add(k);
            }
        }
        s.AutoFoldersDay = today;
        return true;
    }

    /// <summary>손대기 전 기본 폴더를 "지금 보이는 내용" 그대로 손댄 폴더로 굳힘 (넣기·빼기·이름 바꾸기 전에).</summary>
    private static AppGroupDef Touch(Settings settings, IReadOnlyList<AppEntry> apps, string id)
    {
        var s = settings.AllApps;
        var d = Def(s, id);
        if (d.Touched || IsCustom(id)) { d.Apps ??= new List<string>(); return d; }
        var shown = Visible(settings, apps).FirstOrDefault(f => f.Id == id)?.Apps.Select(a => a.Key).ToList()
                    ?? (d.AutoApps ?? AutoMembers(settings, apps, id)).ToList();
        d.Apps = shown;
        d.AutoApps = null;
        d.Touched = true;
        return d;
    }

    /// <summary>앱을 폴더에 넣기 (다른 손댄 폴더에 있으면 거기서 뺌).</summary>
    public static void AddTo(Settings settings, IReadOnlyList<AppEntry> apps, string id, string appKey)
    {
        var s = settings.AllApps;
        var target = Touch(settings, apps, id);
        foreach (var g in s.Groups.Where(g => g != target)) Forget(g, appKey);
        target.Apps ??= new List<string>();
        if (!target.Apps.Contains(appKey, StringComparer.OrdinalIgnoreCase)) target.Apps.Add(appKey);
        s.Customized = true;
    }

    /// <summary>폴더에서 빼기 (앱은 모든 앱에 그대로).</summary>
    public static void RemoveFrom(Settings settings, IReadOnlyList<AppEntry> apps, string id, string appKey)
    {
        var d = Touch(settings, apps, id);
        d.Apps!.RemoveAll(k => k.Equals(appKey, StringComparison.OrdinalIgnoreCase));
        // 다른 자동 폴더에도 들어 있으면 거기서도 뺌 (안 그러면 손댄 폴더에서 풀리자마자 그쪽에 다시 나타남)
        foreach (var g in settings.AllApps.Groups.Where(g => g != d)) g.AutoApps?.RemoveAll(k => k.Equals(appKey, StringComparison.OrdinalIgnoreCase));
        settings.AllApps.Customized = true;
    }

    private static void Forget(AppGroupDef g, string appKey)
    {
        g.Apps?.RemoveAll(k => k.Equals(appKey, StringComparison.OrdinalIgnoreCase));
        g.AutoApps?.RemoveAll(k => k.Equals(appKey, StringComparison.OrdinalIgnoreCase));
    }

    public static void Rename(Settings settings, IReadOnlyList<AppEntry> apps, string id, string name)
    {
        var d = Touch(settings, apps, id);
        name = name.Trim();
        d.Name = name.Length == 0 || name == AllAppsCatalog.DefaultName(id) ? null : name;
        settings.AllApps.Customized = true;
    }

    /// <summary>폴더 통째 지우기 (앱은 모든 앱에 그대로). 기본 폴더는 다시 자동으로 생기지 않게 표시만.</summary>
    public static void Delete(Settings settings, string id)
    {
        var s = settings.AllApps;
        if (IsCustom(id)) s.Groups.RemoveAll(g => g.Id == id);
        else
        {
            var d = Def(s, id);
            d.Deleted = true;
            d.Apps = null;
            d.AutoApps = null;
        }
        s.Customized = true;
    }

    /// <summary>새 폴더 ("기타" 앞). 처음 앱들이 있으면 넣음.</summary>
    public static string Create(Settings settings, IReadOnlyList<AppEntry> apps, string name, params string[] appKeys)
    {
        var s = settings.AllApps;
        EnsureOrder(s);
        string id = "g-" + Guid.NewGuid().ToString("N")[..8];
        s.Groups.Insert(Math.Max(0, s.Groups.FindIndex(g => g.Id == AllAppsCatalog.Other)), new AppGroupDef { Id = id, Name = name, Touched = true, Apps = new List<string>() });
        foreach (var k in appKeys) AddTo(settings, apps, id, k);
        s.Customized = true;
        return id;
    }

    /// <summary>지금 화면 순서를 설정에 적어 둠 (순서 바꾸기 전에) — "기타"는 늘 끝.</summary>
    public static void EnsureOrder(AllAppsSettings s)
    {
        foreach (var id in AllAppsCatalog.GroupOrder(s))
            if (!s.Groups.Any(g => g.Id == id)) s.Groups.Add(new AppGroupDef { Id = id });
        var other = s.Groups.First(g => g.Id == AllAppsCatalog.Other);
        s.Groups.Remove(other);
        s.Groups.Add(other);
    }

    /// <summary>옛 모양(앱 → 묶음 overrides)을 손댄 폴더로 옮김 (한 번).</summary>
    private static void MigrateOverrides(Settings settings, IReadOnlyList<AppEntry> apps)
    {
        var s = settings.AllApps;
        if (s.Overrides.Count == 0 || apps.Count == 0) return;
        var moves = s.Overrides.ToList();
        s.Overrides.Clear();
        foreach (var (key, id) in moves)
            if (AllAppsCatalog.GroupOrder(s).Contains(id)) AddTo(settings, apps, id, key);
    }

    /// <summary>앱이 들어 있는 폴더 id (보이는 폴더 기준). 없으면 null.</summary>
    public static string? FolderOf(List<AppFolder> folders, string appKey) =>
        folders.FirstOrDefault(f => f.Apps.Any(a => a.Key.Equals(appKey, StringComparison.OrdinalIgnoreCase)))?.Id;
}
