using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 창 최소화 효과 (#20, 0.8): 다른 앱 창이 최소화되면 그 창이 독 쪽으로 들어가고, 몽독에서 다시 열면 같은 길을 거꾸로 나옴.
/// - 모양(설정 → 독): 살짝 내려앉기 / 아이콘으로 줄어들기 / 아래로 미끄러지기 / 부드러운 지니. 모두 끝에서 천천히 멈춤(튀는 탄성 없음).
///   되살리기는 같은 길 거꾸로, 0.8배 시간. 윈도우 「애니메이션 효과」가 꺼져 있거나 절전 모드면 짧은 흐려짐만(되살리기는 바로).
/// - 알아채기: EVENT_SYSTEM_MINIMIZESTART (SetWinEventHook, 몽독 밖에서 받음 — 다른 앱에 끼어들지 않음).
///   되살리기는 몽독이 창을 여는 때만(독 클릭 등 AppLauncher.ActivateWindow): 효과를 먼저 보이고 끝에서 창을 되살림. 다른 곳(Alt+Tab 등)에서 열면 효과 없음.
/// - 그리기: 모니터 크기 투명 덮개 창 하나(계속 다시 씀)에 그 창의 DWM 썸네일(윈도우가 그려 둔 창 모습 — 최소화 뒤에도 마지막 모습을 들고 있음)을
///   띄워 움직임. 그림 복사가 없어 메모리를 거의 안 씀. 지니 = 가로 띠 24장(띠마다 다른 폭·위치), 나머지 = 썸네일 1장.
/// - 윈도우 기본 최소화 효과와 겹치지 않게 켜 둔 동안만 윈도우 「최소화·최대화 애니메이션」을 끔 (WindowAnimationSetting — 되돌림 보장).
/// 독이 아래가 아니면(왼쪽·오른쪽·위) 지니 대신 아이콘으로 줄어들기, 내려앉기·미끄러지기는 독 쪽으로. 독 아이콘을 못 찾으면 독 가운데로.
/// </summary>
internal sealed class MinimizeEffectController : IDisposable
{
    private const int GenieStrips = 24;

    private readonly AppServices _services;
    private readonly Func<IntPtr, Rect?> _iconRect;
    private readonly Func<DockEdge> _edge;
    private WinEventProc? _proc;
    private IntPtr _hook;
    private OverlayWindow? _overlay;
    private bool _disposed;

    public static MinimizeEffectController? Instance { get; private set; }

    /// <summary>시험용: 마지막으로 시작한 효과 (원래 자리, 도착 자리, 그린 모양, 되살리기인지).</summary>
    internal static (Rect From, Rect To, MinimizeEffect Effect, bool Restore)? LastPlayedForTest { get; private set; }

    /// <summary>시험용: 「애니메이션 줄이기·절전」 판단을 바꿔 끼움.</summary>
    internal static Func<bool>? ReducedForTest { get; set; }

    public MinimizeEffectController(AppServices services, Func<IntPtr, Rect?> iconRect, Func<DockEdge> edge)
    {
        _services = services;
        _iconRect = iconRect;
        _edge = edge;
        Instance = this;
        services.Settings.SettingsChanged += (_, _) => Sync();
        AppLauncher.AnimatedRestore = TryRestoreAnimated;
        Sync();
    }

    private MinimizeEffect Effect => _services.Settings.Current.Dock.MinimizeEffect;

    /// <summary>설정에 맞춰 훅·윈도우 애니메이션을 켜고 끔. 몽독 효과를 켤 때 윈도우 효과를 껐으면 true.</summary>
    public bool Sync()
    {
        if (_disposed) return false;
        if (Effect == MinimizeEffect.Off)
        {
            Unhook();
            WindowAnimationSetting.Restore("몽독 최소화 효과 끔");
            return false;
        }
        bool turnedOff = WindowAnimationSetting.TurnOff();
        Hook();
        return turnedOff;
    }

    private void Hook()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = OnEvent;
        _hook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        Log.Info(_hook != IntPtr.Zero ? $"창 최소화 효과 켬 ({Effect})" : "창 최소화 훅 실패");
    }

    private void Unhook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        Log.Info("창 최소화 효과 끔");
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || _disposed) return;
        try { Play(hwnd); }
        catch (Exception ex) { Log.Warn($"창 최소화 효과 실패: {ex.GetType().Name} {ex.Message}"); }
    }

    /// <summary>윈도우 「애니메이션 효과」가 꺼져 있거나 절전 모드 → 짧은 흐려짐만.</summary>
    private static bool Reduced()
    {
        if (ReducedForTest is { } f) return f();
        try { if (!SystemParameters.ClientAreaAnimation) return true; } catch { }
        try { return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On; } catch { return false; }
    }

    /// <summary>설정 모양 → 이 독 자리에서 그릴 모양 (지니는 독이 아래일 때만, 줄이기·절전이면 Off = 흐려짐만).</summary>
    internal static MinimizeEffect Resolve(MinimizeEffect effect, DockEdge edge, bool reduced) =>
        reduced ? MinimizeEffect.Off : effect == MinimizeEffect.Genie && edge != DockEdge.Bottom ? MinimizeEffect.Scale : effect;

    /// <summary>창 하나를 독 쪽으로 (UI 스레드).</summary>
    internal void Play(IntPtr hwnd) => Start(hwnd, restore: false, onDone: null);

    /// <summary>
    /// 몽독이 최소화된 창을 열 때 (AppLauncher.ActivateWindow): 독 쪽에서 창 자리로 나오는 효과를 보이고 끝에서 <paramref name="restore"/>(창 되살리기)를 부름.
    /// 효과를 못 보이면(꺼짐·흐려짐만·썸네일 실패·UI 스레드 아님) false — 부르는 쪽이 바로 되살림.
    /// </summary>
    internal bool TryRestoreAnimated(IntPtr hwnd, Action restore)
    {
        if (_disposed || _hook == IntPtr.Zero || Effect == MinimizeEffect.Off || Reduced()) return false;
        if (Application.Current?.Dispatcher is not { } d || !d.CheckAccess()) return false;
        try { return Start(hwnd, restore: true, onDone: restore); }
        catch (Exception ex) { Log.Warn($"창 되살리기 효과 실패: {ex.GetType().Name} {ex.Message}"); return false; }
    }

    private bool Start(IntPtr hwnd, bool restore, Action? onDone)
    {
        if (!TryWindowRect(hwnd, out var from)) return false;
        var monitor = MonitorFor(from);
        var bounds = monitor.BoundsRect;
        var edge = _edge();
        Rect to = _iconRect(hwnd) ?? DockCenter(bounds, edge);
        var shape = Resolve(Effect, edge, Reduced());
        LastPlayedForTest = (from, to, shape, restore);
        var overlay = _overlay ??= new OverlayWindow(_services);
        return overlay.Run(hwnd, new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), from, to, shape, edge, restore, onDone);
    }

    /// <summary>사각형(물리 픽셀) 가운데가 있는 모니터 (없으면 주 모니터).</summary>
    private static MonitorInfo MonitorFor(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        return Monitors.GetAll().FirstOrDefault(m => cx >= m.BoundsRect.Left && cx < m.BoundsRect.Right && cy >= m.BoundsRect.Top && cy < m.BoundsRect.Bottom) ?? Monitors.GetPrimary();
    }

    /// <summary>독 아이콘을 모를 때: 그 모니터의 독 쪽 가장자리 가운데 작은 칸.</summary>
    private static Rect DockCenter(Native.RECT b, DockEdge edge) => edge switch
    {
        DockEdge.Left => new Rect(b.Left + 10, (b.Top + b.Bottom) / 2 - 30, 60, 60),
        DockEdge.Right => new Rect(b.Right - 70, (b.Top + b.Bottom) / 2 - 30, 60, 60),
        DockEdge.Top => new Rect((b.Left + b.Right) / 2 - 30, b.Top + 10, 60, 60),
        _ => new Rect((b.Left + b.Right) / 2 - 30, b.Bottom - 70, 60, 60),
    };

    /// <summary>최소화 직전 창 자리 (물리 픽셀): DWM 테두리 사각형, 이미 최소화로 옮겨졌으면 되살릴 자리(최대화였으면 모니터 작업 영역).</summary>
    private static bool TryWindowRect(IntPtr hwnd, out Rect rect)
    {
        rect = default;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Left > -30000 && r.Right - r.Left > 60 && r.Bottom - r.Top > 40)
        {
            rect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return true;
        }
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return false;
        var n = wp.rcNormalPosition;
        var guess = new Rect(n.Left, n.Top, Math.Max(1, n.Right - n.Left), Math.Max(1, n.Bottom - n.Top));
        var m = MonitorFor(guess);
        if ((wp.flags & WPF_RESTORETOMAXIMIZED) != 0)
        {
            var w = m.WorkRect;
            rect = new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }
        else
        {
            // 작업 공간 좌표 → 화면 좌표 (작업 영역 왼쪽 위만큼 밀림)
            var w = m.WorkRect;
            var b = m.BoundsRect;
            rect = new Rect(guess.X + (w.Left - b.Left), guess.Y + (w.Top - b.Top), guess.Width, guess.Height);
        }
        return rect.Width > 60 && rect.Height > 40;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unhook();
        AppLauncher.AnimatedRestore = null;
        _overlay?.Close();
        WindowAnimationSetting.Restore("몽독 종료");
        if (Instance == this) Instance = null;
    }

    // ───────────────────────── 덮개 창 ─────────────────────────

    /// <summary>모니터 크기 투명 창 하나 — 썸네일을 띄워 움직이고 끝나면 숨김 (클릭은 지나감, 포커스 안 뺏음).</summary>
    private sealed class OverlayWindow : Window
    {
        private IntPtr _hwnd;
        private readonly List<IntPtr> _thumbs = new();
        private EventHandler? _frame;
        private Action? _onDone;
        private DispatcherTimer? _hideLater;
        private Rect _bounds;

        public OverlayWindow(AppServices services)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            Title = "mongdock Minimize";
            SourceInitialized += (_, _) =>
            {
                services.DesktopWindows.MakeOverlay(this);
                _hwnd = new WindowInteropHelper(this).Handle;
                long ex = Native.User32.GetWindowLong(_hwnd, Native.User32.GWL_EXSTYLE);
                Native.User32.SetWindowLong(_hwnd, Native.User32.GWL_EXSTYLE, ex | 0x20 /* WS_EX_TRANSPARENT */);
            };
        }

        /// <summary>효과 시작. 썸네일을 못 만들면 false (되살리기면 부르는 쪽이 바로 되살림).</summary>
        public bool Run(IntPtr source, Rect monitorPx, Rect from, Rect to, MinimizeEffect shape, DockEdge edge, bool restore, Action? onDone)
        {
            Stop();
            if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            if (_bounds != monitorPx)
            {
                _bounds = monitorPx;
                Native.User32.SetWindowPos(_hwnd, IntPtr.Zero, (int)monitorPx.X, (int)monitorPx.Y, (int)monitorPx.Width, (int)monitorPx.Height, Native.User32.SWP_NOZORDER | Native.User32.SWP_NOACTIVATE);
            }
            if (!IsVisible) Show();
            int strips = shape == MinimizeEffect.Genie ? GenieStrips : 1;
            for (int i = 0; i < strips; i++)
            {
                if (DwmRegisterThumbnail(_hwnd, source, out var t) != 0) { Stop(); Hide(); return false; }
                _thumbs.Add(t);
            }
            if (DwmQueryThumbnailSourceSize(_thumbs[0], out var src) != 0 || src.cx <= 0 || src.cy <= 0) { Stop(); Hide(); return false; }
            var local = new Rect(from.X - monitorPx.X, from.Y - monitorPx.Y, from.Width, from.Height);
            var target = new Rect(to.X - monitorPx.X, to.Y - monitorPx.Y, to.Width, to.Height);
            int duration = MinimizeMotion.DurationMs(shape, restore);
            _onDone = onDone;
            var clock = Stopwatch.StartNew();
            _frame = (_, _) =>
            {
                double t = Math.Min(1, clock.ElapsedMilliseconds / (double)duration);
                double p = MinimizeMotion.Path(t, restore);
                for (int i = 0; i < _thumbs.Count; i++)
                {
                    double v0 = (double)i / _thumbs.Count, v1 = (double)(i + 1) / _thumbs.Count;
                    var (dst, opacity) = shape == MinimizeEffect.Genie
                        ? (GenieStrip(local, target, v0, v1, p), MinimizeMotion.Opacity(shape, p))
                        : MinimizeMotion.Frame(shape, edge, local, target, p);
                    var props = new DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY | DWM_TNP_VISIBLE,
                        rcDestination = new RECT((int)dst.Left, (int)dst.Top, (int)Math.Ceiling(dst.Right), (int)Math.Ceiling(dst.Bottom)),
                        rcSource = new RECT(0, (int)(src.cy * v0), src.cx, (int)Math.Ceiling(src.cy * v1)),
                        opacity = (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)),
                        fVisible = true,
                    };
                    DwmUpdateThumbnailProperties(_thumbs[i], ref props);
                }
                if (t < 1) return;
                if (_frame is not null) { CompositionTarget.Rendering -= _frame; _frame = null; }
                if (Finish())
                {
                    // 되살린 창이 그려질 때까지 마지막 모습을 잠깐 더 들고 있다가 숨김 (빈 화면 깜빡임 방지)
                    _hideLater ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Normal, (_, _) => { _hideLater!.Stop(); Stop(); Hide(); }, Dispatcher);
                    _hideLater.Start();
                }
                else { Stop(); Hide(); }
            };
            CompositionTarget.Rendering += _frame;
            return true;
        }

        /// <summary>기다리던 되살리기를 부름 (있었으면 true).</summary>
        private bool Finish()
        {
            var done = _onDone;
            _onDone = null;
            if (done is null) return false;
            try { done(); } catch (Exception ex) { Log.Warn($"창 되살리기 실패: {ex.Message}"); }
            return true;
        }

        private void Stop()
        {
            _hideLater?.Stop();
            if (_frame is not null) { CompositionTarget.Rendering -= _frame; _frame = null; }
            Finish(); // 중간에 끊겨도 창은 꼭 되살림
            foreach (var t in _thumbs) DwmUnregisterThumbnail(t);
            _thumbs.Clear();
        }

        protected override void OnClosed(EventArgs e)
        {
            Stop();
            base.OnClosed(e);
        }
    }

    // ───────────────────────── 모양 계산 (시험·업데이트 카드 그림에서도 씀) ─────────────────────────

    /// <summary>
    /// 지니 (독이 아래): 창 높이의 v0~v1 띠가 길 위치 p(0 창 자리 → 1 아이콘)일 때 놓일 자리.
    /// 아래 띠가 조금 먼저 좁아지고(휘는 정도 <see cref="MinimizeMotion.GenieBend"/>), 창 전체가 아이콘 쪽으로 내려감.
    /// </summary>
    internal static Rect GenieStrip(Rect from, Rect to, double v0, double v1, double p) => GenieStrip(from, to, v0, v1, p, MinimizeMotion.GenieBend);

    internal static Rect GenieStrip(Rect from, Rect to, double v0, double v1, double p, double bend)
    {
        double down = Math.Clamp((p - 0.1) / 0.9, 0, 1); // 내려가기 (처음 10% 는 좁아지기만)
        double Y(double v) => from.Top + v * from.Height + (to.Top + v * to.Height - (from.Top + v * from.Height)) * down;
        (double L, double R) X(double v)
        {
            // 아래(v=1)일수록 먼저 좁아짐 — bend 1 = 아래가 먼저 확(강한 지니), 0 = 모든 띠가 같이(크기 줄이기와 같음)
            double k = Math.Clamp(p * (1 + 0.8 * bend) - (1 - v) * 0.8 * bend, 0, 1);
            k = k * k * (3 - 2 * k); // 부드럽게
            return (from.Left + (to.Left - from.Left) * k, from.Right + (to.Right - from.Right) * k);
        }
        double y0 = Y(v0), y1 = Y(v1);
        var (l0, r0) = X(v0);
        var (l1, r1) = X(v1);
        double l = Math.Min(l0, l1), r = Math.Max(r0, r1);
        return new Rect(l, y0, Math.Max(1, r - l), Math.Max(1, y1 - y0));
    }

    // ───────────────────────── Win32 ─────────────────────────

    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int WPF_RESTORETOMAXIMIZED = 0x0002;
    private const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public System.Drawing.Point ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
}

