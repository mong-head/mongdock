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
/// - 모양(설정 → 독): 차분하게(살짝 내려앉기) / 아이콘으로(줄어들며 끝에서 흐림) / 지니(느리게 단계적으로 휨) + 속도 빠르게·보통·느리게.
///   들어갈 때 천천히 시작해 천천히 멈춤, 튀는 탄성 없음. 되살리기는 같은 길 거꾸로, 0.8배 시간. 윈도우 「애니메이션 효과」가 꺼져 있거나 절전 모드면 짧은 흐려짐만(되살리기는 바로).
/// - 알아채기: EVENT_SYSTEM_MINIMIZESTART (SetWinEventHook, 몽독 밖에서 받음 — 다른 앱에 끼어들지 않음).
///   되살리기는 몽독이 창을 여는 때만(독 클릭 등 AppLauncher.ActivateWindow): 효과를 먼저 보이고 끝에서 창을 되살림. 다른 곳(Alt+Tab 등)에서 열면 효과 없음.
/// - 그리기: 모니터 크기 투명 덮개 창 하나(계속 다시 씀)에 그 창의 DWM 썸네일(윈도우가 그려 둔 창 모습 — 최소화 뒤에도 마지막 모습을 들고 있음)을
///   띄워 움직임. 그림 복사가 없어 메모리를 거의 안 씀. 지니 = 가로 띠 24장(띠마다 다른 폭·위치), 아이콘으로 = 5장(끝 흐림: 조금씩 어긋나게 겹침), 차분하게 = 1장.
/// - 윈도우 기본 최소화 효과와 겹치지 않게 켜 둔 동안만 윈도우 「최소화·최대화 애니메이션」을 끔 (WindowAnimationSetting — 되돌림 보장).
/// 독이 아래가 아니면(왼쪽·오른쪽·위) 지니 대신 아이콘으로, 차분하게는 독 쪽으로. 독 아이콘을 못 찾으면 독 가운데로.
/// </summary>
internal sealed class MinimizeEffectController : IDisposable
{
    private const int GenieStrips = 24;

    /// <summary>아이콘으로 끝 흐림: 썸네일 5장의 어긋남 방향 (가운데 + 네 방향).</summary>
    private static readonly (double X, double Y)[] BlurCopies = { (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1) };

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

    /// <summary>시험용(실제 창 프레임 사진): 값이 있으면 그 시간 비율(0~1)에 멈춰 있음, null 로 돌리면 이어서 끝남.</summary>
    internal static double? FreezeAtForTest { get; set; }

    /// <summary>시험용: 마지막 되살리기의 끝 장면 붙잡기 (#27) — 그림 찍는 데 든 ms, 그림이 보인 ms(찍은 뒤 → 치움)와 프레임 수, 기다린 기준 ms. 못 찍었으면 null.</summary>
    internal static (long CaptureMs, long ShownMs, int ShownFrames, int HoldMs)? LastStillForTest { get; private set; }

