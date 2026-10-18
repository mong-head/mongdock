using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 절전·잠금·모니터 바뀜 뒤 자동 숨김 독이 안 나타나던 것 (#35, 사용자 신고: 「독 보이기」를 두 번 눌러야 보임 — 창을 새로 만들면 풀리는 상태가 남음).
/// - 깨어남(PowerModeChanged Resume)·잠금 해제(SessionSwitch Unlock): 2초·8초 뒤 두 번 다시 맞춤 — 화면 크기·모니터 다시 읽기, 전체 화면 판정 다시 보기, 자동 숨김이면 숨김+트리거 대기로.
/// - 5초마다 스스로 확인: ① 독이 「전체 화면」으로 숨어 있는데 지금 그 모니터에 전체 화면 앱이 없음 ② 자동 숨김인데 커서 확인 타이머가 멈춤 → 다시 맞춤.
/// 고친 때마다 로그를 남겨 실제로 어떤 상태가 남았는지 알 수 있게.
/// </summary>
public partial class DockWindow
{
    private DispatcherTimer? _healTimer;
    private int _fullscreenStaleTicks;
    private long _dpiHealAt = long.MinValue / 2;

    private void StartHeal()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChangedForHeal;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitchForHeal;
        _healTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _healTimer.Tick += (_, _) => Heal("5초 확인", quietIfFine: true);
        _healTimer.Start();
    }

    private void StopHeal()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChangedForHeal;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitchForHeal;
        _healTimer?.Stop();
    }

    // SystemEvents 는 다른 스레드에서 올 수 있음
    private void OnPowerModeChangedForHeal(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) Dispatcher.BeginInvoke(() => HealSoon("절전에서 깨어남"));
    }

    private void OnSessionSwitchForHeal(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            Dispatcher.BeginInvoke(() => HealSoon($"세션 {e.Reason}"));
    }

    /// <summary>깨어난 직후엔 모니터·탐색기가 아직 자리를 잡는 중 → 2초·8초 뒤 두 번.</summary>
    private void HealSoon(string why)
    {
        Log.Info($"독: {why} → 2초·8초 뒤 다시 맞춤");
        foreach (int s in new[] { 2, 8 })
        {
            var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(s) };
            t.Tick += (_, _) => { t.Stop(); Heal($"{why} {s}초 뒤", quietIfFine: false); };
            t.Start();
        }
    }

    /// <summary>원인을 좁히기 위한 상태 한 줄 (깨어남 때마다).</summary>
    private string Diagnose()
    {
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
        return $"숨김 {_hide:0.##}→{_hideTo:0.##}, 창 숨김 {_windowsHidden}, 보임 {IsVisible}, 불투명 {Opacity:0.##}, 맨 위 {Topmost}, 커서 확인 {_pollTimer.IsEnabled}, 전체 화면 {_fullscreen}, "
             + $"배율 창 {dpi:0.##}/모니터 {_monitor.Scale:0.##}, 모니터 {_monitor.DeviceName} {_screen}, 위치 {Left:0},{Top:0}, "
             + $"막힘 끌기 {_dragArmed}/{AnyItemDrag} 메뉴 {PanelBorder.ContextMenu?.IsOpen == true} 대화 {_dialogOpen} 창 고르기 {_picker != null} 폴더 판 {_folderPanel != null}";
    }

    /// <summary>남은 상태 고치기. quietIfFine 이면 고칠 게 없을 때 아무것도 안 함(5초 확인).</summary>
    private void Heal(string why, bool quietIfFine)
    {
        if (_closed || !IsLoaded || !DockActive) return;
        try
        {
            if (!quietIfFine) Log.Info($"독 상태 ({why}): {Diagnose()}");
            // ① 전체 화면으로 숨었는데 지금은 전체 화면 앱이 없음 (끝 알림을 놓침 — 깨어날 때 모니터 이름이 바뀌는 경우 등).
            //    5초 확인은 두 번 연속일 때만 (모니터 이름이 바뀌는 순간 전체 화면 판정이 디바운스로 잠깐 옛 이름 → 독이 전체 화면 위로 깜빡이지 않게, 리뷰)
            bool stale = _fullscreen && !_services.DesktopWindows.IsFullscreenOn(_monitor.DeviceName);
            _fullscreenStaleTicks = stale ? _fullscreenStaleTicks + 1 : 0;
            if (stale && (!quietIfFine || _fullscreenStaleTicks >= 2))
            {
                _fullscreenStaleTicks = 0;
                Log.Warn($"독: 전체 화면으로 숨은 채 남아 있었음 ({why}, 모니터 {_monitor.DeviceName}) → 다시 보이게");
                OnFullscreenChanged(this, false);
                return;
            }
            // ② 자동 숨김인데 커서 확인이 멈춤 → 가장자리에 대도 안 나타남
            if (!_fullscreen && _layout.Mode == DockMode.AutoHide && !_pollTimer.IsEnabled)
            {
                Log.Warn($"독: 자동 숨김 커서 확인이 멈춰 있었음 ({why}) → 다시 시작");
                Place();
                ApplyMode(initial: true);
                return;
            }
            // ③ 창 배율이 모니터 배율과 다름 (깨어날 때 WM_DPICHANGED 를 놓치면 좌표가 옛 배율로 계산돼 독이 화면 밖에 놓일 수 있음 — 리뷰)
            double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
            // 계속 어긋나는 환경에서 5초마다 다시 적용(흔들림)하지 않게 1분에 한 번까지
            bool dpiOff = Math.Abs(dpi - _monitor.Scale) > 0.01 && Environment.TickCount64 - _dpiHealAt > 60_000;
            if (dpiOff)
            {
                _dpiHealAt = Environment.TickCount64;
                Log.Warn($"독: 창 배율 {dpi:0.##} ≠ 모니터 배율 {_monitor.Scale:0.##} ({why}) → 설정 다시 적용");
            }
            if (quietIfFine && !dpiOff) return;
            // 깨어남·잠금 해제(또는 배율 어긋남): 「독 보이기」를 껐다 켤 때와 같은 설정 전체 다시 적용 (화면 크기·위치·자동 숨김 깨끗한 숨김 상태)
            if (!_fullscreen && (_layout.Mode != DockMode.AutoHide || _hideTo >= 1 || dpiOff))
                ApplyAll();
        }
        catch (Exception ex) { Log.Warn($"독 다시 맞추기 실패 ({why}): {ex.GetType().Name} {ex.Message}"); }
    }
}
