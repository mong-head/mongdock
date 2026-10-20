using System.Runtime.InteropServices;
using System.Windows.Interop;
using Mongdock.Models;
using Mongdock.Native;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 화면 끝 밀기 감지 (#39 사용자 결정 B): 커서 정지로 숨긴 독은 커서가 그 자리 그대로면 다시 안 나오는데, 화면 끝에 붙은 커서는
/// 독 쪽으로 더 밀어도 좌표가 안 바뀜 → 마우스 장치의 Raw Input 상대 이동(lLastX/lLastY)으로 「독 쪽으로 밀었다」를 앎.
/// 붙잡아 둔 동안만 등록(메시지 전용 창, RIDEV_INPUTSINK)하고 풀리면 바로 해제 — 평소엔 마우스 이동 메시지를 받지 않음.
/// 키보드는 등록 안 함. 절대 좌표 입력(원격 StarDesk·펜·터치)과 장치 없는 주입 입력은 무시 → 그때는 지금처럼 움직여야 나옴.
/// </summary>
internal sealed class EdgePushWatch : IDisposable
{
    /// <summary>독 쪽으로 이만큼(마우스 카운트) 모이면 밀었다고 봄 — 센서 떨림 한두 칸은 무시.</summary>
    public const int PushCounts = 3;

    private const int WM_INPUT = 0x00FF, RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0;
    private const int RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100, MOUSE_MOVE_ABSOLUTE = 0x1;

    private const int BufSize = 256;

    private HwndSource? _sink;
    private IntPtr _buf; // WM_INPUT 읽기 칸 (붙잡아 둔 동안 마우스 이동마다 쓰므로 한 번만 잡음)
    private bool _armed, _failed;
    private DockEdge _edge;
    private int _sum;

    /// <summary>등록한 뒤 독 쪽으로 밀었는지.</summary>
    public bool Pushed => _sum >= PushCounts;

    /// <summary>붙잡아 둔 동안 매 틱. 처음 한 번만 등록하고 밀기 합계를 0부터.</summary>
    public void Arm(DockEdge edge)
    {
        if (_failed || (_armed && edge == _edge)) return; // 실패했으면 이번 붙잡힘 동안은 다시 시도·로그하지 않음 (Disarm 에서 풂)
        _edge = edge;
        _sum = 0;
        if (_armed) return;
        try
        {
            _sink ??= new HwndSource(new HwndSourceParameters("mongdock.EdgePush") { ParentWindow = TrayApi.HWND_MESSAGE, WindowStyle = 0 });
            _sink.AddHook(WndProc);
            var rid = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = _sink.Handle };
            _armed = RegisterRawInputDevices(new[] { rid }, 1, Marshal.SizeOf<RAWINPUTDEVICE>());
            if (!_armed)
            {
                _failed = true;
                _sink.RemoveHook(WndProc);
                Log.Info($"독: 화면 끝 밀기 감시 등록 실패 {Marshal.GetLastWin32Error()} — 커서를 움직여야 다시 나옴");
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            _sink?.RemoveHook(WndProc);
            Log.Info($"독: 화면 끝 밀기 감시 안 됨: {ex.Message}");
        }
    }

    /// <summary>풀릴 때·독이 나타날 때·창이 닫힐 때. 등록을 지워 마우스 이동 메시지를 더 받지 않음.</summary>
    public void Disarm()
    {
        _sum = 0;
        _failed = false;
        if (!_armed) return;
        _armed = false;
        var rid = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero };
        RegisterRawInputDevices(new[] { rid }, 1, Marshal.SizeOf<RAWINPUTDEVICE>());
        _sink?.RemoveHook(WndProc);
    }

    public void Dispose()
    {
        Disarm();
        _sink?.Dispose();
        _sink = null;
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
    }

    /// <summary>상대 이동 한 번(dx, dy)이 독 쪽(화면 바깥쪽)으로 미는 양 — 다른 방향이면 0. 시험에서 직접 부름.</summary>
    public static int TowardDock(DockEdge edge, int dx, int dy) => edge switch
    {
        DockEdge.Left => Math.Max(0, -dx),
        DockEdge.Right => Math.Max(0, dx),
        DockEdge.Top => Math.Max(0, -dy),
        _ => Math.Max(0, dy),
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT || !_armed) return IntPtr.Zero;
        int size = 0, hdr = Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, hdr);
        if (size < hdr + Marshal.SizeOf<RAWMOUSE>() || size > BufSize) return IntPtr.Zero;
        if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(BufSize);
        if (GetRawInputData(lParam, RID_INPUT, _buf, ref size, hdr) != size) return IntPtr.Zero;
        var head = Marshal.PtrToStructure<RAWINPUTHEADER>(_buf);
        if (head.dwType != RIM_TYPEMOUSE || head.hDevice == IntPtr.Zero) return IntPtr.Zero; // 장치 없는 주입 입력은 무시
        var m = Marshal.PtrToStructure<RAWMOUSE>(_buf + hdr);
        if ((m.usFlags & MOUSE_MOVE_ABSOLUTE) != 0) return IntPtr.Zero; // 원격·펜·태블릿의 절대 좌표
        _sum += TowardDock(_edge, m.lLastX, m.lLastY);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public int dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public int dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWMOUSE { public ushort usFlags, pad, usButtonFlags, usButtonData; public uint ulRawButtons; public int lLastX, lLastY; public uint ulExtraInformation; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, int count, int size);
    [DllImport("user32.dll")] private static extern int GetRawInputData(IntPtr hRawInput, int command, IntPtr data, ref int size, int headerSize);
}
