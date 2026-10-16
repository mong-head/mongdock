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
    // 흔들림 막기 (#18): 줄이기는 바로(넘치면 안 됨), 키우기(설정값으로 되돌리기 포함)는 줄인 지 10초 지나고 2초 동안 계속 들어갈 때만.
    // 앱 아이콘이 몇 초마다 생겼다 없어지면 50↔47↔52 로 몇 초마다 바뀌던 것 (1080p 125%, 독 오른쪽)
    internal const int FitGrowHoldMs = 10_000, FitGrowSteadyMs = 2_000;
    private long _fitShrunkAt;
    private long _fitGrowSince;
    private double _fitLastAvailable;
    private DispatcherTimer? _fitGrowTimer;

    /// <summary>새 크기·간격이 지금보다 커지는지 (0 = 설정값 = 가장 큼, -1 간격 = 설정값).</summary>
    internal static bool FitGrows(double oldFit, double oldSpacing, double newFit, double newSpacing) =>
        (oldFit > 0 && (newFit == 0 || newFit > oldFit + 0.5)) || (oldSpacing >= 0 && (newSpacing < 0 || newSpacing > oldSpacing + 0.5));

    /// <summary>키우기를 지금 해도 되는지 (흔들림 막기 규칙). now·shrunkAt·growSince 는 TickCount64.</summary>
    internal static bool FitGrowAllowed(long now, long shrunkAt, long growSince) =>
        now - shrunkAt >= FitGrowHoldMs && growSince > 0 && now - growSince >= FitGrowSteadyMs;

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
            else if (!IsOverflowItem(i)) icons++;
        }
        icons += _hiddenItems.Count; // 감춘 앱까지 다 넣었을 때 기준 (넘침이 풀리는지 보려고)
        // 간격은 설정값 기준으로 계산 (지금 줄어 있는 간격 기준이면 다시 넓어지지 않음)
        double settingSpacing = double.IsNaN(s.IconSpacing) ? 5 : Math.Clamp(s.IconSpacing, 0, 64);
        // 확대 여유: 끝 아이콘을 확대해도 화면 안에 다 보이게 (가벼운 모드·확대 끔이면 0)
        DockFit.HoverScale = _layout.HoverScale;
        DockFit.Wave = _layout.Wave;
        var (want, spacing) = DockFit.Fit(s.IconSize, settingSpacing, icons, seps, available);
        double setting = Math.Floor(Math.Clamp(double.IsNaN(s.IconSize) ? 52 : s.IconSize, DockFit.MinIcon, 256));
        double newFit = want < setting ? want : 0;
        double newSpacing = spacing < settingSpacing ? spacing : -1;
        int newCapacity = DockFit.Capacity(want, spacing, icons, seps, available); // 하한·간격 1 로도 넘치면 "⋯ +n"
        if (Math.Abs(newFit - _fitIcon) < 0.5 && Math.Abs(newSpacing - _fitSpacing) < 0.5 && newCapacity == _overflowCapacity) { _fitGrowSince = 0; return; }
        _fitLastAvailable = available;
        long now = Environment.TickCount64;
        if (FitGrows(_fitIcon, _fitSpacing, newFit, newSpacing))
        {
            if (_fitGrowSince == 0) _fitGrowSince = now;
            if (!FitGrowAllowed(now, _fitShrunkAt, _fitGrowSince))
            {
                // 조금 뒤 다시 재 봄 (그때도 들어가면 키움)
                _fitGrowTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => { _fitGrowTimer!.Stop(); if (!_closed) CheckFit(_fitLastAvailable); }, Dispatcher);
                if (!_fitGrowTimer.IsEnabled) _fitGrowTimer.Start();
                return;
            }
        }
        else _fitGrowSince = 0;
        _fitPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _fitPending = false;
            if (_closed || AnyItemDrag || _itemDragging || _dragArmed) return;
            if (Math.Abs(newFit - _fitIcon) < 0.5 && Math.Abs(newSpacing - _fitSpacing) < 0.5 && newCapacity == _overflowCapacity) return;
            if (newCapacity != _overflowCapacity) Log.Info(newCapacity == int.MaxValue ? "독 넘침 없음" : $"독 넘침: 보일 칸 {newCapacity}개");
            _overflowCapacity = newCapacity;
            Log.Info(newFit > 0
                ? Loc.F($"독이 화면보다 길어 아이콘을 {newFit:0} 으로 줄임 (설정 {setting:0}{(newSpacing >= 0 ? Loc.F($", 간격 {newSpacing:0}") : "")})")
                : Loc.T("독 아이콘 크기를 설정값으로 되돌림"));
            if (FitGrows(_fitIcon, _fitSpacing, newFit, newSpacing)) _fitGrowSince = 0;
            else if (newFit > 0 || newSpacing >= 0) _fitShrunkAt = Environment.TickCount64;
            _fitIcon = newFit;
            _fitSpacing = newSpacing;
            ApplyAll();
        }, DispatcherPriority.Background);
    }
}
