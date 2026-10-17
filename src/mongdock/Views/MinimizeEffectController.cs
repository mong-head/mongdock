using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 창 최소화 효과 (#20, 0.8): 다른 앱 창이 최소화되면 그 창이 독 아이콘으로 빨려 들어가는 모습.
/// - 알아채기: EVENT_SYSTEM_MINIMIZESTART (SetWinEventHook, 몽독 밖에서 받음 — 다른 앱에 끼어들지 않음).
/// - 그리기: 모니터 크기 투명 덮개 창 하나(계속 다시 씀)에 그 창의 DWM 썸네일(윈도우가 그려 둔 창 모습 — 최소화 뒤에도 마지막 모습을 들고 있음)을
///   띄워 움직임. 그림 복사가 없어 메모리를 거의 안 씀. 크기 줄이기 = 썸네일 1장, 지니 = 가로 띠 24장(띠마다 다른 폭·위치).
/// - 윈도우 기본 최소화 효과와 겹치지 않게 켜 둔 동안만 윈도우 「최소화·최대화 애니메이션」을 끔 (WindowAnimationSetting — 되돌림 보장).
/// 독이 아래가 아니면(왼쪽·오른쪽·위) 지니 대신 크기 줄이기. 독 아이콘을 못 찾으면 독 가운데로.
/// </summary>
internal sealed class MinimizeEffectController : IDisposable
{
    private const int DurationMs = 320;
    private const int GenieStrips = 24;

    private readonly AppServices _services;
    private readonly Func<IntPtr, Rect?> _iconRect;
    private readonly Func<DockEdge> _edge;
    private WinEventProc? _proc;
    private IntPtr _hook;
    private OverlayWindow? _overlay;
    private bool _disposed;

    public static MinimizeEffectController? Instance { get; private set; }

    /// <summary>시험용: 마지막으로 시작한 효과 (원래 자리, 도착 자리, 효과).</summary>
    internal static (Rect From, Rect To, MinimizeEffect Effect)? LastPlayedForTest { get; private set; }

    public MinimizeEffectController(AppServices services, Func<IntPtr, Rect?> iconRect, Func<DockEdge> edge)
    {
        _services = services;
        _iconRect = iconRect;
        _edge = edge;
        Instance = this;
        services.Settings.SettingsChanged += (_, _) => Sync();
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

    /// <summary>창 하나를 독 아이콘으로 (UI 스레드).</summary>
    internal void Play(IntPtr hwnd)
    {
        if (!TryWindowRect(hwnd, out var from)) return;
        var monitor = MonitorFor(from);
        var bounds = monitor.BoundsRect;
        var edge = _edge();
        Rect to = _iconRect(hwnd) ?? DockCenter(bounds, edge);
        var effect = Effect == MinimizeEffect.Genie && edge == DockEdge.Bottom ? MinimizeEffect.Genie : MinimizeEffect.Scale;
        LastPlayedForTest = (from, to, effect);
        var overlay = _overlay ??= new OverlayWindow(_services);
        overlay.Run(hwnd, new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), from, to, effect);
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

        public void Run(IntPtr source, Rect monitorPx, Rect from, Rect to, MinimizeEffect effect)
        {
            Stop();
            if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            if (_bounds != monitorPx)
            {
                _bounds = monitorPx;
                Native.User32.SetWindowPos(_hwnd, IntPtr.Zero, (int)monitorPx.X, (int)monitorPx.Y, (int)monitorPx.Width, (int)monitorPx.Height, Native.User32.SWP_NOZORDER | Native.User32.SWP_NOACTIVATE);
            }
            if (!IsVisible) Show();
            int strips = effect == MinimizeEffect.Genie ? GenieStrips : 1;
            for (int i = 0; i < strips; i++)
            {
                if (DwmRegisterThumbnail(_hwnd, source, out var t) != 0) { Stop(); Hide(); return; }
                _thumbs.Add(t);
            }
            if (DwmQueryThumbnailSourceSize(_thumbs[0], out var src) != 0 || src.cx <= 0 || src.cy <= 0) { Stop(); Hide(); return; }
            var local = new Rect(from.X - monitorPx.X, from.Y - monitorPx.Y, from.Width, from.Height);
            var target = new Rect(to.X - monitorPx.X, to.Y - monitorPx.Y, to.Width, to.Height);
            var clock = Stopwatch.StartNew();
            _frame = (_, _) =>
            {
                double t = Math.Min(1, clock.ElapsedMilliseconds / (double)DurationMs);
                for (int i = 0; i < _thumbs.Count; i++)
                {
                    double v0 = (double)i / _thumbs.Count, v1 = (double)(i + 1) / _thumbs.Count;
                    var dst = effect == MinimizeEffect.Genie ? GenieStrip(local, target, v0, v1, t) : ScaleRect(local, target, t);
                    var props = new DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY | DWM_TNP_VISIBLE,
                        rcDestination = new RECT((int)dst.Left, (int)dst.Top, (int)Math.Ceiling(dst.Right), (int)Math.Ceiling(dst.Bottom)),
                        rcSource = new RECT(0, (int)(src.cy * v0), src.cx, (int)Math.Ceiling(src.cy * v1)),
                        opacity = (byte)Math.Round(255 * (t < 0.8 ? 1 : 1 - (t - 0.8) / 0.2)),
                        fVisible = true,
                    };
                    DwmUpdateThumbnailProperties(_thumbs[i], ref props);
                }
                if (t >= 1) { Stop(); Hide(); }
            };
            CompositionTarget.Rendering += _frame;
        }

        private void Stop()
        {
            if (_frame is not null) { CompositionTarget.Rendering -= _frame; _frame = null; }
            foreach (var t in _thumbs) DwmUnregisterThumbnail(t);
            _thumbs.Clear();
        }

        protected override void OnClosed(EventArgs e)
        {
            Stop();
            base.OnClosed(e);
        }
    }

    // ───────────────────────── 모양 계산 (시험에서도 씀) ─────────────────────────

    private static double EaseIn(double t) => t * t;

    /// <summary>크기 줄이기: 창 사각형이 아이콘 사각형으로 (빨라지며).</summary>
    internal static Rect ScaleRect(Rect from, Rect to, double t)
    {
        double e = EaseIn(t);
        return new Rect(from.X + (to.X - from.X) * e, from.Y + (to.Y - from.Y) * e,
            Math.Max(1, from.Width + (to.Width - from.Width) * e), Math.Max(1, from.Height + (to.Height - from.Height) * e));
    }

    /// <summary>
    /// 지니 (독이 아래): 창 높이의 v0~v1 띠가 t 일 때 놓일 자리. 아래 띠부터 아이콘 폭으로 좁아지고(앞 절반), 창 전체가 아이콘 쪽으로 내려감(뒤로 갈수록 빠르게).
    /// </summary>
    internal static Rect GenieStrip(Rect from, Rect to, double v0, double v1, double t)
    {
        double down = EaseIn(Math.Clamp((t - 0.15) / 0.85, 0, 1)); // 내려가기 (처음 15% 는 좁아지기만)
        double Y(double v) => from.Top + v * from.Height + (to.Top + v * to.Height - (from.Top + v * from.Height)) * down;
        (double L, double R) X(double v)
        {
            // 아래(v=1)일수록 먼저 좁아짐
            double k = Math.Clamp(t * 1.8 - (1 - v) * 0.8, 0, 1);
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
