using System.IO;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 스스로 업데이트하는 Squirrel 앱(Discord·Slack 클래식·Teams 클래식·GitHub Desktop 등)의 핀 경로 고치기 (mong-head/mongdock-issues#43).
/// 이 앱들은 %LOCALAPPDATA%\앱\app-1.0.9213\앱.exe 처럼 버전 폴더에서 돌고, 업데이트하면 새 app-* 폴더로 옮긴 뒤 옛 폴더를 지움 →
/// 독 핀이 옛 경로를 가리켜 아이콘이 기본 exe 그림이 되고 눌러도 「실행 파일이 없음」. 같은 앱 폴더(옆에 Update.exe)의 가장 새 app-* 에서
/// 같은 이름의 exe 를 찾아 핀을 그 경로로 바꿈 (독이 30초에 한 번까지 확인, 누를 때도).
/// </summary>
internal static class SquirrelPaths
{
    /// <summary>없는 경로가 Squirrel 앱의 옛 버전이면 가장 새 버전의 같은 exe, 아니면 null.</summary>
    public static string? Heal(string? target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            string path = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return null; // 네트워크 경로는 보지 않음 (끊겼을 때 독이 멈추지 않게)
            // Squirrel 앱은 늘 사용자 앱 데이터 아래 — 그 밖은 파일 확인도 하지 않음
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length == 0 || !path.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            if (File.Exists(path)) return null;
            string? versionDir = Path.GetDirectoryName(path);
            string? appDir = versionDir is null ? null : Path.GetDirectoryName(versionDir);
            if (versionDir is null || appDir is null) return null;
            if (!Path.GetFileName(versionDir).StartsWith("app-", StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(Path.Combine(appDir, "Update.exe"))) return null;
            string exe = Path.GetFileName(path);
            return Directory.EnumerateDirectories(appDir, "app-*")
                .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : null))
                .Where(x => x.Ver is not null && File.Exists(Path.Combine(x.Dir, exe)))
                .OrderByDescending(x => x.Ver)
                .Select(x => Path.Combine(x.Dir, exe))
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static long _lastCheck;

    /// <summary>원래 경로가 %LOCALAPPDATA% 처럼 변수로 시작했으면 고친 경로도 그 모양으로 (설정 옮기기·사용자 이름 노출 방지).</summary>
    internal static string KeepVariable(string original, string healed)
    {
        string o = original.Trim().Trim('"');
        if (!o.StartsWith('%')) return healed;
        int end = o.IndexOf('%', 1);
        if (end < 0) return healed;
        string variable = o[..(end + 1)];
        string expanded = Environment.ExpandEnvironmentVariables(variable);
        return expanded != variable && healed.StartsWith(expanded, StringComparison.OrdinalIgnoreCase) ? variable + healed[expanded.Length..] : healed;
    }

    /// <summary>독 핀 중 옛 버전 경로를 가리키는 Squirrel 앱 핀을 새 경로로 (30초에 한 번까지). 바꿨으면 true — 부른 쪽이 저장·아이콘 다시.</summary>
    public static bool HealPins(IList<PinItem> pins, bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && now - _lastCheck < 30_000) return false;
        _lastCheck = now;
        bool changed = false;
        foreach (var pin in pins)
        {
            if (pin.Kind != PinKind.Exe || Heal(pin.Target) is not { } healed) continue;
            Log.Info($"독 핀 경로 고침 (앱이 스스로 업데이트함): {Path.GetFileName(healed)} → {Path.GetFileName(Path.GetDirectoryName(healed))}");
            pin.Target = KeepVariable(pin.Target, healed);
            changed = true;
        }
        return changed;
    }
}
