using System.Runtime.CompilerServices;
using System.Windows.Automation;
using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 「지금 화면을 루틴으로 저장」 때 브라우저 창의 보고 있는 탭 주소 (기술 검토 C 권장 ①, PM 10-23): UI Automation 으로 그 창의
/// 보이는 문서 요소 값(전체 주소)만 — 나머지 탭은 주소를 알 수 없어 개수만 (「탭 N개 중 1개 저장됨」).
/// 저장 버튼을 눌렀을 때만 읽음(크롬은 처음 질의 때 접근성 모드가 켜져 무거워질 수 있음). 창마다 최대 0.8초, 주소는 로그에 남기지 않음.
/// </summary>
internal static class BrowserTabs
{
    private const int TimeoutMs = 800;
    private static readonly ConditionalWeakTable<RoutineItem, StrongBox<int>> TabCounts = new();

    /// <summary>이 항목을 만들 때 창에 있던 탭 수 (모르면 null).</summary>
    public static int? TabsOf(RoutineItem item) => TabCounts.TryGetValue(item, out var box) ? box.Value : null;

    /// <summary>창의 보고 있는 탭 주소(http·https 만)를 항목의 「함께 열 것」에, 탭 수를 기억. 못 읽으면 그대로.</summary>
    public static void Fill(IntPtr hwnd, RoutineItem item)
    {
        string? url = null;
        int tabs = 0;
        var t = new Thread(() =>
        {
            try { (url, tabs) = Read(hwnd); }
            catch (Exception ex) { Log.Warn($"브라우저 탭 읽기 실패: {ex.GetType().Name}"); }
        }) { IsBackground = true, Name = "mongdock-browser-tab" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        if (!t.Join(TimeoutMs))
        {
            Log.Info("브라우저 탭 읽기: 0.8초 안에 답 없음 → 주소 없이");
            return;
        }
        if (url is not null) item.Open = url;
        if (tabs > 0) TabCounts.AddOrUpdate(item, new StrongBox<int>(tabs));
        Log.Info($"브라우저 탭 읽기: 탭 {tabs}개, 보고 있는 탭 주소 {(url is null ? "없음" : "있음")}");
    }

    private static (string? Url, int Tabs) Read(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        string? url = null;
        foreach (AutomationElement d in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)))
        {
            if (d.Current.IsOffscreen) continue;
            if (d.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && ((ValuePattern)vp).Current.Value is { Length: > 0 } v
                && Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
            {
                url = v;
                break;
            }
        }
        int tabs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Count;
        return (url, tabs);
    }
}
