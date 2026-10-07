using System.Runtime.InteropServices;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 포그라운드 앱의 한/영 상태 조회와 전환.
///
/// 조회 방식: GetForegroundWindow → (GetGUIThreadInfo 의 포커스 창) → ImmGetDefaultIMEWnd →
/// SendMessageTimeout(WM_IME_CONTROL, IMC_GETOPENSTATUS / IMC_GETCONVERSIONMODE).
/// ImmGetContext/ImmGetConversionStatus 는 같은 스레드의 입력 컨텍스트만 볼 수 있어 다른 프로세스 창에선 실패하지만,
/// 기본 IME 창에 WM_IME_CONTROL 을 보내면 대상 스레드 안에서 처리되므로 프로세스 경계를 넘어 동작한다.
/// Windows 11 의 새 Microsoft 한국어 IME(TSF 기반)도 IMM 호환 계층으로 이 메시지에 응답한다
/// (이 PC Win11 26200 에서 open=1, conv=1 응답 확인). 새 IME 는 영문 모드에서도 open 이 1 로 남을 수 있어
/// 최종 판정은 conversion mode 의 IME_CMODE_NATIVE 비트로 한다. open 이 0 이면(IME 꺼짐) 영문.
///
/// UI 스레드를 막지 않도록 실제 조회는 백그라운드 스레드에서 한다:
/// IsHangulMode() 는 마지막 조회 값을 즉시 반환하고 새 조회를 요청한다 (UI 가 주기적으로 부르면 한 주기 늦게 반영).
/// 아무도 묻지 않을 때는 조회하지 않는다.
/// </summary>
public sealed class ImeService : IImeService, IDisposable
{
    private const uint TimeoutMs = 100;

    private readonly AutoResetEvent _request = new(false);
    private readonly Thread _worker;
    private volatile bool _disposed;
    private int _state = Unknown; // Interlocked 로 읽고 씀
    private const int Unknown = -1, English = 0, Hangul = 1;

    public ImeService()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "mongdock.Ime" };
        _worker.Start();
    }

    public bool? IsHangulMode()
    {
        _request.Set();
        int s = Volatile.Read(ref _state);
        return s == Unknown ? null : s == Hangul;
    }

    public void ToggleHangul()
    {
        KeyChord.Send("VK_HANGUL", User32.VK_HANGUL);
        // 즉시 표시가 바뀌도록 낙관적으로 뒤집고, 잠시 뒤 실제 값으로 다시 조회
        int s = Volatile.Read(ref _state);
        if (s != Unknown) Interlocked.CompareExchange(ref _state, s == Hangul ? English : Hangul, s);
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(150); _request.Set(); });
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            _request.WaitOne();
            if (_disposed) break;
            bool? v = Query();
            Volatile.Write(ref _state, v is null ? Unknown : v.Value ? Hangul : English);
            Thread.Sleep(50); // 요청 폭주 방지
        }
    }

    private static bool? Query()
    {
        try
        {
            IntPtr fg = User32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;

            IntPtr target = fg;
            uint tid = User32.GetWindowThreadProcessId(fg, out _);
            var gti = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (tid != 0 && User32.GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
                target = gti.hwndFocus;

            IntPtr imeWnd = Imm32.ImmGetDefaultIMEWnd(target);
            if (imeWnd == IntPtr.Zero && target != fg) imeWnd = Imm32.ImmGetDefaultIMEWnd(fg);
            if (imeWnd == IntPtr.Zero) return null;

            if (!Send(imeWnd, Imm32.IMC_GETOPENSTATUS, out long open)) return null;
            if (open == 0) return false;
            if (!Send(imeWnd, Imm32.IMC_GETCONVERSIONMODE, out long conv)) return null;
            return (conv & Imm32.IME_CMODE_NATIVE) != 0;
        }
        catch (Exception ex)
        {
            Log.Error("IME 상태 조회 실패", ex);
            return null;
        }
    }

    private static bool Send(IntPtr imeWnd, int command, out long value)
    {
        IntPtr ok = User32.SendMessageTimeout(imeWnd, User32.WM_IME_CONTROL, new IntPtr(command), IntPtr.Zero,
            User32.SMTO_ABORTIFHUNG, TimeoutMs, out IntPtr result);
        value = result.ToInt64();
        return ok != IntPtr.Zero;
    }

    public void Dispose()
    {
        _disposed = true;
        _request.Set();
    }
}
