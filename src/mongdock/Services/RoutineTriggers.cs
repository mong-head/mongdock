using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Native;
using Mongdock.ViewModels;
using Mongdock.Views;

namespace Mongdock.Services;

/// <summary>
/// 루틴 "▸ 더 보기" 동작 (spec-routines §14) — 모두 이 PC 안에서만 판정, UI 스레드.
/// - 시작 조건: 켜면(시작 10초 뒤) · 모니터 연결 · 오디오 장치 연결 · 시간·요일(묻기만) · 이 앱을 켜면 → 묻기 배너 "…루틴을 열까요? [열기] [오늘은 안 함]" + 작은 "다시 묻지 않기".
///   같은 날 같은 조건은 한 번만. 실행 중이면 안 물음. 전체 화면·방해 금지 중이면 끝난 뒤(30분 안) 물음. "묻지 않고 바로 열기"는 시간 조건 빼고.
/// - 끝 조건: 오디오 장치를 빼면·시간 → "…루틴을 끝낼까요? [끝내기] [계속]", 루틴으로 연 앱을 다 닫으면 → 묻지 않고 끝(데스크톱 닫기·되돌리기).
/// - 함께 바꿀 것: 방해 금지·독 자동 숨김은 루틴 데스크톱에 있는 동안만(저장하지 않는 덮어쓰기), 소리 장치·볼륨은 루틴 전체 — 열기 직전 값을 settings 에 두고 끝내면 되돌림
///   (도중에 사용자가 바꿨거나 다른 실행 중 루틴이 같은 항목을 바꿨으면 되돌리지 않음).
/// - 비슷하게 열면 물어보기: 한 데스크톱에 루틴 앱이 70% 이상·2개 이상이면 "…루틴인가요? [맞아요] [아니요]".
/// - 머문 시간: 루틴 데스크톱을 보는 동안만, 잠금·5분 무입력이면 멈춤 → 상단바 "업무 시작 · 2/3 · 1:24".
/// </summary>
internal static class RoutineTriggers
{
    private static AppServices? _s;
    private static Dispatcher? _d;
    private static readonly HashSet<string> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _audio = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<IntPtr> _knownWindows = new();
    private static readonly HashSet<Guid> _declinedDesktops = new();
    private static readonly List<(string Key, RoutineDef Routine, int Index, DateTime Since)> _pending = new();
    private static readonly HashSet<string> _askedEnd = new();
    private static bool _locked;
    private static DispatcherTimer? _similarTimer;

    /// <summary>방해 금지 (몽독 배너) — 지금 데스크톱이 이 기능을 켠 실행 중 루틴의 데스크톱일 때.</summary>
    public static bool DndActive { get; private set; }
    /// <summary>독 자동 숨김 강제 — 같은 규칙.</summary>
    public static bool DockHideActive { get; private set; }
    private static HashSet<string> _dndExceptions = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>방해 금지·독 숨김이 바뀜 (독이 모양을 다시 잡음).</summary>
    public static event Action? FocusChanged;
    /// <summary>1초마다 (상단바 머문 시간).</summary>
    public static event Action? Tick;

    public static void Init(AppServices services)
    {
        _s = services;
        _d = Dispatcher.CurrentDispatcher;
        foreach (var m in Monitors.GetAll()) _monitors.Add(m.DeviceName);
        foreach (var a in services.Status.OutputDevices) _audio.Add(a.Id);
        foreach (var w in services.Windows.Windows) _knownWindows.Add(w.Hwnd);
        services.DesktopWindows.DisplayChanged += (_, _) => Post(OnDisplays);
        services.Status.Changed += (_, _) => Post(OnAudio);
        services.Windows.WindowsChanged += (_, _) => Post(OnWindows);
        services.VirtualDesktops.Changed += (_, _) => Post(UpdateFocus);
        RoutineService.Changed += UpdateFocus;
        RoutineService.Started += OnStarted;
        RoutineService.Ended += OnEnded;
        RoutineService.AllClosed += OnAllClosed;
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) => _locked = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect;

