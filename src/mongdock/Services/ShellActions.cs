using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 원격(StarDesk)에서 Win 단축키가 안 넘어가므로 로컬에서 SendInput 으로 대신 보냄.
/// + 로고 메뉴(맥 Apple 메뉴 대응) 동작. 전원 동작은 UI 가 확인 카드를 띄운 뒤에만 호출할 것.
/// </summary>
public sealed class ShellActions : IShellActions
{
    public void OpenStartMenu() => KeyChord.Send("Win", User32.VK_LWIN);

    public void OpenSearch() => KeyChord.Send("Win+S", User32.VK_LWIN, User32.VK_S);

    public void OpenQuickSettings() => KeyChord.Send("Win+A", User32.VK_LWIN, User32.VK_A);

    /// <summary>Win11 은 Win+N(알림 센터). 윈도우 10 은 Win+N 이 없고 알림이 관리 센터(Win+A) 안에 있다.</summary>
    public void OpenNotificationCenter()
    {
        if (Environment.OSVersion.Version.Build < 22000) KeyChord.Send("Win+A", User32.VK_LWIN, User32.VK_A);
        else KeyChord.Send("Win+N", User32.VK_LWIN, User32.VK_N);
    }

    public void OpenTaskView() => KeyChord.Send("Win+Tab", User32.VK_LWIN, User32.VK_TAB);

    public void ShowDesktop() => KeyChord.Send("Win+D", User32.VK_LWIN, User32.VK_D);

    public void OpenAbout() => ShellOpen("ms-settings:about");

    public void OpenSettings() => ShellOpen("ms-settings:");

    public void OpenStore() => ShellOpen("ms-windows-store:");

    public void OpenTaskManager() => ShellOpen("taskmgr.exe");

    /// <summary>절전 (최대 절전 아님). SetSuspendState(hibernate=false, force=false, wakeupEventsDisabled=false).</summary>
    public void Sleep()
    {
        try
        {
            Log.Info("절전 요청");
            if (!PowerApi.SetSuspendState(false, false, false))
                Log.Error($"SetSuspendState 실패 err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log.Error("절전 실패", ex); }
    }

    public void Restart() => RunShutdown("/r /t 0", "다시 시작");

    public void Shutdown() => RunShutdown("/s /t 0", "시스템 종료");

    /// <summary>업데이트를 설치하고 다시 시작 (시작 메뉴 「업데이트 후 다시 시작」과 같게). 안 되면 보통 다시 시작.</summary>
    public void RestartWithUpdates()
    {
        if (!InitiateWithUpdates(restart: true)) RunShutdown("/r /t 0", "다시 시작 (업데이트 설치 요청 실패 — 보통으로)");
    }

    /// <summary>업데이트를 설치하고 시스템 종료. 안 되면 보통 종료.</summary>
    public void ShutdownWithUpdates()
    {
        if (!InitiateWithUpdates(restart: false)) RunShutdown("/s /t 0", "시스템 종료 (업데이트 설치 요청 실패 — 보통으로)");
    }

    private const uint SHUTDOWN_RESTART = 0x4, SHUTDOWN_POWEROFF = 0x8, SHUTDOWN_INSTALL_UPDATES = 0x40;
    private const uint SHTDN_REASON_MAJOR_OPERATINGSYSTEM = 0x00020000, SHTDN_REASON_MINOR_UPGRADE = 0x00000003, SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint InitiateShutdownW(string? machine, string? message, uint gracePeriod, uint flags, uint reason);

    /// <summary>InitiateShutdown + SHUTDOWN_INSTALL_UPDATES (관리자 권한 필요 없음, 종료 권한만 켬). 성공하면 true.</summary>
    private static bool InitiateWithUpdates(bool restart)
    {
        string what = restart ? "업데이트 후 다시 시작" : "업데이트 후 시스템 종료";
        try
        {
            Log.Info($"{what} 요청");
            if (!EnableShutdownPrivilege()) Log.Warn("종료 권한 켜기 실패 — 그래도 시도");
            uint flags = (restart ? SHUTDOWN_RESTART : SHUTDOWN_POWEROFF) | SHUTDOWN_INSTALL_UPDATES;
            uint err = InitiateShutdownW(null, null, 0, flags, SHTDN_REASON_MAJOR_OPERATINGSYSTEM | SHTDN_REASON_MINOR_UPGRADE | SHTDN_REASON_FLAG_PLANNED);
            if (err == 0) return true;
            Log.Warn($"{what}: InitiateShutdown 실패 {err}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"{what} 실패", ex);
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static bool EnableShutdownPrivilege()
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out var luid)) return false;
            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally { CloseHandle(token); }
    }

    public void LockScreen()
    {
        try
        {
            if (!PowerApi.LockWorkStation()) Log.Error($"LockWorkStation 실패 err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log.Error("화면 잠금 실패", ex); }
    }

    public void SignOut()
    {
        try
        {
            Log.Info("로그아웃 요청");
            if (!PowerApi.ExitWindowsEx(PowerApi.EWX_LOGOFF, PowerApi.SHTDN_REASON_FLAG_PLANNED))
                Log.Error($"ExitWindowsEx(LOGOFF) 실패 err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex) { Log.Error("로그아웃 실패", ex); }
    }

    private static void RunShutdown(string args, string what)
    {
        try
        {
            Log.Info($"{what} 요청 (shutdown {args})");
            string exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex) { Log.Error($"{what} 실패", ex); }
    }

    private static void ShellOpen(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error($"열기 실패: {target}", ex); }
    }
}
