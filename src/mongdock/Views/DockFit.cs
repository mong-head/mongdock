namespace Mongdock.Views;

/// <summary>
/// 독이 화면 길이를 넘을 때 아이콘 크기 자동 축소 계산 (UI 요소 없이 숫자만 — 검증하기 쉽게 분리).
/// 세로 모니터 아래 독(1080 px @150% = 720 DIP)이나 왼쪽/오른쪽 독에 앱이 많을 때.
/// 길이 공식은 DockLayout/DockItemView 와 같다: 패딩 = round(max(5, 아이콘×0.12)), 아이콘 슬롯 = 아이콘 + 간격,
/// 구분선 슬롯 = 1 + max(5, 간격+4)×2, 테두리 2.
/// </summary>
internal static class DockFit
{
    /// <summary>설정 슬라이더 최솟값보다 낮게 (설정값 범위 확인용).</summary>
    public const double MinIcon = 16;
    /// <summary>자동 축소 하한 (#25 §1 — 그보다 작으면 알아보기 어려움). 넘치면 "⋯ +n" 칸으로. 설정값이 이보다 작으면 설정값까지.</summary>
    public const double ShrinkFloor = 28;

    /// <summary>줄일 수 있는 가장 작은 크기.</summary>
    public static double Floor(double settingIcon) => Math.Min(ShrinkFloor, Math.Floor(Math.Clamp(double.IsNaN(settingIcon) ? 52 : settingIcon, MinIcon, 256)));
    /// <summary>패널 양 끝과 화면 끝 사이에 남길 여유 (각 쪽).</summary>
    public const double EndGap = 8;

    public static double PanelLength(double icon, double spacing, int icons, int separators, double extra = 0)
    {
        double pad = Math.Round(Math.Max(5, icon * 0.12));
        double sep = 1 + Math.Max(5, spacing + 4) * 2;
        return pad * 2 + 2 + icons * (icon + spacing) + separators * sep + extra + 2 * Growth(icon);
    }

    /// <summary>
    /// 확대(파도)로 독 양 끝이 한쪽에 튀어나오는 길이 계수 — DockLayout.GrowthRoom 과 같은 식 (아이콘 × 계수 + 12, 확대 끔이면 0).
    /// 맞춤 계산이 이것을 빼지 않으면 쉬는 상태에선 들어가도 끝 아이콘을 확대할 때 화면 밖으로 잘림 (#25 사용자: 오른쪽 세로 독 위·아래 잘림).
    /// UI 스레드에서 Fit 부르기 전에 정함.
    /// </summary>
    [ThreadStatic] public static double GrowthFactor;

    public static double Growth(double icon) => GrowthFactor > 0 ? Math.Ceiling(icon * GrowthFactor + 12) : 0;

    /// <summary>
    /// 사용 가능한 길이(available, DIP) 안에 들어가는 가장 큰 아이콘 크기 (설정값 이하, 정수, 최소 <see cref="MinIcon"/>).
    /// 설정값 그대로 들어가면 설정값을 돌려준다.
    /// </summary>
    public static double FitIcon(double settingIcon, double spacing, int icons, int separators, double available, double extra = 0)
    {
        double icon = Math.Floor(Math.Clamp(double.IsNaN(settingIcon) ? 52 : settingIcon, MinIcon, 256));
        double floor = Floor(settingIcon);
        if (icons <= 0 || double.IsNaN(available) || available <= 0) return icon;
        double limit = available - EndGap * 2;
        if (PanelLength(icon, spacing, icons, separators, extra) <= limit) return icon;
        // 선형이라 바로 계산 뒤 패딩 반올림 때문에 한두 칸 보정
        double fixedLen = separators * (1 + Math.Max(5, spacing + 4) * 2) + 2 + extra + icons * spacing + (GrowthFactor > 0 ? 24 : 0);
        double guess = Math.Floor((limit - fixedLen) / (icons + 0.24 + 2 * GrowthFactor));
        icon = Math.Clamp(guess + 2, floor, icon);
        while (icon > floor && PanelLength(icon, spacing, icons, separators, extra) > limit) icon--;
        return icon;
    }

    /// <summary>
    /// 아이콘 크기 + 간격. 아이콘을 하한(28)까지 줄여도 넘치면 간격도 1 까지 줄인다
    /// (세로 모니터 @200% 이상에 앱이 아주 많을 때). 그래도 넘치면 <see cref="Overflow"/> 로 "⋯ +n" 칸.
    /// </summary>
    public static (double Icon, double Spacing) Fit(double settingIcon, double spacing, int icons, int separators, double available, double extra = 0)
    {
        spacing = double.IsNaN(spacing) ? 5 : Math.Clamp(spacing, 0, 64);
        double icon = FitIcon(settingIcon, spacing, icons, separators, available, extra);
        double limit = available - EndGap * 2;
        if (icon > Floor(settingIcon) || icons <= 0 || PanelLength(icon, spacing, icons, separators, extra) <= limit) return (icon, spacing);
        double sp = spacing;
        while (sp > 1 && PanelLength(icon, sp, icons, separators, extra) > limit) sp--;
        return (icon, sp);
    }

    /// <summary>
    /// 하한 크기·간격으로도 넘칠 때 보일 수 있는 칸 수 ("⋯ +n" 칸 포함, 최소 2). 넘치지 않으면 int.MaxValue (#25 §2).
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
