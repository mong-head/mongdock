using System.Reflection;
using Mongdock.Models;

namespace Mongdock.ViewModels;

/// <summary>
/// 가벼운 모드 (저사양 PC): 애니메이션·블러·독 호버 확대를 끄고 UI 확인 주기를 2배로.
/// 시스템 "애니메이션 효과" 끔과는 별개 — 둘 중 하나라도 꺼져 있으면 애니메이션 없음 (Views/Anim.Enabled).
///
/// 값은 Settings.PerformanceMode (bool, 기본 false) 에 저장한다. 설정 모델은 백엔드 소유라
/// 속성이 아직 없으면 <see cref="Available"/> = false → 항상 꺼짐, 설정 창 토글도 숨김.
/// 속성이 생기면 코드 변경 없이 바로 동작한다 (settings.json 저장도 System.Text.Json 이 그대로 처리).
/// </summary>
public static class PerfMode
{
    private static readonly PropertyInfo? Prop = FindProperty();

    private static PropertyInfo? FindProperty()
    {
        try
        {
            var p = typeof(Settings).GetProperty("PerformanceMode", BindingFlags.Public | BindingFlags.Instance);
            return p != null && p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite ? p : null;
        }
        catch { return null; }
    }

    /// <summary>설정 모델에 PerformanceMode 가 있는지.</summary>
    public static bool Available => Prop != null;

    /// <summary>마지막으로 <see cref="Sync"/> 한 값 (설정 객체가 없는 곳 — Anim 등 — 에서 씀).</summary>
    public static bool Current { get; private set; }

    public static bool IsOn(Settings s)
    {
        if (Prop == null) return false;
        try { return Prop.GetValue(s) is true; }
        catch { return false; }
    }

    public static void Set(Settings s, bool on)
    {
        if (Prop == null) return;
        try { Prop.SetValue(s, on); }
        catch { return; }
        Current = on;
    }

    /// <summary>설정이 바뀔 때마다 (독·상단바 설정 반영 시작에서) 호출.</summary>
    public static bool Sync(Settings s) => Current = IsOn(s);

    /// <summary>확인 주기: 가벼운 모드면 2배.</summary>
    public static TimeSpan Interval(double ms) => TimeSpan.FromMilliseconds(Current ? ms * 2 : ms);
}