/// <summary>
/// 창 최소화 효과의 움직임 값과 모양 (#20 — 사용자 시안 후보 4가지, 2026-10-17). 값은 여기 한곳에서 바꿈.
/// p = 길 위치 (0 창 자리 → 1 끝). 되살리기는 같은 길을 거꾸로(p 1 → 0), 0.8배 시간. 둘 다 끝에서 천천히 멈춤(튀는 탄성 없음).
/// </summary>
internal static class MinimizeMotion
{
    /// <summary>들어가는 시간(ms): 살짝 내려앉기 200 · 아이콘으로 줄어들기 260 · 아래로 미끄러지기 220 · 부드러운 지니 300, 흐려짐만(Off) 120.</summary>
    public static int Minimize(MinimizeEffect shape) => shape switch
    {
        MinimizeEffect.Settle => 200,
        MinimizeEffect.Scale => 260,
        MinimizeEffect.Slide => 220,
        MinimizeEffect.Genie => 300,
        _ => 120,
    };

    /// <summary>되살리기 시간 = 들어가는 시간 × 이 값.</summary>
    public const double RestoreFactor = 0.8;

    /// <summary>지니 휘는 정도 (0 = 휘지 않음, 1 = 예전 강한 지니). 부드러운 지니 = 0.35.</summary>
    public const double GenieBend = 0.35;

