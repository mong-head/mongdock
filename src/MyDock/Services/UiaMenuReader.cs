using System.Diagnostics;
using System.Windows.Automation;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 범용: UI 자동화(UIA)로 앱 창 안의 메뉴 막대(ControlType.MenuBar) 제목을 읽는다 — Windows 11 메모장·그림판(WinUI/XAML),
/// WinForms MenuStrip, WPF Menu, Qt 등 표준 Win32 메뉴가 아닌 메뉴 막대.
/// - 제목만 읽는다. 하위 항목은 펼쳐야(ExpandCollapsePattern) 생기므로, 제목을 누르면 앱의 실제 메뉴를 펼친다(<see cref="ExpandAsync"/>).
///   (하위 항목을 읽어 몽독 메뉴로 그리려면 앱 메뉴를 펼쳤다 접어야 해 화면이 깜빡이고, 실행 때 또 펼쳐야 해서 택하지 않음.)
/// - 크로미움/Electron 창(Chrome_WidgetWin_*)은 읽지 않는다: UIA 로 조회하면 그 앱 전체의 접근성 모드가 켜져 느려짐.
/// - 모든 UIA 호출은 백그라운드 스레드 하나에서 차례로(응답 없는 앱이 UI 스레드를 막지 않게). 결과는 hwnd 별 캐시.
/// </summary>
internal sealed class UiaMenuReader
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SlowScan = TimeSpan.FromMilliseconds(300);

    private sealed record Scan(IReadOnlyList<string> Titles, DateTime At);

    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, Scan> _cache = new();
    private IntPtr _scanning = IntPtr.Zero;
    private IntPtr _pending = IntPtr.Zero;

    /// <summary>백그라운드 조회가 끝나 hwnd 의 결과가 바뀜 (스레드 풀).</summary>
    public event Action<IntPtr>? Scanned;

    /// <summary>UIA 로 볼 만한 창인지 (자기 프로세스·크로미움·콘솔·관리자 권한 창 제외).</summary>
    public static bool IsCandidate(IntPtr hwnd, string windowClass)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (windowClass.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return false;
        if (windowClass is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "CabinetWClass" or "Progman" or "WorkerW") return false;
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;
        return !Kernel32.IsHigherIntegrity(pid);
    }

    /// <summary>
    /// 캐시된 제목 (없거나 오래됐으면 null 을 돌려주고 백그라운드 조회 시작 → 끝나면 <see cref="Scanned"/>).
    /// 빈 목록 = 메뉴 막대 없음(캐시됨).
    /// </summary>
    public IReadOnlyList<string>? GetTitles(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(hwnd, out var s) && DateTime.UtcNow - s.At < CacheTtl) return s.Titles;
            if (_scanning == hwnd) return s?.Titles;
            if (_scanning != IntPtr.Zero)
            {
                _pending = hwnd; // 지금 조회가 끝나면 이어서
                return s?.Titles;
            }
            _scanning = hwnd;
            Task.Run(() => ScanLoop(hwnd));
            return s?.Titles;
        }
    }

    private void ScanLoop(IntPtr hwnd)
    {
        while (true)
        {
            IReadOnlyList<string> titles = Array.Empty<string>();
            var sw = Stopwatch.StartNew();
            try
            {
                if (User32.IsWindow(hwnd) && !MenuApi.IsHungAppWindow(hwnd)) titles = ReadTitles(hwnd);
            }
            catch (Exception ex)
            {
                Log.Warn($"UI 자동화 메뉴 막대 조회 실패: {ex.GetType().Name} {ex.Message}");
            }
            if (sw.Elapsed > SlowScan) Log.Info($"UI 자동화 메뉴 막대 조회 느림 {sw.ElapsedMilliseconds}ms ({titles.Count}개)");

            bool changed, more;
            IntPtr next;
            lock (_gate)
            {
                // 처음 조회에서 메뉴 막대가 없으면(대부분의 앱) 알리지 않음 — 이미 대체 메뉴가 보이는 중
                changed = _cache.TryGetValue(hwnd, out var old) ? !old.Titles.SequenceEqual(titles) : titles.Count > 0;
                _cache[hwnd] = new Scan(titles, DateTime.UtcNow);
                if (_cache.Count > 64)
                {
                    foreach (var dead in _cache.Keys.Where(h => !User32.IsWindow(h)).ToList()) _cache.Remove(dead);
                }
                next = _pending;
                _pending = IntPtr.Zero;
                more = next != IntPtr.Zero && next != hwnd;
                _scanning = more ? next : IntPtr.Zero;
            }
            if (changed)
            {
                try { Scanned?.Invoke(hwnd); }
                catch (Exception ex) { Log.Error("UI 자동화 메뉴 알림 실패", ex); }
            }
            if (!more) return;
            hwnd = next;
        }
    }

    private static readonly Condition MenuBarCondition =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuBar);

    private static IReadOnlyList<string> ReadTitles(IntPtr hwnd)
    {
        var bar = FindAppMenuBar(hwnd);
        if (bar is null) return Array.Empty<string>();
        var titles = new List<string>();
        foreach (AutomationElement item in bar.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            var c = item.Current;
            if (c.ControlType != ControlType.MenuItem || c.IsOffscreen) continue;
            string name = AppMenuService.SplitMenuText(c.Name ?? "").Text;
            if (name.Length > 0 && !titles.Contains(name)) titles.Add(name);
            if (titles.Count >= 30) break;
        }
        return titles;
    }

    /// <summary>창 안의 앱 메뉴 막대 (시스템 메뉴 막대·화면 밖 막대 제외).</summary>
    private static AutomationElement? FindAppMenuBar(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        foreach (AutomationElement bar in root.FindAll(TreeScope.Descendants, MenuBarCondition))
        {
            var c = bar.Current;
            if (c.AutomationId == "SystemMenuBar" || c.IsOffscreen) continue;
            return bar;
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
            var bar = FindAppMenuBar(hwnd);
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
