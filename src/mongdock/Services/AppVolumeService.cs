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
                var (path, aumid, name) = Describe(s);
                key = KeyOf(s);
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
            string k = s.System ? SystemKey : KeyOf(s);
            if (k != key) return false;
            var g = Guid.Empty;
            if (volume is { } v) vol.SetMasterVolume((float)Math.Clamp(v, 0, 1), ref g);
            if (mute is { } m) vol.SetMute(m, ref g);
            changed++;
            return true;
        });
        return changed > 0;
    }

    /// <summary>
    /// 세션의 앱 키 = 실행 파일 경로(소문자). 크롬·엣지·웨일·디스코드·스포티파이처럼 소리를 하위 프로세스가 내도 실행 파일이 같아 한 줄로 묶임
    /// (윈도우 볼륨 믹서와 같게). 프로세스 경로를 못 읽거나 pid 가 0(여러 프로세스 세션)이면 세션 식별자 안의 경로.
    /// </summary>
    internal static string KeyOf(AudioSessions.RenderSession s)
    {
        var (path, _, _) = Describe(s);
        return path.Length > 0 ? path.ToLowerInvariant() : $"pid:{s.Pid}";
    }

    private static (string Path, string? Aumid, string Name) Describe(AudioSessions.RenderSession s)
    {
        if (s.Pid != 0)
        {
            var d = Describe(s.Pid);
            if (d.Path.Length > 0 || s.IdPath.Length == 0) return d;
        }
        return (s.IdPath, null, NameOfExe(s.IdPath));
    }

    /// <summary>
    /// 소리 판 이름 칸이 좁아 잘리지 않게 알려진 앱은 짧은 제품 이름 (실행 파일 이름 → 이름). 파일 설명이 "Google Chrome"·"Microsoft Edge"처럼 길어서.
    /// </summary>
    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Chrome", ["msedge"] = "Edge", ["whale"] = "Whale", ["firefox"] = "Firefox", ["brave"] = "Brave",
        ["opera"] = "Opera", ["vivaldi"] = "Vivaldi", ["arc"] = "Arc", ["discord"] = "Discord", ["spotify"] = "Spotify",
        ["steam"] = "Steam", ["steamwebhelper"] = "Steam", ["ms-teams"] = "Teams", ["teams"] = "Teams", ["zoom"] = "Zoom",
        ["slack"] = "Slack", ["vlc"] = "VLC", ["potplayermini64"] = "PotPlayer", ["potplayermini"] = "PotPlayer",
        ["kakaotalk"] = "KakaoTalk", ["obs64"] = "OBS", ["msedgewebview2"] = "Edge WebView", ["applemusic"] = "Apple Music",
        ["itunes"] = "iTunes", ["telegram"] = "Telegram", ["whatsapp"] = "WhatsApp", ["youtube music"] = "YouTube Music",
    };

    /// <summary>시험용: 실행 파일 경로 → 소리 판 이름.</summary>
    public static string NameForTest(string path) => NameOfExe(path);

    /// <summary>실행 파일의 보일 이름: 알려진 앱은 짧은 이름, 아니면 파일 설명, 없으면 파일 이름.</summary>
    private static string NameOfExe(string path)
    {
        if (ShortNames.TryGetValue(Path.GetFileNameWithoutExtension(path), out var shortName)) return shortName;
        string name = "";
        try { name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or IOException) { }
        return name.Length > 0 ? name : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>pid → (경로, AUMID, 보일 이름). 프로세스마다 한 번만 (같은 pid 가 다른 앱이 되는 일은 드묾 — 경로가 바뀌면 다시).</summary>
    private static (string Path, string? Aumid, string Name) Describe(uint pid)
    {
        lock (NameCache)
            if (NameCache.TryGetValue(pid, out var hit)) return hit;
        var (path, aumid) = Kernel32.QueryProcess(pid);
        string name = "";
        if (path.Length > 0) name = NameOfExe(path);
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
