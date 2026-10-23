using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 루틴 항목의 모니터를 사람이 알아보게 (사용자 10-23 「모니터 21」): 윈도우 장치 번호(\\.\DISPLAY21 = 원격 가상 화면) 대신
/// 화면 배치 왼쪽→오른쪽(같으면 위→아래) 순서로 「모니터 1·2」, 주 모니터엔 「(주)」. 모니터가 하나면 모니터는 보이지도 저장하지도 않음
/// (지금 화면 저장은 「주 모니터」로 — 원격 가상 화면·노트북 화면 하나). 저장된 모니터가 지금 없으면 주 모니터 (열 때 RoutineService.ResolveMonitor 와 같음).
/// </summary>
internal static class RoutineMonitors
{
    /// <summary>지금 연결된 모니터, 배치 순서 (실제 화소 좌표 기준 — 모니터마다 배율이 달라도 순서가 맞게).</summary>
    public static List<MonitorInfo> Ordered() =>
        Monitors.GetAll().OrderBy(m => m.BoundsRect.Left).ThenBy(m => m.BoundsRect.Top).ToList();

    public static string Label(MonitorInfo m, IReadOnlyList<MonitorInfo> ordered)
    {
        int at = -1;
        for (int i = 0; i < ordered.Count; i++)
            if (string.Equals(ordered[i].DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
        return Loc.F($"모니터 {at + 1}") + (m.IsPrimary ? " " + Loc.T("(주)") : "");
    }

    /// <summary>저장된 값이 가리키는 지금 모니터 (그대로 = null, 없는 모니터 = 주 모니터).</summary>
    public static MonitorInfo? Target(RoutineMonitor? m, IReadOnlyList<MonitorInfo> all)
    {
        var primary = all.FirstOrDefault(x => x.IsPrimary) ?? all.FirstOrDefault();
        return (m?.Mode ?? RoutineMonitorMode.Keep) switch
        {
            RoutineMonitorMode.Keep => null,
            RoutineMonitorMode.Primary => primary,
            _ => Find(m!, all) ?? primary,
        };
    }

    /// <summary>모니터를 골랐는데 그 모니터가 지금 없음 (원격에서 저장한 「21」 등).</summary>
    public static bool Missing(RoutineMonitor? m, IReadOnlyList<MonitorInfo> all) =>
        m is { Mode: RoutineMonitorMode.Index } && Find(m, all) is null;

    private static MonitorInfo? Find(RoutineMonitor m, IReadOnlyList<MonitorInfo> all) =>
        (string.IsNullOrEmpty(m.DeviceName) ? null : all.FirstOrDefault(x => string.Equals(x.DeviceName, m.DeviceName, StringComparison.OrdinalIgnoreCase)))
        ?? all.FirstOrDefault(x => x.Number == m.Index);

    /// <summary>지금 화면 저장: 모니터가 하나면 「주 모니터」(장치 이름을 남기지 않음), 여럿이면 그 모니터.</summary>
    public static RoutineMonitor ForSave(MonitorInfo monitor, int count) => count <= 1
        ? new RoutineMonitor { Mode = RoutineMonitorMode.Primary }
        : new RoutineMonitor { Mode = RoutineMonitorMode.Index, Index = monitor.Number, DeviceName = monitor.DeviceName };

    /// <summary>접힌 줄 요약의 모니터 부분 (모니터가 하나거나 「그대로」면 null).</summary>
    public static string? Summary(RoutineMonitor? m)
    {
        var all = Ordered();
        if (all.Count <= 1 || Target(m, all) is not { } target) return null;
        return Label(target, all);
    }
}
