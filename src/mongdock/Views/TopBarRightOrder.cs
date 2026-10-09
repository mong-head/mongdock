namespace Mongdock.Views;

/// <summary>
/// 상단바 오른쪽 아이콘 순서 (TopBarSettings.RightItemsOrder) 해석. 시계는 항상 맨 오른쪽이라 목록에 없다.
/// 저장값의 아는 키를 그 순서대로 쓰고, 빠진 키(새 항목)는 기본 순서에서 바로 앞 항목 뒤에 끼워 넣는다. 모르는 키·중복은 버림.
/// </summary>
internal static class TopBarRightOrder
{
    public const string Privacy = "privacy";
    public const string Desktops = "desktops";
    public const string NetSpeed = "netSpeed";
    public const string Tray = "tray";
    public const string Bluetooth = "bluetooth";
    public const string Wifi = "wifi";
    public const string Volume = "volume";
    public const string Search = "search";
    public const string ControlCenter = "controlCenter";
    public const string Ime = "ime";
    public const string Battery = "battery";

    /// <summary>
    /// 기본 순서 (0.3.4 까지의 고정 배치 — 배터리는 시계 바로 왼쪽).
    /// 카메라·마이크 점은 맨 왼쪽: 오른쪽 정렬 구역이라 점이 나타나고 사라져도 다른 아이콘이 밀리지 않음.
    /// </summary>
    public static readonly IReadOnlyList<string> Default = new[]
    {
        Privacy, Desktops, NetSpeed, Tray, Bluetooth, Wifi, Volume, Search, ControlCenter, Ime, Battery,
    };

    /// <summary>설정 창 목록에 보일 이름.</summary>
    public static string Label(string key) => key switch
    {
        Privacy => Loc.T("카메라·마이크 사용 중 표시"),
        Desktops => Loc.T("가상 데스크톱"),
        NetSpeed => Loc.T("네트워크 속도"),
        Tray => Loc.T("앱 트레이 아이콘"),
        Bluetooth => Loc.T("블루투스"),
        Wifi => "Wi-Fi",
        Volume => Loc.T("볼륨"),
        Search => Loc.T("검색"),
        ControlCenter => Loc.T("제어 센터"),
        Ime => Loc.T("한/영"),
        Battery => Loc.T("배터리"),
        _ => key,
    };

    /// <summary>저장값 → 모든 키가 한 번씩 들어 있는 순서.</summary>
    public static List<string> Resolve(IEnumerable<string>? saved)
    {
        var result = new List<string>(Default.Count);
        if (saved != null)
            foreach (var raw in saved)
            {
                var key = Default.FirstOrDefault(k => string.Equals(k, raw?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (key != null && !result.Contains(key)) result.Add(key);
            }
        for (int i = 0; i < Default.Count; i++)
        {
            var key = Default[i];
            if (result.Contains(key)) continue;
            // 기본 순서에서 앞쪽에 있는 항목 중 가장 가까운 것 바로 뒤 (없으면 맨 앞)
            int at = 0;
            for (int j = i - 1; j >= 0; j--)
            {
                int idx = result.IndexOf(Default[j]);
                if (idx >= 0) { at = idx + 1; break; }
            }
            result.Insert(at, key);
        }
        return result;
    }

    public static bool IsDefault(IEnumerable<string>? saved) => Resolve(saved).SequenceEqual(Default);

    /// <summary>key 를 delta(-1 왼쪽 / +1 오른쪽) 만큼 옮긴 새 순서.</summary>
    public static List<string> Nudge(IEnumerable<string>? saved, string key, int delta)
    {
        var order = Resolve(saved);
        int i = order.IndexOf(key);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return order;
        (order[i], order[j]) = (order[j], order[i]);
        return order;
    }
}
