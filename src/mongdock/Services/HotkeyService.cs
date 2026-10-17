using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Native;
using K = Mongdock.Native.KeyboardHookApi;

namespace Mongdock.Services;

/// <summary>
/// 몽독 전역 단축키 (0.6 — 예전 SpotlightHotkeyService 를 키 표로 넓힘) — 저수준 키보드 훅(WH_KEYBOARD_LL).
///
/// RegisterHotKey 를 쓰지 않는 이유:
/// - Win+Space 는 윈도우가 입력 언어 전환용으로 예약 → RegisterHotKey 가 ERROR_HOTKEY_ALREADY_REGISTERED(1409)로 실패.
/// - Alt+키 는 등록은 되지만 키만 가로채면 Alt 단독 탭으로 보여 앱 메뉴 막대가 활성화됨 → 어차피 훅이 필요.
/// 그래서 모든 키를 같은 훅 경로로 처리한다 (키 반복 무시·원격 입력 처리도 한 곳에서).
///
/// 동작:
/// - 수정자가 정확히 표의 조합과 같을 때 키 다운 → 삼키고 Pressed(할 일 이름) (UI 스레드, BeginInvoke).
///   같은 키의 반복 다운·업도 삼킴 → 누르고 있어도 한 번만.
/// - Win/Alt 가 든 조합이면 키를 삼킨 순간 더미 키(VK 0xE8, 미할당) 다운/업을 주입 → 수정자를 뗄 때
///   "단독 탭"으로 인식되지 않아 시작 메뉴(Win)·메뉴 막대(Alt)가 열리지 않는다.
/// - 내가 주입한 입력(LLKHF_INJECTED + dwExtraInfo == InjectMarker)은 무시. 다른 프로세스가 주입한 키
///   (원격 데스크톱·StarDesk 등)는 실제 키처럼 처리한다.
/// - 키 받기(<see cref="Record"/>): 설정에서 키 칸을 누르면 다음 조합 하나를 삼키고 넘겨줌 (Win 조합도 받으려고 훅에서 받음).
///   수정자 없이 Esc = 취소, Backspace·Delete = 지우기.
///
/// 한계: 관리자 권한 창(작업 관리자 등)이 포그라운드면 UIPI 때문에 LL 훅이 그 키를 받지 못한다.
/// 훅은 전용 백그라운드 스레드(<see cref="LowLevelHookThread"/>)에서 돈다. 조용히 빠질 수 있어 <see cref="Reinstall"/> 제공.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>내가 주입한 입력 표식 (dwExtraInfo). "MONG".</summary>
    private static readonly IntPtr InjectMarker = new(0x4D4F4E47);
    /// <summary>마지막으로 삼킨 키 다운 이후 이 시간 넘게 지나면 반복이 아닌 새 누름으로 본다 (키 업을 놓친 경우 대비).</summary>
    private const long RepeatGapMs = 1500;

    /// <summary>키 받기 결과: 조합 / 취소 / 지우기.</summary>
    public enum RecordKind { Chord, Cancel, Clear }

    private readonly Dispatcher _dispatcher;
    private readonly LowLevelHookThread _hook;
    // 표는 통째로 바꿔 끼움 (훅 스레드는 읽기만)
    private volatile Dictionary<HotkeyChord, string> _table = new();
    private volatile Action<RecordKind, HotkeyChord>? _recorder;
    // 아래 둘은 훅 스레드에서만 읽고 쓴다
    private int _heldVk;          // 우리가 삼킨 키가 아직 눌려 있음 (0 = 없음)
    private long _lastDownAt;
    private bool _disposed;

    /// <summary>단축키가 눌림 (UI 스레드). 인수 = 할 일 이름 ("search", "allApps", "routine:id" …).</summary>
    public event Action<string>? Pressed;

    /// <summary>UI 스레드에서 만들 것 (Pressed 를 그 Dispatcher 로 올린다).</summary>
    public HotkeyService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _hook = new LowLevelHookThread(K.WH_KEYBOARD_LL, "전역 키보드", OnKey);
    }

    /// <summary>키 표 적용 (빈 표면 키 받기 중이 아닐 때 훅 해제). 같은 표면 아무것도 안 함.</summary>
    public void Apply(IReadOnlyDictionary<HotkeyChord, string> table)
    {
        if (_disposed) return;
        var copy = new Dictionary<HotkeyChord, string>(table);
        bool same = copy.Count == _table.Count && copy.All(kv => _table.TryGetValue(kv.Key, out var v) && v == kv.Value);
        _table = copy;
        if (!same) Log.Info(copy.Count == 0 ? "단축키 없음 (쉼)" : $"단축키 {copy.Count}개: {string.Join(", ", copy.Select(kv => $"{kv.Key}→{Short(kv.Value)}"))}");
        SyncHook();
    }

    /// <summary>로그용 — 루틴 id 는 앞 몇 글자만.</summary>
    private static string Short(string action) => action.StartsWith("routine:", StringComparison.Ordinal) && action.Length > 16 ? action[..16] : action;

    /// <summary>다음 조합 하나 받기 (설정 키 칸). 받는 동안은 단축키가 동작하지 않음. null 이면 받기 그만.</summary>
    public void Record(Action<RecordKind, HotkeyChord>? recorder)
    {
        if (_disposed) return;
        _recorder = recorder;
        _heldVk = 0;
        SyncHook();
    }

    public bool IsRecording => _recorder is not null;

    private void SyncHook()
    {
        bool want = (_table.Count > 0 || _recorder is not null) && KeyStateForTest is null; // 시험 중엔 진짜 훅을 걸지 않음
        if (!want) { _hook.Stop(); return; }
        if (!_hook.IsRunning)
        {
            _hook.Stop(); // 설치 실패로 반쯤 남은 상태 정리
            _hook.Start();
        }
    }

    /// <summary>훅 재설치 (켜져 있을 때만). 조용히 제거됐을 경우 대비.</summary>
    public void Reinstall(string reason)
    {
        if (_disposed || _table.Count == 0 && _recorder is null) return;
        _hook.Restart(reason);
    }

    /// <summary>
    /// LL 훅 콜백 (훅 스레드): 판단만 하고 즉시 반환. true = 삼킴.
    /// 무거운 일(창 열기)은 Dispatcher.BeginInvoke.
    /// </summary>
    private bool OnKey(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var k = Marshal.PtrToStructure<K.KBDLLHOOKSTRUCT>(lParam);
        // 내가 주입한 더미 키는 그대로 통과 (다른 프로세스의 주입 입력은 아래에서 정상 처리)
        if ((k.flags & K.LLKHF_INJECTED) != 0 && k.dwExtraInfo == InjectMarker) return false;
        int vk = (int)k.vkCode;
        if (HotkeyChord.IsModifierKey(vk)) return false;
        int msg = (int)wParam.ToInt64();
        bool down = msg is K.WM_KEYDOWN or K.WM_SYSKEYDOWN;
        bool up = msg is K.WM_KEYUP or K.WM_SYSKEYUP;
        if (up)
        {
            if (_heldVk != vk) return false;
            _heldVk = 0;
            return true;
        }
        if (!down) return false;

        long now = Environment.TickCount64;
        // 볼륨 올림·내림 (#21): 누르고 있으면 자동 반복마다 한 칸씩 (다른 단축키와 달리 한 번만이 아님). 업은 위에서 삼킴(_heldVk)
        if (_recorder is null && _table.TryGetValue(Current(vk), out var repeatAction) && RepeatsWhileHeld(repeatAction))
        {
            _heldVk = vk;
            _lastDownAt = now;
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RaisePressed(repeatAction)));
            return true;
        }
        if (_heldVk == vk)
        {
            // 누르고 있는 동안의 자동 반복 → 다시 하지 않고 계속 삼킴
            if (now - _lastDownAt < RepeatGapMs) { _lastDownAt = now; return true; }
            _heldVk = 0; // 업을 놓친 듯 → 새 누름으로
        }

        var chord = Current(vk);
        if (_recorder is { } rec)
        {
            var kind = !chord.HasModifier && !chord.Shift && vk == HotkeyChord.VK_ESCAPE ? RecordKind.Cancel
                : !chord.HasModifier && !chord.Shift && vk is HotkeyChord.VK_BACK or HotkeyChord.VK_DELETE ? RecordKind.Clear
                : RecordKind.Chord;
            _recorder = null;
            Hold(vk, now, chord);
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                try { rec(kind, chord); }
                catch (Exception ex) { Log.Error("단축키 받기 처리 실패", ex); }
                SyncHook();
            }));
            return true;
        }

        if (!_table.TryGetValue(chord, out var action)) return false;
        Hold(vk, now, chord);
        _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RaisePressed(action)));
        return true;
    }

    /// <summary>누르고 있는 동안 반복할 할 일 (볼륨 올림·내림).</summary>
    private static bool RepeatsWhileHeld(string action) => action is "volume:up" or "volume:down";

    /// <summary>삼킨 키를 기억 (반복·업도 삼키게) + Win/Alt 면 더미 키.</summary>
    private void Hold(int vk, long now, HotkeyChord chord)
    {
        _heldVk = vk;
        _lastDownAt = now;
        // 훅 안에서 바로 주입해야 수정자 업보다 먼저 들어간다 (BeginInvoke 로 미루면 순서가 뒤집힐 수 있음)
        if ((chord.Win || chord.Alt) && KeyStateForTest is null) InjectDummyKey(); // 시험 중엔 실제 입력을 보내지 않음
    }

    /// <summary>지금 눌린 수정자 + 이 키.</summary>
    private static HotkeyChord Current(int vk)
    {
        var down = KeyStateForTest ?? K.IsDown;
        return new(down(K.VK_CONTROL), down(K.VK_MENU), down(K.VK_SHIFT), down(K.VK_LWIN) || down(K.VK_RWIN), vk);
    }

    /// <summary>시험용: 수정자 눌림 상태를 대신 알려 줌 (null = 실제 키보드).</summary>
    internal static Func<int, bool>? KeyStateForTest { get; set; }

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

    private void RaisePressed(string action)
    {
        try { Pressed?.Invoke(action); }
        catch (Exception ex) { Log.Error($"단축키 처리 실패 ({Short(action)})", ex); }
    }

    /// <summary>시험용 (live-test·report-test): 훅에 키가 들어온 것처럼 — 실제 입력 없이.</summary>
    internal bool SimulateForTest(int vk, bool down) => OnKey(0, new IntPtr(down ? K.WM_KEYDOWN : K.WM_KEYUP), MakeKey(vk));

    private static IntPtr MakeKey(int vk)
    {
        var s = new K.KBDLLHOOKSTRUCT { vkCode = (uint)vk };
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<K.KBDLLHOOKSTRUCT>());
        Marshal.StructureToPtr(s, p, false);
        return p; // 시험에서만 — 몇 바이트 새는 것은 상관없음
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _table = new();
        _recorder = null;
        _hook.Dispose();
    }
}