    /// <summary>살짝 내려앉기: 끝 크기 0.82배, 독 쪽으로 창 높이의 18%, 독 아이콘 쪽으로 가로 거리의 35%.</summary>
    public const double SettleScale = 0.82, SettleDrop = 0.18, SettleTowardIcon = 0.35;

    /// <summary>아래로 미끄러지기: 독 쪽으로 창 높이의 70%.</summary>
    public const double SlideDistance = 0.70;

    public static int DurationMs(MinimizeEffect shape, bool restore) => (int)Math.Round(Minimize(shape) * (restore ? RestoreFactor : 1));

    /// <summary>끝에서 천천히 멈춤 (3제곱 ease-out).</summary>
    public static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>시간 t(0~1) → 길 위치 p. 되살리기는 끝(1)에서 창 자리(0)로, 창 자리에 닿을 때 천천히.</summary>
    public static double Path(double t, bool restore) => restore ? 1 - EaseOut(t) : EaseOut(t);

    /// <summary>길 위치 p 의 불투명도.</summary>
    public static double Opacity(MinimizeEffect shape, double p) => shape switch
    {
        MinimizeEffect.Scale => 1 - 0.85 * p,                                 // 아이콘에 닿을 때 옅게
        MinimizeEffect.Genie => p < 0.45 ? 1 : 1 - 0.9 * (p - 0.45) / 0.55,     // 뒤 절반에서 옅어짐
        _ => 1 - p,                                                             // 내려앉기·미끄러지기·흐려짐만: 끝에서 사라짐
    };

