using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;
using Mongdock.Models;
using Mongdock.Native;
using Mongdock.ViewModels;

namespace Mongdock.Services;

/// <summary>
/// 루틴 실행·끝내기 (#24-A, spec-routines.md §4·§5).
/// - 실행: 데스크톱 준비(지금 / 새로 만들어 이동 — 이 루틴이 만든 데스크톱이 아직 있으면 그리로 / N번, 없으면 만들어서) →
///   항목을 순서대로 연다(간격 150ms + 기다리기). 이미 켜져 있고 "앞으로 가져오기"면 실행하지 않고 앞으로.
///   새로 뜬 창은 정한 모니터·위치로 옮기고(최대 15초 기다림), 1초 뒤 한 번 더 맞춘 뒤로는 손대지 않음.
/// - 이번 실행에 열린 창(핸들)을 기억 → 독 점(실행 중)·끝내기. 몽독을 다시 시작하면 잊음.
/// - 끝내기: WM_CLOSE 만 (강제 종료 없음), 실행 전부터 떠 있던 창은 닫지 않음. 데스크톱 닫기는 창이 다 닫힌 뒤에만.
/// </summary>
internal static class RoutineService
{
    private const int GapMs = 150, FindTimeoutMs = 15_000, ReplaceAfterMs = 1000;

    private sealed class RunState
    {
        public readonly List<IntPtr> Windows = new();
        public Guid? Desktop;
        public bool CreatedDesktop;
    }

    private static AppServices? _services;
    private static readonly Dictionary<string, RunState> Runs = new();
    private static Dispatcher? _dispatcher;

    /// <summary>실행 상태가 바뀜 (창이 열림·닫힘) — 독·판이 점을 다시 그림. UI 스레드.</summary>
    public static event Action? Changed;

    public static void Init(AppServices services)
    {
        _services = services;
        _dispatcher = Dispatcher.CurrentDispatcher;
        services.Windows.WindowsChanged += (_, _) =>
        {
            bool any = false;
            lock (Runs)
                foreach (var r in Runs.Values)
                    any |= r.Windows.RemoveAll(h => !User32.IsWindow(h)) > 0;
            if (any) RaiseChanged();
        };
    }

