using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 볼륨 키를 누르면 뜨는 몽독 표시 (#21, 0.8): 오른쪽 위(알림 배너 자리)에 작은 카드 — 스피커 모양·막대·퍼센트.
/// 볼륨 키는 몽독이 받아(HotkeyController — 윈도우로 안 넘김) 직접 바꾸므로 윈도우 기본 볼륨 표시는 뜨지 않음. 설정으로 끄면 윈도우 그대로.
/// 창은 하나를 만들어 계속 다시 씀(크기 고정 — 그래픽 메모리 #14), 1.5초 동안 다시 안 누르면 사라짐. 클릭은 지나감(포커스 안 뺏음).
/// </summary>
internal sealed class VolumeOsdWindow : Window
{
    /// <summary>한 번 누를 때 바뀌는 양 (윈도우와 같은 2%).</summary>
    public const double Step = 0.02;
    private const double CardWidth = 240, CardHeight = 52, Shadow = 18;
    private const int ShowMs = 1500;

    private static VolumeOsdWindow? _instance;

    private readonly Border _card;
    private readonly TextBlock _glyph;
    private readonly Border _track;
    private readonly Border _fill;
    private readonly TextBlock _percent;
    private readonly DispatcherTimer _hideTimer;
    private bool _light;

    /// <summary>시험용: 마지막으로 보인 (볼륨, 음소거).</summary>
    internal static (double Volume, bool Muted)? LastShownForTest { get; private set; }

    /// <summary>볼륨 키 하나 처리: 볼륨을 바꾸고 표시 (UI 스레드).</summary>
    public static void Handle(AppServices services, string action)
    {
        var st = services.Status;
        try
        {
            switch (action)
            {
                case HotkeyController.VolumeMute:
                    st.SetMuted(!st.Muted);
                    break;
                case HotkeyController.VolumeUp or HotkeyController.VolumeDown:
                    bool up = action == HotkeyController.VolumeUp;
                    double v = Next(st.Volume, up);
                    if (up && st.Muted) st.SetMuted(false);
                    st.SetVolume(v);
                    break;
                default:
                    return;
            }
        }
        catch (Exception ex) { Log.Error($"볼륨 키 처리 실패 ({action})", ex); }
        Show(services, st.Volume, st.Muted);
    }

    /// <summary>다음 볼륨: 2% 칸의 다음 칸 (칸 사이에 있으면 그 방향의 가장 가까운 칸 — 0.51 올림 = 0.52, 내림 = 0.50).</summary>
    internal static double Next(double volume, bool up)
    {
        double pos = volume / Step;
        double steps = up ? Math.Floor(pos + 1e-6) + 1 : Math.Ceiling(pos - 1e-6) - 1;
        return Math.Clamp(Math.Round(steps * Step, 4), 0, 1);
    }

    /// <summary>표시를 띄우거나(이미 떠 있으면 값만) 바꿈.</summary>
    public static void Show(AppServices services, double volume, bool muted)
    {
        try
        {
            var w = _instance ??= new VolumeOsdWindow(services);
            w.Update(UiTheme.Palette(services.Settings.Current), volume, muted);
            w.Place(Monitors.FromCursor());
            if (!w.IsVisible)
            {
                w._card.Opacity = 0;
                w.Show();
                Anim.Appear(w._card, 120, fromY: -6);
            }
            else
            {
                w._card.BeginAnimation(OpacityProperty, null);
                w._card.Opacity = 1;
            }
            w._hideTimer.Stop();
            w._hideTimer.Start();
            LastShownForTest = (volume, muted);
        }
        catch (Exception ex) { Log.Warn($"볼륨 표시 실패: {ex.GetType().Name}"); }
    }

    /// <summary>표시를 바로 숨김 (설정을 끄거나 몽독을 끌 때).</summary>
    public static void HideNow()
    {
        if (_instance is { IsVisible: true } w) { w._hideTimer.Stop(); w.Hide(); }
    }

    private VolumeOsdWindow(AppServices services)
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
        Title = "mongdock Volume";
        Width = CardWidth + Shadow * 2;
        Height = CardHeight + Shadow * 2;
        SetResourceReference(FontFamilyProperty, UiFonts.Key);
        _glyph = new TextBlock { FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, Width = 24, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        _fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
        _track = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), Child = _fill };
        _percent = new TextBlock { FontSize = 13, Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(16, 0, 16, 0) };
        DockPanel.SetDock(_glyph, Dock.Left);
        DockPanel.SetDock(_percent, Dock.Right);
        row.Children.Add(_glyph);
        row.Children.Add(_percent);
        row.Children.Add(_track);
        _card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(0.75),
            Margin = new Thickness(Shadow),
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
        };
        Content = _card;
        SourceInitialized += (_, _) =>
        {
            services.DesktopWindows.MakeOverlay(this);
            // 클릭이 지나가게 (WS_EX_TRANSPARENT)
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.User32.GetWindowLong(hwnd, Native.User32.GWL_EXSTYLE);
            Native.User32.SetWindowLong(hwnd, Native.User32.GWL_EXSTYLE, ex | 0x20 /* WS_EX_TRANSPARENT */);
        };
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ShowMs) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Anim.Disappear(_card, 180, () => { if (!_hideTimer.IsEnabled) Hide(); });
        };
        _light = true;
        Update(UiTheme.Palette(services.Settings.Current), 0, false);
    }

    private void Update(UiPalette p, double volume, bool muted)
    {
        if (p.IsLight != _light || _card.Background is null)
        {
            _light = p.IsLight;
            _card.Background = p.CardBackground;
            _card.BorderBrush = p.CardBorder;
            _track.Background = p.SliderTrack;
            _fill.Background = p.AccentFill;
            _glyph.Foreground = p.Text;
            _percent.Foreground = p.SubText;
        }
        double shown = muted ? 0 : Math.Clamp(volume, 0, 1);
        _glyph.Text = muted || shown <= 0 ? "" : shown < 0.34 ? "" : shown < 0.67 ? "" : ""; // Mute / Volume1·2·3
        _percent.Text = muted ? Loc.T("음소거") : $"{Math.Round(shown * 100):0}%";
        _fill.Width = Math.Max(0, (CardWidth - 16 * 2 - 24 - 38 - 24) * shown);
        _fill.Opacity = muted ? 0 : 1;
    }

    /// <summary>모니터 작업 영역 오른쪽 위 (상단바 아래, 알림 배너 자리).</summary>
    private void Place(MonitorInfo monitor)
    {
        var work = monitor.WorkArea;
        Left = Math.Round(work.Right - Width + Shadow - 12);
        Top = Math.Round(work.Top - Shadow + 10);
    }
}
