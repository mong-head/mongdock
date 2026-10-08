using System.Globalization;
using Microsoft.Win32;
using Mongdock.Native;
using Windows.Devices.Power;
using Windows.System.Power;

namespace Mongdock.Services;

/// <summary>
/// 배터리: GetSystemPowerStatus(퍼센트·전원·충전 플래그·절전 모드·남은 시간) + WinRT AggregateBattery(충전 속도 → 완충까지 시간).
/// 갱신: 30초 폴링 + 전원 변경(SystemEvents.PowerModeChanged) + 배터리 보고서 변경 + 절전 모드 변경 이벤트.
/// 배터리 없는 PC 는 Battery = null.
/// 디버그: 환경 변수 MONGDOCK_FAKE_BATTERY="57,charging" 같은 가짜 값을 쓰면 실제 값 대신 사용 (없으면 무시, Release 에서도 무해).
///   토큰: 숫자(퍼센트) · charging · ac(연결·충전 안 함) · full · saver · notime · none(배터리 없음)
/// </summary>
public sealed partial class StatusService
{
    private const string FakeBatteryVariable = "MONGDOCK_FAKE_BATTERY";

    private BatteryInfo? _battery;
    private Timer? _batteryTimer;
    private bool _batteryEventsHooked;
    private Battery? _aggregate;
    private int _batteryPolling;

    public BatteryInfo? Battery { get { lock (_gate) return _battery; } }

    public void OpenBatterySettings() => OpenUri("ms-settings:batterysaver");
    public void OpenPowerSettings() => OpenUri("ms-settings:powersleep");

    private void StartBattery()
    {
        _batteryTimer = new Timer(_ => SafeRun(PollBattery, "배터리"), null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        if (FakeBattery(out _)) return; // 가짜 값이면 이벤트 불필요

        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _batteryEventsHooked = true;
        }
        catch (Exception ex) { Log.Warn($"전원 변경 이벤트 구독 실패: {ex.Message}"); }

        // 배터리 없는 PC 에서도 구독은 무해 (보고서가 NotPresent). 실패하면 폴링만.
        try
        {
            _aggregate = Windows.Devices.Power.Battery.AggregateBattery;
            _aggregate.ReportUpdated += OnBatteryReportUpdated;
        }
        catch (Exception ex)
        {
            _aggregate = null;
            Log.Warn($"배터리 보고서 구독 실패: {ex.Message}");
        }
        try { PowerManager.EnergySaverStatusChanged += OnEnergySaverChanged; }
        catch (Exception ex) { Log.Warn($"절전 모드 이벤트 구독 실패: {ex.Message}"); }
    }

    private void StopBattery()
    {
        _batteryTimer?.Dispose();
        _batteryTimer = null;
        if (_batteryEventsHooked)
        {
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            _batteryEventsHooked = false;
            try { PowerManager.EnergySaverStatusChanged -= OnEnergySaverChanged; } catch { }
        }
        if (_aggregate is not null)
        {
            try { _aggregate.ReportUpdated -= OnBatteryReportUpdated; } catch { }
            _aggregate = null;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) QueueBatteryPoll();
    }

    private void OnBatteryReportUpdated(Battery sender, object args) => QueueBatteryPoll();
    private void OnEnergySaverChanged(object? sender, object e) => QueueBatteryPoll();

    private void QueueBatteryPoll() => ThreadPool.QueueUserWorkItem(_ => SafeRun(PollBattery, "배터리"));

    private void PollBattery()
    {
        if (Interlocked.Exchange(ref _batteryPolling, 1) == 1) return;
        try
        {
            var info = FakeBattery(out var fake) ? fake : ReadBattery();
            Update(() =>
            {
                if (Equals(_battery, info)) return false;
                _battery = info;
                return true;
            });
        }
        finally
        {
            Interlocked.Exchange(ref _batteryPolling, 0);
        }
    }

