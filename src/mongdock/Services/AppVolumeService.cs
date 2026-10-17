using System.Diagnostics;
using System.IO;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>소리 판의 앱 한 줄 (#21): 같은 실행 파일의 세션을 묶음. Key = "system" 또는 실행 파일 경로(소문자) 또는 "pid:N".</summary>
public sealed record AppVolume(string Key, string Name, string? ExePath, string? Aumid, IReadOnlyList<uint> Pids, double Volume, bool Muted, bool Active, bool System);

/// <summary>
/// 앱별 볼륨 (#21, 0.8): 기본 출력 장치의 WASAPI 세션을 앱(실행 파일)마다 묶어 볼륨·음소거를 읽고 바꿈.
/// 소리 판이 열려 있을 때만 부름(평소엔 아무 일도 안 함). COM 은 스레드 풀(MTA)에서 — UI 스레드에서 부르지 말 것.
/// </summary>
public static class AppVolumeService
{
    public const string SystemKey = "system";
    /// <summary>한 판에 보일 앱 줄 수 (소리 나는 앱 먼저).</summary>
    public const int MaxRows = 6;

    private static readonly Dictionary<uint, (string Path, string? Aumid, string Name)> NameCache = new();

    /// <summary>시험용: 실제 장치 대신 이 목록.</summary>
    internal static Func<IReadOnlyList<AppVolume>>? ListForTest { get; set; }

    /// <summary>지금 출력 장치의 앱 목록 (소리 나는 앱 먼저, 그다음 이름 순). 시스템 소리는 맨 끝.</summary>
    public static IReadOnlyList<AppVolume> List()
    {
        if (ListForTest is { } t) return t();
        var sessions = AudioSessions.RenderSessions();
        var groups = new Dictionary<string, List<AudioSessions.RenderSession>>();
        var info = new Dictionary<string, (string Name, string? Path, string? Aumid)>();
        foreach (var s in sessions)
        {
            string key;
            if (s.System)
            {
                key = SystemKey;
                info[key] = (Loc.T("시스템 소리"), null, null);
            }
            else
            {
                var (path, aumid, name) = Describe(s.Pid);
                key = path.Length > 0 ? path.ToLowerInvariant() : $"pid:{s.Pid}";
                info.TryAdd(key, (name, path.Length > 0 ? path : null, aumid));
            }
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new();
            g.Add(s);
        }
        var rows = groups.Select(kv =>
        {
            var g = kv.Value;
            var (name, path, aumid) = info[kv.Key];
            // 여러 세션이면 가장 큰 볼륨을 보임, 하나라도 소리를 내면 Active, 모두 음소거여야 음소거
            return new AppVolume(kv.Key, name, path, aumid, g.Select(x => x.Pid).Distinct().ToList(),
                g.Max(x => x.Volume), g.All(x => x.Muted), g.Any(x => x.Active), kv.Key == SystemKey);
        });
        return rows.OrderBy(r => r.System).ThenByDescending(r => r.Active).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>앱 하나의 볼륨(0~1)·음소거를 바꿈 (그 앱의 모든 세션). 바꾼 세션이 있으면 true.</summary>
    public static bool Set(string key, double? volume, bool? mute)
    {
        int changed = 0;
        AudioSessions.RenderSessions((s, vol) =>
        {
            string k = s.System ? SystemKey : KeyOf(s.Pid);
            if (k != key) return false;
            var g = Guid.Empty;
            if (volume is { } v) vol.SetMasterVolume((float)Math.Clamp(v, 0, 1), ref g);
            if (mute is { } m) vol.SetMute(m, ref g);
            changed++;
            return true;
        });
        return changed > 0;
    }

    private static string KeyOf(uint pid)
    {
        var (path, _, _) = Describe(pid);
        return path.Length > 0 ? path.ToLowerInvariant() : $"pid:{pid}";
    }

    /// <summary>pid → (경로, AUMID, 보일 이름). 프로세스마다 한 번만 (같은 pid 가 다른 앱이 되는 일은 드묾 — 경로가 바뀌면 다시).</summary>
    private static (string Path, string? Aumid, string Name) Describe(uint pid)
    {
        lock (NameCache)
            if (NameCache.TryGetValue(pid, out var hit)) return hit;
        var (path, aumid) = Kernel32.QueryProcess(pid);
        string name = "";
        if (path.Length > 0)
        {
            try { name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; }
            catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or IOException) { }
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { name = $"PID {pid}"; }
        }
        var r = (path, aumid, name);
        lock (NameCache)
        {
            if (NameCache.Count > 200) NameCache.Clear();
            NameCache[pid] = r;
        }
        return r;
    }
}
