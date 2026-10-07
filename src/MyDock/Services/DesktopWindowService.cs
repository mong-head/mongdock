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
/// 여러 모니터: 앱은 PerMonitorV2 이므로 물리 픽셀 ↔ DIP 변환은 "대상 모니터의 DPI" 로 한다
/// (DIP = px / 그 모니터 배율 = 그 모니터 위 WPF 창의 Left/Top — <see cref="MonitorInfo"/> 참고).
/// 모니터 인자가 없는 기존 메서드는 주 모니터 기준(단일 모니터에서 예전과 동일). 생성자는 UI 스레드에서 호출해야 한다.
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
        /// <summary>요청한 모니터 장치 이름 (null/"" = 주 모니터). 매 배치 때 다시 찾는다.</summary>
        public string? Monitor;
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
    private readonly WallpaperSampler _wallpaper = new();
    private readonly Timer _wallpaperPoll;
    private string? _wallpaperSignature;
    private bool _displayQueued;
    private bool _wallpaperQueued;
    private bool _disposed;

    public event EventHandler<bool>? FullscreenAppChanged;
    public event EventHandler? DisplayChanged;
    private EventHandler? _wallpaperChanged;

    /// <summary>구독자가 있을 때만 2초 서명 폴링을 돌린다 (UI 스레드에서 구독/해제).</summary>
    public event EventHandler? WallpaperChanged
    {
        add
        {
            _wallpaperChanged += value;
            UpdateWallpaperPolling();
        }
        remove
        {
            _wallpaperChanged -= value;
            UpdateWallpaperPolling();
        }
    }

    private void UpdateWallpaperPolling()
    {
        bool on = _wallpaperChanged is not null && !_disposed;
        if (on)
        {
            _wallpaperSignature = null; // 처음 폴링은 기준값만 기록
            _wallpaperPoll.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }
        else
        {
            _wallpaperPoll.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    public DesktopWindowService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _callbackMsg = User32.RegisterWindowMessage("mongdock.AppBarCallback");
        _taskbarCreatedMsg = User32.RegisterWindowMessage("TaskbarCreated");
        InstallExitHooks();

        // 브로드캐스트(WM_DISPLAYCHANGE / WM_SETTINGCHANGE / TaskbarCreated)는 메시지 전용 창으로 오지 않으므로 숨은 최상위 창.
        _broadcastWindow = CreateHiddenWindow("mongdock.DesktopEvents");
        _broadcastWindow.AddHook(BroadcastWndProc);

        _fullscreenTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => { CheckMonitorMetrics(); EvaluateFullscreen(); }, _dispatcher);
        _fullscreenTimer.Start();

        // 가상 데스크톱 전환(데스크톱별 배경)·슬라이드쇼 감지: 백그라운드에서 2초마다 서명 비교
        _wallpaperPoll = new Timer(_ => PollWallpaper(), null, Timeout.Infinite, Timeout.Infinite); // 구독 시 시작
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

    /// <summary>주 모니터 위의 커서 위치 (주 모니터 DPI 기준 DIP). 커서가 다른 모니터에 있으면 null.</summary>
    public Point? GetCursorPosition()
    {
        if (!DesktopApi.GetCursorPos(out POINT p)) return null;
        IntPtr primary = DesktopApi.PrimaryMonitor;
        if (DesktopApi.MonitorFromPoint(p, DesktopApi.MONITOR_DEFAULTTONULL) != primary) return null;
        double s = DesktopApi.GetMonitorScale(primary);
        return new Point(p.X / s, p.Y / s);
    }

    // ───────────────────────── 여러 모니터 ─────────────────────────

    public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors.GetAll();

    public MonitorInfo ResolveMonitor(string? deviceName) => Monitors.Resolve(deviceName);

    /// <summary>커서가 monitor 영역(물리 px) 안이면 그 모니터 기준 DIP.</summary>
    public Point? GetCursorPosition(MonitorInfo monitor)
    {
        if (!DesktopApi.GetCursorPos(out POINT p)) return null;
        var px = new Point(p.X, p.Y);
        return monitor.ContainsPx(px) ? monitor.ToDip(px) : null;
    }

    public bool EnsureOnMonitor(Window window, MonitorInfo monitor)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            // DIP→px 변환은 창의 현재 DPI 로 일어나므로, DPI 가 같으면 Left/Top 만으로 정확 (단일 모니터는 항상 여기서 끝)
            double current = VisualTreeHelper.GetDpi(window).DpiScaleX;
            if (Math.Abs(current - monitor.Scale) < 0.001) return false;
            // 같은 모니터인데 DPI 만 다름 = 런타임 배율 변경 직후 (WM_DPICHANGED 대기) → 옮겨도 소용없으니 기존 흐름에 맡김
            if (DesktopApi.TryGetMonitorRects(DesktopApi.MonitorFromWindow(hwnd, DesktopApi.MONITOR_DEFAULTTONEAREST), out RECT cur, out _)
                && monitor.SameBounds(cur))
                return false;
            RECT b = monitor.BoundsRect;
            // 위치만 옮김 (크기는 WM_DPICHANGED 에서 WPF 가 DIP 크기를 유지하도록 다시 계산)
            User32.SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, 0, 0,
                User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            Log.Info($"창을 모니터 {monitor.DeviceName} 로 이동 (DPI {current * 96:0}→{monitor.Scale * 96:0})");
            return true;
        }
        catch (Exception e)
        {
            Log.Error("EnsureOnMonitor 실패", e);
            return false;
        }
    }

    // ───────────────────────── 전역 마우스 누름 (WH_MOUSE_LL) ─────────────────────────

    private EventHandler<Point?>? _globalMouseDown;
    private IntPtr _mouseHook;
    private DesktopApi.LowLevelMouseProc? _mouseProc; // GC 방지용으로 필드 보관

    /// <summary>
    /// 화면 어디서든 왼/오/가운데 버튼이 눌린 순간 (UI 스레드). 인자는 눌린 곳 모니터 기준 DIP 위치(어느 모니터에도 없으면 null).
    /// 첫 구독 때 WH_MOUSE_LL 훅 설치, 마지막 해제 때 제거 (UI 스레드에서 구독/해제할 것 — 훅은 설치한 스레드의 메시지 루프로 호출됨).
    /// </summary>
    public event EventHandler<Point?>? GlobalMouseDown
    {
        add
        {
            _globalMouseDown += value;
            if (_globalMouseDown is not null && _mouseHook == IntPtr.Zero && !_disposed) InstallMouseHook();
        }
        remove
        {
            _globalMouseDown -= value;
            if (_globalMouseDown is null) RemoveMouseHook();
        }
    }

    private void InstallMouseHook()
    {
        _mouseProc ??= MouseHookProc;
        IntPtr hMod = DesktopApi.GetModuleHandle(null);
        _mouseHook = DesktopApi.SetWindowsHookEx(DesktopApi.WH_MOUSE_LL, _mouseProc, hMod, 0);
        if (_mouseHook == IntPtr.Zero)
        {
            hMod = DesktopApi.GetModuleHandle("user32.dll");
            _mouseHook = DesktopApi.SetWindowsHookEx(DesktopApi.WH_MOUSE_LL, _mouseProc, hMod, 0);
        }
        if (_mouseHook == IntPtr.Zero) Log.Error($"WH_MOUSE_LL 설치 실패 err={Marshal.GetLastWin32Error()}");
        else Log.Info("전역 마우스 훅 설치");
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        DesktopApi.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
        Log.Info("전역 마우스 훅 해제");
    }

    /// <summary>LL 훅 콜백: 좌표만 읽어 넘기고 즉시 CallNextHookEx (시간 초과 방지 — 변환·이벤트는 Dispatcher 에서).</summary>
    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = (int)wParam.ToInt64();
            if (m is DesktopApi.WM_LBUTTONDOWN or DesktopApi.WM_RBUTTONDOWN or DesktopApi.WM_MBUTTONDOWN)
            {
                // MSLLHOOKSTRUCT.pt = 처음 두 int (물리 px)
                var pt = new POINT { X = Marshal.ReadInt32(lParam, 0), Y = Marshal.ReadInt32(lParam, 4) };
                _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RaiseGlobalMouseDown(pt)));
            }
        }
        return DesktopApi.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void RaiseGlobalMouseDown(POINT p)
    {
        try
        {
            // 그 점이 있는 모니터의 배율로 DIP 변환 → 그 모니터 위 창(메뉴·패널)의 화면 DIP 와 같은 기준
            Point? dip = null;
            IntPtr mon = DesktopApi.MonitorFromPoint(p, DesktopApi.MONITOR_DEFAULTTONULL);
            if (mon != IntPtr.Zero)
            {
                double s = DesktopApi.GetMonitorScale(mon);
                dip = new Point(p.X / s, p.Y / s);
            }
            _globalMouseDown?.Invoke(this, dip);
        }
        catch (Exception e)
        {
            Log.Error("GlobalMouseDown 핸들러 예외", e);
        }
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
    /// - SetWindowRgn 은 WPF 내용은 자르지만 아크릴 배경은 자르지 못해(모서리가 네모로 남음) 쓰지 않는다.
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

    // ───────────────────────── DWM 창 미리보기 ─────────────────────────

    /// <summary>
    /// DWM 실시간 창 미리보기 (DwmRegisterThumbnail). host 창의 클라이언트 영역 destDip 위치(host 의 DPI 로 px 변환)에 그린다.
    /// host 창 구성: 최상위(top-level) WPF 창이면 됨. Win11 26200 에서 AllowsTransparency=True(레이어드)·False 둘 다
    /// 실제로 미리보기가 보이는 것을 화면 캡처로 확인. DWM 이 host 내용 "위에" 합성하므로 WPF 요소로 미리보기를 덮을 수는 없다
    /// (미리보기 위에 그릴 것은 별도 창으로). 최소화된 창은 원본이 마지막 모습이거나 비어 보이고,
    /// 다른 가상 데스크톱(cloaked) 창은 비어 보일 수 있음. 실패 시 null.
    /// </summary>
    public IWindowThumbnail? CreateThumbnail(Window host, IntPtr source, Rect destDip)
    {
        try
        {
            if (source == IntPtr.Zero || !User32.IsWindow(source)) return null;
            IntPtr hostHwnd = new WindowInteropHelper(host).EnsureHandle();
            int hr = DwmThumbnail.DwmRegisterThumbnail(hostHwnd, source, out IntPtr thumb);
            if (hr != 0 || thumb == IntPtr.Zero)
            {
                Log.Warn($"DwmRegisterThumbnail 실패 hr=0x{hr:X8}");
                return null;
            }
            var t = new WindowThumbnail(host, thumb);
            t.Update(destDip);
            return t;
        }
        catch (Exception e)
        {
            Log.Error("CreateThumbnail 실패", e);
            return null;
        }
    }

    private sealed class WindowThumbnail : IWindowThumbnail
    {
        private readonly Window _host;
        private IntPtr _thumb;

        public WindowThumbnail(Window host, IntPtr thumb)
        {
            _host = host;
            _thumb = thumb;
        }

        public Size SourceSize
        {
            get
            {
                if (_thumb == IntPtr.Zero || DwmThumbnail.DwmQueryThumbnailSourceSize(_thumb, out SIZE s) != 0) return Size.Empty;
                return new Size(s.cx, s.cy);
            }
        }

        public void Update(Rect destDip)
        {
            if (_thumb == IntPtr.Zero) return;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(_host);
                var props = new DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = DwmThumbnail.DWM_TNP_RECTDESTINATION | DwmThumbnail.DWM_TNP_VISIBLE |
                              DwmThumbnail.DWM_TNP_OPACITY | DwmThumbnail.DWM_TNP_SOURCECLIENTAREAONLY,
                    rcDestination = new RECT(
                        (int)Math.Round(destDip.Left * dpi.DpiScaleX), (int)Math.Round(destDip.Top * dpi.DpiScaleY),
                        (int)Math.Round(destDip.Right * dpi.DpiScaleX), (int)Math.Round(destDip.Bottom * dpi.DpiScaleY)),
                    opacity = 255,
                    fVisible = true,
                    fSourceClientAreaOnly = false,
                };
                int hr = DwmThumbnail.DwmUpdateThumbnailProperties(_thumb, ref props);
                if (hr != 0) Log.Warn($"DwmUpdateThumbnailProperties 실패 hr=0x{hr:X8}");
            }
            catch (Exception e)
            {
                Log.Error("썸네일 위치 갱신 실패", e);
            }
        }

        public void Dispose()
        {
            if (_thumb == IntPtr.Zero) return;
            DwmThumbnail.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
        }
    }

    // ───────────────────────── 화면 / 배경 색 ─────────────────────────

    /// <summary>
    /// 화면 DC 에서 BitBlt(SRCCOPY) 로 영역을 읽어 최빈색(채널당 4비트 양자화 → 최빈 버킷의 실제 평균)을 반환.
    /// 비용을 줄이려고 높이는 최대 4px 로 자르고 가로는 최대 ~480 샘플. 영역이 화면 밖이면 null.
    /// </summary>
    public Color? SampleScreenColor(Rect areaDip) => SampleScreenColorCore(areaDip, PrimaryScale, PrimaryBoundsPx);

    public Color? SampleScreenColor(Rect areaDip, MonitorInfo monitor) =>
        SampleScreenColorCore(areaDip, monitor.Scale, monitor.BoundsRect);

    private static Color? SampleScreenColorCore(Rect areaDip, double scale, RECT screen)
    {
        try
        {
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
    /// IDesktopWallpaper(파일·맞춤 방식·배경색) → 그림을 맞춤 방식대로 주 모니터에 매핑.
    /// 디코드·계산은 백그라운드 스레드. 캐시 적중이면 즉시 완료된 Task. 결과의 연속 작업은 호출한 컨텍스트(UI)로 돌아온다.
    /// </summary>
    public Task<Color?> SampleWallpaperColorAsync(Rect areaDip) => SampleWallpaperCore(areaDip, PrimaryScale);

    /// <summary>monitor 기준 DIP 영역 → 물리 px → 그 영역이 있는 모니터의 배경으로 계산.</summary>
    public Task<Color?> SampleWallpaperColorAsync(Rect areaDip, MonitorInfo monitor) => SampleWallpaperCore(areaDip, monitor.Scale);

    private Task<Color?> SampleWallpaperCore(Rect areaDip, double scale)
    {
        try
        {
            RECT px = ToPx(areaDip, scale);
            if (_wallpaper.TryGetCached(px, out Color? cached)) return Task.FromResult(cached);
            return Task.Run(() =>
            {
                try { return _wallpaper.Sample(px); }
                catch (Exception e)
                {
                    Log.Error("SampleWallpaperColor 실패", e);
                    return (Color?)null;
                }
            });
        }
        catch (Exception e)
        {
            Log.Error("SampleWallpaperColorAsync 실패", e);
            return Task.FromResult<Color?>(null);
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
        _wallpaperSignature = null;
        Log.Info($"배경 변경 감지 ({reason})");
        try { _wallpaperChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception e) { Log.Error("WallpaperChanged 핸들러 예외", e); }
    }

    // ───────────────────────── 전체 화면 감지 ─────────────────────────

    /// <summary>
    /// 포그라운드 창이 자기가 있는 모니터 전체를 덮고 캡션이 없으면 그 모니터에 전체 화면 앱이 있다고 본다.
    /// (포그라운드 창 하나만 보므로 전체 화면 모니터는 많아야 하나.)
    /// </summary>
    private void EvaluateFullscreen()
    {
        string? fs = null;
        try
        {
            IntPtr fg = User32.GetForegroundWindow();
            IntPtr mon = fg == IntPtr.Zero ? IntPtr.Zero : DesktopApi.MonitorFromWindow(fg, DesktopApi.MONITOR_DEFAULTTONULL);
            if (mon != IntPtr.Zero)
            {
                User32.GetWindowThreadProcessId(fg, out uint pid);
                string cls = User32.GetClassNameOf(fg);
                bool shell = cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                             || pid == (uint)Environment.ProcessId || IsExplorer(pid);
                long style = User32.GetWindowLong(fg, User32.GWL_STYLE);
                bool caption = (style & DesktopApi.WS_CAPTION) == DesktopApi.WS_CAPTION;
                if (!shell && !caption && User32.GetWindowRect(fg, out RECT r)
                    && DesktopApi.TryGetMonitorInfoEx(mon, out MONITORINFOEX mi))
                {
                    RECT m = mi.rcMonitor;
                    if (r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom)
                        fs = mi.szDevice ?? "";
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"전체 화면 확인 실패: {e.Message}");
        }
        // 디바운스: 같은 판정이 700ms 이상 유지돼야 발생 (배율·해상도 전환 순간 창이 잠깐 화면을 덮는 경우 무시)
        if (SameDevice(fs, _fullscreenDevice))
        {
            _fullscreenPendingSince = null;
            return;
        }
        var now = DateTime.UtcNow;
        if (!SameDevice(_fullscreenPending, fs) || _fullscreenPendingSince is null)
        {
            _fullscreenPending = fs;
            _fullscreenPendingSince = now;
            // 1초 타이머를 기다리지 않고 750ms 뒤 다시 확인
            _dispatcher.InvokeAsync(async () => { await Task.Delay(750); EvaluateFullscreen(); });
            return;
        }
        if (now - _fullscreenPendingSince.Value < FullscreenDebounce) return;
        _fullscreenPendingSince = null;
        _fullscreenDevice = fs;
        Log.Info(fs is null ? "전체 화면 앱: False" : $"전체 화면 앱: True ({fs})");
        try { FullscreenAppChanged?.Invoke(this, fs is not null); }
        catch (Exception e) { Log.Error("FullscreenAppChanged 핸들러 예외", e); }
    }

    private static bool SameDevice(string? a, string? b) =>
        a is null ? b is null : b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public string? FullscreenMonitor => _fullscreenDevice;

    public bool IsFullscreenOn(string? deviceName)
    {
        if (_fullscreenDevice is null) return false;
        string target = string.IsNullOrWhiteSpace(deviceName) ? Monitors.GetPrimary().DeviceName : deviceName.Trim();
        return string.Equals(_fullscreenDevice, target, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly TimeSpan FullscreenDebounce = TimeSpan.FromMilliseconds(700);
    private string? _fullscreenDevice;
    private string? _fullscreenPending;
    private DateTime? _fullscreenPendingSince;

    // ───────────────────────── 모니터 구성/DPI/해상도 감시 ─────────────────────────
    // 시스템 배율이 바뀌어도(원격 접속 시 100%→125% 등) 숨은 창에는 WM_DPICHANGED 가 오지 않고
    // WM_DISPLAYCHANGE·WM_SETTINGCHANGE 도 오지 않을 수 있어, 1초 타이머에서 모든 모니터의
    // 장치·영역·DPI·주 모니터 여부(작업 영역 제외)를 비교한다. 모니터 연결/분리도 여기서 잡힌다.

    private string? _lastMonitors;

    private void CheckMonitorMetrics()
    {
        try
        {
            string sig = Monitors.Signature(Monitors.GetAll());
            if (_lastMonitors is null) { _lastMonitors = sig; return; }
            if (sig == _lastMonitors) return;
            Log.Info($"모니터 구성/DPI 변경 {_lastMonitors} → {sig}");
            _lastMonitors = sig;
            QueueDisplayChanged();
        }
        catch (Exception e)
        {
            Log.Warn($"DPI 확인 실패: {e.Message}");
        }
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
                VirtualDesktopHelper.Invalidate();
                _dispatcher.InvokeAsync(() =>
                {
                    ReRegisterAll();
                    if (_taskbarHidden) ApplyTaskbarVisibility(hidden: true); // 새 작업 표시줄도 다시 숨김
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
                    if (spi is DesktopApi.SPI_SETWORKAREA or DesktopApi.SPI_SETLOGICALDPIOVERRIDE) QueueDisplayChanged();
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
                // 어느 경로로 왔든 기준값을 맞춰 두어 DPI 폴링이 같은 변경으로 한 번 더 발생시키지 않게
                var monitors = Monitors.GetAll();
                _lastMonitors = Monitors.Signature(monitors);
                foreach (var s in _slots.ToList()) Reposition(s);
                Log.Info($"DisplayChanged (모니터 {monitors.Count}개: {string.Join(" / ", monitors)}; 주 모니터 DIP {GetPrimaryScreenBounds()})");
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
    public IEdgeReservation ReserveEdge(DockEdge edge, double thickness) => ReserveEdge(edge, thickness, null);

    /// <summary>지정 모니터(장치 이름)에 예약. 없거나 분리되면 주 모니터 — 배치할 때마다 다시 찾으므로 다시 연결되면 복귀.</summary>
    public IEdgeReservation ReserveEdge(DockEdge edge, double thickness, string? monitor)
    {
        var res = new EdgeReservation(this);
        try
        {
            var src = CreateHiddenWindow("mongdock.EdgeReservation");
            var slot = new Slot
            {
                Hwnd = src.Handle,
                Source = src,
                OwnsSource = true,
                Edge = ToAbe(edge),
                ThicknessDip = thickness,
                Reservation = res,
                Monitor = monitor,
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
    public void RegisterTopAppBar(Window window, double thickness) => RegisterTopAppBar(window, thickness, null);

    /// <summary>지정 모니터(장치 이름, null/"" = 주 모니터) 맨 위 상단바. 그 모니터가 없으면 배치하지 않음(창을 닫을 것).</summary>
    public void RegisterTopAppBar(Window window, double thickness, string? monitor)
    {
        try
        {
            var existing = _slots.FirstOrDefault(s => s.Window == window);
            if (existing is not null)
            {
                existing.ThicknessDip = thickness;
                existing.Monitor = monitor;
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
                Monitor = monitor,
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
        Log.Info($"AppBar 등록 edge={slot.Edge} topbar={slot.IsTopBar} monitor={(string.IsNullOrEmpty(slot.Monitor) ? "주" : slot.Monitor)} thickness={slot.ThicknessDip}DIP rect={slot.LastRect}");
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

    /// <summary>
    /// 슬롯이 놓일 모니터. 상단바: 지정 모니터(없으면 주 모니터), 지정했는데 분리됐으면 null(배치 안 함 — 창이 곧 닫힘).
    /// 예약 띠(독): 지정 모니터, 분리됐으면 주 모니터.
    /// </summary>
    private static MonitorInfo? SlotMonitor(Slot slot)
    {
        if (string.IsNullOrWhiteSpace(slot.Monitor)) return Monitors.GetPrimary();
        return slot.IsTopBar ? Monitors.Find(slot.Monitor) : Monitors.Resolve(slot.Monitor);
    }

    /// <summary>ABM_QUERYPOS → (상단바 아래로 오프셋) → 두께 보정 → ABM_SETPOS → (상단바면) SetWindowPos.</summary>
    private void Reposition(Slot slot)
    {
        if (slot.Positioning || !slot.Registered) return;
        slot.Positioning = true;
        try
        {
            var monitor = SlotMonitor(slot);
            if (monitor is null)
            {
                Log.Warn($"상단바 모니터 {slot.Monitor} 없음 — 배치 건너뜀");
                return;
            }
            double scale = monitor.Scale;
            RECT screen = monitor.BoundsRect;
            int px = Math.Max(1, (int)Math.Round(slot.ThicknessDip * scale));

            var abd = NewData(slot.Hwnd);
            abd.uEdge = slot.Edge;
            abd.rc = screen;
            OffsetBelowTopBar(slot, screen, ref abd.rc);
            ApplyThickness(ref abd.rc, slot.Edge, px);

            Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref abd);
            OffsetBelowTopBar(slot, screen, ref abd.rc);
            ApplyThickness(ref abd.rc, slot.Edge, px);
            Shell32.SHAppBarMessage(Shell32.ABM_SETPOS, ref abd);
            ApplyThickness(ref abd.rc, slot.Edge, px);

            RECT rc = abd.rc;
            bool changed = rc.Left != slot.LastRect.Left || rc.Top != slot.LastRect.Top ||
                           rc.Right != slot.LastRect.Right || rc.Bottom != slot.LastRect.Bottom;
            slot.LastRect = rc;

            if (slot.Window is not null)
            {
                // 다른 모니터(다른 DPI)로 옮기는 경우: 먼저 위치만 옮겨 WM_DPICHANGED(WPF 가 제안 크기로 바꿈)를 끝내고,
                // 그다음 최종 px 크기를 설정해야 크기가 튀지 않는다. 같은 모니터면(단일 모니터 포함) 예전과 같이 한 번만.
                if (DesktopApi.TryGetMonitorRects(DesktopApi.MonitorFromWindow(slot.Hwnd, DesktopApi.MONITOR_DEFAULTTONEAREST), out RECT cur, out _)
                    && !monitor.SameBounds(cur))
                {
                    User32.SetWindowPos(slot.Hwnd, IntPtr.Zero, rc.Left, rc.Top, 0, 0,
                        User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                }
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

    /// <summary>상단바가 아닌 상단/좌/우 AppBar 는 같은 모니터(screen)에 있는 우리 상단바 아래에서 시작.</summary>
    private void OffsetBelowTopBar(Slot slot, RECT screen, ref RECT rc)
    {
        if (slot.IsTopBar || slot.Edge == Shell32.ABE_BOTTOM) return;
        var top = _slots.FirstOrDefault(b => b.IsTopBar && b.Registered && b != slot && b.LastRect.Height > 0 && OnScreen(b.LastRect, screen));
        if (top is null) return;
        if (rc.Top < top.LastRect.Bottom) rc.Top = top.LastRect.Bottom;
    }

    /// <summary>r 의 가운데가 screen 안에 있는지.</summary>
    private static bool OnScreen(RECT r, RECT screen)
    {
        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        return cx >= screen.Left && cx < screen.Right && cy >= screen.Top && cy < screen.Bottom;
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
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { RemoveAll("ProcessExit"); RestoreTaskbar("ProcessExit"); };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { RemoveAll("UnhandledException"); RestoreTaskbar("UnhandledException"); };
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

    // ───────────────────────── 윈도우 작업 표시줄 숨김 ─────────────────────────

    private static volatile bool _taskbarHidden;

    /// <summary>
    /// 윈도우 작업 표시줄(Shell_TrayWnd + 모든 Shell_SecondaryTrayWnd) 숨김/복원 (ShowWindowAsync — 탐색기가 응답 없어도 안 멈춤).
    /// 작업 영역은 AppBar 가 관리하므로 건드리지 않고, 작업 표시줄 자동 숨김 레지스트리도 바꾸지 않는다.
    /// 숨긴 상태로 끝나면 ProcessExit/UnhandledException/Dispose 에서 복원. 탐색기 재시작(TaskbarCreated) 후 다시 숨김.
    /// </summary>
    public void SetWindowsTaskbarHidden(bool hidden)
    {
        if (_taskbarHidden == hidden) return;
        _taskbarHidden = hidden;
        ApplyTaskbarVisibility(hidden);
        Log.Info(hidden ? "윈도우 작업 표시줄 숨김" : "윈도우 작업 표시줄 복원");
    }

    private static void ApplyTaskbarVisibility(bool hidden)
    {
        try
        {
            foreach (IntPtr h in FindTaskbars())
                User32.ShowWindowAsync(h, hidden ? User32.SW_HIDE : User32.SW_SHOWNA);
        }
        catch (Exception e)
        {
            Log.Error("작업 표시줄 표시 상태 변경 실패", e);
        }
    }

    private static List<IntPtr> FindTaskbars()
    {
        var list = new List<IntPtr>();
        IntPtr main = User32.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);
        IntPtr after = IntPtr.Zero;
        for (int i = 0; i < 16; i++)
        {
            after = User32.FindWindowEx(IntPtr.Zero, after, "Shell_SecondaryTrayWnd", null);
            if (after == IntPtr.Zero) break;
            list.Add(after);
        }
        return list;
    }

    /// <summary>숨겨 둔 상태면 복원 (어느 스레드에서나).</summary>
    private static void RestoreTaskbar(string reason)
    {
        if (!_taskbarHidden) return;
        _taskbarHidden = false;
        ApplyTaskbarVisibility(hidden: false);
        Log.Info($"윈도우 작업 표시줄 복원 ({reason})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreTaskbar("Dispose");
        RemoveMouseHook();
        _globalMouseDown = null;
        _fullscreenTimer.Stop();
        _wallpaperPoll.Dispose();
        foreach (var s in _slots.ToList()) RemoveSlot(s);
        try { _broadcastWindow.RemoveHook(BroadcastWndProc); _broadcastWindow.Dispose(); } catch { }
    }
}
