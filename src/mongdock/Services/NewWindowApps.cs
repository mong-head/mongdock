using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Mongdock.Models;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 독 아이콘 ＋ = 「지금 데스크톱에 새 창」 (#41 루틴 v2, 사용자 결정 2026-10-22): 창을 여러 개 띄울 수 있는 앱에만.
/// 판정: ① 새 창 인자를 아는 앱(크로미움 계열 --new-window, 파이어폭스 -new-window, VS Code·Cursor -n, 탐색기) — 루틴과 같은 표 ②「한 개만 켜지는 앱」 고정 목록은 없음
/// ③ 메모장은 없음(윈도우 11 메모장은 다른 데스크톱 창에 탭으로 붙음 — 10-22 화면 시험) ④ 그 밖은 지금 창이 2개 이상 떠 있으면 후보.
/// 배우기: ＋를 누른 뒤 5초 안에 지금 데스크톱에 그 앱의 새 창이 안 뜨면 그 앱은 「한 개만」으로 기억하고 ＋를 숨김 (cache/multiwindow-apps.json, 앱 이름만).
/// 크롬·엣지는 다른 데스크톱 창에 탭을 붙이지 않게 새 창 인자를 늘 붙임 (10-22 시험: 인자 없으면 새 창 없음).
/// </summary>
internal static class NewWindowApps
{
    private static readonly string StorePath = Path.Combine(AppInfo.DataDirectory, "cache", "multiwindow-apps.json");
    private static Dictionary<string, string>? _learned;
    private static readonly object Gate = new();

    /// <summary>한 개만 켜지는 앱 (실행 파일 이름, 소문자) — ＋ 없음. 누르면 그 창이 있는 데스크톱으로 이동 + 알약.</summary>
    private static readonly HashSet<string> Single = new(StringComparer.OrdinalIgnoreCase)
    {
        "kakaotalk", "discord", "spotify", "slack", "ms-teams", "teams", "telegram", "whatsapp", "zoom", "steam", "line", "notion", "olk", "notepad",
    };

    /// <summary>새 창 인자 (없으면 null = 모름). 탐색기는 인자 없이 새 창.</summary>
    internal static string? NewWindowArg(string? exePath) => ExeName(exePath) switch
    {
        "chrome" or "msedge" or "whale" or "brave" or "vivaldi" or "opera" => "--new-window",
        "firefox" => "-new-window",
        "code" or "cursor" => "-n",
        "explorer" => "",
        _ => null,
    };

    private static string ExeName(string? path) => string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

    /// <summary>이 앱의 실행 파일 (핀이 exe 면 그것, 아니면 첫 창의 프로세스 경로). 스토어 앱(AUMID 만)은 null.</summary>
    internal static string? ExeOf(PinItem? pin, IReadOnlyList<AppWindowInfo> windows)
    {
        if (pin is { Kind: PinKind.Exe } p && !string.IsNullOrEmpty(p.Target) && p.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return p.Target;
        var w = windows.FirstOrDefault(x => !string.IsNullOrEmpty(x.ProcessPath));
        if (w is null || !string.IsNullOrEmpty(w.Aumid) && !string.Equals(ExeName(w.ProcessPath), "explorer", StringComparison.OrdinalIgnoreCase)) return null;
        return w.ProcessPath;
    }

    /// <summary>＋를 보일지: 실행 중이고(창이 있음) 여러 창 앱일 때.</summary>
    internal static bool CanOpenNew(PinItem? pin, IReadOnlyList<AppWindowInfo> windows)
    {
        if (windows.Count == 0) return false;
        string? exe = ExeOf(pin, windows);
        if (exe is null) return false;
        string name = ExeName(exe);
        if (Single.Contains(name)) return false;
        string? learned = Learned(name);
        if (learned == "single") return false;
        if (NewWindowArg(exe) is not null) return true;
        return learned == "multi" || windows.Count >= 2;
    }

    /// <summary>
    /// 지금 데스크톱에 새 창 (＋ 누름). 5초 안에 지금 데스크톱에 그 앱의 새 창이 뜨면 「여러 창」, 아니면 「한 개만」으로 배우고 false.
    /// </summary>
    internal static async Task<bool> OpenNewAsync(IWindowTracker tracker, PinItem? pin, IReadOnlyList<AppWindowInfo> windows)
    {
        string? exe = ExeOf(pin, windows);
        if (exe is null) return false;
        string name = ExeName(exe);
        var before = tracker.Windows.Select(w => w.Hwnd).ToHashSet();
        try
        {
            string arg = NewWindowArg(exe) ?? "";
            Process.Start(new ProcessStartInfo(name == "explorer" ? "explorer.exe" : exe, arg) { UseShellExecute = true });
            Log.Info($"독 ＋: 새 창 열기 {name} {arg}");
        }
        catch (Exception ex)
        {
            Log.Warn($"독 ＋: 실행 실패 {name}: {ex.Message}");
            return false;
        }
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(200);
            var fresh = tracker.Windows.FirstOrDefault(w => !before.Contains(w.Hwnd) && string.Equals(ExeName(w.ProcessPath), name, StringComparison.OrdinalIgnoreCase));
            if (fresh is not null)
            {
                bool here = VirtualDesktopHelper.IsOnCurrentDesktop(fresh.Hwnd) ?? fresh.OnCurrentDesktop;
                Learn(name, here ? "multi" : "single");
                Log.Info($"독 ＋: {name} 새 창 {(here ? "지금 데스크톱에 뜸" : "다른 데스크톱에 뜸 → 한 개만으로 배움")} ({sw.ElapsedMilliseconds}ms)");
                return here;
            }
        }
        Learn(name, "single");
        Log.Info($"독 ＋: {name} 5초 안에 새 창이 없음 → 한 개만으로 배움");
        return false;
    }

    private static string? Learned(string name)
    {
        lock (Gate)
        {
            Load();
            return _learned!.TryGetValue(name, out var v) ? v : null;
        }
    }

    private static void Learn(string name, string kind)
    {
        lock (Gate)
        {
            Load();
            if (_learned!.TryGetValue(name, out var old) && old == kind) return;
            _learned[name] = kind;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(_learned));
            }
            catch (Exception ex) { Log.Warn($"독 ＋: 배운 것 저장 실패 {ex.Message}"); }
        }
    }

    private static void Load()
    {
        if (_learned is not null) return;
        try
        {
            _learned = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch { _learned = new(); }
        _learned = new Dictionary<string, string>(_learned, StringComparer.OrdinalIgnoreCase);
    }
}
