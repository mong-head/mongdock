using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Mongdock.Services;

/// <summary>
/// 갑자기 꺼져도 사용자가 갇히지 않게 (#36). 몽독이 시작하면 같은 exe 를 「--watchdog 몽독pid」 로 하나 더 띄움 — 창 없이 몽독 프로세스가 끝나기만 기다림.
/// - 정상 종료(트레이 종료·--exit·교체·업데이트: 세션 표시가 지워짐)면 아무것도 안 하고 같이 끝남.
/// - 세션 표시가 남은 채 끝나면(크래시·강제 종료) 바로 작업 표시줄·자동 숨김·윈도우 애니메이션을 기록대로 되돌림 (힙 손상처럼 몽독 안의 처리기가 못 도는 경우도).
///   종료 코드가 크래시(NTSTATUS 오류·.NET 처리 안 된 예외·FailFast)면 몽독을 다시 켬 — 10분에 3번까지 (그 뒤로는 작업 표시줄만 둠). 작업 관리자 「작업 끝내기」(코드 1)는 다시 켜지 않음.
/// - 예비: 윈도우 「다시 시작」 등록(RegisterApplicationRestart, 크래시 포함, 실행 60초 뒤부터) — 감시 프로세스까지 죽었을 때. 둘이 겹쳐도 두 번째는 --crash-restart 라 조용히 끝남.
/// </summary>
internal static class CrashWatchdog
{
    public const string WatchArg = "--watchdog";
    public const string RestartArg = "--crash-restart";
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(10);
    private static string RestartsPath => Path.Combine(AppInfo.DataDirectory, "cache", "watchdog-restarts.json");

    /// <summary>몽독 쪽 (OnStartup, 단일 실행 확인 뒤): 감시 프로세스 띄우기 + 윈도우 다시 시작 등록.</summary>
    public static void Start()
    {
        try
        {
            int hr = RegisterApplicationRestart(RestartArg, RESTART_NO_HANG | RESTART_NO_REBOOT);
            if (hr != 0) Log.Warn($"갑자기 꺼짐 대비: 윈도우 다시 시작 등록 실패 0x{hr:X8}");
        }
        catch (Exception ex) { Log.Warn($"갑자기 꺼짐 대비: 윈도우 다시 시작 등록 실패 {ex.Message}"); }
        try
        {
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{WatchArg} {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
            Log.Info($"갑자기 꺼짐 대비: 감시 프로세스 시작 (pid {p?.Id})");
        }
        catch (Exception ex) { Log.Warn($"갑자기 꺼짐 대비: 감시 프로세스 시작 실패 {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>감시 프로세스 쪽: <paramref name="pid"/> 가 끝날 때까지 기다렸다가 필요하면 되돌리고 다시 켬.</summary>
    public static void Run(int pid)
    {
        // 핸들을 먼저 엶 — 쥐고 있는 동안 pid 가 다른 프로세스로 다시 쓰이지 않음. 종료 코드도 이 핸들로 (Process.ExitCode 는 GetProcessById 로 얻은 프로세스에서 못 읽음)
        IntPtr h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return; // 이미 끝남
        uint u;
        DateTime started;
        try
        {
            try { using var p = Process.GetProcessById(pid); started = p.StartTime; } catch { started = DateTime.Now; }
            WaitForSingleObject(h, INFINITE);
            if (!GetExitCodeProcess(h, out u)) u = 0;
        }
        finally { CloseHandle(h); }
        var end = CrashReporter.EndOf(pid);
        if (end == CrashReporter.SessionEnd.Clean) return;
        if (end == CrashReporter.SessionEnd.Exiting)
        {
            // 끝내는 정리 도중 끝남 (정리 중 크래시·설치 프로그램의 강제 종료·로그오프) → 되돌리기만, 다시 켜지 않음
            Log.Warn($"감시: 몽독(pid {pid})이 끝내는 중에 끝남 (종료 코드 0x{u:X8}) → 작업 표시줄 되돌림");
            try { DesktopWindowService.RestoreTaskbarFromRecord("감시: 끝내는 중 끝남"); }
            catch (Exception ex) { Log.Error("감시: 작업 표시줄 되돌리기 실패", ex); }
            return;
        }

        Log.Warn($"감시: 몽독(pid {pid}, 시작 {started:HH:mm:ss})이 정상 종료 없이 끝남 (종료 코드 0x{u:X8}) → 작업 표시줄 되돌림");
        try { DesktopWindowService.RestoreTaskbarFromRecord("감시: 갑자기 꺼짐"); }
        catch (Exception ex) { Log.Error("감시: 작업 표시줄 되돌리기 실패", ex); }

        if (!IsCrashCode(u))
        {
            Log.Info("감시: 강제 종료로 보임 (크래시 코드 아님) → 다시 켜지 않음");
            return;
        }
        if ((DateTime.Now - started).TotalSeconds < 20)
        {
            Log.Warn("감시: 시작 20초 안에 꺼짐 → 다시 켜지 않음 (반복 방지)");
            return;
        }
        if (RecentRestarts() >= MaxRestarts)
        {
            Log.Warn($"감시: {RestartWindow.TotalMinutes:0}분에 {MaxRestarts}번 넘게 꺼짐 → 다시 켜지 않음 (작업 표시줄만 둠)");
            return;
        }
        try
        {
            Thread.Sleep(500); // 단일 실행 표시가 풀리고 탐색기가 작업 표시줄을 다시 그릴 틈
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, RestartArg) { UseShellExecute = false });
            Log.Info("감시: 몽독 다시 켬");
        }
        catch (Exception ex) { Log.Error("감시: 몽독 다시 켜기 실패", ex); }
    }

    /// <summary>크래시 종료 코드: NTSTATUS 오류(0xC…: 접근 위반·힙 손상·스택 넘침), .NET 처리 안 된 예외(0xE0434352), FailFast(0x80131623).</summary>
    internal static bool IsCrashCode(uint code) => code >= 0xC0000000 || code == 0xE0434352 || code == 0x80131623;

    private static List<DateTime> ReadRestarts()
    {
        var now = DateTime.Now;
        try
        {
            if (File.Exists(RestartsPath))
                return (JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(RestartsPath)) ?? new()).Where(t => now - t < RestartWindow).ToList();
        }
        catch { }
        return new();
    }

    private static int RecentRestarts() => ReadRestarts().Count;

    /// <summary>
    /// 크래시 뒤 다시 켜진 몽독(--crash-restart, 단일 실행 첫 번째)이 부름: 다시 켠 시각을 기록하고, 10분 안에 3번이 넘으면 false (그러면 켜지 않음).
    /// 감시 프로세스와 윈도우 다시 시작(WER) 둘 다 이 한 곳에서 세짐 (리뷰: WER 반복).
    /// </summary>
    internal static bool NoteRestart()
    {
        var now = DateTime.Now;
        var times = ReadRestarts();
        if (times.Count >= MaxRestarts) return false;
        times.Add(now);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RestartsPath)!);
            AtomicFile.WriteAllText(RestartsPath, JsonSerializer.Serialize(times));
        }
        catch { }
        return true;
    }

    private const uint RESTART_NO_HANG = 2, RESTART_NO_REBOOT = 8;
    private const uint SYNCHRONIZE = 0x00100000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);
}
