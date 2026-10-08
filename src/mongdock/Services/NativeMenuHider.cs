using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 실험: 옛날식(표준 Win32 메뉴) 앱 창 안의 메뉴 줄을 떼어 내고 상단바에서만 보이게 (설정 TopBar.HideNativeMenuBars, 기본 꺼짐).
/// - SetMenu(hwnd, NULL) 은 다른 프로세스 창에도 동작하고, 뗀 HMENU 는 살아 있어 읽기·WM_COMMAND 실행이 그대로 된다(직접 확인).
/// - 뗀 HMENU 는 여기 보관 → 기능 끔·일시 정지·종료 때 SetMenu 로 다시 붙임.
/// - 크래시 대비: 뗀 목록을 %APPDATA%\mongdock\cache\hidden-menus.json 에 적어 두고, 다음 시작 때 아직 같은 프로세스의 창이면 복원.
/// - 대상: 메뉴가 고정된 시스템 앱 화이트리스트만. MDI 앱·관리자 권한 창·몽독 자신은 제외.
/// SetMenu 는 대상 스레드에 동기 메시지를 보내므로 항상 백그라운드 스레드에서 부른다.
/// </summary>
internal sealed class NativeMenuHider : IDisposable
{
    /// <summary>메뉴가 동적으로 바뀌지 않는(또는 WM_INITMENUPOPUP 정도만 쓰는) 시스템 앱.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad.exe", "wordpad.exe", "write.exe", "mspaint.exe", "regedit.exe", "msinfo32.exe",
    };

    private sealed record Entry(long Hwnd, long Menu, uint Pid, long StartTicks, string Exe);

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, Entry> _hidden = new();
    private readonly string _statePath;
    private bool _disposed;

    public NativeMenuHider()
    {
        _statePath = Path.Combine(AppInfo.DataDirectory, "cache", "hidden-menus.json");
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
    }

    /// <summary>몽독 시작 때: 지난번에 떼어 놓고 복원하지 못한 메뉴를 다시 붙임 (백그라운드).</summary>
    public void RecoverFromLastRun() => Task.Run(() =>
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_statePath)) ?? new();
            int restored = 0;
            foreach (var e in list)
            {
                IntPtr hwnd = new(e.Hwnd), menu = new(e.Menu);
                if (!User32.IsWindow(hwnd) || !SameProcess(hwnd, e.Pid, e.StartTicks)) continue;
                if (MenuApi.GetMenu(hwnd) != IntPtr.Zero || !MenuApi.IsMenu(menu)) continue;
                if (MenuApi.SetMenu(hwnd, menu)) restored++;
            }
            File.Delete(_statePath);
            if (restored > 0) Log.Warn($"지난 실행에서 숨긴 앱 메뉴 줄 {restored}개 복원");
        }
        catch (Exception ex)
        {
            Log.Error("숨긴 앱 메뉴 줄 복원(지난 실행) 실패", ex);
        }
    });

    /// <summary>hwnd 에서 떼어 둔 HMENU (없으면 Zero). 창이 없어졌으면 정리.</summary>
    public IntPtr GetHiddenMenu(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (!_hidden.TryGetValue(hwnd, out var e)) return IntPtr.Zero;
            if (!User32.IsWindow(hwnd) || !MenuApi.IsMenu(new IntPtr(e.Menu)))
            {
                _hidden.Remove(hwnd);
                SaveLocked();
                return IntPtr.Zero;
            }
            return new IntPtr(e.Menu);
        }
    }

    public bool IsHidden(IntPtr hwnd)
    {
        lock (_gate) return _hidden.ContainsKey(hwnd);
    }

    /// <summary>상단바가 이 창의 Win32 메뉴를 읽은 직후 호출. 대상이면 백그라운드에서 메뉴 줄을 뗌.</summary>
    public void TryHide(IntPtr hwnd, string processPath)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;
        string exe = Path.GetFileName(processPath ?? "");
        if (!Allowed.Contains(exe)) return;
        lock (_gate)
        {
            if (_hidden.ContainsKey(hwnd) && MenuApi.GetMenu(hwnd) == IntPtr.Zero) return;
        }
        Task.Run(() => HideCore(hwnd, exe));
    }

    private void HideCore(IntPtr hwnd, string exe)
    {
        try
        {
            if (_disposed || !User32.IsWindow(hwnd) || MenuApi.IsHungAppWindow(hwnd)) return;
            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return;
            if (Kernel32.IsHigherIntegrity(pid)) return; // UIPI — 관리자 권한 창은 건드리지 않음
            if (User32.FindWindowEx(hwnd, IntPtr.Zero, "MDIClient", null) != IntPtr.Zero) return; // MDI: 자식 창 메뉴 병합
            IntPtr menu = MenuApi.GetMenu(hwnd);
            if (menu == IntPtr.Zero || !MenuApi.IsMenu(menu)) return;
            long start = StartTicks(pid);
            if (start == 0) return;

            lock (_gate)
            {
                if (_disposed) return;
                // 먼저 기록(파일까지) → 떼는 도중 몽독이 죽어도 다음 시작에 복원 가능
                _hidden[hwnd] = new Entry(hwnd.ToInt64(), menu.ToInt64(), pid, start, exe);
                SaveLocked();
            }
            if (!MenuApi.SetMenu(hwnd, IntPtr.Zero) || MenuApi.GetMenu(hwnd) != IntPtr.Zero)
            {
                Log.Warn($"앱 메뉴 줄 숨기기 실패 ({exe}) err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                lock (_gate)
                {
                    _hidden.Remove(hwnd);
                    SaveLocked();
                }
                return;
            }
            Log.Info($"앱 메뉴 줄 숨김: {exe}");
        }
        catch (Exception ex)
        {
            Log.Error("앱 메뉴 줄 숨기기 실패", ex);
        }
    }

    /// <summary>숨긴 메뉴 줄을 모두 되돌림. wait 이면 최대 timeoutMs 만큼 기다림(종료 때).</summary>
    public void RestoreAll(bool wait = false, int timeoutMs = 1500)
    {
        List<(IntPtr Hwnd, Entry E)> list;
        lock (_gate)
        {
            if (_hidden.Count == 0) return;
            list = _hidden.Select(kv => (kv.Key, kv.Value)).ToList();
        }
        var task = Task.Run(() =>
        {
            foreach (var (hwnd, e) in list)
            {
                bool done = false;
                try
                {
                    IntPtr menu = new(e.Menu);
                    if (!User32.IsWindow(hwnd) || !MenuApi.IsMenu(menu)) done = true;
                    else if (MenuApi.GetMenu(hwnd) != IntPtr.Zero) done = true; // 앱이 직접 다시 붙임
                    else if (!MenuApi.IsHungAppWindow(hwnd)) done = MenuApi.SetMenu(hwnd, menu);
                }
                catch (Exception ex)
                {
                    Log.Error("앱 메뉴 줄 복원 실패", ex);
                }
                if (done)
                {
                    lock (_gate)
                    {
                        if (_hidden.TryGetValue(hwnd, out var cur) && cur == e) _hidden.Remove(hwnd);
                    }
                }
            }
            lock (_gate) SaveLocked(); // 복원 못 한 것(응답 없음)은 파일에 남아 다음 시작 때 다시 시도
        });
        if (wait)
        {
            try { task.Wait(timeoutMs); }
            catch (Exception ex) { Log.Error("앱 메뉴 줄 복원 대기 실패", ex); }
        }
    }

    private void SaveLocked()
    {
        try
        {
            if (_hidden.Count == 0)
            {
                if (File.Exists(_statePath)) File.Delete(_statePath);
                return;
            }
            AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(_hidden.Values.ToList()));
        }
        catch (Exception ex)
        {
            Log.Error("숨긴 앱 메뉴 줄 목록 저장 실패", ex);
        }
    }

    private static long StartTicks(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static bool SameProcess(IntPtr hwnd, uint pid, long startTicks)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint now);
        return now == pid && StartTicks(pid) == startTicks;
    }

    private void OnProcessExit(object? sender, EventArgs e) => RestoreAll(wait: true, timeoutMs: 1000);

    private void OnUnhandled(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.IsTerminating) RestoreAll(wait: true, timeoutMs: 1000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        RestoreAll(wait: true);
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
    }
}
