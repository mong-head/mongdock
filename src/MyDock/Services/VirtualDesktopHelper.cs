using System.Runtime.InteropServices;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 창 ↔ 가상 데스크톱 (문서화된 IVirtualDesktopManager + 레지스트리 ID 순서).
/// COM 객체는 처음 쓰는 스레드(UI)에서 만들고 그 스레드에서만 사용한다.
/// </summary>
internal static class VirtualDesktopHelper
{
    [ThreadStatic] private static IVirtualDesktopManager? _manager;
    [ThreadStatic] private static bool _failed;

    private static IVirtualDesktopManager? Manager
    {
        get
        {
            if (_manager is not null || _failed) return _manager;
            try { _manager = (IVirtualDesktopManager)new VirtualDesktopManagerClass(); }
            catch (Exception ex)
            {
                _failed = true;
                Log.Warn($"IVirtualDesktopManager 생성 실패: {ex.Message}");
            }
            return _manager;
        }
    }

    /// <summary>창이 현재 데스크톱에 있는지. 모르면 null.</summary>
    public static bool? IsOnCurrentDesktop(IntPtr hwnd)
    {
        try
        {
            var m = Manager;
            if (m is null || m.IsWindowOnCurrentVirtualDesktop(hwnd, out int on) != 0) return null;
            return on != 0;
        }
        catch (COMException) { return null; }
    }

    /// <summary>창이 있는 데스크톱 번호 (1부터, ids 순서). 모든 데스크톱 고정 창·실패면 0.</summary>
    public static int GetDesktopIndex(IntPtr hwnd, IReadOnlyList<Guid> ids)
    {
        try
        {
            var m = Manager;
            if (m is null || ids.Count == 0 || m.GetWindowDesktopId(hwnd, out Guid id) != 0 || id == Guid.Empty) return 0;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == id) return i + 1;
            return 0;
        }
        catch (COMException) { return 0; }
    }
}
