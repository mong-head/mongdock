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
/// </summary>
public static partial class AppNames
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? _startMenuByPath;
    private static DateTime _startMenuTime;

    public static string Get(AppWindowInfo w)
    {
        // 스토어 앱: 시작 메뉴 이름
        if (AppsFolder.IsPackagedAumid(w.Aumid))
        {
            var packaged = AppsFolder.GetAppDisplayName(w.Aumid!);
            if (!string.IsNullOrWhiteSpace(packaged)) return packaged;
        }
        if (string.IsNullOrEmpty(w.ProcessPath)) return w.Title;
        if (Cache.TryGetValue(w.ProcessPath, out var cached)) return cached;

        string name = StartMenuName(w.ProcessPath) ?? "";
        if (name.Length == 0)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(w.ProcessPath);
                name = Clean(vi.FileDescription);
                if (name.Length == 0) name = Clean(vi.ProductName);
            }
            catch
            {
                // WindowsApps 접근 거부 등 → 파일명으로
            }
        }
        if (name.Length == 0)
        {
            name = Path.GetFileNameWithoutExtension(w.ProcessPath);
            if (name.Length > 0) name = char.ToUpperInvariant(name[0]) + name[1..];
        }
        if (name.Length == 0) name = w.Title;

        Cache[w.ProcessPath] = name;
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

    /// <summary>시작 메뉴(AppsFolder) 바로 가기 대상 exe 경로 → 표시 이름. 1분 캐시.</summary>
    private static string? StartMenuName(string exePath)
    {
        try
        {
            if (_startMenuByPath is null || DateTime.UtcNow - _startMenuTime > TimeSpan.FromMinutes(1))
            {
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (parsing, display) in AppsFolder.Enumerate())
                {
                    if (string.IsNullOrWhiteSpace(display) || !parsing.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    // 데스크톱 앱 파싱 이름은 "{KNOWNFOLDER-GUID}\Kakao\KakaoTalk\KakaoTalk.exe" 또는 전체 경로
                    string full = NotifyIconSettingsReader.ExpandPath(parsing);
                    if (full.Length > 0) map.TryAdd(full, display);
                }
                _startMenuByPath = map;
                _startMenuTime = DateTime.UtcNow;
            }
            return _startMenuByPath.TryGetValue(exePath, out var name) ? name : null;
        }
        catch (Exception ex)
        {
            Log.Warn("시작 메뉴 앱 이름 조회 실패", ex);
            return null;
        }
    }
}
