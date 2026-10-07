using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyDock.Models;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 창 스타일·AppBar·블러·둥근 모양·화면/배경 색 샘플링·디스플레이/전체화면/배경 변경 알림.
/// 주 모니터 기준. 앱은 PerMonitorV2 이므로 물리 픽셀 ↔ DIP 변환은 "주 모니터의 DPI" 로 한다
/// (독·상단바·예약 띠가 모두 주 모니터에 있음). 생성자는 UI 스레드에서 호출해야 한다.
/// 종료/크래시 시 등록된 모든 AppBar 를 ABM_REMOVE 하는 안전장치 포함.
/// </summary>
public sealed class DesktopWindowService : IDesktopWindowService, IDisposable
{
    // ───────────────────────── AppBar 슬롯 ─────────────────────────

    /// <summary>등록된 AppBar 하나. 상단바(보이는 창을 직접 배치) 또는 예약 띠(숨은 도우미 창).</summary>
    private sealed class Slot
    {
        public required IntPtr Hwnd { get; init; }
        public required HwndSource Source { get; init; }
        public HwndSourceHook? Hook;
        public Window? Window;          // 상단바: 배치할 창. 예약: null
        public bool OwnsSource;         // 예약 도우미 창이면 Dispose 때 파괴
        public uint Edge;
        public double ThicknessDip;
        public bool IsTopBar;
        public bool Registered;
        public bool Positioning;
        public bool Queued;
        public RECT LastRect;
        public EdgeReservation? Reservation;
    }

    private sealed class EdgeReservation : IEdgeReservation
    {
        private readonly DesktopWindowService _owner;
        internal Slot? Slot;
        public EdgeReservation(DesktopWindowService owner) => _owner = owner;
        public Rect Bounds { get; internal set; }
        public event EventHandler? BoundsChanged;
        internal void RaiseBoundsChanged() => BoundsChanged?.Invoke(this, EventArgs.Empty);
        public void Dispose()
        {
            var s = Slot;
            Slot = null;
            if (s is not null) _owner.RemoveSlot(s);
        }
    }

    private static readonly object RegistryGate = new();
    /// <summary>프로세스 종료 안전장치용 — 등록된 모든 AppBar hwnd.</summary>
    private static readonly HashSet<IntPtr> RegisteredHwnds = new();
    private static bool _exitHooksInstalled;

    private readonly Dispatcher _dispatcher;
    private readonly List<Slot> _slots = new(); // 상단바가 항상 앞
    private readonly uint _callbackMsg;
    private readonly uint _taskbarCreatedMsg;
    private readonly HwndSource _broadcastWindow;
    private readonly DispatcherTimer _fullscreenTimer;
    private readonly Dictionary<Window, double> _rounded = new();
    private readonly WallpaperSampler _wallpaper = new();
    private readonly Timer _wallpaperPoll;
    private string? _wallpaperSignature;
    private bool _fullscreen;
    private bool _displayQueued;
    private bool _wallpaperQueued;
    private bool _disposed;

    public event EventHandler<bool>? FullscreenAppChanged;
    public event EventHandler? DisplayChanged;
    public event EventHandler? WallpaperChanged;

    public DesktopWindowService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callbackMsg = User32.RegisterWindowMessage("MyDock.AppBarCallback");
        _taskbarCreatedMsg = User32.RegisterWindowMessage("TaskbarCreated");
        InstallExitHooks();

        // 브로드캐스트(WM_DISPLAYCHANGE / WM_SETTINGCHANGE / TaskbarCreated)는 메시지 전용 창으로 오지 않으므로 숨은 최상위 창.
        _broadcastWindow = CreateHiddenWindow("MyDock.DesktopEvents");
        _broadcastWindow.AddHook(BroadcastWndProc);

