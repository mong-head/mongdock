using System.Diagnostics;
using System.Windows.Automation;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 범용: UI 자동화(UIA)로 앱 창 안의 메뉴 막대(ControlType.MenuBar) 제목을 읽는다 — Windows 11 메모장·그림판(WinUI/XAML),
/// WinForms MenuStrip, Qt 등 표준 Win32 메뉴가 아닌 메뉴 막대.
/// - 제목만 읽는다. 하위 항목은 펼쳐야(ExpandCollapsePattern) 생기므로, 제목을 누르면 앱의 실제 메뉴를 펼친다(<see cref="ExpandAsync"/>).
///   (하위 항목을 읽어 몽독 메뉴로 그리려면 앱 메뉴를 펼쳤다 접어야 해 화면이 깜빡이고, 실행 때 또 펼쳐야 해서 택하지 않음.)
/// - 창 전체 트리를 훑지 않는다: ControlViewWalker 로 깊이 3까지만(메모장은 깊이 3), 한 번에 250ms 를 넘기면 중단("없음").
/// - 크로미움/Electron 창(Chrome_WidgetWin_*)은 읽지 않는다: UIA 로 조회하면 그 앱 전체의 접근성 모드가 켜져 느려짐.
///   오피스(Word·Excel·PowerPoint·Outlook)·Visual Studio 같은 트리가 무거운 창도 제외.
/// - 모든 UIA 호출은 백그라운드 스레드 하나에서 차례로(응답 없는 앱이 UI 스레드를 막지 않게).
/// - 캐시: 메뉴 막대가 있으면 hwnd 별 30초, "없음"은 프로세스 경로 단위로 30분 (그 앱의 다른 창도 다시 조회하지 않음).
///   시간 초과는 hwnd 별 30초만 (UIA 첫 호출이 느릴 수 있어서), 같은 앱에서 2번 연속이면 경로 단위 30분.
/// </summary>
internal sealed class UiaMenuReader
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoneTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SlowScan = TimeSpan.FromMilliseconds(150);
    /// <summary>제목 조회 한 번의 시간 상한. 넘으면 중단하고 "없음".</summary>
    private const int ScanBudgetMs = 250;
    /// <summary>펼치기는 사용자가 누른 것이라 조금 더 기다림.</summary>
    private const int ExpandBudgetMs = 1000;
    private const int MaxDepth = 3;
    /// <summary>깊이 3 안에서도 살펴볼 요소 수 상한 (도구 모음이 아주 많은 앱).</summary>
    private const int MaxNodes = 300;

    /// <summary>트리가 크거나 UIA 조회가 느린 것으로 알려진 창 클래스 (메뉴는 리본·자체 그리기라 UIA 메뉴 막대도 없음).</summary>
    private static readonly HashSet<string> HeavyClasses = new(StringComparer.Ordinal)
    {
        "OpusApp",        // Word
        "XLMAIN",         // Excel
        "PPTFrameClass",  // PowerPoint
        "rctrl_renwnd32", // Outlook
    };

    private static readonly HashSet<string> HeavyExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "devenv.exe", "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe",
    };

    private sealed record Scan(IReadOnlyList<string> Titles, DateTime At);
    private enum Outcome { Found, None, Timeout }

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, Scan> _cache = new();
    /// <summary>메뉴 막대가 없는 프로세스 경로(소문자) → 기록 시각.</summary>
    private readonly Dictionary<string, DateTime> _noneByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>경로별 연속 시간 초과 횟수.</summary>
    private readonly Dictionary<string, int> _timeouts = new(StringComparer.OrdinalIgnoreCase);
    private IntPtr _scanning = IntPtr.Zero;
    private (IntPtr Hwnd, string? Path) _pending;

    /// <summary>백그라운드 조회가 끝나 hwnd 의 결과가 바뀜 (스레드 풀).</summary>
    public event Action<IntPtr>? Scanned;

    /// <summary>UIA 로 볼 만한 창인지 (자기 프로세스·크로미움·콘솔·탐색기·무거운 앱·관리자 권한 창 제외).</summary>
    public static bool IsCandidate(IntPtr hwnd, string windowClass, string? processPath = null)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (windowClass.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return false;
        if (windowClass is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "CabinetWClass" or "Progman" or "WorkerW") return false;
        if (HeavyClasses.Contains(windowClass)) return false;
        if (windowClass.StartsWith("HwndWrapper[", StringComparison.Ordinal) && windowClass.Contains("VisualStudio", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(processPath) && HeavyExes.Contains(System.IO.Path.GetFileName(processPath))) return false;
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
        return !Kernel32.IsHigherIntegrity(pid);
    }

    /// <summary>
    /// 캐시된 제목 (없거나 오래됐으면 null 을 돌려주고 백그라운드 조회 시작 → 끝나면 <see cref="Scanned"/>).
    /// 빈 목록 = 메뉴 막대 없음(캐시됨). processPath = 창의 exe 경로 ("없음" 캐시 단위).
    /// </summary>
    public IReadOnlyList<string>? GetTitles(IntPtr hwnd, string? processPath)
    {
        string path = processPath ?? "";
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (path.Length > 0 && _noneByPath.TryGetValue(path, out var noneAt))
            {
                if (now - noneAt < NoneTtl) return Array.Empty<string>();
                _noneByPath.Remove(path);
            }
            if (_cache.TryGetValue(hwnd, out var s) && now - s.At < CacheTtl) return s.Titles;
            if (_scanning == hwnd) return s?.Titles;
            if (_scanning != IntPtr.Zero)
            {
                _pending = (hwnd, path); // 지금 조회가 끝나면 이어서
                return s?.Titles;
            }
            _scanning = hwnd;
            Task.Run(() => ScanLoop(hwnd, path));
            return s?.Titles;
        }
    }

    private void ScanLoop(IntPtr hwnd, string path)
    {
        while (true)
        {
            IReadOnlyList<string> titles = Array.Empty<string>();
            var outcome = Outcome.None;
            var sw = Stopwatch.StartNew();
            try
            {
                if (User32.IsWindow(hwnd) && !MenuApi.IsHungAppWindow(hwnd))
                    (titles, outcome) = ReadTitles(hwnd);
            }
            catch (Exception ex)
            {
                Log.Warn($"UI 자동화 메뉴 막대 조회 실패: {ex.GetType().Name} {ex.Message}");
            }
            if (outcome == Outcome.Timeout)
                Log.Info($"UI 자동화 메뉴 막대 조회 시간 초과 {sw.ElapsedMilliseconds}ms ({System.IO.Path.GetFileName(path)}) → 없음으로 처리");
            else if (sw.Elapsed > SlowScan)
                Log.Info($"UI 자동화 메뉴 막대 조회 느림 {sw.ElapsedMilliseconds}ms ({titles.Count}개, {System.IO.Path.GetFileName(path)})");

            bool changed, more;
            (IntPtr Hwnd, string? Path) next;
            lock (_gate)
            {
                // 처음 조회에서 메뉴 막대가 없으면(대부분의 앱) 알리지 않음 — 이미 대체 메뉴가 보이는 중
                changed = _cache.TryGetValue(hwnd, out var old) ? !old.Titles.SequenceEqual(titles) : titles.Count > 0;
                _cache[hwnd] = new Scan(titles, DateTime.UtcNow);
                if (path.Length > 0)
                {
                    if (outcome == Outcome.Found)
                    {
                        _timeouts.Remove(path);
                    }
                    else if (outcome == Outcome.None)
                    {
                        _timeouts.Remove(path);
                        _noneByPath[path] = DateTime.UtcNow;
                    }
                    else
                    {
                        int n = _timeouts.TryGetValue(path, out int c) ? c + 1 : 1;
                        _timeouts[path] = n;
                        if (n >= 2) _noneByPath[path] = DateTime.UtcNow;
                    }
                }
                if (_cache.Count > 64)
                {
                    foreach (var dead in _cache.Keys.Where(h => !User32.IsWindow(h)).ToList()) _cache.Remove(dead);
                }
                if (_noneByPath.Count > 256)
                {
                    var cut = DateTime.UtcNow - NoneTtl;
                    foreach (var stale in _noneByPath.Where(kv => kv.Value < cut).Select(kv => kv.Key).ToList()) _noneByPath.Remove(stale);
                }
                next = _pending;
                _pending = default;
                more = next.Hwnd != IntPtr.Zero && next.Hwnd != hwnd;
                _scanning = more ? next.Hwnd : IntPtr.Zero;
            }
            if (changed)
            {
                try { Scanned?.Invoke(hwnd); }
                catch (Exception ex) { Log.Error("UI 자동화 메뉴 알림 실패", ex); }
            }
            if (!more) return;
            hwnd = next.Hwnd;
            path = next.Path ?? "";
        }
    }

    private static (IReadOnlyList<string> Titles, Outcome Outcome) ReadTitles(IntPtr hwnd)
    {
        var sw = Stopwatch.StartNew();
        var bar = FindAppMenuBar(hwnd, sw, ScanBudgetMs, out bool timedOut);
        if (bar is null) return (Array.Empty<string>(), timedOut ? Outcome.Timeout : Outcome.None);
        var titles = new List<string>();
        foreach (AutomationElement item in bar.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            var c = item.Current;
            if (c.ControlType != ControlType.MenuItem || c.IsOffscreen) continue;
            string name = AppMenuService.SplitMenuText(c.Name ?? "").Text;
            if (name.Length > 0 && !titles.Contains(name)) titles.Add(name);
            if (titles.Count >= 30) break;
        }
        // 메뉴 막대는 있는데 제목이 없음(전부 숨김) → 이 창만 "없음" (경로 단위로는 기록하지 않음)
        return (titles, Outcome.Found);
    }

    /// <summary>
    /// 창 안의 앱 메뉴 막대 (시스템 메뉴 막대·화면 밖 막대 제외). ControlViewWalker 로 너비 우선, 깊이 3까지만,
    /// budgetMs 를 넘기거나 요소 300개를 넘기면 중단(timedOut).
    /// </summary>
    private static AutomationElement? FindAppMenuBar(IntPtr hwnd, Stopwatch sw, int budgetMs, out bool timedOut)
    {
        timedOut = false;
        var walker = TreeWalker.ControlViewWalker;
        var level = new List<AutomationElement> { AutomationElement.FromHandle(hwnd) };
        int nodes = 0;
        for (int depth = 1; depth <= MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<AutomationElement>();
            foreach (var parent in level)
            {
                for (var child = walker.GetFirstChild(parent); child is not null; child = walker.GetNextSibling(child))
                {
                    if (++nodes > MaxNodes || sw.ElapsedMilliseconds > budgetMs)
                    {
                        timedOut = true;
                        return null;
                    }
                    var c = child.Current;
                    if (c.ControlType == ControlType.MenuBar)
                    {
                        if (c.AutomationId != "SystemMenuBar" && !c.IsOffscreen) return child;
                        continue; // 메뉴 막대 안은 보지 않음
                    }
                    if (depth < MaxDepth) next.Add(child);
                }
            }
            level = next;
        }
        return null;
    }

    /// <summary>
    /// 앱의 실제 메뉴를 펼침 (메뉴는 앱 창 쪽, 앱 메뉴 막대 아래에 뜬다). 같은 이름의 제목을 찾고, 없으면 같은 순서의 제목.
    /// ExpandCollapsePattern 이 없으면 InvokePattern.
    /// </summary>
    public Task<bool> ExpandAsync(IntPtr hwnd, string title, int index) => Task.Run(() =>
    {
        try
        {
            if (!User32.IsWindow(hwnd) || MenuApi.IsHungAppWindow(hwnd)) return false;
            var bar = FindAppMenuBar(hwnd, Stopwatch.StartNew(), ExpandBudgetMs, out _);
            if (bar is null) return false;
            var items = bar.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem))
                .Cast<AutomationElement>().ToList();
            var target = items.FirstOrDefault(i => AppMenuService.SplitMenuText(i.Current.Name ?? "").Text == title)
                         ?? (index >= 0 && index < items.Count ? items[index] : null);
            if (target is null) return false;
            if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec))
            {
                var p = (ExpandCollapsePattern)ec;
                if (p.Current.ExpandCollapseState == ExpandCollapseState.Expanded) p.Collapse();
                else p.Expand();
                return true;
            }
            if (target.TryGetCurrentPattern(InvokePattern.Pattern, out var inv))
            {
                ((InvokePattern)inv).Invoke();
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"UI 자동화 메뉴 펼치기 실패 '{title}': {ex.GetType().Name} {ex.Message}");
            return false;
        }
    });
}
