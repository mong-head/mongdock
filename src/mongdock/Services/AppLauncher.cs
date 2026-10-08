using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Mongdock.Models;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>핀 실행, 창 활성화/최소화/닫기. 모든 예외는 로그만 남기고 삼킨다.</summary>
public sealed class AppLauncher : IAppLauncher
{
    private readonly IWindowTracker _tracker;

    public AppLauncher(IWindowTracker tracker)
    {
        _tracker = tracker;
    }

    public void Launch(PinItem pin)
    {
        if (pin is null) return;
        try
        {
            switch (pin.Kind)
            {
                case PinKind.Exe:
                    LaunchExe(pin);
                    break;
                case PinKind.Aumid:
                    LaunchAumid(pin.Target.Trim(), pin.Arguments);
                    break;
                case PinKind.Special:
                    LaunchSpecial(pin.Target.Trim());
                    break;
                case PinKind.Separator:
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"실행 실패: {pin.Kind} '{pin.Target}'", ex);
        }
    }

    private static void LaunchExe(PinItem pin)
    {
        string target = Environment.ExpandEnvironmentVariables((pin.Target ?? "").Trim().Trim('"'));
        if (target.Length == 0)
        {
            Log.Warn($"실행할 경로가 없는 핀 → 무시: '{pin.Name}'");
            return;
        }
        // 전체 경로인데 없으면 실행하지 않음 (파일명만이면 ShellExecute 가 PATH/App Paths 에서 찾게 둠)
        if (Path.IsPathFullyQualified(target) && !File.Exists(target) && !Directory.Exists(target))
        {
            Log.Warn($"실행 파일이 없음 → 무시: {target}");
            return;
        }
        var psi = new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            Arguments = pin.Arguments ?? "",
        };
        string? dir = File.Exists(target) ? Path.GetDirectoryName(target) : null;
        if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
        Process.Start(psi)?.Dispose();
        Log.Info($"실행(exe): {target}");
    }

    private static void LaunchAumid(string aumid, string? arguments)
    {
        if (aumid.Length == 0) return;

        // 인자가 있으면 IApplicationActivationManager (explorer 경유로는 인자 전달 불가)
        if (!string.IsNullOrWhiteSpace(arguments) && TryActivateApplication(aumid, arguments)) return;

        try
        {
            var psi = new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + aumid) { UseShellExecute = true };
            Process.Start(psi)?.Dispose();
            Log.Info($"실행(aumid via explorer): {aumid}");
        }
        catch (Exception ex)
        {
            Log.Error($"explorer shell:AppsFolder 실행 실패 → ActivateApplication 시도: {aumid}", ex);
            TryActivateApplication(aumid, arguments);
        }
    }

    private static bool TryActivateApplication(string aumid, string? arguments)
    {
        object? mgr = null;
        try
        {
            mgr = new ApplicationActivationManager();
            int hr = ((IApplicationActivationManager)mgr).ActivateApplication(aumid, arguments, 0, out uint pid);
            if (hr != 0)
            {
                Log.Error($"ActivateApplication 실패 hr=0x{hr:X8}: {aumid}");
                return false;
            }
            Log.Info($"실행(aumid via ActivateApplication): {aumid} pid={pid}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"ActivateApplication 예외: {aumid}", ex);
            return false;
        }
        finally
        {
            if (mgr is not null && Marshal.IsComObject(mgr)) Marshal.ReleaseComObject(mgr);
        }
    }

    private static void LaunchSpecial(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "launchpad":
            case "start":
                // 시작 메뉴 열기: Win 키 down/up
                KeyChord.Send("Win", User32.VK_LWIN);
                break;
            default:
                Log.Warn($"알 수 없는 Special 핀: '{name}'");
                break;
        }
    }

    /// <summary>창을 앞으로. 다른 가상 데스크톱의 창이면 먼저 그 데스크톱으로 이동(Ctrl+Win+←/→)한 뒤 활성화.</summary>
    public void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return;
        if (VirtualDesktopHelper.IsOnCurrentDesktop(hwnd) == false)
            _ = SwitchDesktopThenActivateAsync(hwnd);
        else
            ActivateWindow(hwnd);
    }

    /// <summary>
    /// 창이 있는 데스크톱으로 이동(VirtualDesktopService 의 공유 게이트 사용) → 도착하면 활성화.
    /// 이동이 진행 중이거나, 수식키가 눌려 있거나, 도착을 확인하지 못하면 활성화하지 않고 로그만 남김.
    /// </summary>
    private static async Task SwitchDesktopThenActivateAsync(IntPtr hwnd)
    {
        try
        {
            int target = VirtualDesktopHelper.GetDesktopIndex(hwnd, VirtualDesktopService.ReadDesktopIds());
            if (target <= 0)
            {
                Log.Warn("창의 데스크톱 번호를 알 수 없음 → 바로 활성화 시도");
                ActivateWindow(hwnd);
                return;
            }
            if (!await VirtualDesktopService.MoveToAsync(target))
            {
                Log.Warn($"데스크톱 {target} 로 이동하지 못해 활성화 생략 hwnd=0x{hwnd.ToInt64():X}");
                return;
            }
            ActivateWindow(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("다른 데스크톱 창 활성화 실패", ex);
        }
    }

    /// <summary>창을 앞으로 (최소화면 복원). AppMenuService 등 다른 서비스도 사용. 실패 시 false.</summary>
    internal static bool ActivateWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return false;
            // 응답 없는 창에 동기 호출을 하면 UI 스레드가 멈추므로 비동기 ShowWindowAsync 사용
            if (User32.IsIconic(hwnd)) User32.ShowWindowAsync(hwnd, User32.SW_RESTORE);

            if (TrySetForeground(hwnd)) return true;

            // 관리자 권한(더 높은 무결성) 창: UIPI 때문에 AttachThreadInput·입력 주입이 막힘 → 다른 순서로
            User32.GetWindowThreadProcessId(hwnd, out uint targetPid);
            if (targetPid != 0 && Kernel32.IsHigherIntegrity(targetPid))
                return ActivateElevated(hwnd, targetPid);

            // 1) 빈 입력 이벤트: "마지막 입력을 받은 프로세스" 조건을 만족시켜 포그라운드 잠금을 푼다.
            //    (예전 Alt 키 트릭은 대상 앱의 메뉴바를 활성화할 수 있어 0 이동 마우스 입력으로 대체)
            User32.Send(User32.EmptyMouseInput());
            if (TrySetForeground(hwnd)) return true;

            // 2) 포그라운드 스레드에 입력 큐를 붙여서 재시도 — 응답 없는 창이 관련되면 UI 스레드가 멈출 수 있어 건너뜀
            IntPtr fg = User32.GetForegroundWindow();
            uint fgThread = fg != IntPtr.Zero ? User32.GetWindowThreadProcessId(fg, out _) : 0;
            uint myThread = Kernel32.GetCurrentThreadId();
            bool hung = (fg != IntPtr.Zero && DesktopApi.IsHungAppWindow(fg)) || DesktopApi.IsHungAppWindow(hwnd);
            if (!hung && fgThread != 0 && fgThread != myThread)
            {
                bool attached = User32.AttachThreadInput(myThread, fgThread, true);
                try
                {
                    User32.BringWindowToTop(hwnd);
                    if (TrySetForeground(hwnd)) return true;
                }
                finally
                {
                    if (attached) User32.AttachThreadInput(myThread, fgThread, false);
                }
            }
            Log.Warn($"SetForegroundWindow 실패 hwnd=0x{hwnd.ToInt64():X}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Activate 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// 관리자 권한 창(작업 관리자, UAC 프롬프트 consent.exe 등) 활성화:
    /// (a) SetForegroundWindow — 포그라운드 잠금은 호출 프로세스(방금 사용자 클릭을 받은 우리) 기준이라 대상 무결성과 무관
    /// (b) 실패 시 SwitchToThisWindow(hwnd, TRUE)
    /// (c) 최소화 상태면 ShowWindowAsync(SW_RESTORE) 후 다시 시도. 결과는 로그.
    /// UAC 프롬프트가 보안 데스크톱에 떠 있으면 어떤 방법으로도 접근할 수 없다.
    /// </summary>
    private static bool ActivateElevated(IntPtr hwnd, uint pid)
    {
        string who = $"hwnd=0x{hwnd.ToInt64():X} ({Kernel32.ProcessName(pid) ?? "?"}, 관리자 권한)";
        if (TrySetForeground(hwnd)) { Log.Info($"활성화 성공(SetForegroundWindow): {who}"); return true; }

        User32.SwitchToThisWindow(hwnd, true);
        if (User32.GetForegroundWindow() == hwnd) { Log.Info($"활성화 성공(SwitchToThisWindow): {who}"); return true; }

        if (User32.IsIconic(hwnd))
        {
            User32.ShowWindowAsync(hwnd, User32.SW_RESTORE);
            Thread.Sleep(50);
            if (TrySetForeground(hwnd)) { Log.Info($"활성화 성공(복원 후): {who}"); return true; }
            User32.SwitchToThisWindow(hwnd, true);
            if (User32.GetForegroundWindow() == hwnd) { Log.Info($"활성화 성공(복원 후 SwitchToThisWindow): {who}"); return true; }
        }
        Log.Warn($"관리자 권한 창 활성화 실패: {who}");
        return false;
    }

    private static bool TrySetForeground(IntPtr hwnd)
    {
        User32.SetForegroundWindow(hwnd);
        return User32.GetForegroundWindow() == hwnd;
    }

    public void ToggleActivate(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return;
            IntPtr fg = User32.GetForegroundWindow();
            if (fg == IntPtr.Zero) fg = _tracker.ForegroundWindow; // 전환 중 등 일시적으로 0 일 때
            if (fg == hwnd && !User32.IsIconic(hwnd) && VirtualDesktopHelper.IsOnCurrentDesktop(hwnd) != false)
            {
                User32.ShowWindowAsync(hwnd, User32.SW_MINIMIZE);
                return;
            }
            Activate(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("ToggleActivate 실패", ex);
        }
    }

    // ───────────────────────── 브라우저 프로필 ─────────────────────────

    /// <summary>크로미움 계열(크롬·엣지·웨일) Exe 핀의 프로필 목록 (Local State 순서). 그 외는 빈 목록.</summary>
    public IReadOnlyList<AppProfile> GetProfiles(PinItem pin)
    {
        try
        {
            if (pin is null || pin.Kind != PinKind.Exe) return Array.Empty<AppProfile>();
            string exe = Environment.ExpandEnvironmentVariables(pin.Target.Trim().Trim('"'));
            return ChromiumProfiles.Read(exe)
                .Select(p => new AppProfile(p.Dir, p.Name, ChromiumProfiles.LoadPicture(p.PicturePath)))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("프로필 목록 조회 실패", ex);
            return Array.Empty<AppProfile>();
        }
    }

    /// <summary>해당 프로필로 새 창: exe --profile-directory="Profile 1".</summary>
    public void LaunchProfile(PinItem pin, AppProfile profile)
    {
        try
        {
            if (pin is null || profile is null) return;
            string exe = Environment.ExpandEnvironmentVariables(pin.Target.Trim().Trim('"'));
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = $"--profile-directory=\"{profile.Id.Replace("\"", "")}\"",
            };
            string? dir = File.Exists(exe) ? Path.GetDirectoryName(exe) : null;
            if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            Process.Start(psi)?.Dispose();
            Log.Info($"프로필로 실행: {exe} {psi.Arguments}");
        }
        catch (Exception ex)
        {
            Log.Error($"프로필 실행 실패: {profile?.Id}", ex);
        }
    }

    /// <summary>창 최소화 (비동기 ShowWindowAsync — 응답 없는 창에도 UI 스레드가 멈추지 않음).</summary>
    public void Minimize(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return;
            User32.ShowWindowAsync(hwnd, User32.SW_MINIMIZE);
        }
        catch (Exception ex)
        {
            Log.Error("Minimize 실패", ex);
        }
    }

    public void Close(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            if (!User32.PostMessage(hwnd, User32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                Log.Warn($"WM_CLOSE 전송 실패 hwnd=0x{hwnd.ToInt64():X} err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Log.Error("Close 실패", ex);
        }
    }

    public void OpenFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"파일 열기 실패: {path}", ex);
        }
    }
}
