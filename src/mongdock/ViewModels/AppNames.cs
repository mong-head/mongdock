using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.ViewModels;

/// <summary>
/// 창 → 사람이 읽을 앱 이름. 경로별 캐시.
/// 순서: 시작 메뉴 표시 이름(AppsFolder — 예 "카카오톡") → 다듬은 FileDescription → ProductName → 파일명.
/// FileDescription 은 "KakaoTalk(32-bit)", "memo.exe" 처럼 군더더기가 붙거나 파일명 그대로인 경우가 있다.
/// AppsFolder 열거(수백 ms)는 백그라운드에서: 처음 보는 exe 는 일단 FileDescription 이름으로 보여 주고,
/// 매핑이 끝나 시작 메뉴 이름이 다르면 캐시를 고치고 <see cref="NamesChanged"/> (UI 스레드) → 상단바가 다시 그림.
/// </summary>
public static partial class AppNames
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>시작 메뉴 매핑 없이 임시 이름으로 캐시한 경로 — 매핑이 끝나면 다시 봄.</summary>
    private static readonly HashSet<string> Provisional = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? _startMenuByPath;
    private static DateTime _startMenuTime;
    private static bool _refreshing;

    /// <summary>백그라운드 시작 메뉴 매핑으로 일부 앱 이름이 바뀜 (UI 스레드).</summary>
    public static event EventHandler? NamesChanged;

    public static string Get(AppWindowInfo w)
    {
        // 스토어 앱: 시작 메뉴 이름
        if (AppsFolder.IsPackagedAumid(w.Aumid))
        {
            var packaged = AppsFolder.GetAppDisplayName(w.Aumid!);
            if (!string.IsNullOrWhiteSpace(packaged)) return packaged;
        }
        if (string.IsNullOrEmpty(w.ProcessPath)) return w.Title;
        lock (Gate)
        {
            if (Cache.TryGetValue(w.ProcessPath, out var cached)) return cached;
        }

        string? startMenu = StartMenuName(w.ProcessPath, out bool final);
        string name = startMenu ?? FallbackName(w.ProcessPath);
        if (name.Length == 0) name = w.Title;

        lock (Gate)
        {
            Cache[w.ProcessPath] = name;
            if (startMenu is null && !final) Provisional.Add(w.ProcessPath);
        }
        return name;
    }

    /// <summary>다듬은 FileDescription → ProductName → 파일명(첫 글자 대문자).</summary>
    private static string FallbackName(string path)
    {
        string name = "";
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            name = Clean(vi.FileDescription);
            if (name.Length == 0) name = Clean(vi.ProductName);
        }
        catch
        {
            // WindowsApps 접근 거부 등 → 파일명으로
        }
        if (name.Length == 0)
        {
            name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > 0) name = char.ToUpperInvariant(name[0]) + name[1..];
        }
        return name;
    }

    /// <summary>"KakaoTalk(32-bit)" → "KakaoTalk", "memo.exe" → "" (파일명 그대로면 버림), 너무 긴 설명도 버림.</summary>
    private static string Clean(string? text)
    {
        var s = text?.Trim() ?? "";
        if (s.Length == 0) return "";
        s = BitnessSuffix().Replace(s, "").Trim();
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
        return s.Length <= 40 ? s : "";
    }

    [GeneratedRegex(@"\s*[\(\[]\s*(32|64)\s*-?\s*(bit|비트)\s*[\)\]]\s*$|\s*\((x86|x64)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BitnessSuffix();

    /// <summary>
    /// 시작 메뉴(AppsFolder) 바로 가기 대상 exe 경로 → 표시 이름 (지금 가진 매핑에서만, UI 스레드를 막지 않음).
    /// 매핑이 없거나 1분이 지났고 이 경로가 없으면 백그라운드 갱신을 시작 (final = false: 나중에 바뀔 수 있음).
    /// </summary>
    private static string? StartMenuName(string exePath, out bool final)
    {
        lock (Gate)
        {
            var map = _startMenuByPath;
            if (map is not null && map.TryGetValue(exePath, out var name))
            {
                final = true;
                return name;
            }
            bool stale = map is null || DateTime.UtcNow - _startMenuTime > TimeSpan.FromMinutes(1);
            final = !stale;
            if (stale && !_refreshing)
            {
                _refreshing = true;
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                Task.Run(() => RefreshStartMenu(dispatcher));
            }
            return null;
        }
    }

    /// <summary>AppsFolder 열거 (백그라운드) → 임시 이름으로 캐시한 경로를 고치고, 바뀐 게 있으면 NamesChanged.</summary>
    private static void RefreshStartMenu(System.Windows.Threading.Dispatcher? dispatcher)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool ok = false;
        try
        {
            foreach (var (parsing, display) in AppsFolder.Enumerate())
            {
                if (string.IsNullOrWhiteSpace(display) || !parsing.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                // 데스크톱 앱 파싱 이름은 "{KNOWNFOLDER-GUID}\Kakao\KakaoTalk\KakaoTalk.exe" 또는 전체 경로
                string full = NotifyIconSettingsReader.ExpandPath(parsing);
                if (full.Length > 0) map.TryAdd(full, display);
            }
            ok = true;
        }
        catch (Exception ex)
        {
            Log.Warn("시작 메뉴 앱 이름 조회 실패", ex);
        }

        bool changed = false;
        lock (Gate)
        {
            _refreshing = false;
            if (ok)
            {
                _startMenuByPath = map;
                _startMenuTime = DateTime.UtcNow;
            }
            else if (_startMenuByPath is null)
            {
                _startMenuByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _startMenuTime = DateTime.UtcNow; // 실패해도 1분 동안은 다시 열거하지 않음
            }
            foreach (string path in Provisional)
            {
                if (map.TryGetValue(path, out var name) && (!Cache.TryGetValue(path, out var old) || old != name))
                {
                    Cache[path] = name;
                    changed = true;
                }
            }
            Provisional.Clear();
        }
        if (!changed || dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            try { NamesChanged?.Invoke(null, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("앱 이름 갱신 알림 실패", ex); }
        });
    }
}
