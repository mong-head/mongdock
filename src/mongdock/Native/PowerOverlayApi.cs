using System.Runtime.InteropServices;

namespace Mongdock.Native;

/// <summary>
/// 윈도우 10/11 "전원 모드"(설정 → 전원 → 전원 모드, 예전 배터리 슬라이더) = 전원 구성표 위의 오버레이.
/// powrprof.dll 의 PowerGetEffectiveOverlayScheme / PowerSetActiveOverlayScheme 은 문서화되지 않았지만
/// 설정 앱이 쓰는 함수이고 관리자 권한 없이 동작한다 (현재 전원 원천(AC/배터리)의 값을 읽고/씀).
/// GUID 는 Microsoft Q&A 와 powercfg /getactiveoverlay 결과로 확인:
///   최고 전원 효율 961cc777-2547-4f9d-8174-7d86181b8a7a, 균형 00000000-…, 최고 성능 ded574b5-45a0-4f42-8737-46345c09c238.
/// 윈도우 10 일부 기기의 "더 나은 성능" 3af9b8d9-7c97-431d-ad78-34a8bfea439f 는 균형으로 취급.
/// </summary>
internal static class PowerOverlayApi
{
    public static readonly Guid BestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid Balanced = Guid.Empty;
    public static readonly Guid BetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");
    public static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    /// <summary>기본 전원 구성표: 오버레이(전원 모드)는 "균형 조정" 계열에서만 동작 — 이 셋이면 설정 앱도 전원 모드를 못 바꾼다.</summary>
    public static readonly Guid SchemeHighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid SchemeUltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid SchemePowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>현재 적용 중인 오버레이 GUID. 성공하면 0 (ERROR_SUCCESS).</summary>
    [DllImport("powrprof.dll", ExactSpelling = true)]
    public static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    /// <summary>현재 전원 원천의 오버레이를 바꿈 (GUID 는 값으로 전달). 성공하면 0.</summary>
    [DllImport("powrprof.dll", ExactSpelling = true)]
    public static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    /// <summary>활성 전원 구성표 GUID. 받은 포인터는 LocalFree 로 해제. 성공하면 0.</summary>
    [DllImport("powrprof.dll", ExactSpelling = true)]
    public static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>활성 전원 구성표 GUID (실패하면 null).</summary>
    public static Guid? GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }
}