    private static BatteryInfo? ReadBattery()
    {
        if (!PowerApi.GetSystemPowerStatus(out var ps)) return null;
        if ((ps.BatteryFlag & PowerApi.BATTERY_FLAG_NO_BATTERY) != 0 && ps.BatteryFlag != PowerApi.BATTERY_FLAG_UNKNOWN) return null;

        BatteryReport? report = null;
        try { report = Windows.Devices.Power.Battery.AggregateBattery.GetReport(); }
        catch { }
        if (ps.BatteryFlag == PowerApi.BATTERY_FLAG_UNKNOWN && (report is null || report.Status == BatteryStatus.NotPresent)) return null;
        if (report?.Status == BatteryStatus.NotPresent && ps.BatteryLifePercent == 255) return null;

        int? full = report?.FullChargeCapacityInMilliwattHours;
        int? remaining = report?.RemainingCapacityInMilliwattHours;
        int percent;
        if (ps.BatteryLifePercent <= 100) percent = ps.BatteryLifePercent;
        else if (full is > 0 && remaining is >= 0) percent = (int)Math.Round(100.0 * remaining.Value / full.Value);
        else return null;
        percent = Math.Clamp(percent, 0, 100);

        bool connected = ps.ACLineStatus == 1;
        bool charging = (ps.BatteryFlag != PowerApi.BATTERY_FLAG_UNKNOWN && (ps.BatteryFlag & PowerApi.BATTERY_FLAG_CHARGING) != 0)
                        || report?.Status == BatteryStatus.Charging;
        BatteryCharge charge = charging ? BatteryCharge.Charging
            : !connected ? BatteryCharge.Discharging
            : percent >= 98 ? BatteryCharge.Full
            : BatteryCharge.NotCharging;
        if (charge == BatteryCharge.Charging) connected = true;

        TimeSpan? toEmpty = null, toFull = null;
        if (charge == BatteryCharge.Discharging && ps.BatteryLifeTime > 0)
            toEmpty = RoundMinutes(TimeSpan.FromSeconds(ps.BatteryLifeTime));
        if (charge == BatteryCharge.Charging && full is > 0 && remaining is >= 0 && report?.ChargeRateInMilliwatts is > 0 and var rate)
        {
            double hours = Math.Max(0, full.Value - remaining.Value) / (double)rate;
            if (hours > 0 && hours < 48) toFull = RoundMinutes(TimeSpan.FromHours(hours));
        }

        return new BatteryInfo(percent, connected, charge, ps.SystemStatusFlag == 1, toEmpty, toFull);
    }

    private static TimeSpan RoundMinutes(TimeSpan t) => TimeSpan.FromMinutes(Math.Max(1, Math.Round(t.TotalMinutes)));

    /// <summary>MONGDOCK_FAKE_BATTERY 가 있으면 true 와 가짜 값 (none 이면 null = 배터리 없음).</summary>
    internal static bool FakeBattery(out BatteryInfo? info) => TryParseFakeBattery(Environment.GetEnvironmentVariable(FakeBatteryVariable), out info);

    internal static bool TryParseFakeBattery(string? spec, out BatteryInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        int percent = 57;
        bool charging = false, ac = false, full = false, saver = false, noTime = false;
        foreach (var raw in spec.Split(',', ';', ' '))
        {
            var t = raw.Trim().ToLowerInvariant();
            if (t.Length == 0) continue;
            if (int.TryParse(t.TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)) percent = Math.Clamp(p, 0, 100);
            else if (t == "none") return true;
            else if (t == "charging") charging = true;
            else if (t is "ac" or "plugged") ac = true;
            else if (t == "full") full = true;
            else if (t == "saver") saver = true;
            else if (t == "notime") noTime = true;
        }
        var charge = charging ? BatteryCharge.Charging : full ? BatteryCharge.Full : ac ? BatteryCharge.NotCharging : BatteryCharge.Discharging;
        TimeSpan? toEmpty = !noTime && charge == BatteryCharge.Discharging ? TimeSpan.FromMinutes(Math.Max(1, percent * 4)) : null;
        TimeSpan? toFull = !noTime && charge == BatteryCharge.Charging ? TimeSpan.FromMinutes(Math.Max(1, 100 - percent)) : null;
        info = new BatteryInfo(percent, charge != BatteryCharge.Discharging, charge, saver, toEmpty, toFull);
        return true;
    }
}
