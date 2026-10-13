using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Mongdock.Services;

/// <summary>
/// 잠자는 앱 실제 지우기 (2026-10-13 사용자: [Windows에서 제거…]가 설정 앱 화면만 열고 끝나서 쓸모없음).
/// - 일반 프로그램: 제거 프로그램 레지스트리의 UninstallString 을 실행 (QuietUninstallString 은 안 씀 — 제어판처럼 앱 자체 제거 창이 뜨고, UAC 도 그 프로그램이 띄움).
///   하나가 끝날 때까지 기다렸다가 다음 것. 같은 설치 묶음(한컴오피스처럼 앱 여럿)은 한 번만.
///   끝 판정: 제거 프로그램과 그것이 띄운 자식 프로세스가 모두 끝난 뒤 레지스트리 항목이 사라졌으면 지움, 남았으면 실패(사용자 취소 포함).
/// - 스토어 앱: PackageManager.RemovePackageAsync 는 Store 심사에서 제한된 기능(packageManagement)이 필요해서 안 씀 — 그 앱의 설정 페이지를 엶.
/// - 윈도우 기본 앱·보호된 앱(SystemComponent·NoRemove)·제거 프로그램이 없는 앱은 "지울 수 없음"으로 건너뜀.
/// 몽독이 직접 파일을 지우지 않음. 사용자 앱을 시험으로 실행하지 않음 — 시험은 가짜 Uninstall 항목으로만 (report-test --uninstall).
/// </summary>
internal static class AppUninstall
{
    /// <summary>지우는 단위 하나 (같은 설치 묶음 앱 여럿 = 한 단위).</summary>
    internal sealed record Removal(string Id, string Name, string? Command, string? RegKey, string? Family, string? Cannot);

    internal sealed record Job(Removal What, List<UnusedApps.Row> Rows)
    {
        public string Name => What.Name;
        public long? Bytes => UnusedApps.TotalBytes(Rows);
    }

    internal enum Outcome { Removed, Failed, OpenedSettings, Skipped }

    internal sealed record Result(int Removed, int Failed, int Settings, int Skipped, bool Cancelled);

    /// <summary>고른 줄 → 지우는 단위 (같은 묶음은 하나로, 지울 수 없는 것은 Cannot 이 있음).</summary>
    public static List<Job> Plan(IEnumerable<UnusedApps.Row> rows) =>
        rows.GroupBy(r => r.Removal?.Id ?? "app:" + r.App.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Job(g.First().Removal ?? new Removal("app:" + g.First().App.Key, g.First().App.Name, null, null, null, Loc.T("제거 프로그램이 없어요")), g.ToList()))
            .ToList();

    /// <summary>지금 지우는 중인지 (판을 다시 열어도 진행을 보여 주려고).</summary>
    public static bool Running { get; private set; }

