using System.IO;
using System.Text.Json;

namespace Mongdock.Services;

/// <summary>
/// 업데이트 전 설정 백업 (#18): 새 버전이 처음 실행될 때(설정 파일의 마지막 실행 버전 &lt; 지금 버전, 또는 settingsVersion 이관이 필요할 때)
/// 이관·저장 <b>전에</b> settings.json·calendars.json·notifications-hidden.json 을 "&lt;파일&gt;.bak-v&lt;이전 버전&gt;" 으로 복사한다
/// (이전 버전을 모르는 옛 파일이면 "bak-pre-v&lt;지금 버전&gt;"). 파일마다 최근 3개만 남김. 되돌리기 버튼은 없음 —
/// 지원·되돌리기 버전을 낼 때 쓰는 용도. 실패해도 로그만.
/// </summary>
public static class UpdateBackup
{
    private static readonly string[] Files = { "settings.json", "calendars.json", "notifications-hidden.json" };
    private const int Keep = 3;

    /// <summary>settings.json 원문을 읽은 직후, 이관·저장 전에 부름.</summary>
    public static void BeforeLoad(string dataDirectory, string settingsText)
    {
        try
        {
            string? lastText = null;
            int settingsVersion = 0;
            using (var doc = JsonDocument.Parse(settingsText, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Name.Equals("lastRunVersion", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        lastText = p.Value.GetString();
                    else if (lastText is null && p.Name.Equals("lastSeenVersion", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        lastText ??= p.Value.GetString();
                    else if (p.Name.Equals("settingsVersion", StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out int v))
                        settingsVersion = v;
                }
            }
            var last = WhatsNew.Parse(lastText);
            var current = WhatsNew.Current;
            bool upgraded = last is null || last < current;
            bool migrating = settingsVersion < Models.Settings.CurrentVersion;
            if (!upgraded && !migrating) return;

            string suffix = last is null ? $".bak-pre-v{current.ToString(3)}" : $".bak-v{last.ToString(3)}";
            int copied = 0;
            foreach (string name in Files)
            {
                string src = Path.Combine(dataDirectory, name);
                string dst = src + suffix;
                if (!File.Exists(src) || File.Exists(dst)) continue;
                File.Copy(src, dst);
                File.SetCreationTimeUtc(dst, DateTime.UtcNow); // 정리 순서용 (복사는 원본 수정 시각을 그대로 가져옴)
                copied++;
            }
            if (copied > 0) Log.Info($"업데이트 전 설정 백업: {copied}개 → *{suffix} (v{lastText ?? "?"} → v{current.ToString(3)})");
            Prune(dataDirectory);
        }
        catch (Exception ex)
        {
            Log.Warn($"업데이트 전 설정 백업 실패: {ex.Message}");
        }
    }

    /// <summary>가장 최근 settings.json 백업의 버전 표시 ("v0.4.2" / "pre-v0.5.0"). 없으면 null. 신고 진단용.</summary>
    public static string? LatestLabel(string dataDirectory)
    {
        try
        {
            var latest = Backups(Path.Combine(dataDirectory, "settings.json")).FirstOrDefault();
            if (latest is null) return null;
            string name = latest.Name;
            return name[(name.IndexOf(".bak-", StringComparison.Ordinal) + ".bak-".Length)..];
        }
        catch { return null; }
    }

    /// <summary>파일마다 우리 백업(.bak-v*·.bak-pre-v*)을 최근 3개만 남김. 사람이 만든 다른 .bak-* 는 건드리지 않음.</summary>
    private static void Prune(string dataDirectory)
    {
        foreach (string name in Files)
        {
            foreach (var old in Backups(Path.Combine(dataDirectory, name)).Skip(Keep))
            {
                try { old.Delete(); }
                catch (Exception ex) { Log.Warn($"오래된 설정 백업 삭제 실패 ({old.Name}): {ex.Message}"); }
            }
        }
    }

    /// <summary>그 파일의 업데이트 백업, 최근 것 먼저.</summary>
    private static IEnumerable<FileInfo> Backups(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (!dir.Exists) return Enumerable.Empty<FileInfo>();
        string baseName = Path.GetFileName(path);
        return dir.EnumerateFiles(baseName + ".bak-*")
            .Where(f => f.Name.StartsWith(baseName + ".bak-v", StringComparison.OrdinalIgnoreCase)
                        || f.Name.StartsWith(baseName + ".bak-pre-v", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();
    }
}
