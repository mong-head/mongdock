using System.Runtime.InteropServices;
using System.Windows.Threading;
using MyDock.Models;
using MyDock.Native;
using K = MyDock.Native.KeyboardHookApi;

namespace MyDock.Services;

/// <summary>
/// Spotlight 전역 단축키 (Win/Alt/Ctrl + Space) — 저수준 키보드 훅(WH_KEYBOARD_LL).
///
/// RegisterHotKey 를 쓰지 않는 이유:
/// - Win+Space 는 윈도우가 입력 언어 전환용으로 예약 → RegisterHotKey 가 ERROR_HOTKEY_ALREADY_REGISTERED(1409)로 실패.
/// - Alt+Space 는 등록은 되지만 Space 만 가로채면 Alt 단독 탭으로 보여 앱 메뉴 막대가 활성화됨 → 어차피 훅이 필요.
/// 그래서 세 가지 모두 같은 훅 경로로 처리한다 (키 반복 무시·원격 입력 처리도 한 곳에서).
///
/// 동작:
/// - 수정자(Win/Alt/Ctrl 중 정확히 그것만, Shift 없이)가 눌린 상태의 Space 다운 → 삼키고 Pressed (UI 스레드, BeginInvoke).
///   같은 Space 의 반복 다운·업도 삼킴 → 누르고 있어도 한 번만.
/// - Win/Alt 인 경우 Space 를 삼킨 순간 더미 키(VK 0xE8, 미할당) 다운/업을 주입 → 수정자를 뗄 때
///   "단독 탭"으로 인식되지 않아 시작 메뉴(Win)·메뉴 막대(Alt)가 열리지 않는다.
/// - 내가 주입한 입력(LLKHF_INJECTED + dwExtraInfo == InjectMarker)은 무시. 다른 프로세스가 주입한 키
///   (원격 데스크톱·StarDesk 등)는 실제 키처럼 처리한다.
///
/// 한계: 관리자 권한 창(작업 관리자 등)이 포그라운드면 UIPI 때문에 LL 훅이 그 키를 받지 못한다.
/// 훅은 전용 백그라운드 스레드(<see cref="LowLevelHookThread"/>)에서 돈다 — UI 가 바빠도 시스템 키 입력이 늦지 않고,
/// LowLevelHooksTimeout 초과로 훅이 조용히 빠질 위험이 줄어든다. 그래도 빠질 수 있어 <see cref="Reinstall"/> 제공
/// (세션 잠금 해제·디스플레이 변경 때 컨트롤러가 호출).
/// </summary>
public sealed class SpotlightHotkeyService : IDisposable
{
    /// <summary>내가 주입한 입력 표식 (dwExtraInfo). "MONG".</summary>
    private static readonly IntPtr InjectMarker = new(0x4D4F4E47);
    /// <summary>마지막으로 삼킨 Space 다운 이후 이 시간 넘게 지나면 반복이 아닌 새 누름으로 본다 (Space 업을 놓친 경우 대비).</summary>
    private const long RepeatGapMs = 1500;

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelHookThread _hook;
    private volatile SpotlightHotkey _mode = SpotlightHotkey.None;
    // 아래 둘은 훅 스레드에서만 읽고 쓴다
    private bool _spaceHeld;       // 우리가 삼킨 Space 가 아직 눌려 있음
    private long _lastSpaceDownAt;
    private bool _disposed;

    /// <summary>단축키가 눌림 (UI 스레드).</summary>
    public event EventHandler? Pressed;

