using System.Diagnostics;

namespace Mongdock.Services;

/// <summary>
/// 메모리 진단 로그 (#15): 시작 2분 뒤, 그다음 30분마다 한 줄 — 전용·작업 집합, GC 힙(관리)·커밋, 그 밖(네이티브 = 전용 − GC 커밋:
/// WPF 렌더·비트맵 픽셀·D3D·WinRT), 핸들·스레드, 아이콘 캐시 개수·대략 크기. 외부 도구 없이 사용자 PC 로그로도 비교할 수 있게.
/// </summary>
public static class MemoryReport
{
    private static Timer? _timer;
    private static Func<string>? _extra;

    /// <param name="extra">덧붙일 앱 정보 (예: 아이콘 캐시) — 스레드 풀에서 부름.</param>
    public static void Start(Func<string>? extra = null)
    {
        _extra = extra;
        _timer ??= new Timer(_ => Write(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30));
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public static string Snapshot()
    {
        using var p = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        long mb = 1024 * 1024;
        long priv = p.PrivateMemorySize64, gcCommitted = gc.TotalCommittedBytes;
        string extra = "";
        try { extra = _extra?.Invoke() ?? ""; } catch { }
        return $"메모리: 전용 {priv / mb}MB, 작업 집합 {p.WorkingSet64 / mb}MB | GC 힙 {gc.HeapSizeBytes / mb}MB (커밋 {gcCommitted / mb}MB, 조각 {gc.FragmentedBytes / mb}MB, " +
               $"GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}) | 그 밖 {Math.Max(0, priv - gcCommitted) / mb}MB | " +
               $"핸들 {p.HandleCount}, 스레드 {p.Threads.Count}, 실행 {(DateTime.Now - p.StartTime).TotalMinutes:0}분{(extra.Length > 0 ? " | " + extra : "")}";
    }

    private static void Write()
    {
        try { Log.Info(Snapshot()); }
        catch (Exception ex) { Log.Warn($"메모리 기록 실패: {ex.Message}"); }
    }
}
