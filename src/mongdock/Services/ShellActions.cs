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
