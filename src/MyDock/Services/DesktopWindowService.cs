using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyDock.Models;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 오버레이 창 스타일 적용 + AppBar(SHAppBarMessage) 등록/해제.
/// 주 모니터 기준. 창 크기·위치는 물리 픽셀로 SetWindowPos.
/// 프로세스 종료/처리되지 않은 예외 시 등록된 모든 AppBar 를 ABM_REMOVE 하는 안전장치 포함.
/// </summary>
public sealed class DesktopWindowService : IDesktopWindowService
{
    private sealed class AppBarState
    {
        public required Window Window { get; init; }
        public required IntPtr Hwnd { get; init; }
        public required HwndSource Source { get; init; }
        public required HwndSourceHook Hook { get; init; }
        public uint Edge;
        public double ThicknessDip;
        /// <summary>RegisterTopAppBar 로 등록된 상단바 — 같은 ABE_TOP 의 독보다 항상 화면 가장자리 쪽.</summary>
        public bool IsTopBar;
        public bool Positioning;
        public bool RepositionQueued;
        public RECT LastRect;
    }

    private static readonly object RegistryGate = new();
    /// <summary>프로세스 종료 안전장치용 — 등록된 모든 AppBar hwnd.</summary>
    private static readonly HashSet<IntPtr> RegisteredHwnds = new();
    private static bool _exitHooksInstalled;

    private readonly Dictionary<IntPtr, AppBarState> _bars = new();
    private readonly uint _callbackMsg;

    public DesktopWindowService()
    {
        _callbackMsg = User32.RegisterWindowMessage("MyDock.AppBarCallback");
        InstallExitHooks();
    }

    // ───────────────────────── 오버레이 ─────────────────────────

