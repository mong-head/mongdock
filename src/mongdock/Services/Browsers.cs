using System.IO;
using Microsoft.Win32;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 이 PC 에 깔린 웹 브라우저 (루틴 링크 항목의 「브라우저」 줄, 사용자 10-25): 윈도우가 브라우저 목록에 쓰는
/// SOFTWARE\Clients\StartMenuInternet (HKCU·HKLM·32비트) 의 open 명령 exe 중 실제로 있는 것. 1분 캐시. IE 는 뺌.
/// 링크 항목이 고른 브라우저가 지워졌으면 기본 브라우저로 (<see cref="For"/>).
/// </summary>
internal static class Browsers
{
    internal sealed record Browser(string Exe, string Name);

    private static (long At, List<Browser> List)? _cache;

    /// <summary>시험용: 깔린 브라우저 목록을 바꿔 넣음 (null = 실제).</summary>
    internal static Func<List<Browser>>? InstalledForTest { get; set; }

    public static List<Browser> Installed()
    {
        if (InstalledForTest is { } fake) return fake();
        if (_cache is { } c && Environment.TickCount64 - c.At < 60_000) return c.List;
        var list = new List<Browser>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, path) in new[]
        {
            (Registry.CurrentUser, @"SOFTWARE\Clients\StartMenuInternet"),
            (Registry.LocalMachine, @"SOFTWARE\Clients\StartMenuInternet"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"),
        })
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) continue;
                foreach (string sub in key.GetSubKeyNames())
                {
                    using var client = key.OpenSubKey(sub);
                    using var cmd = client?.OpenSubKey(@"shell\open\command");
                    if (cmd?.GetValue(null) is not string line) continue;
                    string? exe = RoutineService.SplitArgs(line).FirstOrDefault();
                    if (string.IsNullOrEmpty(exe)) continue;
                    exe = Environment.ExpandEnvironmentVariables(exe);
                    if (!File.Exists(exe) || Path.GetFileNameWithoutExtension(exe).Equals("iexplore", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(exe)) continue;
                    list.Add(new Browser(exe, NameOf(exe, client?.GetValue(null) as string)));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        _cache = (Environment.TickCount64, list);
        return list;
    }

    /// <summary>보이는 이름: 아는 브라우저는 짧게(Chrome·Edge·Whale…), 그 밖은 레지스트리 이름 또는 파일 이름.</summary>
    public static string NameOf(string exe, string? registryName = null) => Path.GetFileNameWithoutExtension(exe).ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "whale" => "Whale",
        "brave" => "Brave",
        "firefox" => "Firefox",
        "opera" or "launcher" when exe.Contains("Opera", StringComparison.OrdinalIgnoreCase) => "Opera",
        "vivaldi" => "Vivaldi",
        _ => string.IsNullOrWhiteSpace(registryName) ? Path.GetFileNameWithoutExtension(exe) : registryName.Trim(),
    };

    /// <summary>
    /// 링크 항목을 열 브라우저: 고른 브라우저가 아직 있으면 그것, 없거나 안 골랐으면 기본 브라우저.
    /// </summary>
    public static string? For(RoutineItem item)
    {
        if (!string.IsNullOrEmpty(item.Browser))
        {
            string exe = Environment.ExpandEnvironmentVariables(item.Browser);
            if (File.Exists(exe)) return exe;
            Log.Info("루틴 링크: 고른 브라우저가 없어 기본 브라우저로 엶");
        }
        return RoutineService.DefaultBrowser();
    }
}
