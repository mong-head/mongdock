using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 포커스를 뺏지 않는(NOACTIVATE) 창의 메뉴·패널은 바깥(다른 앱 창)을 클릭해도 WPF 가 닫힘을 알지 못한다.
/// 열려 있는 동안만 전역 마우스 다운 이벤트(GlobalMouseDown, 저수준 훅)를 구독하고, 눌린 위치가
/// 안쪽 영역(메뉴·하위 메뉴·독 등) 밖이거나 주 모니터 밖(null)이면 닫는다. 다른 창이 활성화돼도 닫는다.
/// 닫히면 반드시 구독 해제 → 백엔드 훅이 남지 않는다.
/// </summary>
internal sealed class OutsideClickWatcher
{
    /// <summary>열린 직후 이 시간 안의 이벤트는 무시 (메뉴를 연 클릭의 여파).</summary>
    private const long IgnoreAfterOpenMs = 100;

    private readonly AppServices _services;
    private readonly Func<IEnumerable<Rect>> _insideAreas;
    private readonly Action _close;
    private bool _running;
    private long _startedAt;

    /// <summary>
    /// 다른 창이 활성화돼도 닫을지 (기본 true). 트레이·알림 패널처럼 패널 안에서 누른 것이 다른 앱의
    /// 메뉴·창을 띄우는 경우 false — 그 메뉴가 떠도 패널은 남고, 바깥을 클릭할 때만 닫힌다.
    /// </summary>
    public bool CloseOnActivation { get; set; } = true;

    /// <summary>있으면 닫는 이유(바깥 클릭 위치·활성화된 창의 프로세스)를 로그에 남김 — 확인 카드처럼 드물게 뜨는 창만 (메뉴는 너무 잦음).</summary>
    public string? LogName { get; set; }

    public OutsideClickWatcher(AppServices services, Func<IEnumerable<Rect>> insideAreas, Action close)
    {
        _services = services;
        _insideAreas = insideAreas;
        _close = close;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _startedAt = Environment.TickCount64;
        _services.DesktopWindows.GlobalMouseDown += OnGlobalMouseDown;
        _services.Windows.WindowActivated += OnWindowActivated;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _services.DesktopWindows.GlobalMouseDown -= OnGlobalMouseDown;
        _services.Windows.WindowActivated -= OnWindowActivated;
    }

    private void OnWindowActivated(object? sender, IntPtr hwnd)
    {
        if (!CloseOnActivation)
        {
            if (LogName is not null && _running) Log.Info($"{LogName}: 다른 창 활성화 무시 ({ProcessOf(hwnd)})");
            return;
        }
        Fire(LogName is null ? null : Loc.F($"다른 창 활성화 ({ProcessOf(hwnd)})"));
    }

    private static string ProcessOf(IntPtr hwnd)
    {
        try
        {
            Native.User32.GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return "?"; }
    }

    private void OnGlobalMouseDown(object? sender, Point? position)
    {
        if (!_running || Environment.TickCount64 - _startedAt < IgnoreAfterOpenMs) return;
        if (position is Point c)
        {
            foreach (var r in _insideAreas())
            {
                if (!r.IsEmpty && r.Contains(c)) return; // 안쪽 클릭은 WPF 가 처리 (메뉴 항목 클릭 등)
            }
        }
        Fire(LogName is null ? null : Loc.F($"바깥 클릭 {position?.ToString() ?? Loc.T("(모니터 밖)")} / 안쪽 {string.Join(" ", _insideAreas())}"));
    }

    private void Fire(string? reason)
    {
        if (!_running) return;
        Stop();
        if (reason is not null) Log.Info($"{LogName}: 닫음 — {reason}");
        // 훅 콜백 안에서 바로 창/팝업을 닫지 않고 디스패처로 미룸
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try { _close(); }
            catch (Exception ex) { Log.Error("바깥 클릭 닫기 실패", ex); }
        });
    }

    // ───────────────────────── 영역 계산 헬퍼 ─────────────────────────

    /// <summary>요소의 화면 영역 (DIP). 화면에 없으면 Rect.Empty.</summary>
    public static Rect ScreenRect(FrameworkElement? el)
    {
        if (el == null || !el.IsVisible || el.ActualWidth <= 0) return Rect.Empty;
        var source = PresentationSource.FromVisual(el);
        if (source?.CompositionTarget == null) return Rect.Empty;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var a = toDip.Transform(el.PointToScreen(new Point(0, 0)));
        var b = toDip.Transform(el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight)));
        return new Rect(a, b);
    }

    /// <summary>메뉴 본체 + 열린 하위 메뉴들의 화면 영역.</summary>
    public static IEnumerable<Rect> MenuAreas(ContextMenu menu)
    {
        var list = new List<Rect> { ScreenRect(menu) };
        AddSubmenus(menu, list);
        return list;
    }

    private static void AddSubmenus(ItemsControl parent, List<Rect> list)
    {
        foreach (var obj in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(obj) is not MenuItem mi || !mi.IsSubmenuOpen) continue;
            if (mi.Template?.FindName("PART_Popup", mi) is Popup { Child: FrameworkElement child })
                list.Add(ScreenRect(child));
            AddSubmenus(mi, list);
        }
    }

    /// <summary>
    /// 메뉴에 바깥 클릭 닫힘을 붙인다. extraInside 는 메뉴 외에 "안쪽"으로 볼 영역(독/상단바 등 — 그 안의 클릭은 WPF 가 처리).
    /// </summary>
    public static void Attach(ContextMenu menu, AppServices services, Func<IEnumerable<Rect>>? extraInside = null)
    {
        OutsideClickWatcher? watcher = null;
        menu.Opened += (_, _) =>
        {
            watcher ??= new OutsideClickWatcher(services,
                () => extraInside == null ? MenuAreas(menu) : MenuAreas(menu).Concat(extraInside()),
                () => menu.IsOpen = false);
            watcher.Start();
        };
        menu.Closed += (_, _) => watcher?.Stop();
    }
}