    /// <summary>UI 스레드에서 만들 것 (Pressed 를 그 Dispatcher 로 올린다).</summary>
    public SpotlightHotkeyService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hook = new LowLevelHookThread(K.WH_KEYBOARD_LL, "전역 키보드", OnKey);
    }

    /// <summary>단축키 적용. None 이면 훅 해제. 같은 값이면 아무것도 안 함.</summary>
    public void Apply(SpotlightHotkey mode)
    {
        if (_disposed) return;
        if (mode == _mode && (mode == SpotlightHotkey.None || _hook.IsRunning)) return;
        _mode = mode;
        if (mode == SpotlightHotkey.None)
        {
            _hook.Stop();
            return;
        }
        if (!_hook.IsRunning)
        {
            _hook.Stop(); // 설치 실패로 반쯤 남은 상태 정리
            if (_hook.Start()) Log.Info($"Spotlight 단축키 {mode}");
        }
        else Log.Info($"Spotlight 단축키 변경: {mode}");
    }

    /// <summary>훅 재설치 (켜져 있을 때만). 조용히 제거됐을 경우 대비.</summary>
    public void Reinstall(string reason)
    {
        if (_disposed || _mode == SpotlightHotkey.None) return;
        _hook.Restart(reason);
    }

    /// <summary>
    /// LL 훅 콜백 (훅 스레드): 판단만 하고 즉시 반환. true = 삼킴.
    /// 무거운 일(창 열기)은 Dispatcher.BeginInvoke.
    /// </summary>
    private bool OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (_mode == SpotlightHotkey.None) return false;
        var k = Marshal.PtrToStructure<K.KBDLLHOOKSTRUCT>(lParam);
        // 내가 주입한 더미 키는 그대로 통과 (다른 프로세스의 주입 입력은 아래에서 정상 처리)
        bool mine = (k.flags & K.LLKHF_INJECTED) != 0 && k.dwExtraInfo == InjectMarker;
        if (mine || k.vkCode != K.VK_SPACE) return false;
        int msg = (int)wParam.ToInt64();
        bool down = msg is K.WM_KEYDOWN or K.WM_SYSKEYDOWN;
        bool up = msg is K.WM_KEYUP or K.WM_SYSKEYUP;
        if (down) return OnSpaceDown();
        if (up && _spaceHeld)
        {
            _spaceHeld = false;
            return true;
        }
        return false;
    }

    /// <summary>Space 다운: 단축키면 true (삼킴).</summary>
    private bool OnSpaceDown()
    {
        long now = Environment.TickCount64;
        if (_spaceHeld)
        {
            // 누르고 있는 동안의 자동 반복 → 다시 토글하지 않고 계속 삼킴
            if (now - _lastSpaceDownAt < RepeatGapMs)
            {
                _lastSpaceDownAt = now;
                return true;
            }
            _spaceHeld = false; // Space 업을 놓친 듯 → 새 누름으로
        }

        if (!ModifiersMatch()) return false;

        _spaceHeld = true;
        _lastSpaceDownAt = now;

        // Win/Alt: 수정자를 뗄 때 단독 탭(시작 메뉴/메뉴 막대)으로 인식되지 않게 지금 더미 키를 끼워 넣음.
        // 훅 안에서 바로 주입해야 수정자 업보다 먼저 들어간다 (BeginInvoke 로 미루면 순서가 뒤집힐 수 있음).
        if (_mode is SpotlightHotkey.WinSpace or SpotlightHotkey.AltSpace) InjectDummyKey();

        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(RaisePressed));
        return true;
    }

    /// <summary>선택한 수정자만 눌려 있는지 (다른 수정자·Shift 가 함께면 아님 — Win+Shift+Space 등은 그대로 통과).</summary>
    private bool ModifiersMatch()
    {
        bool win = K.IsDown(K.VK_LWIN) || K.IsDown(K.VK_RWIN);
        bool alt = K.IsDown(K.VK_MENU);
        bool ctrl = K.IsDown(K.VK_CONTROL);
        bool shift = K.IsDown(K.VK_SHIFT);
        if (shift) return false;
        return _mode switch
        {
            SpotlightHotkey.WinSpace => win && !alt && !ctrl,
            SpotlightHotkey.AltSpace => alt && !win && !ctrl, // AltGr(= Ctrl+Alt)은 제외됨
            SpotlightHotkey.CtrlSpace => ctrl && !win && !alt,
            _ => false,
        };
    }

    /// <summary>미할당 VK 다운/업을 내 표식과 함께 주입.</summary>
    private static void InjectDummyKey()
    {
        var down = User32.KeyInput(K.VK_DUMMY, up: false);
        var up = User32.KeyInput(K.VK_DUMMY, up: true);
        down.u.ki.wScan = 0;
        up.u.ki.wScan = 0;
        down.u.ki.dwExtraInfo = InjectMarker;
        up.u.ki.dwExtraInfo = InjectMarker;
        User32.Send(down, up);
    }

    private void RaisePressed()
    {
        try { Pressed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error("Spotlight 단축키 처리 실패", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mode = SpotlightHotkey.None;
        _hook.Dispose();
    }
}
