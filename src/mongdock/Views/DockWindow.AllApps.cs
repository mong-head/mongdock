using System.IO;
using System.Windows;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>앱 모음 판 (#24): 독의 "앱 모음"(Special "launchpad") 누르면 판 열기/닫기 (시작 메뉴 대신).</summary>
public partial class DockWindow
{
    internal static bool IsAllApps(PinItem? pin) =>
        pin is { Kind: PinKind.Special } && pin.Target.Equals("launchpad", StringComparison.OrdinalIgnoreCase);

    private bool _allAppsOpening;
    private int _allAppsOpens;

    /// <summary>몽독 시작 5초 뒤 유휴 때 앱 모음 판 준비 (앱 목록·분류·처음 보이는 아이콘). 그 뒤 판을 닫을 때마다 다음 열기를 위해 다시.</summary>
    private void ScheduleAllAppsWarm()
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_closed) AllAppsPanel.Warm(_services);
        };
        timer.Start();
    }

    private async void ToggleAllAppsPanel(DockItemView? view, PinItem pin)
    {
        bool same = _folderPanel is AllAppsPanel;
        if (same || view == null || _allAppsOpening)
            Log.Info($"앱 모음 클릭: {(same ? "열린 판 닫기" : view == null ? "아이콘 없음" : "여는 중")}");
        _folderPanel?.CloseAnimated();
        if (same || view == null || _allAppsOpening) return;

        // 목록이 아직이면 준비될 때까지(최대 300ms) 기다렸다 다 그린 판을 한 번에 — 검색 칸만 있는 빈 판을 보이지 않게
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long waited = 0;
        if (AllAppsCatalog.Cached is null)
        {
            _allAppsOpening = true;
            try
            {
                var load = Task.Run(() => AllAppsCatalog.Apps());
                await Task.WhenAny(load, Task.Delay(300));
            }
            finally { _allAppsOpening = false; }
            waited = clock.ElapsedMilliseconds;
            if (_closed) return;
        }

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var a = toDip.Transform(view.PointToScreen(new Point(0, 0)));
        var b = toDip.Transform(view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight)));
        var panel = new AllAppsPanel(_services, UiTheme.Palette(_services.Settings.Current), new Rect(a, b), _layout.Edge, _monitor);
        panel.Launched += app => AppUsage.Record(AllAppsCatalog.Identity(app), _services.Settings.Current.AllApps);
        panel.PinToDockRequested += PinAppToDock;
        panel.Closed += (_, _) =>
        {
            if (_folderPanel != panel) return;
            _folderPanel = null;
            _folderPanelPin = null;
            _lastInsideTicks = Environment.TickCount64;
        };
        _folderPanel = panel;
        _folderPanelPin = pin;
        Interlocked.Increment(ref AllAppsCatalog.OpenedSinceSignal);
        int nth = ++_allAppsOpens;
        long requested = 0;
        panel.ContentRendered += (_, _) => Log.Info($"앱 모음 판 열기 {nth}번째: 클릭→첫 프레임 {clock.ElapsedMilliseconds}ms (목록 기다림 {waited}ms, 보이기 요청→첫 프레임 {clock.ElapsedMilliseconds - requested}ms, 창 생성→첫 프레임 {panel.FirstFrameMs}ms, 앱 {AllAppsCatalog.Cached?.Count ?? 0}개)");
        panel.Closed += (_, _) => { if (!_closed && AllAppsCatalog.IsStale) AllAppsPanel.Warm(_services); }; // 앱 설치·삭제 반영 (다음 열기)
        requested = clock.ElapsedMilliseconds;
        panel.Show();
    }

    /// <summary>판의 "독에 고정": 시작 메뉴 바로 가기가 있으면 그것으로(창과 짝이 맞게), 없으면 AppsFolder 항목으로. 이미 있으면 그대로.</summary>
    private void PinAppToDock(AppEntry app)
    {
        PinItem? pin = null;
        if (app.Shortcut is { } lnk && File.Exists(lnk)) pin = PinFactory.CreatePin(lnk, _services.Settings);
        pin ??= new PinItem { Kind = PinKind.Aumid, Target = app.Key, Name = app.Name };
        var add = pin;
        ModifyPins(p =>
        {
            if (p.Any(x => x.Kind == add.Kind && string.Equals(x.Target, add.Target, StringComparison.OrdinalIgnoreCase))) return;
            p.Add(add); // 휴지통 앞 (저장하며 휴지통은 끝으로)
        });
        Log.Info("앱 모음 판 → 독에 고정");
    }
}