        _fullscreenTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => EvaluateFullscreen(), _dispatcher);
        _fullscreenTimer.Start();

        // 가상 데스크톱 전환(데스크톱별 배경)·슬라이드쇼 감지: 백그라운드에서 2초마다 서명 비교
        _wallpaperPoll = new Timer(_ => PollWallpaper(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private static HwndSource CreateHiddenWindow(string name)
    {
        var p = new HwndSourceParameters(name)
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = User32.WS_POPUP, // WS_VISIBLE 없음 → 숨김
            ExtendedWindowStyle = (int)(User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE),
        };
        return new HwndSource(p);
    }

    // ───────────────────────── 좌표 ─────────────────────────

    private static double PrimaryScale => DesktopApi.GetMonitorScale(DesktopApi.PrimaryMonitor);

    private static RECT PrimaryBoundsPx
    {
        get
        {
            if (DesktopApi.TryGetMonitorRects(DesktopApi.PrimaryMonitor, out RECT b, out _)) return b;
            return new RECT(0, 0, User32.GetSystemMetrics(User32.SM_CXSCREEN), User32.GetSystemMetrics(User32.SM_CYSCREEN));
        }
    }

    private static Rect ToDip(RECT r, double scale) =>
        new(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);

    private static RECT ToPx(Rect r, double scale) =>
        new((int)Math.Floor(r.Left * scale), (int)Math.Floor(r.Top * scale),
            (int)Math.Ceiling(r.Right * scale), (int)Math.Ceiling(r.Bottom * scale));

    public Rect GetPrimaryScreenBounds() => ToDip(PrimaryBoundsPx, PrimaryScale);

    /// <summary>주 모니터 작업 영역 (DIP) — 작업표시줄과 등록된 AppBar 를 뺀 영역.</summary>
    public Rect GetPrimaryWorkArea()
    {
        if (DesktopApi.TryGetMonitorRects(DesktopApi.PrimaryMonitor, out _, out RECT work))
            return ToDip(work, PrimaryScale);
        return SystemParameters.WorkArea;
    }

    /// <summary>커서 위치 (DIP). 커서가 있는 모니터의 DPI 로 변환 — PerMonitorV2 WPF 창 좌표와 같은 기준.</summary>
    public Point GetCursorPosition()
    {
        if (!DesktopApi.GetCursorPos(out POINT p)) return new Point();
        double s = DesktopApi.GetMonitorScale(DesktopApi.MonitorFromPoint(p, DesktopApi.MONITOR_DEFAULTTONEAREST));
        return new Point(p.X / s, p.Y / s);
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

    // ───────────────────────── 블러 / 둥근 모양 ─────────────────────────

    /// <summary>
    /// 아크릴 블러(SetWindowCompositionAttribute, ACCENT_ENABLE_ACRYLICBLURBEHIND=4) + Win11 DWM 둥근 모서리.
    ///
    /// Win11 26200 에서 테스트 창을 띄워 스크린샷으로 확인한 결과:
    /// - 동작하는 창 구성: WindowStyle=None, ResizeMode=NoResize, Background=Transparent(또는 반투명 브러시 — 블러 위에 덧칠됨).
    ///   AllowsTransparency=True(레이어드)·False 둘 다 블러가 실제로 보였다. False 인 경우 이 메서드가
    ///   HwndSource.CompositionTarget.BackgroundColor=Transparent 를 자동 설정한다. DwmExtendFrameIntoClientArea 는 불필요.
    ///   WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW(MakeOverlay) 창에서도 동작.
    /// - tint 알파가 거의 0 이면(0x01) 블러 대신 검은색이 나온다 → 알파는 최소 0x20 으로 올려 적용한다.
    ///   권장: 밝은 테마 #60F0F0F0~#C0F6F6F6, 어두운 테마 #A0201E1E 근처.
    /// - ACCENT_ENABLE_BLURBEHIND(3) 는 이 빌드에서 어떤 구성이든 검게 나와 fallback 으로 쓸 수 없음.
    /// - DWMWA_SYSTEMBACKDROP_TYPE(Win11 공식 아크릴)은 비활성 창에서 회색 단색으로 바뀌므로 NOACTIVATE 독에는 부적합.
    /// - SetWindowRgn 은 WPF 내용은 자르지만 아크릴 배경은 자르지 못한다(모서리가 네모로 남음).
    ///   → 블러 창의 모서리는 DWMWA_WINDOW_CORNER_PREFERENCE=ROUND 로 둥글게 한다(반경 약 8px 고정, 그림자 포함).
    ///   이 메서드가 ROUND + 시스템 테두리 없음(DWMWA_BORDER_COLOR=NONE)을 함께 설정한다.
    ///   따라서 블러 창 위에 그리는 테두리/내용의 CornerRadius 는 8 DIP 에 맞추는 것을 권장.
    /// - 드래그·크기 변경 중 성능은 측정하지 않음.
    /// SourceInitialized 이전에 호출해도 됨(핸들을 만들어 적용).
    /// </summary>
    public void EnableBlur(Window window, Color tint)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            if (!window.AllowsTransparency && HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct)
                ct.BackgroundColor = Colors.Transparent;
            byte a = Math.Max(tint.A, (byte)0x20);
            uint abgr = ((uint)a << 24) | ((uint)tint.B << 16) | ((uint)tint.G << 8) | tint.R;
            if (DesktopApi.SetAccent(hwnd, DesktopApi.ACCENT_ENABLE_ACRYLICBLURBEHIND, abgr) == 0)
                Log.Warn("SetWindowCompositionAttribute(acrylic) 실패");
            DesktopApi.SetDwmInt(hwnd, DesktopApi.DWMWA_WINDOW_CORNER_PREFERENCE, DesktopApi.DWMWCP_ROUND);
            DesktopApi.SetDwmInt(hwnd, DesktopApi.DWMWA_BORDER_COLOR, unchecked((int)DesktopApi.DWMWA_COLOR_NONE));
        }
        catch (Exception e)
        {
            Log.Error("EnableBlur 실패", e);
        }
    }

    public void DisableBlur(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            DesktopApi.SetAccent(hwnd, DesktopApi.ACCENT_DISABLED, 0);
            DesktopApi.SetDwmInt(hwnd, DesktopApi.DWMWA_WINDOW_CORNER_PREFERENCE, DesktopApi.DWMWCP_DEFAULT);
            DesktopApi.SetDwmInt(hwnd, DesktopApi.DWMWA_BORDER_COLOR, unchecked((int)DesktopApi.DWMWA_COLOR_DEFAULT));
        }
        catch (Exception e)
        {
            Log.Error("DisableBlur 실패", e);
        }
    }

    /// <summary>
    /// CreateRoundRectRgn + SetWindowRgn 으로 창 모양을 자름 (물리 픽셀, 창의 현재 DPI 반영).
    /// 크기·DPI 가 바뀌면 자동 재적용. radius 0 이면 해제. 레이어드(AllowsTransparency=True) 창에서도 동작 확인.
    /// 주의: 아크릴 블러 배경은 영역으로 잘리지 않는다 — EnableBlur 주석 참고.
    /// </summary>
    public void SetRoundedRegion(Window window, double radiusDip)
    {
        try
        {
            bool known = _rounded.ContainsKey(window);
            if (radiusDip <= 0)
            {
                if (known)
                {
                    _rounded.Remove(window);
                    window.SizeChanged -= OnRoundedWindowSizeChanged;
                    window.DpiChanged -= OnRoundedWindowDpiChanged;
                    window.Closed -= OnRoundedWindowClosed;
                }
                IntPtr h = new WindowInteropHelper(window).Handle;
                if (h != IntPtr.Zero) DesktopApi.SetWindowRgn(h, IntPtr.Zero, true);
                return;
            }

            _rounded[window] = radiusDip;
            if (!known)
            {
                window.SizeChanged += OnRoundedWindowSizeChanged;
                window.DpiChanged += OnRoundedWindowDpiChanged;
                window.Closed += OnRoundedWindowClosed;
            }
            ApplyRegion(window);
        }
        catch (Exception e)
        {
            Log.Error("SetRoundedRegion 실패", e);
        }
    }

    private void OnRoundedWindowSizeChanged(object sender, SizeChangedEventArgs e) => ApplyRegion((Window)sender);

    private void OnRoundedWindowDpiChanged(object sender, DpiChangedEventArgs e) =>
        _dispatcher.InvokeAsync(() => ApplyRegion((Window)sender), DispatcherPriority.Background);

    private void OnRoundedWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        _rounded.Remove(w);
        w.SizeChanged -= OnRoundedWindowSizeChanged;
        w.DpiChanged -= OnRoundedWindowDpiChanged;
        w.Closed -= OnRoundedWindowClosed;
    }

    private void ApplyRegion(Window window)
    {
        if (!_rounded.TryGetValue(window, out double radiusDip)) return;
        IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!User32.GetWindowRect(hwnd, out RECT wr) || wr.Width <= 0 || wr.Height <= 0) return;
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int d = Math.Max(1, (int)Math.Round(radiusDip * 2 * scale)); // CreateRoundRectRgn 은 지름
        IntPtr rgn = DesktopApi.CreateRoundRectRgn(0, 0, wr.Width + 1, wr.Height + 1, d, d);
        if (rgn == IntPtr.Zero) return;
        if (DesktopApi.SetWindowRgn(hwnd, rgn, true) == 0) Gdi32.DeleteObject(rgn); // 성공 시 시스템 소유
    }

    // ───────────────────────── 화면 / 배경 색 ─────────────────────────

    /// <summary>
    /// 화면 DC 에서 BitBlt(SRCCOPY) 로 영역을 읽어 최빈색(채널당 4비트 양자화 → 최빈 버킷의 실제 평균)을 반환.
    /// 비용을 줄이려고 높이는 최대 4px 로 자르고 가로는 최대 ~480 샘플. 영역이 화면 밖이면 null.
    /// </summary>
    public Color? SampleScreenColor(Rect areaDip)
    {
        try
        {
            double scale = PrimaryScale;
            RECT screen = PrimaryBoundsPx;
            RECT a = ToPx(areaDip, scale);
            a.Left = Math.Max(a.Left, screen.Left);
            a.Top = Math.Max(a.Top, screen.Top);
            a.Right = Math.Min(a.Right, screen.Right);
            a.Bottom = Math.Min(a.Bottom, Math.Min(screen.Bottom, a.Top + 4));
            if (a.Width <= 0 || a.Height <= 0) return null;

            int w = a.Width, h = a.Height;
            IntPtr screenDc = DesktopApi.GetDC(IntPtr.Zero);
            IntPtr memDc = Gdi32.CreateCompatibleDC(screenDc);
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Gdi32.BI_RGB,
                },
                bmiColors = new uint[256],
            };
            IntPtr dib = DesktopApi.CreateDIBSection(memDc, ref bmi, Gdi32.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = IntPtr.Zero;
            try
            {
                if (dib == IntPtr.Zero) return null;
                old = DesktopApi.SelectObject(memDc, dib);
                if (!DesktopApi.BitBlt(memDc, 0, 0, w, h, screenDc, a.Left, a.Top, DesktopApi.SRCCOPY)) return null;

                var buf = new byte[w * h * 4];
                Marshal.Copy(bits, buf, 0, buf.Length);
                var counts = new Dictionary<int, (int N, long R, long G, long B)>();
                int step = Math.Max(1, w / 480);
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x += step)
                {
                    int i = (y * w + x) * 4;
                    byte b = buf[i], g = buf[i + 1], r = buf[i + 2];
                    int k = ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
                    counts.TryGetValue(k, out var c);
                    counts[k] = (c.N + 1, c.R + r, c.G + g, c.B + b);
                }
                if (counts.Count == 0) return null;
                var best = counts.Values.MaxBy(v => v.N);
                return Color.FromRgb((byte)(best.R / best.N), (byte)(best.G / best.N), (byte)(best.B / best.N));
            }
            finally
            {
                if (old != IntPtr.Zero) DesktopApi.SelectObject(memDc, old);
                if (dib != IntPtr.Zero) Gdi32.DeleteObject(dib);
                Gdi32.DeleteDC(memDc);
                DesktopApi.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch (Exception e)
        {
            Log.Error("SampleScreenColor 실패", e);
            return null;
        }
    }

    /// <summary>
    /// 바탕화면 배경 그림만으로 계산한 영역 평균 색 (화면 캡처 아님 → 우리 창/다른 창 영향 없음).
    /// IDesktopWallpaper(파일·맞춤 방식·배경색) → 그림을 맞춤 방식대로 주 모니터에 매핑. 결과 캐시.
    /// </summary>
    public Color? SampleWallpaperColor(Rect areaDip)
    {
        try
        {
            return _wallpaper.Sample(ToPx(areaDip, PrimaryScale));
        }
        catch (Exception e)
        {
            Log.Error("SampleWallpaperColor 실패", e);
            return null;
        }
    }

    private void PollWallpaper()
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            string sig = WallpaperSampler.GetSignature();
            if (_wallpaperSignature is null) { _wallpaperSignature = sig; return; }
            if (sig == _wallpaperSignature) return;
            _wallpaperSignature = sig;
            _dispatcher.InvokeAsync(() => RaiseWallpaperChanged("poll"));
        }
        catch (Exception e)
        {
            Log.Warn($"배경 변경 확인 실패: {e.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private int _polling;

    private void QueueWallpaperChanged()
    {
        if (_wallpaperQueued) return;
        _wallpaperQueued = true;
        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(500);
                _wallpaperQueued = false;
                RaiseWallpaperChanged("WM_SETTINGCHANGE");
            }
            catch (Exception e)
            {
                _wallpaperQueued = false;
                Log.Error("WallpaperChanged 처리 실패", e);
            }
        });
    }

    private void RaiseWallpaperChanged(string reason)
    {
        _wallpaper.Invalidate();
        Log.Info($"배경 변경 감지 ({reason})");
        try { WallpaperChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception e) { Log.Error("WallpaperChanged 핸들러 예외", e); }
    }

    // ───────────────────────── 전체 화면 감지 ─────────────────────────

    /// <summary>포그라운드 창이 주 모니터 전체를 덮고 캡션이 없으면 전체 화면 앱으로 본다.</summary>
    private void EvaluateFullscreen()
    {
        bool fs = false;
        try
        {
            IntPtr fg = User32.GetForegroundWindow();
            if (fg != IntPtr.Zero && DesktopApi.MonitorFromWindow(fg, DesktopApi.MONITOR_DEFAULTTONULL) == DesktopApi.PrimaryMonitor)
            {
                User32.GetWindowThreadProcessId(fg, out uint pid);
                string cls = User32.GetClassNameOf(fg);
                bool shell = cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                             || pid == (uint)Environment.ProcessId || IsExplorer(pid);
                long style = User32.GetWindowLong(fg, User32.GWL_STYLE);
                bool caption = (style & DesktopApi.WS_CAPTION) == DesktopApi.WS_CAPTION;
                if (!shell && !caption && User32.GetWindowRect(fg, out RECT r))
                {
                    RECT m = PrimaryBoundsPx;
                    fs = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"전체 화면 확인 실패: {e.Message}");
        }
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        Log.Info($"전체 화면 앱: {fs}");
        try { FullscreenAppChanged?.Invoke(this, fs); }
        catch (Exception e) { Log.Error("FullscreenAppChanged 핸들러 예외", e); }
    }

    private static uint _explorerPid;

    private static bool IsExplorer(uint pid)
    {
        if (pid == 0) return false;
        if (pid == _explorerPid) return true;
        var (path, _) = Kernel32.QueryProcess(pid);
        if (path.EndsWith(@"\explorer.exe", StringComparison.OrdinalIgnoreCase))
        {
            _explorerPid = pid;
            return true;
        }
        return false;
    }

    // ───────────────────────── 브로드캐스트 ─────────────────────────

    private IntPtr BroadcastWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
            {
                Log.Info("TaskbarCreated (탐색기 재시작) → AppBar 재등록");
                _dispatcher.InvokeAsync(() =>
                {
                    ReRegisterAll();
                    QueueDisplayChanged();
                }, DispatcherPriority.Background);
                return IntPtr.Zero;
            }
            switch (msg)
            {
                case User32.WM_DISPLAYCHANGE:
                case User32.WM_DPICHANGED:
                    QueueDisplayChanged();
                    break;
                case User32.WM_SETTINGCHANGE:
                    int spi = (int)wParam.ToInt64();
                    if (spi == DesktopApi.SPI_SETWORKAREA) QueueDisplayChanged();
                    else if (spi == DesktopApi.SPI_SETDESKWALLPAPER) QueueWallpaperChanged();
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error("브로드캐스트 처리 예외", e);
        }
        return IntPtr.Zero;
    }

    private void QueueDisplayChanged()
    {
        if (_displayQueued) return;
        _displayQueued = true;
        _dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(300);
                _displayQueued = false;
                foreach (var s in _slots.ToList()) Reposition(s);
                DisplayChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception e)
            {
                _displayQueued = false;
                Log.Error("DisplayChanged 처리 예외", e);
            }
        });
    }

    // ───────────────────────── AppBar: 공개 API ─────────────────────────

    /// <summary>
    /// 숨은 도우미 창을 AppBar 로 등록해 가장자리 띠만 예약 (보이는 독 창은 움직이지 않음).
    /// Top 이면 상단바 아래에 쌓이고, 좌/우도 상단바 아래에서 시작한다. Dispose 하면 ABM_REMOVE.
    /// </summary>
    public IEdgeReservation ReserveEdge(DockEdge edge, double thickness)
    {
        var res = new EdgeReservation(this);
        try
        {
            var src = CreateHiddenWindow("MyDock.EdgeReservation");
            var slot = new Slot
            {
                Hwnd = src.Handle,
                Source = src,
                OwnsSource = true,
                Edge = ToAbe(edge),
                ThicknessDip = thickness,
                Reservation = res,
            };
            res.Slot = slot;
            AddSlot(slot);
        }
        catch (Exception e)
        {
            Log.Error("ReserveEdge 실패", e);
        }
        return res;
    }

    /// <summary>상단바 AppBar — 창을 시스템이 정한 상단 띠로 직접 배치. 다시 호출하면 두께만 갱신.</summary>
    public void RegisterTopAppBar(Window window, double thickness)
    {
        try
        {
            var existing = _slots.FirstOrDefault(s => s.Window == window);
            if (existing is not null)
            {
                existing.ThicknessDip = thickness;
                Reposition(existing);
                return;
            }
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            var src = HwndSource.FromHwnd(hwnd);
            if (src is null)
            {
                Log.Error("RegisterTopAppBar: HwndSource 없음");
                return;
            }
            var slot = new Slot
            {
                Hwnd = hwnd,
                Source = src,
                Window = window,
                Edge = Shell32.ABE_TOP,
                ThicknessDip = thickness,
                IsTopBar = true,
            };
            window.Closed += OnTopBarClosed;
            AddSlot(slot);
        }
        catch (Exception e)
        {
            Log.Error("RegisterTopAppBar 실패", e);
        }
    }

    /// <summary>등록 안 된 창이어도 무해. 해제 직후 다시 Register 가능.</summary>
    public void UnregisterAppBar(Window window)
    {
        try
        {
            var slot = _slots.FirstOrDefault(s => s.Window == window);
            if (slot is null) return;
            window.Closed -= OnTopBarClosed;
            RemoveSlot(slot);
        }
        catch (Exception e)
        {
            Log.Error("UnregisterAppBar 실패", e);
        }
    }

    private void OnTopBarClosed(object? sender, EventArgs e)
    {
        if (sender is Window w) UnregisterAppBar(w);
    }

    private static uint ToAbe(DockEdge edge) => edge switch
    {
        DockEdge.Left => Shell32.ABE_LEFT,
        DockEdge.Right => Shell32.ABE_RIGHT,
        DockEdge.Top => Shell32.ABE_TOP,
        _ => Shell32.ABE_BOTTOM,
    };

    // ───────────────────────── AppBar: 내부 ─────────────────────────

    private void AddSlot(Slot slot)
    {
        Slot captured = slot;
        slot.Hook = (IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) => SlotWndProc(captured, msg, wParam, lParam);
        slot.Source.AddHook(slot.Hook);

        if (slot.IsTopBar)
        {
            _slots.Insert(0, slot);
            // 상단바가 나중에 등록되면 시스템이 기존 상단 예약 아래에 놓으므로, 순서를 맞추려고 전부 다시 등록.
            if (_slots.Count > 1)
            {
                ReRegisterAll();
                return;
            }
        }
        else
        {
            _slots.Add(slot);
        }
        Register(slot);
        Reposition(slot);
        Log.Info($"AppBar 등록 edge={slot.Edge} topbar={slot.IsTopBar} thickness={slot.ThicknessDip}DIP rect={slot.LastRect}");
    }

    private void RemoveSlot(Slot slot)
    {
        if (!_slots.Remove(slot)) return;
        try { if (slot.Hook is not null) slot.Source.RemoveHook(slot.Hook); } catch { /* 이미 파괴 */ }
        Unregister(slot);
        if (slot.OwnsSource)
        {
            try { slot.Source.Dispose(); } catch { }
        }
        foreach (var s in _slots) QueueReposition(s); // 남은 AppBar 가 빈 공간을 차지하도록
        Log.Info($"AppBar 해제 edge={slot.Edge} topbar={slot.IsTopBar}");
    }

    private void Register(Slot slot)
    {
        var abd = NewData(slot.Hwnd);
        abd.uCallbackMessage = _callbackMsg;
        if (Shell32.SHAppBarMessage(Shell32.ABM_NEW, ref abd) == UIntPtr.Zero)
        {
            // 이미 등록돼 있는 경우(재등록 시)에도 FALSE — 상태는 등록으로 본다.
            Log.Warn($"ABM_NEW FALSE hwnd=0x{slot.Hwnd.ToInt64():X} (이미 등록됐을 수 있음)");
        }
        slot.Registered = true;
        lock (RegistryGate) RegisteredHwnds.Add(slot.Hwnd);
    }

    private static void Unregister(Slot slot)
    {
        if (!slot.Registered) return;
        var abd = NewData(slot.Hwnd);
        Shell32.SHAppBarMessage(Shell32.ABM_REMOVE, ref abd);
        slot.Registered = false;
        lock (RegistryGate) RegisteredHwnds.Remove(slot.Hwnd);
    }

    /// <summary>모두 해제 후 상단바 → 나머지 순서로 다시 등록 (탐색기 재시작, 상단바 늦은 등록).</summary>
    private void ReRegisterAll()
    {
        foreach (var s in _slots) Unregister(s);
        foreach (var s in _slots.OrderByDescending(s => s.IsTopBar).ToList())
        {
            s.LastRect = default;
            Register(s);
            Reposition(s);
        }
    }

    private IntPtr SlotWndProc(Slot slot, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if ((uint)msg == _callbackMsg)
            {
                switch ((int)wParam.ToInt64())
                {
                    case Shell32.ABN_POSCHANGED:
                        QueueReposition(slot);
                        break;
                    case Shell32.ABN_FULLSCREENAPP:
                        _dispatcher.InvokeAsync(EvaluateFullscreen, DispatcherPriority.Background);
                        break;
                }
                return IntPtr.Zero;
            }
            switch (msg)
            {
                case User32.WM_ACTIVATE:
                {
                    var abd = NewData(slot.Hwnd);
                    Shell32.SHAppBarMessage(Shell32.ABM_ACTIVATE, ref abd);
                    break;
                }
                case User32.WM_WINDOWPOSCHANGED:
                {
                    var abd = NewData(slot.Hwnd);
                    Shell32.SHAppBarMessage(Shell32.ABM_WINDOWPOSCHANGED, ref abd);
                    break;
                }
                case User32.WM_DPICHANGED:
                case User32.WM_DISPLAYCHANGE:
                    QueueDisplayChanged();
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error("AppBar WndProc 예외", e);
        }
        return IntPtr.Zero;
    }

    private void QueueReposition(Slot slot)
    {
        if (slot.Queued) return;
        slot.Queued = true;
        _dispatcher.InvokeAsync(() =>
        {
            slot.Queued = false;
            if (_slots.Contains(slot)) Reposition(slot);
        }, DispatcherPriority.Background);
    }

    /// <summary>ABM_QUERYPOS → (상단바 아래로 오프셋) → 두께 보정 → ABM_SETPOS → (상단바면) SetWindowPos.</summary>
    private void Reposition(Slot slot)
    {
        if (slot.Positioning || !slot.Registered) return;
        slot.Positioning = true;
        try
        {
            double scale = PrimaryScale;
            RECT screen = PrimaryBoundsPx;
            int px = Math.Max(1, (int)Math.Round(slot.ThicknessDip * scale));

            var abd = NewData(slot.Hwnd);
            abd.uEdge = slot.Edge;
            abd.rc = screen;
            OffsetBelowTopBar(slot, ref abd.rc);
            ApplyThickness(ref abd.rc, slot.Edge, px);

            Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref abd);
            OffsetBelowTopBar(slot, ref abd.rc);
            ApplyThickness(ref abd.rc, slot.Edge, px);
            Shell32.SHAppBarMessage(Shell32.ABM_SETPOS, ref abd);
            ApplyThickness(ref abd.rc, slot.Edge, px);

            RECT rc = abd.rc;
            bool changed = rc.Left != slot.LastRect.Left || rc.Top != slot.LastRect.Top ||
                           rc.Right != slot.LastRect.Right || rc.Bottom != slot.LastRect.Bottom;
            slot.LastRect = rc;

            if (slot.Window is not null)
            {
                User32.SetWindowPos(slot.Hwnd, IntPtr.Zero, rc.Left, rc.Top, rc.Width, rc.Height,
                    User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            }
            if (slot.Reservation is { } res)
            {
                res.Bounds = ToDip(rc, scale);
                if (changed) res.RaiseBoundsChanged();
            }
            if (changed && slot.IsTopBar)
                foreach (var s in _slots) if (s != slot) QueueReposition(s);
        }
        catch (Exception e)
        {
            Log.Error("AppBar 재배치 실패", e);
        }
        finally
        {
            slot.Positioning = false;
        }
    }

    /// <summary>상단바가 아닌 상단/좌/우 AppBar 는 우리 상단바 아래에서 시작.</summary>
    private void OffsetBelowTopBar(Slot slot, ref RECT rc)
    {
        if (slot.IsTopBar || slot.Edge == Shell32.ABE_BOTTOM) return;
        var top = _slots.FirstOrDefault(b => b.IsTopBar && b.Registered && b != slot);
        if (top is null || top.LastRect.Height <= 0) return;
        if (rc.Top < top.LastRect.Bottom) rc.Top = top.LastRect.Bottom;
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

    private static APPBARDATA NewData(IntPtr hwnd) => new()
    {
        cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
        hWnd = hwnd,
    };

    // ───────────────────────── 종료 ─────────────────────────

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fullscreenTimer.Stop();
        _wallpaperPoll.Dispose();
        foreach (var s in _slots.ToList()) RemoveSlot(s);
        try { _broadcastWindow.RemoveHook(BroadcastWndProc); _broadcastWindow.Dispose(); } catch { }
    }
}