    /// <summary>마지막 되살리기의 끝 자리 차이 (진짜 창 DWM 테두리 − 효과 끝, 물리 px) — live-test --flashmatrix 가 줄에 붙임 (#27).</summary>
    internal static string? LastEndMismatchForTest { get; private set; }

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
        WarmOverlay();
        return turnedOff;
    }

    /// <summary>
    /// 덮개 창을 미리 만들어 한 번 그려 두고 숨김 (#27 독 클릭 되살리기 "빠방"): 처음 효과 때 창 만들기·첫 그리기(지니 약 38ms)가
    /// 첫 프레임을 늦추지 않게. 한가할 때(ApplicationIdle) 한 번만.
    /// </summary>
    private void WarmOverlay()
    {
        if (_overlay is not null || Application.Current?.Dispatcher is not { } d) return;
        d.BeginInvoke(() =>
        {
            if (_disposed || _overlay is not null || Effect == MinimizeEffect.Off) return;
            try { (_overlay = new OverlayWindow(_services)).Warm(); }
            catch (Exception ex) { Log.Warn($"최소화 효과 덮개 미리 만들기 실패: {ex.GetType().Name}"); }
        }, DispatcherPriority.ApplicationIdle);
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
        if (!restore) Remember(hwnd, from);
        var monitor = MonitorFor(from);
        var bounds = monitor.BoundsRect;
        var edge = _edge();
        Rect to = _iconRect(hwnd) ?? DockCenter(bounds, edge);
        var shape = Resolve(Effect, edge, Reduced());
        LastPlayedForTest = (from, to, shape, restore);
        var overlay = _overlay ??= new OverlayWindow(_services);
        int duration = MinimizeMotion.DurationMs(shape, restore, _services.Settings.Current.Dock.MinimizeSpeed);
        return overlay.Run(hwnd, new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), from, to, shape, edge, restore, duration, onDone);
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

    /// <summary>
    /// 최소화할 때 본 창의 보이는 자리 (#27): 최소화 중엔 DWM 테두리 사각형을 못 얻어 저장된 창 자리(보이지 않는 크기 조절 테두리 포함, 화면에 붙인 창이면
    /// 아예 다른 자리)로 어림하게 됨 → 효과가 진짜 창보다 크게 끝났다가 줄어 보임 (사용자 0.8.4 확인: 노션·Claude·크롬, 보통 창·최대화).
    /// 최소화 순간(아직 DWM 값이 있음)의 보이는 자리와 그때의 저장된 창 자리·최대화 여부를 기억해 두고, 되살릴 때 저장된 창 자리가 그대로면 그 자리로 끝냄.
    /// UI 스레드에서만 씀. 창이 많아지면 없어진 창부터 지움.
    /// </summary>
    private static readonly Dictionary<IntPtr, (Rect Visible, RECT Normal, bool Maximized)> Remembered = new();

    private const int RememberMax = 64;

    private static void Remember(IntPtr hwnd, Rect visible)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return;
        if (Remembered.Count >= RememberMax && !Remembered.ContainsKey(hwnd))
        {
            foreach (var gone in Remembered.Keys.Where(h => !IsWindow(h)).ToList()) Remembered.Remove(gone);
            if (Remembered.Count >= RememberMax) Remembered.Clear();
        }
        Remembered[hwnd] = (visible, wp.rcNormalPosition, IsZoomed(hwnd));
    }

    /// <summary>
    /// 최소화 직전 창 자리 (물리 픽셀): 떠 있으면 DWM 테두리 사각형. 이미 최소화됐으면 ① 최소화할 때 기억한 자리(저장된 창 자리·최대화가 그대로일 때)
    /// ② 없으면 되살릴 자리 어림 — 최대화였으면 모니터 작업 영역, 아니면 저장된 창 자리에서 보이지 않는 테두리(왼·오른·아래)를 뺌.
    /// </summary>
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
        bool toMax = (wp.flags & WPF_RESTORETOMAXIMIZED) != 0;
        if (Remembered.Remove(hwnd, out var seen) && seen.Maximized == toMax && seen.Normal.Equals(wp.rcNormalPosition) && seen.Visible.Width > 60 && seen.Visible.Height > 40)
        {
            rect = seen.Visible;
            return true;
        }
        var n = wp.rcNormalPosition;
        var guess = new Rect(n.Left, n.Top, Math.Max(1, n.Right - n.Left), Math.Max(1, n.Bottom - n.Top));
        var m = MonitorFor(guess);
        if (toMax)
        {
            var w = m.WorkRect;
            rect = new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
        }
        else
        {
            // 작업 공간 좌표 → 화면 좌표 (작업 영역 왼쪽 위만큼 밀림), 그리고 보이지 않는 테두리 빼기
            var w = m.WorkRect;
            var b = m.BoundsRect;
            double inv = InvisibleBorder(hwnd);
            rect = new Rect(guess.X + (w.Left - b.Left) + inv, guess.Y + (w.Top - b.Top), Math.Max(1, guess.Width - 2 * inv), Math.Max(1, guess.Height - inv));
        }
        return rect.Width > 60 && rect.Height > 40;
    }

    /// <summary>
    /// 크기 조절 창(WS_THICKFRAME)의 보이지 않는 테두리 폭 (물리 px, 왼·오른·아래 — 위는 0): SIZEFRAME + PADDEDBORDER − 보이는 1px 테두리.
    /// 이 PC 측정과 같음: 100% 7px · 125% 8px (Chrome_WidgetWin_1·탐색기·UWP 틀). 크기 조절이 안 되는 창은 0.
    /// </summary>
    internal static double InvisibleBorder(IntPtr hwnd)
    {
        if ((Native.User32.GetWindowLong(hwnd, Native.User32.GWL_STYLE) & WS_THICKFRAME) == 0) return 0;
        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        return InvisibleBorderAt(dpi);
    }

    internal static double InvisibleBorderAt(uint dpi) =>
        Math.Max(0, GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi) - GetSystemMetricsForDpi(SM_CXBORDER, dpi));

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
        private Rect _bounds;

        /// <summary>끝 장면 붙잡기 (#27): 넘겨주기 직전 덮개 모습을 찍은 그림 한 장 — 썸네일을 숨긴 뒤 진짜 창이 다시 그려질 때까지 이걸 보임.</summary>
        private readonly System.Windows.Controls.Image _still = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed, SnapsToDevicePixels = true };

        public OverlayWindow(AppServices services)
        {
            var canvas = new System.Windows.Controls.Canvas { IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(_still, BitmapScalingMode.NearestNeighbor); // 찍은 픽셀 1:1 — 배율 150% 에서도 흐려지지 않게
            canvas.Children.Add(_still);
            Content = canvas;
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

        /// <summary>
        /// 효과 시작. 썸네일을 못 만들면 false (되살리기면 부르는 쪽이 바로 되살림).
        /// 되살리기 끝 넘기기(#20 깜빡임): 시간 85% 에 진짜 창을 덮개 밑에서 먼저 되살리고(창이 거의 제자리라 가려짐), 끝난 뒤에도
        /// 진짜 창이 그려질 시간(넘긴 뒤 60ms 이상 + 화면 2프레임)을 기다렸다가 덮개를 치움 — 덮개 그림 → 진짜 창이 한 번에 바뀜.
        /// 썸네일 원본은 창의 보이지 않는 테두리(왼·오른·아래 약 8px, 최대화면 위도)를 빼고 잘라 써서 끝 모습이 진짜 창과 같은 자리·크기.
        /// </summary>
        /// <summary>미리 만들기: 핸들을 만들고 화면 밖 작은 크기로 한 번 보였다 숨겨 첫 그리기를 끝내 둠.</summary>
        public void Warm()
        {
            new WindowInteropHelper(this).EnsureHandle();
            Native.User32.SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 1, 1, Native.User32.SWP_NOZORDER | Native.User32.SWP_NOACTIVATE);
            _bounds = new Rect(-32000, -32000, 1, 1);
            Show();
            UpdateLayout();
            Hide();
        }

        public bool Run(IntPtr source, Rect monitorPx, Rect from, Rect to, MinimizeEffect shape, DockEdge edge, bool restore, int duration, Action? onDone)
        {
            Stop();
            if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            if (_bounds != monitorPx)
            {
                _bounds = monitorPx;
                Native.User32.SetWindowPos(_hwnd, IntPtr.Zero, (int)monitorPx.X, (int)monitorPx.Y, (int)monitorPx.Width, (int)monitorPx.Height, Native.User32.SWP_NOZORDER | Native.User32.SWP_NOACTIVATE);
            }
            if (!IsVisible) Show();
            int strips = shape == MinimizeEffect.Genie ? GenieStrips : shape == MinimizeEffect.Scale ? BlurCopies.Length : 1;
            for (int i = 0; i < strips; i++)
            {
                if (DwmRegisterThumbnail(_hwnd, source, out var t) != 0) { Stop(); Hide(); return false; }
                _thumbs.Add(t);
            }
            if (DwmQueryThumbnailSourceSize(_thumbs[0], out var src) != 0 || src.cx <= 0 || src.cy <= 0) { Stop(); Hide(); return false; }
            var crop = SourceCrop(source, src, from);
            var local = new Rect(from.X - monitorPx.X, from.Y - monitorPx.Y, from.Width, from.Height);
            var target = new Rect(to.X - monitorPx.X, to.Y - monitorPx.Y, to.Width, to.Height);
            _onDone = onDone;
            var clock = Stopwatch.StartNew();
            long handedOffAt = -1;
            int endFrames = 0;
            long fadeFrom = -1;
            // 끝 장면 붙잡기 (#27): Electron(크롬 계열) 창은 되살아난 직후 약 50ms 빈 배경만 내보내고, 실시간 썸네일이 그걸 그대로 비춰 끝에 번쩍임 (화면 측정: 효과 끔 0, 켬 5~7프레임).
            // 넘겨주기 직전 덮개 모습을 그림 한 장으로 찍어 깔고 → 2프레임 뒤(그림이 화면에 나온 뒤) 썸네일을 숨기고 진짜 창을 되살림 → 다시 그릴 시간을 기다렸다가 그림을 흐리게 걷어 냄.
            int stillFrames = 0; // 0 = 그림 없음, 1~ = 그림을 깐 뒤 지난 프레임
            Rect stillFrom = default, stillTo = default, drawn = Rect.Empty;
            bool cleared = false;
            double pStill = 0;
            long stillAt = -1, captureMs = 0;
            bool chromium = IsChromiumFamily(source);
            int holdMs = chromium ? ChromiumHoldMs : 60;
            void Draw(double t, double fade = 1)
            {
                double p = MinimizeMotion.Path(t, restore);
                bool genie = shape == MinimizeEffect.Genie;
                drawn = Rect.Empty;
                for (int i = 0; i < _thumbs.Count; i++)
                {
                    double v0 = genie ? (double)i / _thumbs.Count : 0, v1 = genie ? (double)(i + 1) / _thumbs.Count : 1;
                    var (dst, opacity) = genie
                        ? (GenieStrip(local, target, v0, v1, p), MinimizeMotion.Opacity(shape, p))
                        : MinimizeMotion.Frame(shape, edge, local, target, p);
                    if (_thumbs.Count > 1 && !genie)
                    {
                        // 흐림: 같은 모습을 조금씩 어긋나게 겹침. 흐림이 없을 땐 가운데 1장만 보이게, 있을 땐 겹친 합이 원래 불투명도가 되게
                        double blur = MinimizeMotion.Blur(shape, p);
                        var (ox, oy) = BlurCopies[i];
                        dst.Offset(ox * blur, oy * blur);
                        opacity = blur <= 0 ? (i == 0 ? opacity : 0) : 1 - Math.Pow(1 - Math.Clamp(opacity, 0, 0.999), 1.0 / _thumbs.Count);
                    }
                    var props = new DWM_THUMBNAIL_PROPERTIES
                    {
                        dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_OPACITY | DWM_TNP_VISIBLE,
                        rcDestination = new RECT((int)Math.Round(dst.Left), (int)Math.Round(dst.Top), (int)Math.Round(dst.Right), (int)Math.Round(dst.Bottom)),
                        rcSource = new RECT((int)crop.Left, (int)Math.Round(crop.Top + crop.Height * v0), (int)crop.Right, (int)Math.Round(crop.Top + crop.Height * v1)),
                        opacity = (byte)Math.Round(255 * Math.Clamp(opacity * fade, 0, 1)),
                        fVisible = true,
                    };
                    DwmUpdateThumbnailProperties(_thumbs[i], ref props);
                    if (props.opacity > 0) drawn.Union(dst);
                }
            }
            // 붙잡은 그림: 찍은 자리에서 창 자리까지 남은 길을 같은 속도로 (지니 띠는 이때 이미 거의 네모)
            void DrawStill(double t, double fade = 1)
            {
                double k = pStill <= 0 ? 1 : Math.Clamp(1 - MinimizeMotion.Path(t, restore) / pStill, 0, 1);
                var r = new Rect(stillFrom.X + (stillTo.X - stillFrom.X) * k, stillFrom.Y + (stillTo.Y - stillFrom.Y) * k,
                    stillFrom.Width + (stillTo.Width - stillFrom.Width) * k, stillFrom.Height + (stillTo.Height - stillFrom.Height) * k);
                var dpi = VisualTreeHelper.GetDpi(this);
                System.Windows.Controls.Canvas.SetLeft(_still, r.X / dpi.DpiScaleX);
                System.Windows.Controls.Canvas.SetTop(_still, r.Y / dpi.DpiScaleY);
                _still.Width = Math.Max(1, r.Width / dpi.DpiScaleX);
                _still.Height = Math.Max(1, r.Height / dpi.DpiScaleY);
                _still.Opacity = Math.Clamp(fade, 0, 1);
            }
            Draw(0); // 첫 모습을 바로 (최소화되어 창이 사라진 순간과 첫 프레임 사이에 빈 화면이 없게)
            long firstFrameAt = -1;
            _frame = (_, _) =>
            {
                // 덮개 첫 프레임이 늦으면 로그 (#27 되살릴 때 "빠방" — 실기기에서 원인 가르기)
                if (firstFrameAt < 0)
                {
                    firstFrameAt = clock.ElapsedMilliseconds;
                    if (firstFrameAt > 40) Log.Info($"창 {(restore ? "되살리기" : "최소화")} 효과: 덮개 첫 프레임 {firstFrameAt}ms ({shape})");
                }
                double t = FreezeAtForTest ?? Math.Min(1, clock.ElapsedMilliseconds / (double)duration);
                // 되살리기: 끝나기 전에 진짜 창을 덮개 밑에서 먼저 되살림 → 그 뒤로는 썸네일이 되살아난 창의 실제 모습 (원본 크기를 다시 읽음)
                if (restore && handedOffAt < 0 && t >= HandoffAt)
                {
                    // 크롬 계열만 (빈 화면 문제가 거기만 있음 — 다른 앱은 화면 찍기 위험 없이 이전 그대로, 리뷰)
                    if (stillFrames == 0 && chromium && Anim.Enabled && FreezeAtForTest is null && TryCaptureStill(drawn, out captureMs, out var area))
                    {
                        stillFrames = 1;
                        stillAt = clock.ElapsedMilliseconds;
                        // 찍은 픽셀 자리 그대로 놓고, 창 자리도 같은 만큼 넓혀 따라감 (늘어남·밀림 없게)
                        stillFrom = area;
                        stillTo = new Rect(local.X - (drawn.X - area.X), local.Y - (drawn.Y - area.Y),
                            local.Width + (area.Width - drawn.Width), local.Height + (area.Height - drawn.Height));
                        pStill = MinimizeMotion.Path(t, restore);
                        DrawStill(t);
                    }
                    // 그림이 화면에 나올 때까지 2프레임 그리고 33ms 이상 (겹 창 그리기가 UI 프레임보다 늦을 수 있음) — 그동안은 썸네일(아직 최소화 상태 모습)이 위에 그대로
                    else if (stillFrames > 0 && (stillFrames < 3 || clock.ElapsedMilliseconds - stillAt < 33)) stillFrames++;
                    else
                    {
                        if (stillFrames > 0) HideThumbs();
                        handedOffAt = clock.ElapsedMilliseconds;
                        Finish();
                    }
                }
                else if (stillFrames > 0) stillFrames++;
                if (stillFrames == 0 && handedOffAt >= 0 && !IsIconic(source) && DwmQueryThumbnailSourceSize(_thumbs[0], out var live) == 0 && live.cx > 0 && live.cy > 0)
                    crop = SourceCrop(source, live, from);
                if (fadeFrom >= 0)
                {
                    // 끝 크로스페이드 (#27): 진짜 창이 이미 제자리에 있고, 덮개만 CrossfadeMs 동안 흐리게 걷어 냄 — 한 번에 치우면 그림자·둥근 모서리·다시 그리기가 툭 바뀌어 보임
                    double f = 1 - (clock.ElapsedMilliseconds - fadeFrom) / (double)CrossfadeMs;
                    if (f > 0) { if (handedOffAt >= 0 && stillFrames > 0) DrawStill(1, f * f); else Draw(1, f * f); return; } // 끝으로 갈수록 빨리 옅어짐 — 프레임이 드문 원격 화면에서도 마지막에 남는 값이 작게 (리뷰)
                }
                else
                {
                    if (handedOffAt >= 0 && stillFrames > 0) DrawStill(t); else Draw(t);
                    if (t < 1) return;
                    if (stillFrames > 0 && handedOffAt < 0) return; // 짧은 효과: 그림을 깔고 기다리는 중에 시간이 끝나도 넘기기·붙잡기를 거침 (리뷰)
                    if (restore && handedOffAt >= 0)
                    {
                        // 진짜 창이 그려질 시간: 넘긴 뒤 hold 이상(그림을 붙잡았으면 크롬 계열 150 · 그 밖 60), 그리고 끝 모습으로 2프레임 더 — 그다음 크로스페이드.
                        // 넘긴 뒤 최대: 그림을 붙잡았으면 MaxStillMs, 아니면 260ms — 넘으면 그냥 걷어 냄
                        endFrames++;
                        long since = clock.ElapsedMilliseconds - handedOffAt;
                        int hold = stillFrames > 0 ? holdMs : 60;
                        if ((since < hold || endFrames < 3) && since < (stillFrames > 0 ? MaxStillMs : 260)) return;
                        LogEndMismatch(source, from, stillFrames > 0 ? stillTo : drawn, crop, src, chromium, stillFrames > 0);
                        if (Anim.Enabled) { fadeFrom = clock.ElapsedMilliseconds; return; }
                    }
                }
                if (stillAt >= 0 && !cleared)
                {
                    // 그림을 먼저 지우고 한 프레임 그린 뒤 숨김 — 숨긴 겹 창에 그림이 남아 다음에 보일 때 한 프레임 비치지 않게 (리뷰)
                    LastStillForTest = (captureMs, clock.ElapsedMilliseconds - stillAt, stillFrames, holdMs);
                    ClearStill();
                    cleared = true;
                    return;
                }
                if (_frame is not null) { CompositionTarget.Rendering -= _frame; _frame = null; }
                Stop();
                Hide();
            };
            CompositionTarget.Rendering += _frame;
            return true;
        }

        /// <summary>되살리기에서 진짜 창을 되살리는 시간 비율 (그때 썸네일은 창 자리와 1~3px 차이).</summary>
        private const double HandoffAt = 0.85;

        /// <summary>되살리기 끝에 덮개를 흐리게 걷어 내는 시간 (ms).</summary>
        private const int CrossfadeMs = 120;

        /// <summary>크롬 계열 창: 되살린 뒤 붙잡은 그림을 두는 시간 (ms) — 측정한 빈 화면 약 50~65ms + 여유. 150 이면 끝 멈춤이 약 0.35초라 100 으로(약 0.3초, 10-20 PM·사용자 확인용).</summary>
        private const int ChromiumHoldMs = 100;

        /// <summary>붙잡은 그림을 두는 최대 시간 (넘긴 뒤 ms) — 넘으면 그냥 걷어 냄.</summary>
        private const int MaxStillMs = 200;

        /// <summary>
        /// #27 「끝에 창이 컸다가 크기가 조정되는 느낌」 원인 가르기 (보통 창·최대화, 크롬 계열 공통): 진짜 창이 되살아나 덮개를 걷기 직전에
        /// 되살아난 진짜 창의 DWM 테두리 사각형과 ① 덮개가 실제로 끝난 사각형(붙잡은 그림이면 그림이 놓인 자리, 아니면 썸네일) ② 되살릴 자리 어림(from)을 비교해
        /// 어느 쪽이든 1px 넘게 다르면 한 줄. 썸네일 원본 크기·잘라 쓴 영역, 그림의 DIP 자리·크기(배율 반올림), 최대화·배율도 같이 (물리 px).
        /// </summary>
        private void LogEndMismatch(IntPtr source, Rect from, Rect shownLocal, Rect crop, SIZE src, bool chromium, bool still)
        {
            try
            {
                if (DwmGetWindowAttribute(source, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fb, Marshal.SizeOf<RECT>()) != 0 || !GetWindowRect(source, out var wr)) return;
                var shown = new Rect(shownLocal.X + _bounds.X, shownLocal.Y + _bounds.Y, shownLocal.Width, shownLocal.Height);
                static string Diff(RECT f, Rect r, out double max)
                {
                    double l = f.Left - r.Left, t = f.Top - r.Top, rr = f.Right - r.Right, b = f.Bottom - r.Bottom;
                    max = Math.Max(Math.Max(Math.Abs(l), Math.Abs(t)), Math.Max(Math.Abs(rr), Math.Abs(b)));
                    return $"왼 {l:0.#} 위 {t:0.#} 오른 {rr:0.#} 아래 {b:0.#}";
                }
                string vsShown = Diff(fb, shown, out double m1), vsFrom = Diff(fb, from, out double m2);
                LastEndMismatchForTest = $"덮개 끝 대비 {vsShown} · 어림 대비 {vsFrom}";
                if (Math.Max(m1, m2) <= 1) return;
                var dpi = VisualTreeHelper.GetDpi(this);
                string stillDip = still
                    ? $" · 그림 DIP ({System.Windows.Controls.Canvas.GetLeft(_still):0.##},{System.Windows.Controls.Canvas.GetTop(_still):0.##}) {_still.Width:0.##}x{_still.Height:0.##}"
                      + $" → px {_still.Width * dpi.DpiScaleX:0.##}x{_still.Height * dpi.DpiScaleY:0.##}, 원본 {(_still.Source as System.Windows.Media.Imaging.BitmapSource)?.PixelWidth}x{(_still.Source as System.Windows.Media.Imaging.BitmapSource)?.PixelHeight}"
                    : "";
                var sb = new System.Text.StringBuilder(64);
                GetClassName(source, sb, sb.Capacity);
                Log.Info($"창 되살리기 효과: 끝 자리 차이 (진짜 창 − 덮개 끝) {vsShown} · (진짜 창 − 어림) {vsFrom} px"
                    + $" | 덮개 끝 ({shown.Left:0.#},{shown.Top:0.#})-({shown.Right:0.#},{shown.Bottom:0.#}) · 어림 ({from.Left:0},{from.Top:0})-({from.Right:0},{from.Bottom:0})"
                    + $" · DWM 테두리 ({fb.Left},{fb.Top})-({fb.Right},{fb.Bottom}) · 창 사각형 ({wr.Left},{wr.Top})-({wr.Right},{wr.Bottom})"
                    + $" · 썸네일 원본 {src.cx}x{src.cy} 자름 ({crop.Left:0},{crop.Top:0})-({crop.Right:0},{crop.Bottom:0}){stillDip}"
                    + $" · 최대화 {IsZoomed(source)} · 창 배율 {GetDpiForWindow(source) * 100 / 96}% · 덮개 배율 {dpi.DpiScaleX * 100:0}% · {sb} · 크롬 계열 {chromium} · 그림 붙잡음 {still}");
            }
            catch { }
        }

        private static bool IsChromiumFamily(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(64);
            return GetClassName(hwnd, sb, sb.Capacity) > 0 && sb.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal);
        }

        /// <summary>
        /// 덮개가 지금 화면에 보이는 자리(<paramref name="localPx"/>, 덮개 안 물리 픽셀)를 화면에서 찍어 _still 에 깜 (아직 썸네일 밑이라 안 보임).
        /// 화면 DC 복사라 덮개(겹 창)·썸네일이 합쳐진 모습 그대로. GDI 비트맵은 바로 지우고, 그림은 덮개를 치울 때 놓음.
        /// </summary>
        private bool TryCaptureStill(Rect localPx, out long ms, out Rect area)
        {
            ms = 0;
            area = Rect.Empty;
            if (localPx.IsEmpty) return false;
            double l = Math.Floor(localPx.Left), tp = Math.Floor(localPx.Top);
            area = Rect.Intersect(new Rect(l, tp, Math.Ceiling(localPx.Right) - l, Math.Ceiling(localPx.Bottom) - tp),
                new Rect(0, 0, _bounds.Width, _bounds.Height));
            if (area.IsEmpty || area.Width < 2 || area.Height < 2) return false;
            var sw = Stopwatch.StartNew();
            int x = (int)(area.X + _bounds.X), y = (int)(area.Y + _bounds.Y), w = (int)area.Width, h = (int)area.Height;
            IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                if (screen == IntPtr.Zero) return false;
                mem = CreateCompatibleDC(screen);
                bmp = CreateCompatibleBitmap(screen, w, h);
                if (mem == IntPtr.Zero || bmp == IntPtr.Zero) return false;
                old = SelectObject(mem, bmp);
                if (!BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 /* SRCCOPY */)) return false;
                SelectObject(mem, old);
                old = IntPtr.Zero;
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _still.Source = src;
                _still.Visibility = Visibility.Visible;
                ms = sw.ElapsedMilliseconds;
                if (ms > 30) Log.Info($"창 되살리기 효과: 끝 장면 찍기 {ms}ms ({w}x{h})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"창 되살리기 효과: 끝 장면 찍기 실패 {ex.GetType().Name} {ex.Message}");
                return false;
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (mem != IntPtr.Zero) DeleteDC(mem);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }

        private void HideThumbs()
        {
            foreach (var t in _thumbs)
            {
                var props = new DWM_THUMBNAIL_PROPERTIES { dwFlags = DWM_TNP_VISIBLE, fVisible = false };
                DwmUpdateThumbnailProperties(t, ref props);
            }
        }

        /// <summary>붙잡은 그림을 놓음 (참조를 끊어 메모리가 바로 풀리게).</summary>
        private void ClearStill()
        {
            _still.Source = null;
            _still.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 썸네일 원본에서 창의 보이는 부분 (보이지 않는 크기 조절 테두리 빼기). 창이 떠 있으면 GetWindowRect 와 DWM 테두리 사각형으로 정확히,
        /// 최소화 중이면 원본 크기와 보이는 크기 차이로 어림: 좌우 같은 폭, 위는 최대화(위아래 차이가 좌우 합만큼)일 때만.
        /// </summary>
        private static Rect SourceCrop(IntPtr source, SIZE src, Rect visible)
        {
            if (!IsIconic(source) && GetWindowRect(source, out var wr)
                && DwmGetWindowAttribute(source, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fb, Marshal.SizeOf<RECT>()) == 0)
            {
                double l = Math.Max(0, fb.Left - wr.Left), t = Math.Max(0, fb.Top - wr.Top);
                double r = Math.Max(0, wr.Right - fb.Right), b = Math.Max(0, wr.Bottom - fb.Bottom);
                if (l + r < src.cx && t + b < src.cy) return new Rect(l, t, src.cx - l - r, src.cy - t - b);
            }
            double mx = Math.Max(0, (src.cx - visible.Width) / 2);
            double extraY = Math.Max(0, src.cy - visible.Height);
            double top = mx > 0 && extraY >= 2 * mx - 1 ? mx : 0;
            double bottom = Math.Max(0, extraY - top);
            if (2 * mx >= src.cx || top + bottom >= src.cy) return new Rect(0, 0, src.cx, src.cy);
            return new Rect(mx, top, src.cx - 2 * mx, src.cy - top - bottom);
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
            if (_frame is not null) { CompositionTarget.Rendering -= _frame; _frame = null; }
            Finish(); // 중간에 끊겨도 창은 꼭 되살림
            foreach (var t in _thumbs) DwmUnregisterThumbnail(t);
            _thumbs.Clear();
            ClearStill();
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
    /// 단계: 창 아래부터 깔때기 모양으로 천천히 휘고, 그 깔때기를 따라 아이콘으로 내려감(두 단계가 겹쳐 이어짐) — 띠 폭은 띠가 있는 높이로 정해져 각도가 급하게 바뀌지 않음.
    /// </summary>
    internal static Rect GenieStrip(Rect from, Rect to, double v0, double v1, double p) => GenieStrip(from, to, v0, v1, p, MinimizeMotion.GenieBend);

    internal static Rect GenieStrip(Rect from, Rect to, double v0, double v1, double p, double bend)
    {
        static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        // 깔때기: 창 위(from.Top)에서 아이콘 위(to.Top)까지 폭이 창 폭 → 아이콘 폭으로 부드럽게 좁아지는 곡선 (모양은 고정)
        double span = Math.Max(1, to.Top - from.Top);
        // 휨(bend 만큼, 길 85% 동안 천천히)과 내려가기(처음부터 끝까지)가 겹쳐 이어짐: 처음엔 아래만 휘고, 그 깔때기를 따라 아이콘으로
        double curve = bend * Smooth(p / 0.85) + (1 - bend) * Smooth(p);
        double down = Smooth(p);
        double Y(double v) => from.Top + v * from.Height + (to.Top + v * to.Height - (from.Top + v * from.Height)) * down;
        (double L, double R) X(double y)
        {
            double u = Math.Clamp((y - from.Top) / span, 0, 1);
            double f = Smooth(u) * curve; // 양 끝이 평평한 S 곡선 — 아이콘 폭에 닿는 곳에 꺾인 모서리 없음
            return (from.Left + (to.Left - from.Left) * f, from.Right + (to.Right - from.Right) * f);
        }
        double y0 = Y(v0), y1 = Y(v1);
        var (l0, r0) = X(y0);
        var (l1, r1) = X(y1);
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
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    private const int SM_CXBORDER = 5, SM_CXSIZEFRAME = 32, SM_CXPADDEDBORDER = 92;
    private const long WS_THICKFRAME = 0x00040000;
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}

/// <summary>
/// 창 최소화 효과의 움직임 값과 모양 (#20 — 사용자가 고른 3가지, 2026-10-17). 값은 여기 한곳에서 바꿈.
/// p = 길 위치 (0 창 자리 → 1 끝). 들어갈 때는 천천히 시작해 천천히 멈춤(ease-in-out — 처음 몇 프레임에 다 움직여 "그냥 사라지는" 것처럼 보이지 않게),
/// 되살리기는 같은 길을 거꾸로(p 1 → 0) 0.8배 시간, 창 자리에 닿을 때 천천히(ease-out). 튀는 탄성 없음. 흐려짐은 길 끝 30% 안에서만.
/// </summary>
internal static class MinimizeMotion
{
    /// <summary>들어가는 시간(ms, 보통 속도): 차분하게 220 · 아이콘으로 300 · 지니 480, 흐려짐만(Off) 120.</summary>
    public static int Minimize(MinimizeEffect shape) => shape switch
    {
        MinimizeEffect.Settle => 220,
        MinimizeEffect.Scale => 300,
        MinimizeEffect.Genie => 480,
        _ => 120,
    };

    /// <summary>속도 설정 배율: 빠르게 0.7 · 보통 1 · 느리게 1.4 (흐려짐만은 그대로).</summary>
    public static double SpeedFactor(MinimizeSpeed speed) => speed switch
    {
        MinimizeSpeed.Fast => 0.7,
        MinimizeSpeed.Slow => 1.4,
        _ => 1.0,
    };

    /// <summary>되살리기 시간 = 들어가는 시간 × 이 값.</summary>
    public const double RestoreFactor = 0.8;

    /// <summary>지니 1단계에서 휘는 정도 (0 = 휘지 않고 내려가며 좁아짐, 1 = 제자리에서 깔때기 모양까지 다 휨).</summary>
    public const double GenieBend = 0.6;

    /// <summary>차분하게: 끝 크기 0.82배, 독 쪽으로 창 높이의 18%, 독 아이콘 쪽으로 가로 거리의 35%.</summary>
    public const double SettleScale = 0.82, SettleDrop = 0.18, SettleTowardIcon = 0.35;

    /// <summary>흐려지기 시작하는 길 위치 (끝 30%).</summary>
    public const double FadeFrom = 0.7;

    /// <summary>아이콘으로: 끝에서 독에 녹아드는 흐림 반지름(물리 픽셀, 썸네일을 조금씩 어긋나게 겹쳐 그림 — 그림 복사 없음).</summary>
    public const double BlurPx = 4;

    public static int DurationMs(MinimizeEffect shape, bool restore, MinimizeSpeed speed = MinimizeSpeed.Normal) =>
        (int)Math.Round(Minimize(shape) * (restore ? RestoreFactor : 1) * (shape == MinimizeEffect.Off ? 1 : SpeedFactor(speed)));

    /// <summary>끝에서 천천히 멈춤 (3제곱 ease-out) — 되살리기.</summary>
    public static double EaseOut(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

    /// <summary>천천히 시작해 천천히 멈춤 — 들어갈 때. cubic-bezier(.45, 0, .3, 1).</summary>
    public static double EaseInOut(double t) => CubicBezier(0.45, 0, 0.3, 1, t);

    /// <summary>시간 t(0~1) → 길 위치 p. 되살리기는 끝(1)에서 창 자리(0)로.</summary>
    public static double Path(double t, bool restore) => restore ? 1 - EaseOut(t) : EaseInOut(t);

    /// <summary>CSS cubic-bezier (x1,y1,x2,y2) 의 t 시점 값 (x 를 뉴턴법+이분법으로 풀어 y). y 가 0~1 밖으로 안 나가는 값만 씀(탄성 없음).</summary>
    internal static double CubicBezier(double x1, double y1, double x2, double y2, double t)
    {
        t = Math.Clamp(t, 0, 1);
        static double B(double a, double b, double s) => 3 * a * s * (1 - s) * (1 - s) + 3 * b * s * s * (1 - s) + s * s * s;
        static double D(double a, double b, double s) => 3 * a * (1 - s) * (1 - s) + 6 * (b - a) * s * (1 - s) + 3 * (1 - b) * s * s;
        double u = t;
        for (int i = 0; i < 8; i++)
        {
            double dx = D(x1, x2, u);
            if (Math.Abs(dx) < 1e-6) break;
            u = Math.Clamp(u - (B(x1, x2, u) - t) / dx, 0, 1);
        }
        if (Math.Abs(B(x1, x2, u) - t) > 1e-5)
        {
            double lo = 0, hi = 1;
            for (int i = 0; i < 30; i++) { u = (lo + hi) / 2; if (B(x1, x2, u) < t) lo = u; else hi = u; }
        }
        return B(y1, y2, u);
    }

    /// <summary>길 위치 p 의 불투명도: 끝 30% 안에서만 옅어져 끝에서 0. 흐려짐만(Off)은 처음부터 끝까지 옅어짐.</summary>
    public static double Opacity(MinimizeEffect shape, double p)
    {
        if (shape == MinimizeEffect.Off) return 1 - p;
        double f = p <= FadeFrom ? 0 : (p - FadeFrom) / (1 - FadeFrom);
        return 1 - f; // 끝에서 완전히 사라짐 — 최소화 끝에 덮개를 치울 때 옅은 그림이 툭 없어지지 않게 (#27)
    }

    /// <summary>아이콘으로: 길 위치 p 의 흐림 반지름 (끝 30% 안에서 0 → BlurPx).</summary>
    public static double Blur(MinimizeEffect shape, double p) =>
        shape == MinimizeEffect.Scale && p > FadeFrom ? BlurPx * (p - FadeFrom) / (1 - FadeFrom) : 0;

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
            default: // 흐려짐만
                return (from, op);
        }
    }
}
