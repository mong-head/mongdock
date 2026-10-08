using System.IO;
using System.Text.Json;

namespace Mongdock.Services;

/// <summary>변경 내역 항목 종류.</summary>
public enum ChangeKind
{
    Feature,
    Improvement,
    Fix,
}

/// <summary>코치마크로 소개할 새 기능 (Changelog.json 의 coach).</summary>
public sealed record ChangeCoach(string Key, CoachAnchor Anchor, string Title, string Body, string? Condition);

/// <summary>변경 내역 한 줄. Coach 가 있으면 업데이트 후 말풍선으로도 소개. Major = 그 버전의 주요 업데이트(버전당 1~4개).</summary>
public sealed record ChangeEntry(ChangeKind Kind, string Text, ChangeCoach? Coach, bool Major = false);

/// <summary>한 버전의 변경 내역. Headline = 그 버전 한 줄 요약 (없으면 "").</summary>
public sealed record ChangeRelease(Version Version, string VersionText, string Date, IReadOnlyList<ChangeEntry> Entries, IReadOnlyList<string> KnownIssues, string Headline = "")
{
    /// <summary>주요 업데이트 항목 (major: true).</summary>
    public IEnumerable<ChangeEntry> Majors => Entries.Where(e => e.Major);
}

/// <summary>
/// 앱에 포함된 Changelog.json(EmbeddedResource "Mongdock.Changelog.json") 을 읽은 변경 내역.
/// 설정 → 변경 내역 페이지, 새로운 기능 코치마크(<see cref="WhatsNew.Releases"/>), build-release.ps1 릴리스 노트가 같은 파일을 쓴다.
/// 못 읽으면 빈 목록 (앱 동작에는 지장 없음).
/// </summary>
public static class Changelog
{
    public const string ResourceName = "Mongdock.Changelog.json";

    private static IReadOnlyList<ChangeRelease>? _releases;

    /// <summary>버전 내림차순(최신 먼저).</summary>
    public static IReadOnlyList<ChangeRelease> Releases => _releases ??= Load();

    /// <summary>(after, upTo] 사이 버전 — 내림차순.</summary>
    public static List<ChangeRelease> Between(Version after, Version upTo) =>
        Releases.Where(r => r.Version > after && r.Version <= upTo).ToList();

    private static IReadOnlyList<ChangeRelease> Load()
    {
        try
        {
            using var stream = typeof(Changelog).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                Log.Warn("Changelog.json 리소스가 없음");
                return Array.Empty<ChangeRelease>();
            }
            using var reader = new StreamReader(stream);
            return Parse(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Error("Changelog.json 읽기 실패", ex);
            return Array.Empty<ChangeRelease>();
        }
    }

    internal static IReadOnlyList<ChangeRelease> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var list = new List<ChangeRelease>();
        if (!doc.RootElement.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array) return list;
        foreach (var v in versions.EnumerateArray())
        {
            string text = Str(v, "version") ?? "";
            if (WhatsNew.Parse(text) is not { } ver) continue;
            var entries = new List<ChangeEntry>();
            if (v.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    string? line = Str(e, "text");
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var kind = (Str(e, "kind") ?? "").ToLowerInvariant() switch
                    {
                        "fix" => ChangeKind.Fix,
                        "improvement" => ChangeKind.Improvement,
                        _ => ChangeKind.Feature,
                    };
                    bool major = e.TryGetProperty("major", out var m) && m.ValueKind == JsonValueKind.True;
                    entries.Add(new ChangeEntry(kind, line, kind == ChangeKind.Feature ? ReadCoach(e) : null, major));
                }
            }
            var issues = new List<string>();
            if (v.TryGetProperty("knownIssues", out var ki) && ki.ValueKind == JsonValueKind.Array)
            {
                foreach (var i in ki.EnumerateArray())
                    if (i.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(i.GetString())) issues.Add(i.GetString()!);
            }
            list.Add(new ChangeRelease(ver, ver.ToString(3), Str(v, "date") ?? "", entries, issues, Str(v, "headline")?.Trim() ?? ""));
        }
        return list.OrderByDescending(r => r.Version).ToList();
    }

    private static ChangeCoach? ReadCoach(JsonElement entry)
    {
        if (!entry.TryGetProperty("coach", out var c) || c.ValueKind != JsonValueKind.Object) return null;
        string? title = Str(c, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        var anchor = Enum.TryParse<CoachAnchor>(Str(c, "anchor"), ignoreCase: true, out var a) ? a : CoachAnchor.Center;
        string key = Str(c, "key") ?? title;
        return new ChangeCoach(key, anchor, title, Str(c, "body") ?? "", Str(c, "condition"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
