using System.Diagnostics;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>윈도우 전원 모드 (설정 → 전원 → 전원 모드).</summary>
public enum PowerMode
{
    /// <summary>최고 전원 효율.</summary>
    BestEfficiency,
    Balanced,
    /// <summary>최고 성능.</summary>
    BestPerformance,
}

/// <summary>
/// 전원 모드 읽기/바꾸기 (powrprof 전원 오버레이 — <see cref="PowerOverlayApi"/>). 어느 스레드에서나 호출 가능, 호출은 가벼움(수 µs~ms).
/// 사용 불가(조회 실패, 고성능·최고의 성능·절전 구성표가 활성)면 <see cref="Available"/> 가 false → UI 는 행을 숨긴다.
/// 같은 값 반복 조회를 줄이려고 1초 캐시 (설정 앱에서 바꾸면 최대 1초 늦게 보임).
/// </summary>
public static class PowerModeService
{
    private static readonly object Gate = new();
    private static (bool Available, PowerMode? Mode) _cached;
    private static long? _cachedAt;
    private static bool _loggedUnavailable;

    /// <summary>이 PC 에서 전원 모드를 읽고 바꿀 수 있는지.</summary>
    public static bool Available => Snapshot().Available;

    /// <summary>현재 전원 모드. 사용할 수 없거나 모르는 오버레이면 null.</summary>
    public static PowerMode? Current => Snapshot().Mode;

    private static (bool Available, PowerMode? Mode) Snapshot()
    {
        lock (Gate)
        {
            long now = Environment.TickCount64;
            if (_cachedAt is long at && now - at < 1000) return _cached;
            _cached = Read();
            _cachedAt = now;
            return _cached;
        }
    }

    /// <summary>바로 다시 읽게 캐시 비움.</summary>
    public static void Invalidate()
    {
        lock (Gate) _cachedAt = null;
    }

    /// <summary>전원 모드 바꾸기. 성공하면 true. 사용 불가면 아무것도 안 하고 false.</summary>
    public static bool TrySet(PowerMode mode)
    {
        try
        {
            if (!Read().Available) return false;
            Guid g = mode switch
            {
                PowerMode.BestEfficiency => PowerOverlayApi.BestEfficiency,
                PowerMode.BestPerformance => PowerOverlayApi.BestPerformance,
                _ => PowerOverlayApi.Balanced,
            };
            uint rc = PowerOverlayApi.PowerSetActiveOverlayScheme(g);
            Invalidate();
            if (rc != 0)
            {
                Log.Warn($"전원 모드 변경 실패 ({mode}): 0x{rc:X}");
                return false;
            }
            Log.Info($"전원 모드 → {mode}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("전원 모드 변경 실패", ex);
            return false;
        }
    }

    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:powersleep") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Error("전원 설정 열기 실패", ex); }
    }

    /// <summary>오버레이 GUID → 모드. 모르는 GUID 는 null (UI 에서 아무 칸도 선택 안 함).</summary>
    internal static PowerMode? FromGuid(Guid g)
    {
        if (g == PowerOverlayApi.BestEfficiency) return PowerMode.BestEfficiency;
        if (g == PowerOverlayApi.BestPerformance) return PowerMode.BestPerformance;
        if (g == PowerOverlayApi.Balanced || g == PowerOverlayApi.BetterPerformance) return PowerMode.Balanced;
        return null;
    }

    private static (bool Available, PowerMode? Mode) Read()
    {
        try
        {
            var scheme = PowerOverlayApi.GetActiveScheme();
            if (scheme == PowerOverlayApi.SchemeHighPerformance || scheme == PowerOverlayApi.SchemeUltimatePerformance
                || scheme == PowerOverlayApi.SchemePowerSaver)
            {
                LogUnavailableOnce($"전원 모드 사용 불가: 활성 구성표 {scheme}");
                return (false, null);
            }
            uint rc = PowerOverlayApi.PowerGetEffectiveOverlayScheme(out Guid g);
            if (rc != 0)
            {
                LogUnavailableOnce($"전원 모드 조회 실패: 0x{rc:X}");
                return (false, null);
            }
            var mode = FromGuid(g);
            if (mode is null) LogUnavailableOnce($"전원 모드: 알 수 없는 오버레이 {g}");
            return (true, mode);
        }
        catch (Exception ex) // 오래된 윈도우: 진입점 없음
        {
            LogUnavailableOnce($"전원 모드 조회 불가: {ex.Message}");
            return (false, null);
        }
    }

    private static void LogUnavailableOnce(string message)
    {
        if (_loggedUnavailable) return;
        _loggedUnavailable = true;
        Log.Info(message);
    }
}