        // 컴퓨터를 켜면: 몽독 시작 10초 뒤 (로그인 직후 시작이면 그게 "켜면")
        var login = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        login.Tick += (_, _) =>
        {
            login.Stop();
            foreach (var (r, i, s) in Starts(RoutineStartKind.Login)) Fire(r, i, s);
            CheckMissedTimes();
        };
        login.Start();
        var minute = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        minute.Tick += (_, _) => OnClock();
        minute.Start();
        var second = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        second.Tick += (_, _) => OnSecond();
        second.Start();
    }

    private static void Post(Action a) => _d?.BeginInvoke(() =>
    {
        try { a(); }
        catch (Exception ex) { Log.Error("루틴 조건 처리 실패", ex); }
    });

    private static IEnumerable<RoutineDef> Routines => _s?.Settings.Current.Routines ?? Enumerable.Empty<RoutineDef>();

    private static IEnumerable<(RoutineDef R, int I, RoutineStart S)> Starts(RoutineStartKind kind) =>
        Routines.Where(r => r.More is not null && r.Items.Count > 0)
            .SelectMany(r => r.More!.Start.Select((s, i) => (r, i, s)))
            .Where(x => x.s.Kind == kind)
            .ToList();

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ───────────────────────── 시작 조건 ─────────────────────────

    private static void OnDisplays()
    {
        var now = Monitors.GetAll().ToList();
        var added = now.Where(m => !_monitors.Contains(m.DeviceName)).ToList();
        _monitors.Clear();
        foreach (var m in now) _monitors.Add(m.DeviceName);
        if (added.Count == 0) return;
        foreach (var (r, i, s) in Starts(RoutineStartKind.Monitor))
            if (s.Device is null ? added.Any(m => !m.IsPrimary) : added.Any(m => m.DeviceName.Equals(s.Device, StringComparison.OrdinalIgnoreCase)))
                Fire(r, i, s);
    }

    private static void OnAudio()
    {
        var s = _s;
        if (s is null) return;
        var now = s.Status.OutputDevices.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = now.Where(id => !_audio.Contains(id)).ToList();
        var removed = _audio.Where(id => !now.Contains(id)).ToList();
        _audio.Clear();
        foreach (var id in now) _audio.Add(id);
        foreach (var (r, i, st) in Starts(RoutineStartKind.Audio))
            if (st.Device is not null && added.Contains(st.Device, StringComparer.OrdinalIgnoreCase)) Fire(r, i, st);
        // 끝 조건: 오디오 장치를 빼면
        foreach (var r in Routines.Where(r => r.More?.End.AudioRemoved is { } dev && removed.Contains(dev, StringComparer.OrdinalIgnoreCase) && RoutineService.IsRunning(r.Id)))
            AskEnd(r, Loc.F($"{r.More!.End.AudioName ?? Loc.T("오디오 장치")} 연결이 끊겼어요."));
    }

    private static void OnWindows()
    {
        var s = _s;
        if (s is null) return;
        var windows = s.Windows.Windows;
        var fresh = windows.Where(w => !_knownWindows.Contains(w.Hwnd)).ToList();
        _knownWindows.Clear();
        foreach (var w in windows) _knownWindows.Add(w.Hwnd);
        if (fresh.Count > 0)
            foreach (var (r, i, st) in Starts(RoutineStartKind.App))
                if (st.App is { Length: > 0 } app && fresh.Any(w => AppKeyMatches(app, w))
                    && windows.Count(w => AppKeyMatches(app, w)) == fresh.Count(w => AppKeyMatches(app, w))) // 그 앱이 막 켜짐 (원래 떠 있던 앱의 새 창은 아님)
                    Fire(r, i, st);
        // 비슷하게 열면 물어보기: 창이 잠잠해진 2초 뒤에 한 번
        if (Routines.Any(r => r.More?.AskSimilar == true))
        {
            _similarTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _similarTimer.Stop();
            _similarTimer.Tick -= OnSimilarTick;
            _similarTimer.Tick += OnSimilarTick;
            _similarTimer.Start();
        }
    }

    private static bool AppKeyMatches(string key, AppWindowInfo w) =>
        key.Contains('!') ? string.Equals(w.Aumid, key, StringComparison.OrdinalIgnoreCase)
            : System.IO.Path.GetFileNameWithoutExtension(w.ProcessPath).Equals(key, StringComparison.OrdinalIgnoreCase);

    private static void OnClock()
    {
        var now = DateTime.Now;
        string hm = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        foreach (var (r, i, s) in Starts(RoutineStartKind.Time))
            if (s.Time == hm && DayOk(s, now)) Fire(r, i, s);
        // 끝 조건: 시간
        foreach (var r in Routines.Where(r => r.More?.End.Time == hm && RoutineService.IsRunning(r.Id)))
            if (_askedEnd.Add($"{r.Id}|time|{Today}")) AskEnd(r, Loc.F($"{hm} 이 됐어요."));
        RetryPending();
    }

    private static bool DayOk(RoutineStart s, DateTime now) => s.Days is not { Count: > 0 } days || days.Contains((int)now.DayOfWeek);

    /// <summary>그 시각에 몽독이 꺼져 있었으면, 켠 뒤 30분 안일 때만 한 번 묻기.</summary>
    private static void CheckMissedTimes()
    {
        var now = DateTime.Now;
        foreach (var (r, i, s) in Starts(RoutineStartKind.Time))
        {
            if (!DayOk(s, now) || !TimeSpan.TryParse(s.Time, CultureInfo.InvariantCulture, out var t)) continue;
            var at = now.Date + t;
            if (now >= at && now - at <= TimeSpan.FromMinutes(30)) Fire(r, i, s);
        }
    }

    /// <summary>조건이 맞음 → (같은 날 한 번만) 묻기 배너 또는 바로 열기. 전체 화면·방해 금지 중이면 미뤄 둠(30분).</summary>
    private static void Fire(RoutineDef routine, int index, RoutineStart start)
    {
        var s = _s;
        if (s is null || RoutineService.IsRunning(routine.Id) || RoutineService.IsOpening(routine.Id)) return;
        string key = $"{routine.Id}|{index}|{start.Kind}";
        if (s.Settings.Current.RoutineAsked.TryGetValue(key, out var day) && day == Today) return;
        if (Busy())
        {
            if (_pending.All(p => p.Key != key)) _pending.Add((key, routine, index, DateTime.Now));
            Log.Info("루틴 시작 조건: 전체 화면·방해 금지라 나중에 물음");
            return;
        }
        s.Settings.Current.RoutineAsked[key] = Today;
        foreach (var k in s.Settings.Current.RoutineAsked.Where(kv => kv.Value != Today).Select(kv => kv.Key).ToList()) s.Settings.Current.RoutineAsked.Remove(k);
        s.Settings.Save();
        Log.Info($"루틴 시작 조건 맞음: {start.Kind}");
        if (routine.More!.AutoOpen && start.Kind != RoutineStartKind.Time)
        {
            RoutineUi.Run(s, routine);
            return;
        }
        var buttons = new List<NotificationBannerWindow.BannerButton>
        {
            new(Loc.T("열기"), () => RoutineUi.Run(s, routine), Primary: true),
            new(Loc.T("오늘은 안 함"), () => { }),
            new(Loc.T("다시 묻지 않기"), () => StopAsking(routine, start), Small: true),
        };
        Ask(routine, Loc.F($"{routine.Name} 루틴을 열까요?"), Describe(start), buttons);
    }

    private static bool Busy()
    {
        var s = _s!;
        return DndActive || Monitors.GetAll().Any(m => s.DesktopWindows.IsFullscreenOn(m.IsPrimary ? "" : m.DeviceName));
    }

    private static void RetryPending()
    {
        if (_pending.Count == 0) return;
        _pending.RemoveAll(p => DateTime.Now - p.Since > TimeSpan.FromMinutes(30)); // 30분 지나면 안 물음
        if (Busy()) return;
        var list = _pending.ToList();
        _pending.Clear();
        foreach (var p in list)
            if (p.Routine.More is { } m && p.Index < m.Start.Count) Fire(p.Routine, p.Index, m.Start[p.Index]);
    }

    /// <summary>배너 "다시 묻지 않기": 그 시작 조건을 끔.</summary>
    private static void StopAsking(RoutineDef routine, RoutineStart start)
    {
        routine.More?.Start.Remove(start);
        _s?.Settings.Save();
        Log.Info("루틴 시작 조건 끔 (다시 묻지 않기)");
    }

    private static void Ask(RoutineDef routine, string title, string body, List<NotificationBannerWindow.BannerButton> buttons)
    {
        var s = _s!;
        var item = new NotificationItem(0, "", AppInfo.Name, title, new[] { body }, DateTime.Now, null, null, false, null);
        System.Windows.Media.ImageSource? icon = null;
        try { icon = RoutineIcons.Icon(s, routine, s.Settings.Current.Dock.IconStyle); } catch { /* 아이콘 없이 */ }
        if (!NotificationBannerWindow.ShowQuestion(item, icon, buttons, TimeSpan.FromSeconds(10), () => { }))
            Log.Info("루틴 묻기 배너를 띄우지 못함 (일시 정지·전체 화면)");
    }

    // ───────────────────────── 끝 조건 ─────────────────────────

    private static void AskEnd(RoutineDef routine, string why)
    {
        var s = _s!;
        Log.Info("루틴 끝 조건 맞음 — 끝낼지 물음");
        Ask(routine, Loc.F($"{routine.Name} 루틴을 끝낼까요?"), why, new List<NotificationBannerWindow.BannerButton>
        {
            new(Loc.T("끝내기"), () => RoutineService.End(routine, RoutineService.HasOwnDesktop(routine.Id)), Primary: true),
            new(Loc.T("계속"), () => { }),
        });
    }

    private static void OnAllClosed(string id)
    {
        var s = _s;
        if (s is null || RoutineUi.Find(s, id) is not { More.End.AllAppsClosed: true } routine) return;
        Log.Info("루틴 끝 조건: 루틴으로 연 앱을 다 닫음 — 끝냄");
        RoutineService.End(routine, closeDesktop: true);
    }

    // ───────────────────────── 함께 바꿀 것 ─────────────────────────

    private static void OnStarted(RoutineDef routine)
    {
        var s = _s;
        if (s is null || routine.More?.Change is not { } c) { UpdateFocus(); return; }
        if (c.OutputDevice is null && c.Volume is null) { UpdateFocus(); return; }
        var restores = s.Settings.Current.RoutineRestores;
        var rec = restores.FirstOrDefault(x => x.RoutineId == routine.Id);
        if (rec is null)
        {
            rec = new RoutineRestore { RoutineId = routine.Id, At = DateTime.Now };
            rec.OutputDevice = s.Status.OutputDevices.FirstOrDefault(d => d.IsDefault)?.Id;
            rec.Volume = (int)Math.Round(s.Status.Volume * 100);
            restores.Add(rec);
        }
        bool failed = false;
        if (c.OutputDevice is { } dev)
        {
            if (s.Status.OutputDevices.Any(d => d.Id == dev) && s.Status.SetDefaultOutput(dev)) rec.AppliedDevice = dev;
            else failed = true;
        }
        if (c.Volume is int v)
        {
            // 장치를 바꿨으면 새 장치에 걸리게 잠깐 뒤에
            Post(() => { s.Status.SetVolume(v / 100.0); });
            rec.AppliedVolume = v;
        }
        s.Settings.Save();
        if (failed)
        {
            Log.Warn("루틴: 소리 출력 장치를 바꾸지 못함");
            Notify(Loc.T("소리 장치는 바꾸지 못했어요"));
        }
        UpdateFocus();
    }

    private static void OnEnded(RoutineDef routine)
    {
        Restore(routine.Id);
        UpdateFocus();
    }

    /// <summary>열기 직전 값으로 되돌림 — 사용자가 도중에 바꿨거나 다른 실행 중 루틴이 같은 항목을 바꿨으면 그 항목은 그대로.</summary>
    public static void Restore(string routineId)
    {
        var s = _s;
        if (s is null) return;
        var restores = s.Settings.Current.RoutineRestores;
        var rec = restores.FirstOrDefault(x => x.RoutineId == routineId);
        if (rec is null) return;
        restores.Remove(rec);
        var others = RoutineService.RunningIds().Where(id => id != routineId).Select(id => RoutineUi.Find(s, id)?.More?.Change).OfType<RoutineChange>().ToList();
        if (rec.AppliedDevice is not null && rec.OutputDevice is { } back && !others.Any(o => o.OutputDevice is not null))
        {
            string? now = s.Status.OutputDevices.FirstOrDefault(d => d.IsDefault)?.Id;
            if (now == rec.AppliedDevice && s.Status.OutputDevices.Any(d => d.Id == back))
            {
                if (!s.Status.SetDefaultOutput(back)) Notify(Loc.T("소리 장치는 되돌리지 못했어요"));
            }
            else Log.Info("루틴 되돌리기: 소리 장치는 그새 바뀌어 그대로");
        }
        if (rec.AppliedVolume is int applied && rec.Volume is int vol && !others.Any(o => o.Volume is not null))
        {
            if (Math.Abs(s.Status.Volume * 100 - applied) <= 1.5) s.Status.SetVolume(vol / 100.0);
            else Log.Info("루틴 되돌리기: 볼륨은 그새 바뀌어 그대로");
        }
        s.Settings.Save();
        Log.Info("루틴 되돌리기 끝");
    }

    /// <summary>되돌릴 값이 남아 있음 (몽독을 다시 시작해 창 목록을 잊었어도 "설정만 되돌리기").</summary>
    public static bool HasRestore(string routineId) => _s?.Settings.Current.RoutineRestores.Any(x => x.RoutineId == routineId) == true;

    /// <summary>방해 금지·독 숨김: 지금 데스크톱이 그 기능을 켠 실행 중 루틴의 데스크톱일 때만.</summary>
    private static void UpdateFocus()
    {
        var s = _s;
        if (s is null) return;
        var ids = VirtualDesktopService.ReadDesktopIds();
        int cur = VirtualDesktopService.Read().Current;
        Guid? here = cur > 0 && cur <= ids.Count ? ids[cur - 1] : null;
        bool dnd = false, hide = false;
        var exceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in RoutineService.RunningIds())
        {
            if (RoutineUi.Find(s, id) is not { More: { } m }) continue;
            var desk = RoutineService.DesktopOf(id);
            bool onIt = desk is null ? ids.Count <= 1 : desk == here;
            if (!onIt) continue;
            dnd |= m.Change.Dnd;
            hide |= m.Change.DockHide;
            if (m.Change.Dnd) foreach (var e in m.DndExceptions) exceptions.Add(e);
        }
        _dndExceptions = exceptions;
        if (dnd == DndActive && hide == DockHideActive) return;
        DndActive = dnd;
        DockHideActive = hide;
        Log.Info($"루틴 데스크톱: 방해 금지 {(dnd ? "켬" : "끔")}, 독 숨김 {(hide ? "켬" : "끔")}");
        FocusChanged?.Invoke();
        if (!dnd) RetryPending();
    }

    /// <summary>방해 금지 중이어도 띄울 알림인지 (예외 앱).</summary>
    public static bool AllowedDuringDnd(string? aumid, string? appName)
    {
        if (!DndActive) return true;
        if (aumid is { Length: > 0 } a && _dndExceptions.Contains(a)) return true;
        string? exe = aumid?.Split('\\', '/').LastOrDefault();
        if (exe is not null && _dndExceptions.Contains(System.IO.Path.GetFileNameWithoutExtension(exe))) return true;
        return appName is { Length: > 0 } n && _dndExceptions.Any(e => n.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    // ───────────────────────── 비슷하게 열면 물어보기 ─────────────────────────

    private static void OnSimilarTick(object? sender, EventArgs e)
    {
        _similarTimer?.Stop();
        var s = _s;
        if (s is null) return;
        var ids = VirtualDesktopService.ReadDesktopIds();
        if (ids.Count == 0) return;
        var windows = s.Windows.Windows.ToList();
        foreach (var r in Routines.Where(r => r.More?.AskSimilar == true && !RoutineService.IsRunning(r.Id) && !RoutineService.IsOpening(r.Id)))
        {
            var apps = r.Items.Where(i => i.Kind == RoutineItemKind.App).ToList();
            if (apps.Count < 2) continue;
            int need = Math.Max(2, (int)Math.Ceiling(apps.Count * 0.7));
            var byDesktop = new Dictionary<Guid, List<IntPtr>>();
            var counted = new Dictionary<Guid, int>();
            foreach (var item in apps)
            {
                var seen = new HashSet<Guid>();
                foreach (var w in windows.Where(w => RoutineService.IsItemWindow(item, w)))
                {
                    int index = VirtualDesktopHelper.GetDesktopIndex(w.Hwnd, ids);
                    if (index <= 0) continue;
                    var g = ids[index - 1];
                    (byDesktop.TryGetValue(g, out var l) ? l : byDesktop[g] = new List<IntPtr>()).Add(w.Hwnd);
                    if (seen.Add(g)) counted[g] = counted.GetValueOrDefault(g) + 1;
                }
            }
            foreach (var (g, n) in counted)
            {
                if (n < need || _declinedDesktops.Contains(g) || RoutineService.RunningIds().Any(id => RoutineService.DesktopOf(id) == g)) continue;
                var desk = g;
                var hwnds = byDesktop[g];
                _declinedDesktops.Add(g); // 이번 실행 동안 같은 데스크톱은 한 번만 물음
                Log.Info($"비슷하게 열림: 루틴 앱 {n}/{apps.Count}개");
                Ask(r, Loc.F($"{r.Name} 루틴인가요?"), Loc.T("맞으면 함께 바꿀 것을 적용하고 끝내기도 쓸 수 있어요."), new List<NotificationBannerWindow.BannerButton>
                {
                    new(Loc.T("맞아요"), () => RoutineService.Adopt(r, desk, hwnds.Where(User32.IsWindow)), Primary: true),
                    new(Loc.T("아니요"), () => { }),
                });
                return; // 한 번에 하나만 물음
            }
        }
    }

    // ───────────────────────── 머문 시간 ─────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private static TimeSpan Idle()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }

    private static void OnSecond()
    {
        var s = _s;
        if (s is null) return;
        bool any = false;
        var ids = VirtualDesktopService.ReadDesktopIds();
        int cur = VirtualDesktopService.Read().Current;
        Guid? here = cur > 0 && cur <= ids.Count ? ids[cur - 1] : null;
        bool counting = !_locked && Idle() < TimeSpan.FromMinutes(5);
        foreach (var id in RoutineService.RunningIds())
        {
            if (RoutineUi.Find(s, id) is not { More.ShowTime: true }) continue;
            any = true;
            if (counting && RoutineService.DesktopOf(id) is { } g && g == here) RoutineService.AddSeconds(id, 1);
        }
        if (any) Tick?.Invoke();
    }

    /// <summary>상단바 데스크톱 표시 꼬리: " · 1:24" (머문 시간) + " ☾"(방해 금지) — 그 데스크톱이 실행 중 루틴의 것일 때.</summary>
    public static string DesktopSuffix(string routineId)
    {
        var s = _s;
        if (s is null || RoutineUi.Find(s, routineId) is not { } r) return "";
        string text = "";
        if (r.More?.ShowTime == true)
        {
            var t = TimeSpan.FromSeconds(RoutineService.AddSeconds(routineId, 0));
            text += t.TotalHours >= 1 ? $" · {(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $" · {t.Minutes}:{t.Seconds:00}";
        }
        if (r.More?.Change.Dnd == true && DndActive) text += " ☾";
        return text;
    }

    // ───────────────────────── 글 ─────────────────────────

    public static string Describe(RoutineStart s) => s.Kind switch
    {
        RoutineStartKind.Login => Loc.T("컴퓨터를 켜면"),
        RoutineStartKind.Monitor => s.Device is null ? Loc.T("외부 모니터를 연결하면") : Loc.F($"{s.Name ?? s.Device}를 연결하면"),
        RoutineStartKind.Audio => Loc.F($"{s.Name ?? Loc.T("오디오 장치")}를 연결하면"),
        RoutineStartKind.Time => Loc.F($"{DaysText(s.Days)} {s.Time}"),
        RoutineStartKind.App => Loc.F($"{s.Name ?? s.App}을(를) 켜면"),
        _ => "",
    };

    private static string DaysText(List<int>? days)
    {
        if (days is not { Count: > 0 } || days.Count == 7) return Loc.T("매일");
        if (days.Count == 5 && days.All(d => d is >= 1 and <= 5)) return Loc.T("평일");
        if (days.Count == 2 && days.Contains(0) && days.Contains(6)) return Loc.T("주말");
        var names = new[] { Loc.T("일"), Loc.T("월"), Loc.T("화"), Loc.T("수"), Loc.T("목"), Loc.T("금"), Loc.T("토") };
        return string.Join("·", days.OrderBy(d => d).Select(d => names[Math.Clamp(d, 0, 6)]));
    }

    /// <summary>예외 앱 키(AUMID·exe) → 보이는 이름 (지금 떠 있는 창에서, 없으면 키).</summary>
    public static string AppLabel(AppServices services, string key)
    {
        var w = services.Windows.Windows.FirstOrDefault(w => AppKeyMatches(key, w) || string.Equals(w.Aumid, key, StringComparison.OrdinalIgnoreCase));
        return w is not null ? AppNames.Get(w) : key.Contains('!') ? key[(key.IndexOf('!') + 1)..] : key;
    }

    private static void Notify(string text) => _d?.BeginInvoke(() =>
    {
        try { NotificationBannerWindow.ShowCustom(new NotificationItem(0, "", AppInfo.Name, Loc.T("루틴"), new[] { text }, DateTime.Now, null, null, false, null), null, () => { }); }
        catch (Exception ex) { Log.Warn($"루틴 알림 실패: {ex.GetType().Name}"); }
    });
}
