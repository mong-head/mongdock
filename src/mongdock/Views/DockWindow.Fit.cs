using System.Windows.Threading;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독 레이아웃 만들기: 가벼운 모드(PerfMode) + 화면보다 길면 아이콘 자동 축소 (DockFit).
/// 축소는 설정값을 바꾸지 않고 이 창 안에서만 — 앱이 줄거나 화면이 넓어지면 설정 크기로 돌아간다.
/// </summary>
public partial class DockWindow
{
    /// <summary>자동 숨김 트리거 확인 주기 (가벼운 모드면 2배).</summary>
    private const double AutoHidePollMs = 60;

    /// <summary>화면에 맞춰 줄인 아이콘 크기 (0 = 줄이지 않음, 설정값 사용).</summary>
    private double _fitIcon;
    /// <summary>아이콘을 최소까지 줄여도 넘쳐서 줄인 간격 (-1 = 설정값).</summary>
    private double _fitSpacing = -1;
    private bool _fitPending;

    private DockLayout BuildLayout()
    {
        var s = _services.Settings.Current;
        bool light = PerfMode.Sync(s);
        return DockLayout.From(s.Dock, SystemTheme.AppsUseLightTheme(), light, _fitIcon > 0 ? _fitIcon : null,
            _fitSpacing >= 0 ? _fitSpacing : null);
    }

    /// <summary>
    /// Place 에서 독 방향으로 쓸 수 있는 길이를 알게 된 뒤 호출. 지금 아이콘 크기가 맞지 않으면(넘치거나, 줄였는데 이제 들어가면)
    /// 다음 틱에 전체 재적용 (Place 도중 뷰를 다시 만들지 않게).
    /// </summary>
    private void CheckFit(double available)
    {
        if (_fitPending || _closed || AnyItemDrag || _itemDragging || _dragArmed) return;
        var s = _services.Settings.Current.Dock;
        int icons = 0, seps = 0;
        foreach (var i in _items)
        {
            if (i.IsSeparator) seps++;
            else icons++;
        }
        // 간격은 설정값 기준으로 계산 (지금 줄어 있는 간격 기준이면 다시 넓어지지 않음)
        double settingSpacing = double.IsNaN(s.IconSpacing) ? 5 : Math.Clamp(s.IconSpacing, 0, 64);
        var (want, spacing) = DockFit.Fit(s.IconSize, settingSpacing, icons, seps, available);
        double setting = Math.Floor(Math.Clamp(double.IsNaN(s.IconSize) ? 52 : s.IconSize, DockFit.MinIcon, 256));
        double newFit = want < setting ? want : 0;
        double newSpacing = spacing < settingSpacing ? spacing : -1;
        if (Math.Abs(newFit - _fitIcon) < 0.5 && Math.Abs(newSpacing - _fitSpacing) < 0.5) return;
        _fitPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fitPending = false;
            if (_closed || AnyItemDrag || _itemDragging || _dragArmed) return;
            if (Math.Abs(newFit - _fitIcon) < 0.5 && Math.Abs(newSpacing - _fitSpacing) < 0.5) return;
            Log.Info(newFit > 0
                ? Loc.F($"독이 화면보다 길어 아이콘을 {newFit:0} 으로 줄임 (설정 {setting:0}{(newSpacing >= 0 ? Loc.F($", 간격 {newSpacing:0}") : "")})")
                : Loc.T("독 아이콘 크기를 설정값으로 되돌림"));
            _fitIcon = newFit;
            _fitSpacing = newSpacing;
            ApplyAll();
        }, DispatcherPriority.Background);
    }
}
