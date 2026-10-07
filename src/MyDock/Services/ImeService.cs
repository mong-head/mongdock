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
/// Windows 11 의 새 Microsoft 한국어 IME(TSF 기반)도 IMM 호환 계층을 통해 이 메시지에 응답한다
/// (이 PC Win11 26200 에서 Chrome 계열 창 대상으로 open=1, conv=1 응답 확인).
/// 새 IME 에서는 영문 모드에서도 open status 가 1 로 유지되는 경우가 있어, 최종 판정은 conversion mode 의
/// IME_CMODE_NATIVE 비트로 한다. open status 가 0 이면(IME 꺼짐) 영문.
/// </summary>
public sealed class ImeService : IImeService
{
    private const uint TimeoutMs = 100;

    public bool? IsHangulMode()
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

            if (!Query(imeWnd, Imm32.IMC_GETOPENSTATUS, out long open)) return null;
            if (open == 0) return false;
            if (!Query(imeWnd, Imm32.IMC_GETCONVERSIONMODE, out long conv)) return null;
            return (conv & Imm32.IME_CMODE_NATIVE) != 0;
        }
        catch (Exception ex)
        {
            Log.Error("IME 상태 조회 실패", ex);
            return null;
        }
    }

    private static bool Query(IntPtr imeWnd, int command, out long value)
    {
        IntPtr ok = User32.SendMessageTimeout(imeWnd, User32.WM_IME_CONTROL, new IntPtr(command), IntPtr.Zero,
            User32.SMTO_ABORTIFHUNG, TimeoutMs, out IntPtr result);
        value = result.ToInt64();
        return ok != IntPtr.Zero;
    }

    public void ToggleHangul()
    {
        KeyChord.Send("VK_HANGUL", User32.VK_HANGUL);
    }
}
