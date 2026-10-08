namespace Mongdock.Views;

/// <summary>
/// 독이 화면 길이를 넘을 때 아이콘 크기 자동 축소 계산 (UI 요소 없이 숫자만 — 검증하기 쉽게 분리).
/// 세로 모니터 아래 독(1080 px @150% = 720 DIP)이나 왼쪽/오른쪽 독에 앱이 많을 때.
/// 길이 공식은 DockLayout/DockItemView 와 같다: 패딩 = round(max(5, 아이콘×0.12)), 아이콘 슬롯 = 아이콘 + 간격,
/// 구분선 슬롯 = 1 + max(5, 간격+4)×2, 테두리 2.
/// </summary>
internal static class DockFit
{
    public const double MinIcon = 16;
    /// <summary>패널 양 끝과 화면 끝 사이에 남길 여유 (각 쪽).</summary>
    public const double EndGap = 8;

    public static double PanelLength(double icon, double spacing, int icons, int separators, double extra = 0)
    {
        double pad = Math.Round(Math.Max(5, icon * 0.12));
        double sep = 1 + Math.Max(5, spacing + 4) * 2;
        return pad * 2 + 2 + icons * (icon + spacing) + separators * sep + extra;
    }

    /// <summary>
    /// 사용 가능한 길이(available, DIP) 안에 들어가는 가장 큰 아이콘 크기 (설정값 이하, 정수, 최소 <see cref="MinIcon"/>).
    /// 설정값 그대로 들어가면 설정값을 돌려준다.
    /// </summary>
    public static double FitIcon(double settingIcon, double spacing, int icons, int separators, double available, double extra = 0)
    {
        double icon = Math.Floor(Math.Clamp(double.IsNaN(settingIcon) ? 52 : settingIcon, MinIcon, 256));
        if (icons <= 0 || double.IsNaN(available) || available <= 0) return icon;
        double limit = available - EndGap * 2;
        if (PanelLength(icon, spacing, icons, separators, extra) <= limit) return icon;
        // 선형이라 바로 계산 뒤 패딩 반올림 때문에 한두 칸 보정
        double fixedLen = separators * (1 + Math.Max(5, spacing + 4) * 2) + 2 + extra + icons * spacing;
        double guess = Math.Floor((limit - fixedLen) / (icons + 0.24));
        icon = Math.Clamp(guess + 2, MinIcon, icon);
        while (icon > MinIcon && PanelLength(icon, spacing, icons, separators, extra) > limit) icon--;
        return icon;
    }

    /// <summary>
    /// 아이콘 크기 + 간격. 아이콘을 최소(16)까지 줄여도 넘치면 간격도 1 까지 줄인다
    /// (세로 모니터 @200% 이상에 앱이 아주 많을 때). 그래도 넘치면 가운데 정렬로 양 끝이 조금 잘림.
    /// </summary>
    public static (double Icon, double Spacing) Fit(double settingIcon, double spacing, int icons, int separators, double available, double extra = 0)
    {
        spacing = double.IsNaN(spacing) ? 5 : Math.Clamp(spacing, 0, 64);
        double icon = FitIcon(settingIcon, spacing, icons, separators, available, extra);
        double limit = available - EndGap * 2;
        if (icon > MinIcon || icons <= 0 || PanelLength(icon, spacing, icons, separators, extra) <= limit) return (icon, spacing);
        double sp = spacing;
        while (sp > 1 && PanelLength(icon, sp, icons, separators, extra) > limit) sp--;
        return (icon, sp);
    }
}
