using Mongdock.Models;

namespace Mongdock.ViewModels;

/// <summary>
/// 가벼운 모드 (저사양 PC): 애니메이션·블러·독 호버 확대를 끄고 UI 확인 주기를 2배로.
/// 시스템 "애니메이션 효과" 끔과는 별개 — 둘 중 하나라도 꺼져 있으면 애니메이션 없음 (Views/Anim.Enabled).
///
/// 값은 Settings.PerformanceMode (bool, 기본 false).
/// </summary>
public static class PerfMode
{
    /// <summary>마지막으로 <see cref="Sync"/> 한 값 (설정 객체가 없는 곳 — Anim 등 — 에서 씀).</summary>
    public static bool Current { get; private set; }

    public static bool IsOn(Settings s) => s.PerformanceMode;

    public static void Set(Settings s, bool on) => Current = s.PerformanceMode = on;

    /// <summary>설정이 바뀔 때마다 (독·상단바 설정 반영 시작에서) 호출.</summary>
    public static bool Sync(Settings s) => Current = s.PerformanceMode;

    /// <summary>확인 주기: 가벼운 모드면 2배.</summary>
    public static TimeSpan Interval(double ms) => TimeSpan.FromMilliseconds(Current ? ms * 2 : ms);
}
