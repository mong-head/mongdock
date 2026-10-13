namespace Mongdock.Views;

/// <summary>
/// 독이 화면 길이를 넘을 때 아이콘 크기 자동 축소 계산 (UI 요소 없이 숫자만 — 검증하기 쉽게 분리).
/// 세로 모니터 아래 독(1080 px @150% = 720 DIP)이나 왼쪽/오른쪽 독에 앱이 많을 때.
/// 길이 공식은 DockLayout/DockItemView 와 같다: 패딩 = round(max(5, 아이콘×0.12)), 아이콘 슬롯 = 아이콘 + 간격,
/// 구분선 슬롯 = 1 + max(5, 간격+4)×2, 테두리 2.
/// "들어간다" = 쉬는 길이가 아니라 파도 확대가 일어났을 때 독 전체가 차지하는 최대 길이(커서를 끝에서 끝까지 옮기며
/// DockWindow.StepMagnification 과 같은 식으로 계산)가 쓸 수 있는 길이 안 (#25 사용자: 호버 때 위·아래가 잘림).
/// </summary>
internal static class DockFit
{
    /// <summary>설정 슬라이더 최솟값보다 낮게 (설정값 범위 확인용).</summary>
    public const double MinIcon = 16;
    /// <summary>자동 축소 하한 (#25 §1). 넘치면 "⋯ +n" 칸으로. 설정값이 이보다 작으면 설정값까지.</summary>
    public const double ShrinkFloor = 28;

    /// <summary>줄일 수 있는 가장 작은 크기.</summary>
    public static double Floor(double settingIcon) => Math.Min(ShrinkFloor, Math.Floor(Math.Clamp(double.IsNaN(settingIcon) ? 52 : settingIcon, MinIcon, 256)));

    /// <summary>패널 양 끝과 화면 끝 사이에 남길 여유 (각 쪽).</summary>
    public const double EndGap = 8;

    /// <summary>확대 배율·파도 (UI 스레드에서 Fit 부르기 전에 독 레이아웃 값으로 정함). 배율 1 이하면 확대 없음.</summary>
    [ThreadStatic] public static double HoverScale;
    [ThreadStatic] public static bool Wave;
    [ThreadStatic] private static Dictionary<(double, double, int, double, bool), double>? _growthCache;

    /// <summary>쉬는 길이.</summary>
    public static double RestLength(double icon, double spacing, int icons, int separators, double extra = 0)
    {
        double pad = Math.Round(Math.Max(5, icon * 0.12));
        double sep = 1 + Math.Max(5, spacing + 4) * 2;
        return pad * 2 + 2 + icons * (icon + spacing) + separators * sep + extra;
    }

    /// <summary>확대가 일어났을 때 독 전체의 최대 길이 (쉬는 길이 + 파도로 늘어나는 최대 길이).</summary>
    public static double PanelLength(double icon, double spacing, int icons, int separators, double extra = 0)
        => RestLength(icon, spacing, icons, separators, extra) + WaveGrowth(icon, spacing, icons);

    /// <summary>
    /// 파도 확대로 독이 늘어나는 최대 길이: 커서를 독 처음부터 끝까지 1 DIP 씩 옮기며, 각 아이콘의 목표 배율
    /// (파도: 1 + (배율−1)·cos(π·min(d, 2.5·아이콘)/(5·아이콘)), 파도 끔: 커서 아래 아이콘만)로 늘어난 길이 합의 최댓값.
    /// 구분선은 자리만 띄우고 커지지 않으므로 빼고 셈(아이콘 사이가 가까울수록 커지는 합이 커서 더 엄격함).
    /// </summary>
    public static double WaveGrowth(double icon, double spacing, int icons)
    {
        if (HoverScale <= 1.001 || icons <= 0 || icon <= 0) return 0;
        var key = (icon, spacing, icons, HoverScale, Wave);
        _growthCache ??= new();
        if (_growthCache.TryGetValue(key, out double cached)) return cached;
        double slot = icon + spacing, r = icon * 2.5, total = icons * slot, best = 0;
        for (double c = 0; c <= total; c += 1)
        {
            double sum = 0;
            int first = Math.Max(0, (int)((c - r) / slot) - 1), last = Math.Min(icons - 1, (int)((c + r) / slot) + 1);
            for (int i = first; i <= last; i++)
            {
                double d = Math.Abs(c - (i * slot + slot / 2));
                double target = Wave
                    ? 1 + (HoverScale - 1) * Math.Max(0, Math.Cos(Math.PI * Math.Min(d, r) / (2 * r)))
                    : d <= slot / 2 ? HoverScale : 1;
                sum += (target - 1) * icon;
            }
            if (sum > best) best = sum;
        }
        best = Math.Ceiling(best);
        if (_growthCache.Count > 512) _growthCache.Clear();
        _growthCache[key] = best;
        return best;
    }

    /// <summary>
    /// 사용 가능한 길이(available, DIP) 안에 확대 최대 길이까지 들어가는 가장 큰 아이콘 크기 (설정값 이하, 정수, 최소 <see cref="Floor"/>).
    /// 설정값 그대로 들어가면 설정값을 돌려준다.
    /// </summary>
    public static double FitIcon(double settingIcon, double spacing, int icons, int separators, double available, double extra = 0)
    {
        double icon = Math.Floor(Math.Clamp(double.IsNaN(settingIcon) ? 52 : settingIcon, MinIcon, 256));
        double floor = Floor(settingIcon);
        if (icons <= 0 || double.IsNaN(available) || available <= 0) return icon;
        double limit = available - EndGap * 2;
        while (icon > floor && PanelLength(icon, spacing, icons, separators, extra) > limit) icon--;
        return icon;
    }

    /// <summary>아이콘 크기 + 간격 (간격은 줄이지 않음 — 하한에서도 넘치면 "⋯ +n").</summary>
    public static (double Icon, double Spacing) Fit(double settingIcon, double spacing, int icons, int separators, double available, double extra = 0)
    {
        spacing = double.IsNaN(spacing) ? 5 : Math.Clamp(spacing, 0, 64);
        return (FitIcon(settingIcon, spacing, icons, separators, available, extra), spacing);
    }

    /// <summary>
    /// 하한 크기로도 확대 최대 길이가 넘칠 때 보일 수 있는 칸 수 ("⋯ +n" 칸 포함, 최소 2). 넘치지 않으면 int.MaxValue (#25 §2).
    /// </summary>
    public static int Capacity(double icon, double spacing, int icons, int separators, double available, double extra = 0)
    {
        if (icons <= 0 || double.IsNaN(available) || available <= 0) return int.MaxValue;
        double limit = available - EndGap * 2;
        if (PanelLength(icon, spacing, icons, separators, extra) <= limit) return int.MaxValue;
        int n = icons - 1;
        while (n > 2 && PanelLength(icon, spacing, n, separators, extra) > limit) n--;
        return Math.Max(2, n);
    }
}
