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

    /// <summary>시험에서만: 스토어판처럼 셸로 실행 (패키지가 아닌 시험에서 그 경로를 돌려 보려고).</summary>
    internal static bool ShellForTest { get; set; }

    // ── 지금 도는 제거 프로그램 (판의 [제거 창 보기]·[기다리지 않기]) ──

    private static volatile int[] _currentPids = Array.Empty<int>();
    private static volatile bool _skipWait;
    /// <summary>지금 제거 프로그램이 시작된 때 (판이 "오래 걸리면 [기다리지 않기]"를 정하려고).</summary>
    public static DateTime? CurrentStarted { get; private set; }

    /// <summary>[기다리지 않기]: 지금 것은 기다리지 않고(항목이 남았으면 실패로) 다음으로.</summary>
    public static void SkipWait() { _skipWait = true; Log.Info("잠자는 앱 지우기: 기다리지 않기 누름"); }

    /// <summary>
    /// [제거 창 보기]·제거 창이 처음 뜰 때: 제거 프로그램(과 그 자식)의 보이는 창 중 가장 큰 것을 앞으로.
    /// 판이 맨 위(Topmost)라 제거 창을 가려 "멈춘 것처럼" 보였음 (2026-10-14 사용자: 지우는데 갑자기 안 지워짐). 찾으면 true.
    /// </summary>
    public static bool BringWindowToFront()
    {
        var pids = _currentPids;
        if (pids.Length == 0) return false;
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (Array.IndexOf(pids, (int)pid) < 0 || !GetWindowRect(h, out var r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero) return false;
        if (IsIconic(best)) ShowWindow(best, 9 /* SW_RESTORE */);
        SetForegroundWindow(best);
        return true;
    }

    /// <summary>제거 프로그램이 띄우는 웹 브라우저(설문 페이지)·탐색기는 기다리지 않음 — 끝나지 않아 "멈춘" 것처럼 됨.</summary>
    private static readonly HashSet<string> NotUninstaller = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "whale.exe", "opera.exe", "brave.exe", "iexplore.exe", "explorer.exe", "conhost.exe", "msedgewebview2.exe",
    };

    private static Outcome RunOne(Removal what)
    {
        if (what.Family is { Length: > 0 } fam)
        {
            // 스토어 앱: 몽독이 직접 지우지 않고 그 앱의 설정 페이지 (패키지 관리 권한은 Store 제한 기능이라)
            try { using (Process.Start(new ProcessStartInfo("ms-settings:appsfeatures-app?" + fam) { UseShellExecute = true })) { } Log.Info("잠자는 앱 지우기: 스토어 앱 — 설정 페이지 엶"); return Outcome.OpenedSettings; }
            catch (Exception ex) { Log.Warn($"앱 설정 열기 실패: {ex.GetType().Name}"); return Outcome.Failed; }
        }
        if (what.Command is not { Length: > 0 } cmd || Split(cmd) is not { } start)
        {
            Log.Info("잠자는 앱 지우기: 제거 명령을 못 읽음 — 건너뜀");
            return Outcome.Skipped;
        }
        start = (start.Exe, MsiRemoveArgs(start.Exe, start.Args));
        string exeName = Path.GetFileName(start.Exe);
        string kind = exeName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || exeName.Equals("msiexec", StringComparison.OrdinalIgnoreCase) ? "msiexec" : "exe";
        _skipWait = false;
        var started = DateTime.Now;
        CurrentStarted = started;
        Process? root = null;
        try
        {
            if (AppInfo.IsPackaged || ShellForTest)
            {
                // 스토어(MSIX)판: 몽독의 자식으로 띄우면 패키지 가상화 안에서 돌아 제거가 제대로 안 될 수 있음 → 셸(탐색기)에게 실행을 맡김
                ShellRun(start.Exe, start.Args);
                Log.Info($"잠자는 앱 지우기: 제거 프로그램 시작 ({kind}, 셸로 실행 — 패키지 밖)");
            }
            else
            {
                root = Process.Start(new ProcessStartInfo(start.Exe, start.Args) { UseShellExecute = true });
                Log.Info($"잠자는 앱 지우기: 제거 프로그램 시작 ({kind}{(root is null ? ", 프로세스 없음" : "")})");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log.Info("잠자는 앱 지우기: 관리자 권한 창에서 [아니요]"); CurrentStarted = null; return Outcome.Failed; }
        catch (Exception ex) { Log.Warn($"제거 프로그램 실행 실패: {ex.GetType().Name} {ex.Message}"); CurrentStarted = null; return Outcome.Failed; }

        var family = new HashSet<int>();
        if (root is not null) family.Add(root.Id);
        var limit = Stopwatch.StartNew();
        string end = "시간 넘음";
        try
        {
            while (limit.Elapsed < WaitLimit)
            {
                Thread.Sleep(500);
                // 1) 항목이 사라졌으면 지움 — 제거 프로그램 마지막 창(완료 안내)이 떠 있어도 다음으로
                if (!Exists(what.RegKey)) { end = "항목 사라짐"; break; }
                if (_skipWait) { end = "기다리지 않기"; break; }
                // 2) 제거 프로그램과 그 자식: 시작 뒤에 생긴 같은 이름 프로세스(자기를 다시 띄우는 것·셸로 띄운 것)도 포함, 브라우저·탐색기는 뺌
                var alive = Snapshot();
                foreach (var x in alive)
                {
                    if (NotUninstaller.Contains(x.Name)) continue;
                    if (family.Contains(x.Parent) || (x.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase) && !family.Contains(x.Pid) && ProcessStartedAfter(x.Pid, started)))
                        family.Add(x.Pid);
                }
                var live = alive.Where(x => family.Contains(x.Pid) && !NotUninstaller.Contains(x.Name) && ProcessStartedAfter(x.Pid, started)).Select(x => x.Pid).ToArray();
                _currentPids = live;
                // 셸로 띄운 경우 처음 몇 초는 프로세스가 아직 안 보일 수 있음
                if (live.Length == 0 && (root is not null || limit.Elapsed > TimeSpan.FromSeconds(5)))
                {
                    // 끝나고 레지스트리가 늦게 지워지는 제거 프로그램도 있어서 3초 더 봄
                    for (int t = 0; t < 6 && Exists(what.RegKey); t++) Thread.Sleep(500);
                    end = "제거 프로그램 끝남";
                    break;
                }
            }
        }
        finally
        {
            _currentPids = Array.Empty<int>();
            CurrentStarted = null;
        }
        string code = "";
        if (root is not null)
        {
            try { if (root.HasExited) code = $", 종료 코드 {root.ExitCode}"; } catch { /* 관리자 권한 프로세스 — 못 읽음 */ }
            root.Dispose();
        }
        bool removed = !Exists(what.RegKey);
        Log.Info($"잠자는 앱 지우기: {end} ({limit.Elapsed.TotalSeconds:0}초{code}) → {(removed ? "지움" : "항목 남음")}");
        return removed ? Outcome.Removed : Outcome.Failed;
    }

    /// <summary>MsiExec /I{GUID} (설치·고치기 창) → /X{GUID} (제거). 제어판처럼 제거 화면이 바로 뜨게.</summary>
    internal static string MsiRemoveArgs(string exe, string args)
    {
        string name = Path.GetFileName(exe);
        if (!name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("msiexec", StringComparison.OrdinalIgnoreCase)) return args;
        return System.Text.RegularExpressions.Regex.Replace(args, @"(^|\s)[/-][Ii]\s*(\{[0-9A-Fa-f-]{36}\})", "$1/X$2");
    }

    /// <summary>셸(Shell.Application)로 실행 — 탐색기가 띄우므로 몽독 패키지 밖. STA 스레드에서.</summary>
    private static void ShellRun(string exe, string args)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application 없음");
                object shell = Activator.CreateInstance(type)!;
                try { type.InvokeMember("ShellExecute", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { exe, args, "", "open", 1 }); }
                finally { Marshal.FinalReleaseComObject(shell); }
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
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

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc f, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out WinRect r);
    [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int Left, Top, Right, Bottom; }

    private static List<(int Pid, int Parent, string Name)> Snapshot()
    {
        var list = new List<(int, int, string)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* PROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(snap, ref e))
                do list.Add(((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile ?? "")); while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return list;
    }
}
