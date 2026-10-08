namespace Mongdock.Views;

/// <summary>상단바 오른쪽 구역을 폭에 맞추려고 접은 상태. Tray = 바에서 ⌃ 로 더 접은 트레이 아이콘 수.</summary>
internal readonly record struct RightFold(int Tray, bool NetSpeed, bool Desktops)
{
    public bool Any => Tray > 0 || NetSpeed || Desktops;
}

/// <summary>
/// 좁은 화면(세로 모니터 1080 px @125% = 864 DIP, @150% = 720 DIP, @200% = 540 DIP …)에서 상단바 오른쪽 구역이 넘칠 때
/// 우선순위 접기 계산 (UI 요소 없이 숫자만 — 검증하기 쉽게 분리).
/// 접는 순서: ① 트레이 아이콘을 뒤쪽부터 ⌃ 로 ② 네트워크 속도 ③ 데스크톱 ‹ 1/4 › 묶음. 펼 때는 반대 순서.
/// 상태 아이콘(와이파이·소리 등)·한/영·배터리·시계는 접지 않는다 (그래도 넘치면 왼쪽 앱 이름이 말줄임으로 줄어듦).
/// </summary>
internal static class TopBarFit
{
    /// <summary>펼칠 때 여유 (펼치자마자 다시 접히며 깜빡이지 않게).</summary>
    public const double Hysteresis = 8;

    /// <param name="available">오른쪽 구역이 쓸 수 있는 폭 (바 폭 − 왼쪽 최소 폭 − 여백).</param>
    /// <param name="rightNeed">지금 상태(접힘 반영)에서 오른쪽 구역 자식들의 폭 합.</param>
    /// <param name="trayOnBar">지금 바에 보이는 트레이 아이콘 수 (⌃ 제외).</param>
    /// <param name="trayW">트레이 아이콘 하나 폭.</param>
    /// <param name="moreShown">⌃ 가 이미 보이는지 (안 보이면 처음 접을 때 ⌃ 폭만큼 더 듦).</param>
    /// <param name="netW">네트워크 속도 폭 (보이거나 접힌 경우 마지막으로 잰 폭, 꺼져 있으면 0).</param>
    /// <param name="deskW">데스크톱 묶음 폭 (같음).</param>
    public static RightFold Next(RightFold cur, double available, double rightNeed, int trayOnBar, double trayW,
        bool moreShown, double moreW, double netW, double deskW)
    {
        if (double.IsNaN(available) || double.IsNaN(rightNeed) || available <= 0) return cur;
        trayW = Math.Max(8, trayW);
        double over = rightNeed - available;
        var f = cur;
        if (over > 0.5)
        {
            if (trayOnBar > 0)
            {
                double extra = moreShown ? 0 : moreW;
                int n = Math.Min(trayOnBar, (int)Math.Ceiling((over + extra) / trayW));
                f = f with { Tray = f.Tray + n };
                over -= n * trayW - extra;
            }
            if (over > 0.5 && !f.NetSpeed && netW > 0)
            {
                f = f with { NetSpeed = true };
                over -= netW;
            }
            if (over > 0.5 && !f.Desktops && deskW > 0)
            {
                f = f with { Desktops = true };
            }
            return f;
        }

        double slack = -over;
        if (f.Desktops)
        {
            if (slack < deskW + Hysteresis) return f;
            f = f with { Desktops = false };
            slack -= deskW;
        }
        if (f.NetSpeed)
        {
            if (slack < netW + Hysteresis) return f;
            f = f with { NetSpeed = false };
            slack -= netW;
        }
        int tray = f.Tray;
        while (tray > 0 && slack >= trayW + Hysteresis)
        {
            tray--;
            slack -= trayW;
        }
        return f with { Tray = tray };
    }
}
