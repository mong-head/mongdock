using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 앱 모음 판 다시 쓰기 (#14): 닫을 때 창을 없애지 않고 숨겨 두었다가 다음 열기에 그 창을 다시 씀.
/// 판은 큰 일반 창이라 열 때마다 새로 만들면 그래픽 드라이버가 판 크기 면을 새로 잡아 5번 열고 닫으면 +190MB 였음(숨겼다 보이기는 +52MB).
/// 판 뼈대(칸 수·크기·글자·테마·언어·아이콘 모양·모니터)를 정하는 값이 바뀌었으면 다시 쓰지 않고 새로 만듦.
/// </summary>
internal sealed partial class AllAppsPanel
{
    /// <summary>숨겨 둔 판 (다음 열기에 씀). 30분 안 쓰면 닫아 놓아줌.</summary>
    private static AllAppsPanel? _spare;
    private static DispatcherTimer? _spareTimer;
    private const int SpareKeepMinutes = 30;

    /// <summary>이 판을 만든 조건 — 다시 쓸 수 있는지 비교.</summary>
    private string _fingerprint = "";

    /// <summary>시험용: 다시 쓴 횟수.</summary>
    internal static int ReusedForTest { get; private set; }

    private static string Fingerprint(AppServices services, UiPalette palette, MonitorInfo monitor) =>
        string.Join("|", palette.IsLight, Loc.Code, services.Settings.Current.Dock.IconStyle, monitor.DeviceName, monitor.WorkArea, monitor.Scale,
            UiText.Body, Application.Current?.TryFindResource(UiFonts.Key)?.ToString() ?? "", SizeForTest?.ToString() ?? "");

    /// <summary>
    /// 판 하나 받기: 숨겨 둔 판이 있고 조건이 같으면 상태를 처음으로 돌려 다시 보임, 아니면 새로 만듦(보이기는 부르는 쪽이 Show).
    /// 다시 쓴 판은 이미 보이는 중이라 reused = true.
    /// </summary>
    public static AllAppsPanel Acquire(AppServices services, UiPalette palette, Rect anchorDip, DockEdge edge, MonitorInfo monitor, out bool reused)
    {
        var spare = _spare;
        _spare = null;
        _spareTimer?.Stop();
        string fp = Fingerprint(services, palette, monitor);
        if (spare is not null && spare._fingerprint == fp && PresentationSource.FromVisual(spare) is not null)
        {
            spare.Reopen(anchorDip, edge, monitor);
            ReusedForTest++;
            reused = true;
            return spare;
        }
        if (spare is not null)
        {
            Log.Info("앱 모음 판: 설정·화면이 바뀌어 숨겨 둔 판 대신 새로 만듦");
            spare.Close();
        }
        reused = false;
        var panel = new AllAppsPanel(services, palette, anchorDip, edge, monitor) { _fingerprint = fp };
        return panel;
    }

    /// <summary>숨겨 둔 판을 닫음 (독이 꺼질 때 등).</summary>
    public static void DropSpare()
    {
        var spare = _spare;
        _spare = null;
        _spareTimer?.Stop();
        spare?.Close();
    }

    /// <summary>시험·비교용: MONGDOCK_PANEL_REUSE=0 이면 예전처럼 열 때마다 새 판.</summary>
    private static readonly bool ReuseEnabled = Environment.GetEnvironmentVariable("MONGDOCK_PANEL_REUSE") != "0";

    protected override bool ReuseOnClose => ReuseEnabled && !_prewarming && !SceneMode && !_removeRunning && _fingerprint.Length > 0;

    protected override void OnHiddenForReuse()
    {
        if (Current == this) Current = null;
        CancelDragOnClose();
        _body.Content = null; // 숨긴 동안 시각 트리를 들고 있지 않게 (다시 열 때 새로 그림)
        ScheduleTrim();
        if (_spare is not null && _spare != this) _spare.Close();
        _spare = this;
        _spareTimer ??= new DispatcherTimer(TimeSpan.FromMinutes(SpareKeepMinutes), DispatcherPriority.Background, (_, _) =>
        {
            _spareTimer!.Stop();
            if (_spare is { } s && !s.IsVisible) { Log.Info("앱 모음 판: 오래 안 써서 숨겨 둔 판을 닫음"); DropSpare(); }
        }, Dispatcher);
        _spareTimer.Stop();
        _spareTimer.Start();
    }

    /// <summary>다시 열기: 앞 열기의 상태를 모두 처음으로(새로 만든 판과 같게) 돌리고 다시 그린 뒤 보임.</summary>
    private void Reopen(Rect anchorDip, DockEdge edge, MonitorInfo monitor)
    {
        BeginReopen(); // 먼저 "닫히는 중"을 꺼야 아래 다시 그리기가 됨 (#26)
        ClearOpenEvents();
        Launched = null;
        PinToDockRequested = null;
        ResetState();
        _apps = AllAppsCatalog.Cached ?? _apps;
        Rebuild();
        _bodyScroll?.ScrollToHome();
        SpotlightWindow.CloseIfOpen();
        Current = this;
        ReopenAt(anchorDip, edge, monitor);
        Dispatcher.BeginInvoke(() => { _search.Focus(); Keyboard.Focus(_search); }, DispatcherPriority.Input);
    }

    /// <summary>
    /// 판 상태 초기화 목록 (#14) — 새 판의 필드 초깃값과 같게. 새 상태를 판에 더하면 여기에도 더할 것.
    /// 검색어·선택, 펼친 묶음·더 보기·이름 바꾸기, 편집 모드, 정리·잠자는 앱 보기(필터·정렬·체크·선택 막대), 앱 추가, 지우기 결과,
    /// 끌기(누름·끄는 중·가장자리 스크롤), 움직임 기억, 루틴 도움말, 누름 피드백.
    /// </summary>
    private void ResetState()
    {
        // 검색 (TextChanged 가 Rebuild 를 부르므로 먼저 다른 상태를 비운 뒤 마지막에)
        _selected = 0;
        _results = new();
        _editing = false;
        _cleanup = null;
        _cleanupResult = null;
        _bannerCount = null;
        _expanded = null;
        _allOpen = _hiddenOpen = false;
        _showAll.Clear();
        _renaming = null;
        _adding = null;
        _routineHelp = false;
        // 잠자는 앱
        _allFilter = AllFilter.All;
        _unusedSort = UnusedSort.Oldest;
        _unused = null;
        _unusedAt = default;
        _unusedLoading = false;
        _unusedChecked.Clear();
        _fadeChipNumber = false;
        _unusedViewShown = false;
        _selShown = false;
        // 지우기 (진행 중이면 다시 쓰지 않음 — ReuseOnClose)
        _removeJobs = null;
        _removeProgress = default;
        _removeCancel = null;
        _removeSummary = null;
        // 끌기·누름
        CancelDragOnClose();
        _pressedCell = null;
        _dragPending = null;
        _edgeScroll?.Stop();
        _pressRelease = null;
        _holdMs = 0;
        _pressTicks = 0;
        HoldOpen = false;
        KeepOpenOnDeactivate = false;
        // 움직임 기억 (다시 그릴 때 옛 자리에서 미끄러지지 않게)
        _shownEditing = false;
        _justDropped = null;
        _shownExpanded = null;
        _expandedView = null;
        _expandedFromCell = null;
        _lanes.Clear();
        if (_search.Text.Length > 0) _search.Text = "";
        _placeholder.Visibility = Visibility.Visible;
    }
}
