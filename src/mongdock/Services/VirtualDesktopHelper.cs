using System.Runtime.InteropServices;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 창 ↔ 가상 데스크톱 (문서화된 IVirtualDesktopManager + 레지스트리 ID 순서).
/// COM 객체는 처음 쓰는 스레드(UI)에서 만들고 그 스레드에서만 사용한다.
/// </summary>
internal static class VirtualDesktopHelper
{
    [ThreadStatic] private static IVirtualDesktopManager? _manager;
    [ThreadStatic] private static bool _failed;
    [ThreadStatic] private static int _generation;
    private static int _currentGeneration;

    /// <summary>모든 스레드의 캐시된 COM 객체를 다음 사용 때 다시 만들게 함 (탐색기 재시작 등).</summary>
    public static void Invalidate() => Interlocked.Increment(ref _currentGeneration);

    private static void Reset()
    {
        if (_manager is not null)
        {
            try { Marshal.ReleaseComObject(_manager); } catch { }
        }
        _manager = null;
        _failed = false;
    }

    private static IVirtualDesktopManager? Manager
    {
        get
        {
            int gen = Volatile.Read(ref _currentGeneration);
            if (_generation != gen)
            {
                Reset();
                _generation = gen;
            }
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
            if (m is null) return null;
            int hr = m.IsWindowOnCurrentVirtualDesktop(hwnd, out int on);
            if (hr != 0) { OnFailure(hr); return null; }
            return on != 0;
        }
        catch (COMException ex) { OnFailure(ex.HResult); return null; }
        catch (InvalidComObjectException) { Reset(); return null; }
    }

    /// <summary>창이 있는 데스크톱 번호 (1부터, ids 순서). 모든 데스크톱 고정 창·실패면 0.</summary>
    public static int GetDesktopIndex(IntPtr hwnd, IReadOnlyList<Guid> ids)
    {
        try
        {
            var m = Manager;
            if (m is null || ids.Count == 0) return 0;
            int hr = m.GetWindowDesktopId(hwnd, out Guid id);
            if (hr != 0) { OnFailure(hr); return 0; }
            if (id == Guid.Empty) return 0;
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == id) return i + 1;
            return 0;
        }
        catch (COMException ex) { OnFailure(ex.HResult); return 0; }
        catch (InvalidComObjectException) { Reset(); return 0; }
    }

    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int TYPE_E_ELEMENTNOTFOUND = unchecked((int)0x8002802B);

    /// <summary>창이 사라졌거나 해당 없음(E_INVALIDARG, ELEMENTNOTFOUND)은 정상 실패. 그 외(RPC 끊김 등)는 다음에 재생성.</summary>
    private static void OnFailure(int hr)
    {
        if (hr is E_INVALIDARG or TYPE_E_ELEMENTNOTFOUND) return;
        Log.Warn($"IVirtualDesktopManager 호출 실패 hr=0x{hr:X8} → 재생성");
        Reset();
    }
}
