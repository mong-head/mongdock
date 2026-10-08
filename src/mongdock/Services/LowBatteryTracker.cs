namespace Mongdock.Services;

/// <summary>
/// 배터리 부족 알림 단계 판단 (UI 없음, 단위 확인용으로 순수 로직만).
/// 방전 중에 20% → 10% → 5% 를 처음 내려갈 때 한 번씩 알림. 한 번에 여러 단계를 건너뛰면(예: 시작했더니 8%) 가장 낮은 단계 하나만.
/// 충전 중·전원 연결이면 지금 퍼센트보다 낮은 단계만 다시 알릴 수 있게 초기화
/// (19% 에서 잠깐 꽂았다 빼도 20% 알림이 또 뜨지 않게; 21% 이상 충전한 뒤 다시 내려가면 다시 알림).
/// </summary>
public sealed class LowBatteryTracker
{
    public static readonly int[] Thresholds = { 20, 10, 5 };

    private readonly HashSet<int> _notified = new();

    /// <summary>새 배터리 상태. 알림을 띄울 단계(20/10/5)면 그 값, 아니면 null.</summary>
    public int? Update(BatteryInfo? battery)
    {
        if (battery is null) return null;
        int p = battery.Percent;
        if (battery.Charge != BatteryCharge.Discharging)
        {
            _notified.RemoveWhere(t => p > t);
            return null;
        }
        int? fire = null;
        foreach (int t in Thresholds) // 높은 단계부터
        {
            if (p > t || _notified.Contains(t)) continue;
            _notified.Add(t);
            fire = t; // 마지막(가장 낮은) 단계가 남음
        }
        return fire;
    }
}
