using System.IO;
using System.Text.Json;

namespace Mongdock.Services;

/// <summary>
/// Claude 데스크톱 알림 → 그 세션 (사용자 10-26, 링크 확인됨): Claude 알림에는 세션 id 가 없어서, 실행 중인 Claude Code 세션마다
/// Claude 가 쓰는 %USERPROFILE%\.claude\sessions\&lt;pid&gt;.json 에서 이름(name)과 세션 id(hostSessionId = local_…)만 읽어
/// 알림 제목과 맞춤. 대화 내용은 읽지 않고, 이름·id 는 메모리에서만 비교(로그에 남기지 않음).
/// 제목과 이름이 같은 세션이 딱 하나면 그것, 없으면 이름이 제목에 들어 있는 세션이 딱 하나면 그것, 그 밖(없음·여럿)은 null → 창만 앞으로.
/// 형식은 공개 문서가 아니라 바뀔 수 있음 — 읽기 실패·키 없음은 모두 null.
/// </summary>
internal static class ClaudeSessions
{
    /// <summary>시험용: 세션 파일 폴더 (null = 실제 %USERPROFILE%\.claude\sessions).</summary>
    internal static string? DirForTest { get; set; }

    private static string Dir => DirForTest ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    /// <summary>알림 제목에 맞는 세션의 id (local_…). 못 찾거나 여럿이면 null.</summary>
    public static string? Find(string? title)
    {
        title = title?.Trim();
        if (string.IsNullOrEmpty(title)) return null;
        var sessions = Read();
        var exact = sessions.Where(s => string.Equals(s.Name, title, StringComparison.Ordinal)).Select(s => s.Id).Distinct().ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        var inside = sessions.Where(s => s.Name.Length >= 2 && title.Contains(s.Name, StringComparison.Ordinal)).Select(s => s.Id).Distinct().ToList();
        return inside.Count == 1 ? inside[0] : null;
    }

    private static List<(string Name, string Id)> Read()
    {
        var list = new List<(string, string)>();
        try
        {
            if (!Directory.Exists(Dir)) return list;
            foreach (string file in Directory.EnumerateFiles(Dir, "*.json").Take(200))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) continue;
                    if (!root.TryGetProperty("hostSessionId", out var h) || h.ValueKind != JsonValueKind.String) continue;
                    string name = n.GetString()!.Trim(), id = h.GetString()!;
                    if (name.Length > 0 && IsSessionId(id)) list.Add((name, id));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Claude 데스크톱 세션 id 모양 (앱 코드의 local_[A-Za-z0-9-]{1,64}).</summary>
    internal static bool IsSessionId(string? id) => id is not null && System.Text.RegularExpressions.Regex.IsMatch(id, "^local_[A-Za-z0-9-]{1,64}$");
}
