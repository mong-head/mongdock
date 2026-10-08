using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyDock.Models;

namespace MyDock.Services;

/// <summary>
/// 앱 전용 내장 메뉴 규칙 묶음 (menus/app-menus.json 한 파일). 앱에 내장 + GitHub 에서 받아 갱신(<see cref="MenuRulesService"/>).
/// </summary>
internal sealed class MenuRuleSet
{
    public MenuRuleSet(int schema, int revision, string source, IReadOnlyList<MenuRule> apps, string rawText)
    {
        Schema = schema;
        Revision = revision;
        Source = source;
        Apps = apps;
        RawText = rawText;
    }

    public int Schema { get; }
    /// <summary>파일 안 "revision" (규칙을 고칠 때마다 1씩 올림). 로그와 내장/캐시 중 새것 고르기에 사용.</summary>
    public int Revision { get; }
    /// <summary>"내장" / "캐시" / "원격" (로그용).</summary>
    public string Source { get; }
    public IReadOnlyList<MenuRule> Apps { get; }
    /// <summary>원본 JSON (같은 내용인지 비교용).</summary>
    public string RawText { get; }

    public static MenuRuleSet Empty { get; } = new(MenuRules.SupportedSchema, -1, "없음", Array.Empty<MenuRule>(), "");

    /// <summary>창 하나에 맞는 첫 규칙. 없으면 null.</summary>
    public MenuRule? Find(string exe, string windowClass, string? aumid)
    {
        foreach (var r in Apps)
            if (r.Matches(exe, windowClass, aumid)) return r;
        return null;
    }
}

/// <summary>앱 하나의 메뉴 규칙. Menus 는 <see cref="AppMenuDef"/> 목록 (appMenu 는 제목 "@app" 메뉴로 맨 앞에).</summary>
internal sealed class MenuRule
{
    public MenuRule(string id, string[] exe, string[] aumid, string[] windowClass, List<AppMenuDef> menus)
    {
        Id = id;
        Exe = exe;
        Aumid = aumid;
        WindowClass = windowClass;
        Menus = menus;
    }

    public string Id { get; }
    /// <summary>exe 파일 이름 (소문자).</summary>
    public string[] Exe { get; }
    public string[] Aumid { get; }
    /// <summary>비어 있지 않으면 창 클래스가 이 중 하나일 때만.</summary>
    public string[] WindowClass { get; }
    public List<AppMenuDef> Menus { get; }

    public bool Matches(string exe, string windowClass, string? aumid)
    {
        bool app = Exe.Contains(exe, StringComparer.OrdinalIgnoreCase)
                   || (!string.IsNullOrEmpty(aumid) && Aumid.Contains(aumid, StringComparer.OrdinalIgnoreCase));
        if (!app) return false;
        return WindowClass.Length == 0 || WindowClass.Contains(windowClass, StringComparer.Ordinal);
    }
}

/// <summary>
/// menus/app-menus.json 읽기·검증. 원격에서 온 데이터라 메뉴 글자·단축키 이름·고정 동작 몇 가지만 받아들이고,
/// 실행 파일 경로·URL·명령 같은 것은 형식 자체에 없다. 잘못된 앱 항목은 그 항목만 버리고 로그.
/// </summary>
internal static class MenuRules
{
    /// <summary>이 몽독이 읽을 수 있는 형식 버전. 다른 schema 파일은 통째로 무시.</summary>
    public const int SupportedSchema = 1;
    public const int MaxBytes = 512 * 1024;
    public const int MaxApps = 300;
    public const int MaxMenusPerApp = 20;
    public const int MaxItemsPerMenu = 80;
    public const int MaxTotalItems = 10000;
    public const int MaxTextLength = 80;
    public const int MaxMatchEntries = 32;

    private const string EmbeddedName = "mongdock.app-menus.json";