    private static void RaiseChanged() => _dispatcher?.BeginInvoke(() =>
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Log.Error("루틴 상태 갱신 실패", ex); }
    });

    /// <summary>이번 실행에 연 창이 하나라도 살아 있음.</summary>
    public static bool IsRunning(string id)
    {
        lock (Runs) return Runs.TryGetValue(id, out var r) && r.Windows.Any(User32.IsWindow);
    }

    public static int OpenWindowCount(string id)
    {
        lock (Runs) return Runs.TryGetValue(id, out var r) ? r.Windows.Count(User32.IsWindow) : 0;
    }

    /// <summary>몽독을 켠 뒤 이 루틴을 실행한 적 있음 (없으면 끝내기를 회색으로 — 다시 시작하면 창 목록을 잊음).</summary>
    public static bool HasRunState(string id)
    {
        lock (Runs) return Runs.ContainsKey(id);
    }

    /// <summary>데스크톱 번호(1부터)가 실행 중인 루틴이 만든 데스크톱이면 그 루틴 이름 (상단바 데스크톱 표시용). 아니면 null.</summary>
    public static string? DesktopRoutineName(int index)
    {
        var services = _services;
        if (services is null || index <= 0) return null;
        var ids = VirtualDesktopService.ReadDesktopIds();
        if (index > ids.Count) return null;
        var g = ids[index - 1];
        string? id;
        lock (Runs) id = Runs.FirstOrDefault(r => r.Value.CreatedDesktop && r.Value.Desktop == g).Key;
        return id is null ? null : services.Settings.Current.Routines.FirstOrDefault(r => r.Id == id)?.Name;
    }

    /// <summary>이 루틴이 새 데스크톱을 만들었고 그 데스크톱이 아직 있음 (끝내기 카드의 "데스크톱도 닫기").</summary>
    public static bool HasOwnDesktop(string id)
    {
        lock (Runs)
            return Runs.TryGetValue(id, out var r) && r.CreatedDesktop && r.Desktop is { } g && VirtualDesktopService.ReadDesktopIds().Contains(g);
    }

    // ───────────────────────── 실행 ─────────────────────────

    public static void Run(RoutineDef routine)
    {
        var services = _services;
        if (services is null || routine.Items.Count == 0) return;
        // 전체 화면(게임·영상) 중엔 데스크톱 키를 보내지 않음 — 작은 안내만
        if (Monitors.GetAll().Any(m => services.DesktopWindows.IsFullscreenOn(m.IsPrimary ? "" : m.DeviceName)))
        {
            Log.Info("루틴 실행 미룸: 전체 화면 앱");
            Notify(Loc.T("지금은 루틴을 열 수 없어요 (전체 화면)"));
            return;
        }
        services.Settings.Current.RoutineRunsSinceSignal++;
        services.Settings.Save();
        _ = RunAsync(services, routine);
    }

    private static async Task RunAsync(AppServices services, RoutineDef routine)
    {
        Log.Info($"루틴 실행: 항목 {routine.Items.Count}개, 데스크톱 {routine.Desktop.Mode}");
        RunState state;
        lock (Runs)
        {
            if (!Runs.TryGetValue(routine.Id, out state!)) Runs[routine.Id] = state = new RunState();
        }
        try
        {
            await PrepareDesktopAsync(services, routine, state);
        }
        catch (Exception ex)
        {
            Log.Error("루틴 데스크톱 준비 실패", ex);
        }

        var elsewhere = new List<(string Name, IntPtr Hwnd)>();
        foreach (var item in routine.Items.ToList())
        {
            try
            {
                // 이미 켜져 있으면 앞으로 (다른 데스크톱의 창은 옮기지 않음 — 한 번 알림)
                if (item.IfRunning == RoutineIfRunning.Focus && item.Kind == RoutineItemKind.App && string.IsNullOrEmpty(item.Open))
                {
                    var running = services.Windows.Windows.Where(w => Matches(item, w)).ToList();
                    // 방금 새 데스크톱으로 옮겼으면 창 목록의 "지금 데스크톱" 값이 아직 옛것일 수 있어 직접 물어봄 (옛 데스크톱 창을 앞으로 가져오면 그리로 되돌아감)
                    if (running.FirstOrDefault(w => VirtualDesktopHelper.IsOnCurrentDesktop(w.Hwnd) ?? w.OnCurrentDesktop) is { } here)
                    {
                        services.Launcher.Activate(here.Hwnd);
                        await Task.Delay(GapMs + Math.Clamp(item.DelayMs, 0, 10_000));
                        continue;
                    }
                    if (running.Count > 0)
                    {
                        // 다른 데스크톱의 창은 옮기지 않음(공식 방법 없음) — 새로 열지 않고, 다 연 뒤 안내 카드 한 번
                        elsewhere.Add((ItemName(item), running[0].Hwnd));
                        await Task.Delay(GapMs);
                        continue;
                    }
                }

                var before = new HashSet<IntPtr>(services.Windows.Windows.Select(w => w.Hwnd));
                long started = Environment.TickCount64;
                if (!Launch(services, item)) continue;
                _ = PlaceWhenShownAsync(services, item, before, started, state);
            }
            catch (Exception ex)
            {
                Log.Error("루틴 항목 실행 실패", ex);
            }
            await Task.Delay(GapMs + Math.Clamp(item.DelayMs, 0, 10_000));
        }
        if (elsewhere.Count > 0)
        {
            Log.Info($"루틴: 이미 다른 데스크톱에 켜진 앱 {elsewhere.Count}개 — 새로 열지 않음");
            _dispatcher?.BeginInvoke(() => ElsewhereShown?.Invoke(elsewhere));
        }
    }

    /// <summary>이미 다른 데스크톱에 켜져 있어 열지 않은 앱들 (이름, 첫 창) — 안내 카드를 띄움. UI 스레드.</summary>
    public static event Action<List<(string Name, IntPtr Hwnd)>>? ElsewhereShown;

    /// <summary>"그 데스크톱으로 가기": 창이 있는 데스크톱으로 이동한 뒤 그 창을 앞으로. UI 스레드에서 부름(COM).</summary>
    public static async Task GoToWindowAsync(IntPtr hwnd)
    {
        var services = _services;
        if (services is null || !User32.IsWindow(hwnd)) return;
        int index = VirtualDesktopHelper.GetDesktopIndex(hwnd, VirtualDesktopService.ReadDesktopIds());
        if (index > 0 && index != VirtualDesktopService.Read().Current)
        {
            await VirtualDesktopService.MoveToAsync(index);
            await Task.Delay(150);
        }
        services.Launcher.Activate(hwnd);
    }

    private static async Task PrepareDesktopAsync(AppServices services, RoutineDef routine, RunState state)
    {
        switch (routine.Desktop.Mode)
        {
            case RoutineDesktopMode.New:
            {
                var ids = VirtualDesktopService.ReadDesktopIds();
                if (state.CreatedDesktop && state.Desktop is { } mine && ids.IndexOf(mine) is var at && at >= 0)
                {
                    await VirtualDesktopService.MoveToAsync(at + 1); // 이 루틴이 만든 데스크톱이 아직 있으면 그리로
                    return;
                }
                var before = ids.ToHashSet();
                services.VirtualDesktops.New(); // Ctrl+Win+D — 새 데스크톱을 만들고 그리로 감
                for (int i = 0; i < 25; i++)
                {
                    await Task.Delay(80);
                    var now = VirtualDesktopService.ReadDesktopIds();
                    var added = now.Where(g => !before.Contains(g)).ToList();
                    if (added.Count > 0 && now.Count > Math.Max(before.Count, 1))
                    {
                        state.Desktop = added[^1];
                        state.CreatedDesktop = true;
                        break;
                    }
                }
                await Task.Delay(250); // 전환 애니메이션 뒤에 창을 열어야 새 데스크톱에 뜸
                break;
            }
            case RoutineDesktopMode.Index:
            {
                int target = Math.Max(1, routine.Desktop.Index);
                for (int guard = 0; guard < 10 && VirtualDesktopService.Read().Count < target; guard++)
                {
                    services.VirtualDesktops.New();
                    await Task.Delay(400);
                }
                await VirtualDesktopService.MoveToAsync(target);
                await Task.Delay(200);
                break;
            }
        }
    }

    /// <summary>항목 하나 열기. 앱(+ 열 것·인자), 웹 주소(위치를 정했으면 새 창), 파일·폴더(연결된 앱).</summary>
    private static bool Launch(AppServices services, RoutineItem item)
    {
        switch (item.Kind)
        {
            case RoutineItemKind.App:
            {
                string args = string.Join(" ", new[] { item.Args, Quote(item.Open) }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrWhiteSpace(item.Aumid))
                    services.Launcher.Launch(new PinItem { Kind = PinKind.Aumid, Target = item.Aumid, Arguments = args.Length > 0 ? args : null, Name = item.Name ?? "" });
                else if (File.Exists(Environment.ExpandEnvironmentVariables(item.Target)))
                    services.Launcher.Launch(new PinItem { Kind = PinKind.Exe, Target = item.Target, Arguments = args.Length > 0 ? args : null, Name = item.Name ?? "" });
                else
                {
                    Log.Warn("루틴 항목: 앱을 찾을 수 없어 건너뜀");
                    return false;
                }
                return true;
            }
            case RoutineItemKind.Url:
            {
                string url = item.Target.Trim();
                if (!Uri.TryCreate(url.Contains("://") ? url : "https://" + url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
                var placement = item.Placement?.Mode ?? RoutinePlacementMode.Keep;
                if (placement != RoutinePlacementMode.Keep && DefaultBrowser() is { } browser)
                {
                    string flag = Path.GetFileNameWithoutExtension(browser).Equals("firefox", StringComparison.OrdinalIgnoreCase) ? "-new-window" : "--new-window";
                    Start(browser, $"{flag} \"{uri.AbsoluteUri}\"");
                }
                else Start(uri.AbsoluteUri, null);
                return true;
            }
            default:
            {
                string path = Environment.ExpandEnvironmentVariables(item.Target);
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    Log.Warn("루틴 항목: 파일·폴더가 없어 건너뜀");
                    return false;
                }
                Start(path, null);
                return true;
            }
        }
    }

    private static void Start(string file, string? args)
    {
        var t = new Thread(() =>
        {
            try { using (Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true })) { } }
            catch (Exception ex) { Log.Warn($"루틴 열기 실패: {ex.GetType().Name}"); }
        }) { IsBackground = true, Name = "mongdock-routine" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    private static string? Quote(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;

    // ───────────────────────── 창 찾기·자리 잡기 ─────────────────────────

    /// <summary>새로 뜬 창(실행 전 목록에 없던 것)을 찾아 자리 잡기: 15초까지, 1초 뒤 한 번 더. 찾은 창은 이번 실행 목록에.</summary>
    private static async Task PlaceWhenShownAsync(AppServices services, RoutineItem item, HashSet<IntPtr> before, long started, RunState state)
    {
        string? exe = ExpectedExe(item);
        AppWindowInfo? found = null;
        while (Environment.TickCount64 - started < FindTimeoutMs)
        {
            await Task.Delay(200);
            var fresh = services.Windows.Windows.Where(w => !before.Contains(w.Hwnd)).ToList();
            found = fresh.FirstOrDefault(w => Matches(item, w) || (exe is not null && ExeName(w.ProcessPath) == exe));
            // 런처가 다른 프로세스를 띄우는 앱(스팀·디스코드 등): 5초 뒤부터는 같은 이름의 새 창도
            if (found is null && Environment.TickCount64 - started > 5000 && item.Name is { Length: > 0 } name)
                found = fresh.FirstOrDefault(w => AppNames.Get(w).Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (found is not null) break;
        }
        if (found is null)
        {
            Log.Info("루틴: 새 창을 찾지 못해 앱이 정한 자리에 둠");
            return;
        }
        lock (Runs) state.Windows.Add(found.Hwnd);
        RaiseChanged();
        if (!NeedsPlacement(item)) return;
        await Task.Delay(300); // 창이 처음 자리를 잡은 뒤에
        Place(found.Hwnd, item);
        await Task.Delay(ReplaceAfterMs); // 늦게 자기 자리를 다시 잡는 앱이 있어 한 번 더
        Place(found.Hwnd, item);
    }

    private static bool NeedsPlacement(RoutineItem item) =>
        (item.Placement?.Mode ?? RoutinePlacementMode.Keep) != RoutinePlacementMode.Keep || (item.Monitor?.Mode ?? RoutineMonitorMode.Keep) != RoutineMonitorMode.Keep;

    /// <summary>모니터: deviceName → 번호 → 주 모니터. Keep 이면 창이 있는 모니터.</summary>
    internal static MonitorInfo ResolveMonitor(RoutineMonitor? m, IntPtr hwnd)
    {
        switch (m?.Mode ?? RoutineMonitorMode.Keep)
        {
            case RoutineMonitorMode.Primary: return Monitors.GetPrimary();
            case RoutineMonitorMode.Index:
                return Monitors.Find(m!.DeviceName) ?? Monitors.GetAll().FirstOrDefault(x => x.Number == m.Index) ?? Monitors.GetPrimary();
            default:
                var near = Monitors.FromHwnd(hwnd);
                return Monitors.Find(near.DeviceName) ?? near; // 번호까지 있는 것으로
        }
    }

    /// <summary>창을 정한 모니터·위치로 (보이는 테두리 기준, 순간 이동 — 애니메이션 없음). 옮길 수 없는 창(관리자 권한 등)은 그대로.</summary>
    internal static void Place(IntPtr hwnd, RoutineItem item)
    {
        try
        {
            if (!User32.IsWindow(hwnd)) return;
            var monitor = ResolveMonitor(item.Monitor, hwnd);
            var work = monitor.WorkRect;
            var mode = item.Placement?.Mode ?? RoutinePlacementMode.Keep;
            if (mode == RoutinePlacementMode.Min)
            {
                User32.ShowWindowAsync(hwnd, User32.SW_MINIMIZE);
                return;
            }
            if (WindowPosApi.IsZoomed(hwnd) || User32.IsIconic(hwnd)) ShowWindow(hwnd, User32.SW_RESTORE);

            RECT target;
            if (!WindowPosApi.TryGetFrameBounds(hwnd, out var frame)) return;
            int w = work.Right - work.Left, h = work.Bottom - work.Top;
            switch (mode)
            {
                case RoutinePlacementMode.Max:
                case RoutinePlacementMode.Keep:
                {
                    // 그대로(크기 유지)·최대화: 먼저 그 모니터 안으로 (같은 상대 위치, 넘치면 맞춤)
                    var from = ResolveMonitor(null, hwnd).WorkRect;
                    int fw = Math.Min(frame.Right - frame.Left, w), fh = Math.Min(frame.Bottom - frame.Top, h);
                    double rx = (from.Right - from.Left) > fw ? (frame.Left - from.Left) / (double)((from.Right - from.Left) - fw) : 0;
                    double ry = (from.Bottom - from.Top) > fh ? (frame.Top - from.Top) / (double)((from.Bottom - from.Top) - fh) : 0;
                    int x = work.Left + (int)Math.Round(Math.Clamp(rx, 0, 1) * (w - fw)), y = work.Top + (int)Math.Round(Math.Clamp(ry, 0, 1) * (h - fh));
                    target = new RECT(x, y, x + fw, y + fh);
                    break;
                }
                case RoutinePlacementMode.Left: target = new RECT(work.Left, work.Top, work.Left + w / 2, work.Bottom); break;
                case RoutinePlacementMode.Right: target = new RECT(work.Left + w / 2, work.Top, work.Right, work.Bottom); break;
                case RoutinePlacementMode.Top: target = new RECT(work.Left, work.Top, work.Right, work.Top + h / 2); break;
                case RoutinePlacementMode.Bottom: target = new RECT(work.Left, work.Top + h / 2, work.Right, work.Bottom); break;
                default:
                {
                    var r = item.Placement?.Rect is { Length: 4 } ratio ? ratio : new[] { 0.1, 0.1, 0.8, 0.8 };
                    int x = work.Left + (int)Math.Round(Math.Clamp(r[0], 0, 1) * w), y = work.Top + (int)Math.Round(Math.Clamp(r[1], 0, 1) * h);
                    int cw = Math.Max(200, (int)Math.Round(Math.Clamp(r[2], 0.05, 1) * w)), ch = Math.Max(150, (int)Math.Round(Math.Clamp(r[3], 0.05, 1) * h));
                    target = new RECT(x, y, Math.Min(work.Right, x + cw), Math.Min(work.Bottom, y + ch));
                    break;
                }
            }
            // 보이지 않는 크기 조절 테두리(GetWindowRect - DWM 테두리)만큼 바깥으로
            User32.GetWindowRect(hwnd, out var outer);
            int l = frame.Left - outer.Left, t = frame.Top - outer.Top, rgt = outer.Right - frame.Right, b = outer.Bottom - frame.Bottom;
            User32.SetWindowPos(hwnd, IntPtr.Zero, target.Left - l, target.Top - t, target.Right - target.Left + l + rgt, target.Bottom - target.Top + t + b,
                0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */ | WindowPosApi.SWP_NOOWNERZORDER);
            if (mode == RoutinePlacementMode.Max) ShowWindow(hwnd, 3 /* SW_MAXIMIZE */);
        }
        catch (Exception ex)
        {
            Log.Warn($"루틴 창 자리 잡기 실패: {ex.GetType().Name}");
        }
    }

    // ───────────────────────── 끝내기 ─────────────────────────

    /// <summary>이번 실행에 연 창을 WM_CLOSE 로만 닫음. closeDesktop 이면 창이 다 닫힌 뒤(최대 15초) 그 데스크톱도 닫음.</summary>
    public static void End(RoutineDef routine, bool closeDesktop)
    {
        RunState? state;
        lock (Runs) Runs.TryGetValue(routine.Id, out state);
        if (state is null) return;
        List<IntPtr> windows;
        lock (Runs) windows = state.Windows.Where(User32.IsWindow).ToList();
        foreach (var h in windows) User32.PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        Log.Info($"루틴 끝내기: 창 {windows.Count}개에 닫기 요청");
        if (!closeDesktop || !state.CreatedDesktop || state.Desktop is not { } desktop) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 75 && windows.Any(User32.IsWindow); i++) await Task.Delay(200);
            if (windows.Any(User32.IsWindow))
            {
                Log.Info("루틴 끝내기: 닫지 않은 창이 있어 데스크톱은 그대로");
                return;
            }
            int index = VirtualDesktopService.ReadDesktopIds().IndexOf(desktop);
            if (index < 0) return;
            if (!await VirtualDesktopService.MoveToAsync(index + 1)) return;
            await Task.Delay(200);
            if (VirtualDesktopService.ReadDesktopIds().IndexOf(desktop) + 1 != VirtualDesktopService.Read().Current) return; // 다른 데스크톱이면 닫지 않음
            KeyChord.Send("close desktop", User32.VK_LCONTROL, User32.VK_LWIN, 0x73 /* VK_F4 */);
            lock (Runs) { state.Desktop = null; state.CreatedDesktop = false; }
            Log.Info("루틴 끝내기: 데스크톱 닫음");
        });
    }

    // ───────────────────────── 지금 화면 읽기 ─────────────────────────

    /// <summary>
    /// 지금 데스크톱의 보이는 창 → 루틴 항목 (최대 15). 몽독·시스템 창은 뺌. 모니터(장치 이름), 위치(최대화/반쪽/저장한 위치 — 작업 영역 비율),
    /// 열 것(실행 인자에 있는 파일·폴더, 탐색기 창은 그 폴더)은 확실할 때만.
    /// </summary>
    public static List<(RoutineItem Item, IntPtr Hwnd)> CaptureScreen()
    {
        var services = _services;
        var list = new List<(RoutineItem, IntPtr)>();
        if (services is null) return list;
        var explorerFolders = ExplorerFolders();
        List<(string Name, string Lnk)>? recent = null;
        foreach (var w in services.Windows.Windows.Where(w => w.OnCurrentDesktop && !w.IsMinimized))
        {
            if (list.Count >= RoutineDef.MaxItems) break;
            string exe = ExeName(w.ProcessPath) ?? "";
            if (exe is "mongdock" or "taskmgr" or "systemsettings" or "applicationframehost" or "searchhost" or "startmenuexperiencehost" or "shellexperiencehost" or "textinputhost" or "lockapp") continue;
            if (w.ProcessPath.Length == 0 && string.IsNullOrEmpty(w.Aumid)) continue;
            RoutineItem item;
            if (exe == "explorer")
            {
                if (!explorerFolders.TryGetValue(w.Hwnd, out var folder)) continue; // 바탕 화면·작업 표시줄 등
                item = new RoutineItem { Kind = RoutineItemKind.Path, Target = folder, Name = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } fn ? fn : folder };
            }
            else
            {
                bool packaged = AppsFolder.IsPackagedAumid(w.Aumid);
                item = new RoutineItem
                {
                    Kind = RoutineItemKind.App,
                    Target = packaged ? "" : w.ProcessPath,
                    Aumid = packaged ? w.Aumid : null,
                    Name = AppNames.Get(w),
                    Open = packaged ? null : OpenTargetFromCommandLine(w.Hwnd, w.ProcessPath),
                };
                if (item.Open is null && !packaged && !IsBrowser(exe)) item.Open = OpenFromRecent(w.Title, recent ??= RecentDocuments());
            }
            ReadPlacement(w.Hwnd, item);
            list.Add((item, w.Hwnd));
        }
        return list;
    }

    /// <summary>창의 지금 모니터·위치를 항목에 (지금 화면에서 다시 읽기·저장 공용).</summary>
    internal static void ReadPlacement(IntPtr hwnd, RoutineItem item)
    {
        var monitor = ResolveMonitor(null, hwnd);
        item.Monitor = new RoutineMonitor { Mode = RoutineMonitorMode.Index, Index = monitor.Number, DeviceName = monitor.DeviceName };
        if (WindowPosApi.IsZoomed(hwnd))
        {
            item.Placement = new RoutinePlacement { Mode = RoutinePlacementMode.Max };
            return;
        }
        if (!WindowPosApi.TryGetFrameBounds(hwnd, out var f)) return;
        var work = monitor.WorkRect;
        double w = work.Right - work.Left, h = work.Bottom - work.Top;
        double x = (f.Left - work.Left) / w, y = (f.Top - work.Top) / h, cw = (f.Right - f.Left) / w, ch = (f.Bottom - f.Top) / h;
        const double tol = 0.02;
        bool Near(double a, double b) => Math.Abs(a - b) <= tol;
        RoutinePlacementMode mode =
            Near(x, 0) && Near(y, 0) && Near(cw, 0.5) && Near(ch, 1) ? RoutinePlacementMode.Left :
            Near(x, 0.5) && Near(y, 0) && Near(cw, 0.5) && Near(ch, 1) ? RoutinePlacementMode.Right :
            Near(x, 0) && Near(y, 0) && Near(cw, 1) && Near(ch, 0.5) ? RoutinePlacementMode.Top :
            Near(x, 0) && Near(y, 0.5) && Near(cw, 1) && Near(ch, 0.5) ? RoutinePlacementMode.Bottom :
            RoutinePlacementMode.Saved;
        item.Placement = new RoutinePlacement
        {
            Mode = mode,
            Rect = mode == RoutinePlacementMode.Saved ? new[] { Math.Round(x, 4), Math.Round(y, 4), Math.Round(cw, 4), Math.Round(ch, 4) } : null,
        };
    }

    /// <summary>탐색기 폴더 창 → 그 폴더 경로 (Shell.Application.Windows).</summary>
    private static Dictionary<IntPtr, string> ExplorerFolders()
    {
        var map = new Dictionary<IntPtr, string>();
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return map;
            shell = Activator.CreateInstance(type);
            foreach (dynamic win in ((dynamic)shell!).Windows())
            {
                try
                {
                    string? url = win.LocationURL;
                    long hwnd = win.HWND;
                    if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.IsFile) continue;
                    map[new IntPtr(hwnd)] = u.LocalPath;
                }
                catch { /* IE·기타 창 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"탐색기 창 읽기 실패: {ex.GetType().Name}");
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
        return map;
    }

    private static bool IsBrowser(string exe) => exe is "chrome" or "msedge" or "whale" or "firefox" or "brave" or "opera" or "vivaldi" || exe == ExeName(DefaultBrowser());

    /// <summary>최근 문서(윈도우 Recent 폴더의 바로 가기) — 이름(예 "주간보고.hwp")과 바로 가기 경로, 최근 것 300개. 이 PC 안에서만 읽음.</summary>
    private static List<(string Name, string Lnk)> RecentDocuments()
    {
        var list = new List<(string, string)>();
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return list;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(300))
            {
                string name = Path.GetFileNameWithoutExtension(f.Name);
                if (Path.HasExtension(name)) list.Add((name, f.FullName)); // 파일(확장자 있음)만 — 폴더 바로 가기는 뺌
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return list;
    }

    /// <summary>
    /// 창 제목에 최근 문서 이름이 그대로 들어 있고(예 "주간보고.hwp - 한글", "보고서 - Word"는 확장자 없이 " - " 앞이 이름), 그런 문서가 딱 하나일 때만
    /// 그 바로 가기가 가리키는 파일(실제로 있을 때). 확실하지 않으면 null.
    /// </summary>
    private static string? OpenFromRecent(string? title, List<(string Name, string Lnk)> recent)
    {
        if (string.IsNullOrWhiteSpace(title) || recent.Count == 0) return null;
        string head = title.Split(new[] { " - ", " — ", " – " }, StringSplitOptions.None)[0].Trim().TrimStart('*').Trim();
        var hits = recent.Where(r =>
                title.Contains(r.Name, StringComparison.OrdinalIgnoreCase)
                || head.Length > 0 && Path.GetFileNameWithoutExtension(r.Name).Equals(head, StringComparison.OrdinalIgnoreCase))
            .Select(r => PinFactory.ShortcutTarget(r.Lnk))
            .Where(t => !string.IsNullOrEmpty(t) && File.Exists(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>프로세스 실행 인자에 있는 파일·폴더 (예 code C:\dev\mongdock). 확실할 때만 (실제로 있는 경로).</summary>
    private static string? OpenTargetFromCommandLine(IntPtr hwnd, string exePath)
    {
        try
        {
            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            string? cmd = CommandLineOf(pid);
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            var args = SplitArgs(cmd).Skip(1).Where(a => !a.StartsWith('-') && !a.StartsWith('/')).ToList();
            for (int i = args.Count - 1; i >= 0; i--)
            {
                string a = Environment.ExpandEnvironmentVariables(args[i]);
                if (!Path.IsPathFullyQualified(a)) continue;
                if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(a) || Directory.Exists(a)) return a;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    private static IEnumerable<string> SplitArgs(string cmd)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in cmd)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length, MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>다른 프로세스의 실행 줄 (ProcessCommandLineInformation, 윈도우 8.1+). 관리자 권한 프로세스 등은 null.</summary>
    private static string? CommandLineOf(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out int len);
            if (len <= 0 || len > 1 << 20) return null;
            buf = Marshal.AllocHGlobal(len);
            if (NtQueryInformationProcess(h, 60, buf, len, out _) != 0) return null;
            var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
            return us.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }

    // ───────────────────────── 도우미 ─────────────────────────

    internal static string ItemName(RoutineItem item) => item.Name is { Length: > 0 } n ? n
        : item.Kind == RoutineItemKind.Url ? item.Target
        : Path.GetFileNameWithoutExtension(item.Target) is { Length: > 0 } f ? f : item.Aumid ?? item.Target;

    private static string? ExeName(string? path) => string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

    /// <summary>항목이 띄울 창의 프로세스 이름 (앱 exe, 웹 주소 = 기본 브라우저, 파일 = 연결된 앱, 폴더 = 탐색기).</summary>
    private static string? ExpectedExe(RoutineItem item) => item.Kind switch
    {
        RoutineItemKind.App => ExeName(item.Target),
        RoutineItemKind.Url => ExeName(DefaultBrowser()),
        _ => Directory.Exists(item.Target) ? "explorer" : ExeName(AssociatedExe(Path.GetExtension(item.Target))),
    };

    /// <summary>"지금 화면으로 위치 다시 저장"·편집 창의 "지금 화면에서 다시 읽기": 지금 데스크톱에 그 앱 창이 있으면 모니터·위치만 다시 채움 (항목은 그대로). 바꾼 수.</summary>
    public static int RereadPlacements(IEnumerable<RoutineItem> items)
    {
        var services = _services;
        if (services is null) return 0;
        var windows = services.Windows.Windows.Where(w => w.OnCurrentDesktop && !w.IsMinimized).ToList();
        var used = new HashSet<IntPtr>();
        int n = 0;
        foreach (var item in items)
        {
            string? exe = ExpectedExe(item);
            var w = windows.FirstOrDefault(x => !used.Contains(x.Hwnd) && (Matches(item, x) || item.Kind == RoutineItemKind.App && exe is not null && ExeName(x.ProcessPath) == exe));
            if (w is null) continue;
            used.Add(w.Hwnd);
            ReadPlacement(w.Hwnd, item);
            n++;
        }
        return n;
    }

    /// <summary>지금 그 앱 창이 떠 있으면 그 창 (루틴에 넣을 때 위치도 같이 저장).</summary>
    public static IntPtr WindowOf(RoutineItem item)
    {
        var services = _services;
        if (services is null) return IntPtr.Zero;
        string? exe = ExpectedExe(item);
        return services.Windows.Windows.FirstOrDefault(w => w.OnCurrentDesktop && !w.IsMinimized && (Matches(item, w) || exe is not null && ExeName(w.ProcessPath) == exe))?.Hwnd ?? IntPtr.Zero;
    }

    private static bool Matches(RoutineItem item, AppWindowInfo w)
    {
        if (item.Kind != RoutineItemKind.App) return false;
        if (!string.IsNullOrEmpty(item.Aumid)) return string.Equals(w.Aumid, item.Aumid, StringComparison.OrdinalIgnoreCase);
        return item.Target.Length > 0 && string.Equals(w.ProcessPath, Environment.ExpandEnvironmentVariables(item.Target), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>기본 브라우저 exe (https 연결 → ProgId → open 명령). 모르면 null.</summary>
    internal static string? DefaultBrowser()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId) return null;
            using var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            return cmd?.GetValue(null) is string line ? SplitArgs(line).FirstOrDefault() : null;
        }
        catch { return null; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? output, ref uint size);

    private static string? AssociatedExe(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return null;
        try
        {
            uint size = 1024;
            var sb = new StringBuilder((int)size);
            return AssocQueryString(0, 2 /* ASSOCSTR_EXECUTABLE */, ext, null, sb, ref size) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    /// <summary>몽독 알림 배너로 짧게 (다른 데스크톱에 이미 있어요 등).</summary>
    private static void Notify(string text) => _dispatcher?.BeginInvoke(() =>
    {
        try
        {
            Views.NotificationBannerWindow.ShowCustom(new NotificationItem(0, "", AppInfo.Name, Loc.T("루틴"), new[] { text }, DateTime.Now, null, null, false, null), null, () => { });
        }
        catch (Exception ex) { Log.Warn($"루틴 알림 실패: {ex.GetType().Name}"); }
    });
}