    /// <summary>
    /// 차례로 지움. progress = (몇 번째, 모두, 이름). cancel = [나머지 취소] (지금 열린 제거 프로그램은 그대로 두고 다음 것을 시작하지 않음).
    /// UI 스레드에서 부르고 기다림은 백그라운드.
    /// </summary>
    public static async Task<Result> RunAsync(IReadOnlyList<Job> jobs, Action<int, int, string> progress, CancellationToken cancel)
    {
        Running = true;
        int removed = 0, failed = 0, settings = 0, skipped = 0;
        var todo = jobs.Where(j => j.What.Cannot is null).ToList();
        skipped = jobs.Count - todo.Count;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                if (cancel.IsCancellationRequested) { failed += todo.Count - i; break; }
                var job = todo[i];
                progress(i + 1, todo.Count, job.Name);
                var outcome = await Task.Run(() => RunOne(job.What));
                Log.Info($"잠자는 앱 지우기 {i + 1}/{todo.Count}: {outcome}"); // 앱 이름은 로그에 안 남김
                switch (outcome)
                {
                    case Outcome.Removed: removed++; break;
                    case Outcome.OpenedSettings: settings++; break;
                    default: failed++; break;
                }
            }
        }
        finally { Running = false; }
        return new Result(removed, failed, settings, skipped, cancel.IsCancellationRequested);
    }

    /// <summary>시험에서만: 몇 분까지 기다릴지 (기본 30분 — 사용자가 제거 창을 오래 둘 수 있음).</summary>
    internal static TimeSpan WaitLimit { get; set; } = TimeSpan.FromMinutes(30);

    private static Outcome RunOne(Removal what)
    {
        if (what.Family is { Length: > 0 } fam)
        {
            // 스토어 앱: 몽독이 직접 지우지 않고 그 앱의 설정 페이지 (패키지 관리 권한은 Store 제한 기능이라)
            try { using (Process.Start(new ProcessStartInfo("ms-settings:appsfeatures-app?" + fam) { UseShellExecute = true })) { } return Outcome.OpenedSettings; }
            catch (Exception ex) { Log.Warn($"앱 설정 열기 실패: {ex.GetType().Name}"); return Outcome.Failed; }
        }
        if (what.Command is not { Length: > 0 } cmd || Split(cmd) is not { } start) return Outcome.Skipped;
        Process? p;
        var started = DateTime.Now;
        try { p = Process.Start(new ProcessStartInfo(start.Exe, start.Args) { UseShellExecute = true }); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return Outcome.Failed; } // UAC 에서 아니요
        catch (Exception ex) { Log.Warn($"제거 프로그램 실행 실패: {ex.GetType().Name}"); return Outcome.Failed; }
        if (p is null) return Exists(what.RegKey) ? Outcome.Failed : Outcome.Removed;
        using (p)
        {
            // 제거 프로그램 + 그것이 띄운 자식(임시 폴더로 자기를 복사해 다시 띄우는 NSIS 같은 것)이 모두 끝날 때까지
            var family = new HashSet<int> { p.Id };
            var limit = Stopwatch.StartNew();
            while (limit.Elapsed < WaitLimit)
            {
                Thread.Sleep(500);
                var alive = Snapshot();
                foreach (var (pid, parent) in alive) if (family.Contains(parent)) family.Add(pid);
                bool any = alive.Any(x => family.Contains(x.Pid) && ProcessStartedAfter(x.Pid, started));
                if (!any)
                {
                    // 끝나고 레지스트리가 늦게 지워지는 제거 프로그램도 있어서 조금 더 봄
                    for (int t = 0; t < 6 && Exists(what.RegKey); t++) Thread.Sleep(500);
                    break;
                }
            }
        }
        return Exists(what.RegKey) ? Outcome.Failed : Outcome.Removed;
    }

    private static bool ProcessStartedAfter(int pid, DateTime t)
    {
        try { using var p = Process.GetProcessById(pid); return p.StartTime >= t.AddSeconds(-1); }
        catch { return true; } // 못 읽으면 (관리자 권한 프로세스) 살아 있는 것으로
    }

    /// <summary>UninstallString → 실행 파일 + 인자. "따옴표 경로" 또는 공백이 든 따옴표 없는 경로(.exe 까지), msiexec.</summary>
    internal static (string Exe, string Args)? Split(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) return null;
            return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        int at = 0;
        while ((at = cmd.IndexOf(".exe", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            string exe = cmd[..(at + 4)];
            string args = cmd[(at + 4)..].Trim();
            if (File.Exists(exe) || !exe.Contains('\\')) return (exe, args); // msiexec.exe 처럼 경로 없는 이름은 그대로
            at += 4;
        }
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? (cmd, "") : (cmd[..sp], cmd[(sp + 1)..].Trim());
    }

    /// <summary>레지스트리 항목이 아직 있는지 (RegKey = "HKLM64|이름" 같은 꼴).</summary>
    internal static bool Exists(string? regKey)
    {
        if (regKey is null) return false;
        var parts = regKey.Split('|', 2);
        if (parts.Length != 2) return false;
        var (hive, view) = parts[0] switch
        {
            "HKLM64" => (RegistryHive.LocalMachine, RegistryView.Registry64),
            "HKLM32" => (RegistryHive.LocalMachine, RegistryView.Registry32),
            _ => (RegistryHive.CurrentUser, RegistryView.Default),
        };
        try
        {
            using var k = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + parts[1]);
            return k is not null;
        }
        catch { return true; }
    }

    // ── 프로세스 목록 (부모 pid) ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static List<(int Pid, int Parent)> Snapshot()
    {
        var list = new List<(int, int)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* PROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(snap, ref e))
                do list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID)); while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return list;
    }
}