    /// <summary>항목 "action" 허용 목록 → 창에 보낼 WM_SYSCOMMAND 값.</summary>
    public static readonly IReadOnlyDictionary<string, int> AllowedActions = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["close"] = Native.MenuApi.SC_CLOSE,
        ["minimize"] = Native.MenuApi.SC_MINIMIZE,
        ["maximize"] = Native.MenuApi.SC_MAXIMIZE,
        ["restore"] = Native.MenuApi.SC_RESTORE,
    };

    private static readonly Regex ExeRx = new(@"^[A-Za-z0-9 _.\-()]{1,64}\.exe$", RegexOptions.CultureInvariant);
    private static readonly Regex AumidRx = new(@"^[A-Za-z0-9_.\-]{1,200}(![A-Za-z0-9_.\-]{1,100})?$", RegexOptions.CultureInvariant);
    private static readonly Regex IdRx = new(@"^[A-Za-z0-9_.\-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>앱에 들어 있는 기본 규칙. 읽지 못하면 빈 규칙(+ 오류 로그) — 그래도 Electron·기본 메뉴는 코드에 있음.</summary>
    public static MenuRuleSet LoadEmbedded()
    {
        try
        {
            using var s = typeof(MenuRules).Assembly.GetManifestResourceStream(EmbeddedName);
            if (s is null)
            {
                Log.Error($"내장 앱 메뉴 규칙 리소스 없음: {EmbeddedName}");
                return MenuRuleSet.Empty;
            }
            using var r = new StreamReader(s, Encoding.UTF8);
            var set = Parse(r.ReadToEnd(), "내장", out string? error);
            if (set is null) Log.Error($"내장 앱 메뉴 규칙을 읽지 못함: {error}");
            return set ?? MenuRuleSet.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("내장 앱 메뉴 규칙 읽기 실패", ex);
            return MenuRuleSet.Empty;
        }
    }

    /// <summary>
    /// JSON 텍스트 → 규칙 묶음. 파일 전체가 잘못됐으면(크기·JSON·schema·apps 없음·유효한 앱 0개) null + error.
    /// 개별 앱 항목 오류는 그 항목만 버리고 Log.Warn.
    /// </summary>
    public static MenuRuleSet? Parse(string text, string source, out string? error)
    {
        error = null;
        if (text is null) { error = "내용 없음"; return null; }
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) { error = $"크기 상한({MaxBytes / 1024}KB) 초과"; return null; }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32,
            });
        }
        catch (JsonException ex)
        {
            error = $"JSON 오류: {ex.Message}";
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "최상위가 객체가 아님"; return null; }
            if (!root.TryGetProperty("schema", out var schemaEl) || !schemaEl.TryGetInt32(out int schema))
            {
                error = "schema 없음";
                return null;
            }
            if (schema != SupportedSchema) { error = $"지원하지 않는 schema {schema} (이 버전은 {SupportedSchema})"; return null; }
            int revision = 0;
            if (root.TryGetProperty("revision", out var revEl) && (!revEl.TryGetInt32(out revision) || revision < 0))
            {
                error = "revision 이 0 이상의 정수가 아님";
                return null;
            }
            if (!root.TryGetProperty("apps", out var appsEl) || appsEl.ValueKind != JsonValueKind.Array)
            {
                error = "apps 배열 없음";
                return null;
            }
            if (appsEl.GetArrayLength() > MaxApps) { error = $"앱 항목 수 상한({MaxApps}) 초과"; return null; }

            var apps = new List<MenuRule>();
            var budget = new Budget();
            int index = 0;
            foreach (var appEl in appsEl.EnumerateArray())
            {
                index++;
                try
                {
                    apps.Add(ParseApp(appEl, index, budget));
                }
                catch (RuleException ex)
                {
                    Log.Warn($"앱 메뉴 규칙({source}) {index}번째 항목 버림: {ex.Message}");
                }
            }
            if (apps.Count == 0) { error = "유효한 앱 항목 없음"; return null; }
            return new MenuRuleSet(schema, revision, source, apps, text);
        }
    }

    private sealed class RuleException(string message) : Exception(message);

    private sealed class Budget { public int Items; }

    private static MenuRule ParseApp(JsonElement el, int index, Budget budget)
    {
        if (el.ValueKind != JsonValueKind.Object) throw new RuleException("객체가 아님");
        string id = $"#{index}";
        if (el.TryGetProperty("id", out var idEl))
        {
            string? s = idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (s is null || !IdRx.IsMatch(s)) throw new RuleException("id 형식 오류");
            id = s;
        }
        if (!el.TryGetProperty("match", out var match) || match.ValueKind != JsonValueKind.Object)
            throw new RuleException($"{id}: match 없음");
        string[] exe = StringList(match, "exe", id, ExeRx).Select(x => x.ToLowerInvariant()).ToArray();
        string[] aumid = StringList(match, "aumid", id, AumidRx);
        string[] cls = StringList(match, "windowClass", id, null);
        if (exe.Length == 0 && aumid.Length == 0) throw new RuleException($"{id}: match.exe 또는 match.aumid 필요");
        foreach (string c in cls)
            if (c.Length > 128 || c.Any(char.IsControl)) throw new RuleException($"{id}: windowClass 형식 오류");

        var menus = new List<AppMenuDef>();
        if (el.TryGetProperty("appMenu", out var appMenuEl))
            menus.Add(new AppMenuDef { Title = BuiltinMenus.AppNameMenuTitle, Items = ParseItems(appMenuEl, id, depth: 0, budget) });
        if (el.TryGetProperty("menus", out var menusEl))
        {
            if (menusEl.ValueKind != JsonValueKind.Array) throw new RuleException($"{id}: menus 가 배열이 아님");
            if (menusEl.GetArrayLength() > MaxMenusPerApp) throw new RuleException($"{id}: 메뉴 수 상한({MaxMenusPerApp}) 초과");
            foreach (var m in menusEl.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object) throw new RuleException($"{id}: 메뉴가 객체가 아님");
                string title = Text(m, "title", id);
                if (title.StartsWith('@')) throw new RuleException($"{id}: 메뉴 제목 '{title}' 은 쓸 수 없음 (앱 이름 메뉴는 appMenu)");
                if (!m.TryGetProperty("items", out var itemsEl)) throw new RuleException($"{id}: '{title}' items 없음");
                menus.Add(new AppMenuDef { Title = title, Items = ParseItems(itemsEl, id, depth: 0, budget) });
            }
        }
        if (menus.Count == 0) throw new RuleException($"{id}: appMenu·menus 가 모두 없음");
        return new MenuRule(id, exe, aumid, cls, menus);
    }

    private static string[] StringList(JsonElement match, string name, string id, Regex? rx)
    {
        if (!match.TryGetProperty(name, out var el)) return Array.Empty<string>();
        var list = new List<string>();
        if (el.ValueKind == JsonValueKind.String) list.Add(el.GetString()!);
        else if (el.ValueKind == JsonValueKind.Array)
        {
            if (el.GetArrayLength() > MaxMatchEntries) throw new RuleException($"{id}: match.{name} 항목 수 초과");
            foreach (var x in el.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.String) throw new RuleException($"{id}: match.{name} 에 문자열이 아닌 값");
                list.Add(x.GetString()!);
            }
        }
        else throw new RuleException($"{id}: match.{name} 형식 오류");
        foreach (string s in list)
            if (s.Length == 0 || (rx is not null && !rx.IsMatch(s))) throw new RuleException($"{id}: match.{name} 값 '{Short(s)}' 형식 오류");
        return list.ToArray();
    }

    private static List<AppMenuItemDef> ParseItems(JsonElement el, string id, int depth, Budget budget)
    {
        if (el.ValueKind != JsonValueKind.Array) throw new RuleException($"{id}: items 가 배열이 아님");
        if (el.GetArrayLength() > MaxItemsPerMenu) throw new RuleException($"{id}: 항목 수 상한({MaxItemsPerMenu}) 초과");
        var list = new List<AppMenuItemDef>();
        foreach (var it in el.EnumerateArray())
        {
            if (++budget.Items > MaxTotalItems) throw new RuleException($"전체 항목 수 상한({MaxTotalItems}) 초과");
            // 구분선: "-" 또는 { "separator": true }
            if (it.ValueKind == JsonValueKind.String)
            {
                if (it.GetString() != "-") throw new RuleException($"{id}: 문자열 항목은 \"-\"(구분선)만 가능");
                list.Add(new AppMenuItemDef { Text = "-" });
                continue;
            }
            if (it.ValueKind != JsonValueKind.Object) throw new RuleException($"{id}: 항목이 객체가 아님");
            if (it.TryGetProperty("separator", out var sep) && sep.ValueKind == JsonValueKind.True)
            {
                list.Add(new AppMenuItemDef { Text = "-" });
                continue;
            }

            string text = Text(it, "text", id);
            if (text == "-") throw new RuleException($"{id}: 구분선은 \"-\" 또는 separator 로");
            bool hasKeys = it.TryGetProperty("keys", out var keysEl);
            bool hasAction = it.TryGetProperty("action", out var actionEl);
            bool hasSub = it.TryGetProperty("items", out var subEl);
            if ((hasKeys ? 1 : 0) + (hasAction ? 1 : 0) + (hasSub ? 1 : 0) != 1)
                throw new RuleException($"{id}: '{text}' 에는 keys / action / items 중 정확히 하나");

            var item = new AppMenuItemDef { Text = text };
            if (hasKeys) item.Keys = ParseKeys(keysEl, id, text);
            else if (hasAction)
            {
                string? a = actionEl.ValueKind == JsonValueKind.String ? actionEl.GetString() : null;
                if (a is null || !AllowedActions.ContainsKey(a)) throw new RuleException($"{id}: '{text}' 허용되지 않은 action '{Short(a ?? "")}'");
                item.Action = a;
            }
            else
            {
                if (depth >= 1) throw new RuleException($"{id}: 하위 메뉴는 한 단계까지만");
                item.Items = ParseItems(subEl, id, depth + 1, budget);
                if (item.Items.Count == 0) throw new RuleException($"{id}: '{text}' 하위 메뉴가 비어 있음");
            }
            list.Add(item);
        }
        return list;
    }

    /// <summary>"Ctrl+N" 또는 ["Ctrl+K","Ctrl+O"](연속 입력) → "Ctrl+K Ctrl+O". 허용 키·차단 조합 검사.</summary>
    private static string ParseKeys(JsonElement el, string id, string text)
    {
        var chords = new List<string>();
        if (el.ValueKind == JsonValueKind.String)
            chords.AddRange(el.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in el.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.String) throw new RuleException($"{id}: '{text}' keys 에 문자열이 아닌 값");
                string s = x.GetString()!.Trim();
                if (s.Length == 0 || s.Contains(' ')) throw new RuleException($"{id}: '{text}' keys 배열 원소는 입력 하나씩");
                chords.Add(s);
            }
        }
        else throw new RuleException($"{id}: '{text}' keys 형식 오류");
        if (chords.Count is 0 or > 4) throw new RuleException($"{id}: '{text}' 단축키는 1~4개 입력");
        foreach (string c in chords)
        {
            if (c.Length > 40 || !KeyParser.TryParse(c, out var vks)) throw new RuleException($"{id}: '{text}' 단축키 '{Short(c)}' 해석 불가");
            string? why = UnsafeReason(vks);
            if (why is not null) throw new RuleException($"{id}: '{text}' 단축키 '{c}' 거부 ({why})");
        }
        return string.Join(' ', chords);
    }

    private const ushort VkCtrl = 0xA2, VkShift = 0xA0, VkAlt = 0xA4, VkWin = 0x5B;
    private const ushort VkTab = 0x09, VkEsc = 0x1B, VkDelete = 0x2E;

    /// <summary>
    /// 앱 메뉴 단축키로 쓸 수 없는 조합이면 이유. 주 키가 정확히 하나여야 하고(수식키만은 안 됨),
    /// Win 조합은 모두 시스템 단축키(Win+L 잠금, Win+R 실행 등)라 거부, 그 밖에 시스템 전환·보안 조합도 거부.
    /// </summary>
    internal static string? UnsafeReason(ushort[] vks)
    {
        var mods = vks.Where(v => v is VkCtrl or VkShift or VkAlt or VkWin).ToHashSet();
        var main = vks.Where(v => v is not (VkCtrl or VkShift or VkAlt or VkWin)).ToList();
        if (main.Count != 1) return "주 키가 하나가 아님";
        ushort k = main[0];
        if (mods.Contains(VkWin)) return "Win 조합은 시스템 단축키";
        bool ctrl = mods.Contains(VkCtrl), alt = mods.Contains(VkAlt);
        if (k == VkTab && alt) return "Alt+Tab 창 전환";
        if (k == VkEsc && (ctrl || alt)) return "시작 메뉴·작업 관리자·창 전환";
        if (k == VkDelete && ctrl && alt) return "Ctrl+Alt+Delete";
        if (k == VkTab && ctrl && alt) return "Ctrl+Alt+Tab";
        return null;
    }

    private static string Text(JsonElement el, string name, string id)
    {
        if (!el.TryGetProperty(name, out var t) || t.ValueKind != JsonValueKind.String) throw new RuleException($"{id}: {name} 없음");
        string s = t.GetString()!.Trim();
        if (s.Length == 0 || s.Length > MaxTextLength) throw new RuleException($"{id}: {name} 길이 오류 '{Short(s)}'");
        foreach (char c in s)
            if (char.IsControl(c) || c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩')
                throw new RuleException($"{id}: {name} 에 제어 문자");
        return s;
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