    public void MakeOverlay(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            long ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
            long nex = (ex | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW) & ~User32.WS_EX_APPWINDOW;
            if (nex != ex)
            {
                User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, nex);
                User32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED);
            }
        }
        catch (Exception e)
        {
            Log.Error("MakeOverlay 실패", e);
        }
    }

    // ───────────────────────── AppBar ─────────────────────────

    public void RegisterAppBar(Window window, DockEdge edge, double thickness)
    {
        uint abe = edge switch
        {
            DockEdge.Left => Shell32.ABE_LEFT,
            DockEdge.Right => Shell32.ABE_RIGHT,
            DockEdge.Top => Shell32.ABE_TOP,
            _ => Shell32.ABE_BOTTOM,
        };
        Register(window, abe, thickness, isTopBar: false);
    }

    public void RegisterTopAppBar(Window window, double thickness) => Register(window, Shell32.ABE_TOP, thickness, isTopBar: true);

    private void Register(Window window, uint abe, double thickness, bool isTopBar)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (_bars.TryGetValue(hwnd, out var existing))
            {
                // 이미 등록됨 → 가장자리/두께만 바꾸고 재배치
                existing.Edge = abe;
                existing.ThicknessDip = thickness;
                existing.IsTopBar = isTopBar;
                Reposition(existing);
                QueueRepositionOthers(existing);
                return;
            }

            // 상단바를 상단 독보다 나중에 등록하면 시스템이 상단바를 독 아래에 쌓으므로,
            // 상단 독을 잠시 해제했다가 상단바 등록 후 다시 등록해 순서를 맞춘다.
            List<AppBarState> reattach = isTopBar
                ? _bars.Values.Where(b => !b.IsTopBar && b.Edge == Shell32.ABE_TOP).ToList()
                : new List<AppBarState>();
            foreach (var b in reattach)
            {
                var rm = NewData(b.Hwnd);
                Shell32.SHAppBarMessage(Shell32.ABM_REMOVE, ref rm);
            }

            var source = HwndSource.FromHwnd(hwnd);
            if (source is null)
            {
                Log.Error("RegisterAppBar: HwndSource 없음");
                return;
            }

            var abd = NewData(hwnd);
            abd.uCallbackMessage = _callbackMsg;
            if (Shell32.SHAppBarMessage(Shell32.ABM_NEW, ref abd) == UIntPtr.Zero)
            {
                Log.Error($"ABM_NEW 실패 hwnd=0x{hwnd.ToInt64():X}");
                return;
            }
            lock (RegistryGate) RegisteredHwnds.Add(hwnd);

            AppBarState? state = null;
            HwndSourceHook hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                state is null ? IntPtr.Zero : AppBarWndProc(state, msg, wParam, lParam);
            state = new AppBarState
            {
                Window = window,
                Hwnd = hwnd,
                Source = source,
                Hook = hook,
                Edge = abe,
                ThicknessDip = thickness,
                IsTopBar = isTopBar,
            };
            source.AddHook(hook);
            _bars[hwnd] = state;
            window.Closed += OnWindowClosed;

            Reposition(state);
            Log.Info($"AppBar 등록 edge={abe} topbar={isTopBar} thickness={thickness}DIP rect={state.LastRect}");

            foreach (var b in reattach)
            {
                var re = NewData(b.Hwnd);
                re.uCallbackMessage = _callbackMsg;
                Shell32.SHAppBarMessage(Shell32.ABM_NEW, ref re);
                Reposition(b);
            }
            QueueRepositionOthers(state);
        }
        catch (Exception e)
        {
            Log.Error("RegisterAppBar 실패", e);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is Window w) UnregisterAppBar(w);
    }

    public void UnregisterAppBar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !_bars.Remove(hwnd, out var state)) return;
            window.Closed -= OnWindowClosed;
            try { state.Source.RemoveHook(state.Hook); } catch { /* 이미 파괴됨 */ }
            Remove(hwnd);
            state.LastRect = default;
            QueueRepositionOthers(state); // 남은 AppBar 가 빈 공간을 차지하도록
            Log.Info("AppBar 해제");
        }
        catch (Exception e)
        {
            Log.Error("UnregisterAppBar 실패", e);
        }
    }

    private static void Remove(IntPtr hwnd)
    {
        var abd = NewData(hwnd);
        Shell32.SHAppBarMessage(Shell32.ABM_REMOVE, ref abd);
        lock (RegistryGate) RegisteredHwnds.Remove(hwnd);
    }

    private IntPtr AppBarWndProc(AppBarState state, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if ((uint)msg == _callbackMsg)
            {
                switch ((int)wParam.ToInt64())
                {
                    case Shell32.ABN_POSCHANGED:
                        QueueReposition(state);
                        break;
                    case Shell32.ABN_FULLSCREENAPP:
                        // 전체 화면 앱이 열리면 lParam != 0 — 현재는 동작 변경 없음 (UI 가 필요하면 확장)
                        break;
                }
                return IntPtr.Zero;
            }

            switch (msg)
            {
                case User32.WM_ACTIVATE:
                {
                    var abd = NewData(state.Hwnd);
                    Shell32.SHAppBarMessage(Shell32.ABM_ACTIVATE, ref abd);
                    break;
                }
                case User32.WM_WINDOWPOSCHANGED:
                {
                    var abd = NewData(state.Hwnd);
                    Shell32.SHAppBarMessage(Shell32.ABM_WINDOWPOSCHANGED, ref abd);
                    break;
                }
                case User32.WM_DISPLAYCHANGE:
                case User32.WM_DPICHANGED:
                    QueueReposition(state);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error("AppBar WndProc 예외", e);
        }
        return IntPtr.Zero;
    }

    private void QueueReposition(AppBarState state)
    {
        if (state.RepositionQueued) return;
        state.RepositionQueued = true;
        state.Window.Dispatcher.InvokeAsync(() =>
        {
            state.RepositionQueued = false;
            if (_bars.ContainsKey(state.Hwnd)) Reposition(state);
        }, DispatcherPriority.Background);
    }

    private void QueueRepositionOthers(AppBarState changed)
    {
        foreach (var b in _bars.Values)
            if (b != changed) QueueReposition(b);
    }

    /// <summary>ABM_QUERYPOS → 두께 보정 → ABM_SETPOS → SetWindowPos.</summary>
    private void Reposition(AppBarState state)
    {
        if (state.Positioning) return;
        state.Positioning = true;
        try
        {
            var (sx, sy) = GetDeviceScale(state.Window);
            int screenW = User32.GetSystemMetrics(User32.SM_CXSCREEN);
            int screenH = User32.GetSystemMetrics(User32.SM_CYSCREEN);
            bool horizontal = state.Edge is Shell32.ABE_TOP or Shell32.ABE_BOTTOM;
            int px = Math.Max(1, (int)Math.Round(state.ThicknessDip * (horizontal ? sy : sx)));

            var abd = NewData(state.Hwnd);
            abd.uEdge = state.Edge;
            abd.rc = new RECT(0, 0, screenW, screenH);
            ApplyThickness(ref abd.rc, state.Edge, px);

            Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref abd);
            // 우리 상단바가 있으면 독(상단/좌/우)은 그 아래에서 시작 — 시스템 조정이 부족한 경우를 대비한 직접 오프셋
            if (!state.IsTopBar && state.Edge != Shell32.ABE_BOTTOM)
            {
                var top = _bars.Values.FirstOrDefault(b => b.IsTopBar && b.Edge == Shell32.ABE_TOP && b != state);
                if (top is not null && top.LastRect.Height > 0 && abd.rc.Top < top.LastRect.Bottom)
                    abd.rc.Top = top.LastRect.Bottom;
            }
            // 시스템이 다른 AppBar/작업표시줄을 피해 조정한 rc 에서, 붙는 쪽 가장자리를 기준으로 두께 재적용
            ApplyThickness(ref abd.rc, state.Edge, px);
            Shell32.SHAppBarMessage(Shell32.ABM_SETPOS, ref abd);
            // SETPOS 가 다시 조정했을 수 있으므로 두께 보정
            ApplyThickness(ref abd.rc, state.Edge, px);

            RECT rc = abd.rc;
            bool changed = rc.Left != state.LastRect.Left || rc.Top != state.LastRect.Top ||
                           rc.Right != state.LastRect.Right || rc.Bottom != state.LastRect.Bottom;
            state.LastRect = rc;
            User32.SetWindowPos(state.Hwnd, IntPtr.Zero, rc.Left, rc.Top, rc.Width, rc.Height,
                User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            if (changed && state.IsTopBar) QueueRepositionOthers(state);
        }
        catch (Exception e)
        {
            Log.Error("AppBar 재배치 실패", e);
        }
        finally
        {
            state.Positioning = false;
        }
    }

    private static void ApplyThickness(ref RECT rc, uint edge, int px)
    {
        switch (edge)
        {
            case Shell32.ABE_LEFT: rc.Right = rc.Left + px; break;
            case Shell32.ABE_RIGHT: rc.Left = rc.Right - px; break;
            case Shell32.ABE_TOP: rc.Bottom = rc.Top + px; break;
            case Shell32.ABE_BOTTOM: rc.Top = rc.Bottom - px; break;
        }
    }

    private static (double X, double Y) GetDeviceScale(Window window)
    {
        var src = PresentationSource.FromVisual(window);
        if (src?.CompositionTarget is { } ct)
        {
            var m = ct.TransformToDevice;
            if (m.M11 > 0 && m.M22 > 0) return (m.M11, m.M22);
        }
        var dpi = VisualTreeHelper.GetDpi(window);
        return (dpi.DpiScaleX, dpi.DpiScaleY);
    }

    private static APPBARDATA NewData(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = hwnd,
    };

    public Rect GetPrimaryScreenBounds() =>
        new(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

    /// <summary>주 모니터 작업 영역 (DIP) — 작업표시줄과 등록된 AppBar 를 뺀 영역.</summary>
    public Rect GetPrimaryWorkArea() => SystemParameters.WorkArea;

    // ───────────────────────── 종료 안전장치 ─────────────────────────

    private static void InstallExitHooks()
    {
        lock (RegistryGate)
        {
            if (_exitHooksInstalled) return;
            _exitHooksInstalled = true;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAll("ProcessExit");
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RemoveAll("UnhandledException");
    }

    /// <summary>등록된 모든 AppBar 를 해제 (어느 스레드에서나 호출 가능).</summary>
    public static void RemoveAll(string reason)
    {
        IntPtr[] hwnds;
        lock (RegistryGate)
        {
            hwnds = RegisteredHwnds.ToArray();
            RegisteredHwnds.Clear();
        }
        foreach (var h in hwnds)
        {
            try
            {
                var abd = NewData(h);
                Shell32.SHAppBarMessage(Shell32.ABM_REMOVE, ref abd);
            }
            catch
            {
                // 종료 중 — 무시
            }
        }
        if (hwnds.Length > 0) Log.Info($"AppBar {hwnds.Length}개 해제 ({reason})");
    }
}
