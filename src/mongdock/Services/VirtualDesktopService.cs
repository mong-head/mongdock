using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 가상 데스크톱 이동/생성 단축키(Ctrl+Win+←/→/D)를 SendInput 으로 로컬에서 전송 (키 전송은 Native.KeyChord 공유).
/// 현재 번호/개수는 레지스트리에서 읽는다:
///   HKCU\...\Explorer\VirtualDesktops : VirtualDesktopIDs (16바이트 GUID 배열), CurrentVirtualDesktop (Win11)
///   없으면 HKCU\...\Explorer\SessionInfo\&lt;세션ID&gt;\VirtualDesktops\CurrentVirtualDesktop
/// 변경 감지: 백그라운드 스레드에서 RegNotifyChangeKeyValue 대기 (+ 1초 타임아웃 재확인). Changed 는 UI 스레드.
/// 생성자는 UI 스레드에서 호출.
/// </summary>
public sealed class VirtualDesktopService : IVirtualDesktopService, IDisposable
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    private readonly Dispatcher _dispatcher;
    private readonly Thread _watcher;
    private readonly ManualResetEvent _stop = new(false);
    private volatile int _current;
    private volatile int _count;

    public int CurrentIndex => _current;
    public int Count => _count;
    public event EventHandler? Changed;

    public VirtualDesktopService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        (_current, _count) = Read();
        _watcher = new Thread(WatchLoop) { IsBackground = true, Name = "mongdock.VirtualDesktops" };
        _watcher.Start();
    }

    public void Previous() => _ = RunGatedAsync("prev", async () => { SendStep(-1); await Task.Delay(KeyGapMs); return true; });

    public void Next() => _ = RunGatedAsync("next", async () => { SendStep(+1); await Task.Delay(KeyGapMs); return true; });

    public void New() => _ = RunGatedAsync("new", async () =>
    {
        KeyChord.Send("new", User32.VK_LCONTROL, User32.VK_LWIN, User32.VK_D);
        await Task.Delay(KeyGapMs);
        return true;
    });

    // ───────────────────────── 이동 게이트 ─────────────────────────
    // Previous/Next/New/MoveToAsync(AppLauncher 의 다른 데스크톱 창 활성화) 가 하나의 게이트를 공유:
    // 진행 중이면 새 요청은 무시(로그), 수식키가 눌려 있으면 최대 300ms 기다렸다가 그래도 눌려 있으면 취소.

    private const int KeyGapMs = 120;
    private static int _busy;

    private static void SendStep(int dir) =>
        KeyChord.Send(dir > 0 ? "next" : "prev", User32.VK_LCONTROL, User32.VK_LWIN, dir > 0 ? User32.VK_RIGHT : User32.VK_LEFT);

    private static async Task<bool> RunGatedAsync(string what, Func<Task<bool>> body)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            Log.Info($"가상 데스크톱 이동 진행 중 → '{what}' 요청 무시");
            return false;
        }
        try
        {
            for (int i = 0; i < 10 && AnyModifierDown(); i++) await Task.Delay(30);
            if (AnyModifierDown())
            {
                Log.Warn($"수식키가 눌려 있어 가상 데스크톱 '{what}' 취소");
                return false;
            }
            return await body();
        }
        catch (Exception ex)
        {
            Log.Error($"가상 데스크톱 '{what}' 실패", ex);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static bool AnyModifierDown() =>
        MenuApi.IsKeyDown(0x10) || MenuApi.IsKeyDown(0x11) || MenuApi.IsKeyDown(0x12) ||
        MenuApi.IsKeyDown(0x5B) || MenuApi.IsKeyDown(0x5C);

    /// <summary>
    /// target 번호(1부터)의 데스크톱으로 이동: 차이만큼 Ctrl+Win+←/→ (키 사이 120ms) → 최대 1초 도착 확인.
    /// 도착하면 true. 게이트가 사용 중이거나 수식키가 눌려 있거나 도착 실패면 false (로그).
    /// </summary>
    internal static Task<bool> MoveToAsync(int target) => RunGatedAsync($"move to {target}", async () =>
    {
        var (current, count) = Read();
        if (target <= 0 || current <= 0 || target > Math.Max(count, 1))
        {
            Log.Warn($"가상 데스크톱 이동 불가: target={target} current={current} count={count}");
            return false;
        }
        int diff = target - current;
        for (int i = 0; i < Math.Abs(diff); i++)
        {
            if (i > 0) await Task.Delay(KeyGapMs);
            SendStep(Math.Sign(diff));
        }
        for (int i = 0; i < 10; i++)
        {
            if (Read().Current == target) return true;
            await Task.Delay(100);
        }
        bool ok = Read().Current == target;
        if (!ok) Log.Warn($"가상 데스크톱 이동 확인 실패: 목표 {target}, 현재 {Read().Current}");
        return ok;
    });

    /// <summary>데스크톱 전환/추가/삭제 (백그라운드 스레드에서 발생 — 받는 쪽이 UI 스레드로 넘길 것). WindowTracker 가 목록 갱신에 사용.</summary>
    internal static event EventHandler? DesktopsChangedStatic;

    /// <summary>가상 데스크톱 ID 목록 (작업 보기 순서). 데스크톱을 추가한 적 없으면 빈 목록.</summary>
    internal static List<Guid> ReadDesktopIds()
    {
        var list = new List<Guid>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue("VirtualDesktopIDs") is byte[] ids)
                for (int i = 0; i + 16 <= ids.Length; i += 16)
                    list.Add(new Guid(ids.AsSpan(i, 16)));
        }
        catch (Exception ex)
        {
            Log.Warn($"가상 데스크톱 ID 읽기 실패: {ex.Message}");
        }
        return list;
    }

    /// <summary>(현재 번호 1부터, 개수). 모르면 0.</summary>
    internal static (int Current, int Count) Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids || ids.Length < 16)
                return (1, 1); // 데스크톱을 한 번도 추가하지 않은 PC 는 값이 없음 → 1개
            int count = ids.Length / 16;

            byte[]? cur = key.GetValue("CurrentVirtualDesktop") as byte[];
            if (cur is null || cur.Length != 16)
            {
                using var s = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{Process.GetCurrentProcess().SessionId}\VirtualDesktops");
                cur = s?.GetValue("CurrentVirtualDesktop") as byte[];
            }
            if (cur is null || cur.Length != 16) return (0, count);

            for (int i = 0; i < count; i++)
                if (ids.AsSpan(i * 16, 16).SequenceEqual(cur)) return (i + 1, count);
            return (0, count);
        }
        catch (Exception ex)
        {
            Log.Warn($"가상 데스크톱 상태 읽기 실패: {ex.Message}");
            return (0, 0);
        }
    }

    private void WatchLoop()
    {
        using var changed = new AutoResetEvent(false);
        RegistryKey? key = null;
        try
        {
            bool armed = false; // 알림이 등록돼 있고 아직 신호되지 않음
            var handles = new WaitHandle[] { _stop, changed };
            while (!_stop.WaitOne(0))
            {
                // 등록은 신호를 받은 뒤(또는 처음/키가 새로 생겼을 때)에만 다시 한다.
                // 키가 없다가 생길 수 있으므로(첫 데스크톱 추가) 열리지 않았으면 매번 시도.
                if (!armed)
                {
                    key ??= Registry.CurrentUser.OpenSubKey(KeyPath);
                    if (key is not null)
                    {
                        int rc = RegNotifyChangeKeyValue(key.Handle, true,
                            REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true);
                        if (rc == 0) armed = true;
                        else { key.Dispose(); key = null; }
                    }
                }
                // 알림 → 재등록 필요. 1초 타임아웃(세션별 키 fallback 대비) → Check 만.
                int which = WaitHandle.WaitAny(handles, TimeSpan.FromSeconds(1));
                if (which == 0) break;
                if (which == 1) armed = false;
                Check();
            }
        }
        catch (Exception ex)
        {
            Log.Error("가상 데스크톱 감시 실패", ex);
        }
        finally
        {
            key?.Dispose();
        }
    }

    private void Check()
    {
        var (cur, count) = Read();
        if (cur == _current && count == _count) return;
        _current = cur;
        _count = count;
        try { DesktopsChangedStatic?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("DesktopsChangedStatic 핸들러 예외", ex); }
        _dispatcher.InvokeAsync(() =>
        {
            try { Changed?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { Log.Error("VirtualDesktop.Changed 핸들러 예외", ex); }
        });
    }

    public void Dispose() => _stop.Set();

    private const int REG_NOTIFY_CHANGE_NAME = 0x1;
    private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int notifyFilter, SafeWaitHandle hEvent, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