    /// <summary>썸네일 1장 모양의 자리·불투명도. from = 창, icon = 독 아이콘, edge = 독 자리.</summary>
    public static (Rect Rect, double Opacity) Frame(MinimizeEffect shape, DockEdge edge, Rect from, Rect icon, double p)
    {
        double op = Opacity(shape, p);
        switch (shape)
        {
            case MinimizeEffect.Scale:
            case MinimizeEffect.Genie:
                return (new Rect(from.X + (icon.X - from.X) * p, from.Y + (icon.Y - from.Y) * p,
                    Math.Max(1, from.Width + (icon.Width - from.Width) * p), Math.Max(1, from.Height + (icon.Height - from.Height) * p)), op);
            case MinimizeEffect.Settle:
            {
                // 독 쪽 가장자리를 붙잡고 줄어들며 독 쪽으로 조금, 아이콘 쪽으로 조금
                double s = 1 - (1 - SettleScale) * p;
                double w = from.Width * s, h = from.Height * s;
                double icx = icon.X + icon.Width / 2, icy = icon.Y + icon.Height / 2;
                double cx = from.X + from.Width / 2, cy = from.Y + from.Height / 2;
                double ax = cx + (icx - cx) * SettleTowardIcon * p, ay = cy + (icy - cy) * SettleTowardIcon * p;
                return edge switch
                {
                    DockEdge.Left => (new Rect(from.Left - from.Width * SettleDrop * p, ay - h / 2, w, h), op),
                    DockEdge.Right => (new Rect(from.Right + from.Width * SettleDrop * p - w, ay - h / 2, w, h), op),
                    DockEdge.Top => (new Rect(ax - w / 2, from.Top - from.Height * SettleDrop * p, w, h), op),
                    _ => (new Rect(ax - w / 2, from.Bottom + from.Height * SettleDrop * p - h, w, h), op),
                };
            }
            case MinimizeEffect.Slide:
                return edge switch
                {
                    DockEdge.Left => (new Rect(from.X - from.Width * SlideDistance * p, from.Y, from.Width, from.Height), op),
                    DockEdge.Right => (new Rect(from.X + from.Width * SlideDistance * p, from.Y, from.Width, from.Height), op),
                    DockEdge.Top => (new Rect(from.X, from.Y - from.Height * SlideDistance * p, from.Width, from.Height), op),
                    _ => (new Rect(from.X, from.Y + from.Height * SlideDistance * p, from.Width, from.Height), op),
                };
            default: // 흐려짐만
                return (from, op);
        }
    }
}
