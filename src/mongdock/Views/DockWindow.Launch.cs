using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 독에서 핀을 눌러 실행할 때의 반응.
/// - 누르자마자 실행 점을 보여 주고(IsLaunching) 설정에 따라 아이콘 튀기 / 점 깜빡임 (DockItemView 가 그림).
/// - 그 핀의 창이 WindowTracker 에 나타나면(다른 데스크톱 포함) 멈춤, 10초 안에 안 뜨면 멈추고 점도 숨김.
/// - 창을 기다리는 동안 같은 핀을 다시 눌러도 또 실행하지 않음.
/// - 실행 호출은 누름 해제·점이 먼저 그려지도록 렌더 뒤로 미루고, AppLauncher 가 별도 스레드에서 실행한다.
/// </summary>
public partial class DockWindow
{
    private const long LaunchTimeoutMs = 10_000;
    /// <summary>창을 기다리는 실행: 핀 키 → 클릭 시각(Stopwatch 타임스탬프).</summary>
    private readonly Dictionary<string, long> _launching = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _launchTimer;
    /// <summary>마지막 아이콘 클릭(MouseUp) 시각 (Stopwatch 타임스탬프, 0 = 없음).</summary>
    private long _clickTimestamp;

    private static string LaunchKey(PinItem pin) =>
        $"{pin.Kind}|{Environment.ExpandEnvironmentVariables((pin.Target ?? "").Trim().Trim('"'))}";

    /// <summary>창이 뜨는 앱인지 (폴더 핀·시작 메뉴 같은 Special 은 창 매칭이 안 되므로 반응 없이 실행만).</summary>
    private static bool ShowsLaunchFeedback(PinItem pin)
    {
        if (pin.Kind == PinKind.Aumid) return true;
        if (pin.Kind != PinKind.Exe) return false;
        try { return !Directory.Exists(Environment.ExpandEnvironmentVariables(pin.Target.Trim().Trim('"'))); }
        catch { return true; }
    }

    /// <summary>창이 없는 핀을 눌렀을 때: 반응 시작 + 실행.</summary>
    private void LaunchFromDock(DockItemViewModel item, PinItem pin)
    {
        AppUsage.Record(AllAppsCatalog.Identity(pin), _services.Settings.Current.AllApps); // ★ 줄 "자주 쓰는 앱" (이 PC 안에서만)
        string key = LaunchKey(pin);
        if (_launching.ContainsKey(key))
        {
            Log.Info($"독: '{item.Name}' 창을 기다리는 중 → 다시 누름 무시");
            return;
        }
        long click = _clickTimestamp != 0 ? _clickTimestamp : Stopwatch.GetTimestamp();
        _clickTimestamp = 0;

        // 창 목록 설정 때문에 안 보일 뿐 이미 창이 있으면(다른 데스크톱) 실행 반응 없이 실행만 (앱이 기존 창을 띄움)
        if (ShowsLaunchFeedback(pin) && !HasWindowAnywhere(pin))
        {
            _launching[key] = click;
            item.IsLaunching = true;
            _launchTimer ??= CreateLaunchTimer();
            _launchTimer.Start();
        }

        // Background 우선순위 = 이번 렌더(누름 해제·점 표시) 뒤에 실행 호출
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Log.Info($"독 클릭 → 실행 호출 +{Stopwatch.GetElapsedTime(click).TotalMilliseconds:0}ms: '{item.Name}'");
            try { _services.Launcher.Launch(pin); }
            catch (Exception ex) { Log.Error($"독 실행 실패: '{item.Name}'", ex); }
        });
    }

    private bool HasWindowAnywhere(PinItem pin)
    {
        try { return _services.Windows.Windows.Any(w => SafeMatches(pin, w)); }
        catch { return false; }
    }

    private DispatcherTimer CreateLaunchTimer()
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        t.Tick += (_, _) =>
        {
            if (_closed) { t.Stop(); return; }
            foreach (var (key, started) in _launching.ToList())
            {
                if (Stopwatch.GetElapsedTime(started).TotalMilliseconds < LaunchTimeoutMs) continue;
                _launching.Remove(key);
                Log.Info($"독 실행: 10초 안에 창이 나타나지 않아 실행 표시를 끔 ({key})");
            }
            UpdateStates();
            if (_launching.Count == 0) t.Stop();
        };
        return t;
    }

    /// <summary>UpdateStates 에서 항목별로 호출 (IsRunning 을 정한 뒤). 창이 나타났으면 실행 대기를 끝낸다.</summary>
    private void UpdateLaunching(DockItemViewModel item)
    {
        if (item.Pin == null || _launching.Count == 0)
        {
            item.IsLaunching = false;
            return;
        }
        string key = LaunchKey(item.Pin);
        if (!_launching.TryGetValue(key, out long started))
        {
            item.IsLaunching = false;
            return;
        }
        // 독에 보이는 창(설정상 현재 데스크톱만일 수 있음) 또는 다른 데스크톱에 뜬 창
        if (item.Windows.Count > 0 || HasWindowAnywhere(item.Pin))
        {
            _launching.Remove(key);
            Log.Info($"독 실행: 클릭 → 창 나타남 +{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms: '{item.Name}'");
            item.IsLaunching = false;
            return;
        }
        item.IsLaunching = true;
    }
}
