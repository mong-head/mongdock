using System.Windows;
using System.Windows.Threading;

namespace Mongdock.Views;

/// <summary>
/// 좁은 화면에서 오른쪽 구역 우선순위 접기 (계산은 TopBarFit). 앱 메뉴 제목의 » 묶음(TopBarWindow.AppMenus)과 별개로,
/// 오른쪽 구역 자체가 왼쪽(로고·앱 이름)을 덮을 만큼 길 때만 동작한다. 설정값은 바꾸지 않음 — 넓어지면 다시 펼침.
/// </summary>
public partial class TopBarWindow
{
    private RightFold _fold;
    private bool _fitQueued;
    private double _lastRightNeed = -1, _lastBarWidth = -1;
    private double _netSpeedW, _desktopsW; // 접기 전에 잰 폭 (펼칠 때 필요한 폭)

    /// <summary>폭 때문에 ⌃ 로 더 접은 트레이 아이콘 수 (TopBarWindow.Tray / ⌃ 카드).</summary>
    private int TrayFold => _fold.Tray;

    private void HookRightFit()
    {
        SizeChanged += (_, _) => QueueFitRight();
        // 오른쪽 자식 폭은 구역 폭이 바 폭에 막혀 SizeChanged 가 안 올 수 있음 → 레이아웃마다 합만 비교 (자식 10여 개라 가벼움)
        LayoutUpdated += (_, _) =>
        {
            if (_closed || _fitQueued) return;
            double need = RightNeed();
            if (Math.Abs(need - _lastRightNeed) > 0.5 || Math.Abs(ActualWidth - _lastBarWidth) > 0.5) QueueFitRight();
        };
    }

    private void QueueFitRight()
    {
        if (_fitQueued || _closed) return;
        _fitQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fitQueued = false;
            FitRightSection();
        }, DispatcherPriority.Background);
    }

    /// <summary>오른쪽 구역 자식(보이는 것)의 원하는 폭 합 + 구역 여백.</summary>
    private double RightNeed()
    {
        double sum = RightSection.Margin.Left + RightSection.Margin.Right;
        foreach (UIElement c in RightSection.Children)
            if (c.Visibility == Visibility.Visible) sum += c.DesiredSize.Width;
        return sum;
    }

    /// <summary>왼쪽에 꼭 남길 폭: 로고 + 앱 이름(최대 120까지만 셈) + 앱 메뉴 » 자리.</summary>
    private double LeftMinWidth()
    {
        double w = LeftSection.Margin.Left;
        if (LogoButton.Visibility == Visibility.Visible) w += LogoButton.DesiredSize.Width;
        if (AppNameButton.Visibility == Visibility.Visible)
            w += Math.Min(AppNameButton.DesiredSize.Width, 120 + AppNameButton.Margin.Left + 14);
        if (AppMenuBar.Visibility == Visibility.Visible && AppMenuBar.Children.Count > 0) w += 30;
        return w;
    }

    private void FitRightSection()
    {
        if (_closed || !IsLoaded || ActualWidth <= 0 || IsReordering || TrayIconDrag.IsActive) return;
        double barW = ActualWidth;
        double need = RightNeed();
        _lastRightNeed = need;
        _lastBarWidth = barW;

        var s = _services.Settings.Current.TopBar;
        if (NetSpeed.Visibility == Visibility.Visible) _netSpeedW = NetSpeed.DesiredSize.Width;
        if (DesktopButtons.Visibility == Visibility.Visible) _desktopsW = DesktopButtons.DesiredSize.Width;

        int trayOnBar = 0;
        double trayW = 0;
        bool moreShown = false;
        foreach (UIElement c in TrayArea.Children)
        {
            if (c is TrayIconButton) { trayOnBar++; trayW += c.DesiredSize.Width; }
            else moreShown = true;
        }
        trayW = trayOnBar > 0 ? trayW / trayOnBar : 26;
        double moreW = _trayMore?.DesiredSize.Width is > 0 and var mw ? mw : 24;

        double available = barW - LeftMinWidth() - 12;
        var next = TopBarFit.Next(_fold, available, need, trayOnBar, trayW, moreShown, moreW,
            s.ShowNetworkSpeed ? _netSpeedW : 0, s.ShowDesktopButtons ? _desktopsW : 0);
        if (next != _fold)
        {
            // 열린 ⌃ 카드는 처음 받은 접힘 수로 그렸으므로 닫음 (아이콘이 바·카드에 겹치거나 빠지지 않게)
            if (next.Tray != _fold.Tray && _panel?.Kind == StatusPanelKind.Tray) _panel.Close();
            _fold = next;
            Services.Log.Info($"상단바 폭 {barW:0} DIP — 오른쪽 접기: 트레이 {next.Tray}개, 속도 {(next.NetSpeed ? "숨김" : "보임")}, 데스크톱 {(next.Desktops ? "숨김" : "보임")}");
            ApplyFoldVisibility();
            SyncTrayIcons();
            Remeasure(RightSection);
            QueueFitRight(); // 바뀐 폭으로 한 번 더 (앱 이름 폭도)
            return;
        }

        // 다 접어도 넘치면 앱 이름을 말줄임으로 줄임 (최소 48)
        double leftFixed = LeftSection.Margin.Left
                           + (LogoButton.Visibility == Visibility.Visible ? LogoButton.DesiredSize.Width : 0)
                           + AppNameButton.Margin.Left + 14 + 12;
        double nameMax = Math.Clamp(Math.Floor(barW - need - leftFixed), 48, 420);
        if (Math.Abs(AppName.MaxWidth - nameMax) > 0.5) AppName.MaxWidth = nameMax;
    }

    /// <summary>설정 + 접힘 상태로 네트워크 속도·데스크톱 묶음 표시 (ApplySettings 에서도 호출).</summary>
    private void ApplyFoldVisibility()
    {
        var s = _services.Settings.Current.TopBar;
        NetSpeed.Visibility = Vis(s.ShowNetworkSpeed && !_fold.NetSpeed);
        DesktopButtons.Visibility = Vis(s.ShowDesktopButtons && !_fold.Desktops);
    }
}
