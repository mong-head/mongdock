using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>코치마크 진행 하나 (말풍선 방식 CoachSession · 둘러보기 카드 방식 TourSession).</summary>
internal interface ICoachRun
{
    event Action<CoachEndReason>? Ended;
    void Begin();
    void Close(bool markSeen, CoachEndReason reason);
}

/// <summary>
/// 둘러보기 새 방식 진행 (spec-coach-tour.md, 2026-10-14 사용자 확정): 가운데 카드 하나(TourCard)가 내용만 바뀌고,
/// 화면은 35% 어둡게 + 그 기능 자리만 뚫어 연보라 링(TourSpotlight), 단계가 바뀌면 구멍이 미끄러져 옮겨 감.
/// 카드는 강조 자리를 가리지 않게 위·아래로. ←→ 로 넘김, Esc 로 닫음. [직접 해 보기] = 카드·어둡게를 접고 아래 작은 알약 [둘러보기로 돌아가기].
/// 가리킬 자리가 지금 화면에 없으면(기능을 끔·독 폴더 없음) 구멍 없이 카드만 가운데. 일시 정지·전체 화면이면 저장 없이 닫음(다음에 다시).
/// </summary>
internal sealed class TourSession : ICoachRun
{
    private readonly AppServices _services;
    private readonly Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> _resolve;
    private readonly List<CoachPage> _pages;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(2) };
    private TourSpotlightWindow? _dim;
    private TourCardWindow? _card;
    private Window? _pill;
    private int _index;
    private bool _closed;
    private bool _trying;
    private Rect? _hole;
    private MonitorInfo? _monitor;

    public event Action<CoachEndReason>? Ended;

    public TourSession(AppServices services, Func<CoachAnchor, (Rect Rect, MonitorInfo Monitor)?> resolve, List<CoachPage> pages)
    {
        _services = services;
        _resolve = resolve;
        _pages = pages;
    }

    public void Begin()
    {
        UiFonts.Apply(_services.Settings.Current);
        var palette = UiTheme.Palette(_services.Settings.Current);
        _card = new TourCardWindow(_services, palette);
        _card.Card.NextClicked += Next;
        _card.Card.PrevClicked += Prev;
        _card.Card.SkipClicked += () => { Log.Info("둘러보기: 건너뛰기"); Close(markSeen: true, CoachEndReason.Completed); };
        _card.Card.TryClicked += Try;
        _card.Card.InlineClicked += RunInline;
        _card.Card.UndoClicked += UndoInline;
        _card.Card.LinkClicked += () =>
        {
            // 설정 → 변경 내역. 마지막 카드면 안내를 본 것으로 끝냄 (설정 창이 카드에 가리지 않게)
            try { SettingsWindow.OpenChangelogPage(_services); }
            catch (Exception ex) { Log.Error("변경 내역 열기 실패", ex); }
            if (_index >= _pages.Count - 1) Close(markSeen: true, CoachEndReason.Completed);
        };
        _card.KeyPressed += key =>
        {
            if (key == Key.Right) Next();
            else if (key == Key.Left) Prev();
            else if (key == Key.Escape) { Log.Info("둘러보기: Esc"); Close(markSeen: true, CoachEndReason.Completed); }
        };
        if (_pages.Any(p => p.Anchor is CoachAnchor.Dock or CoachAnchor.DockFolder or CoachAnchor.AllApps or CoachAnchor.DockStacks)) DockState.SetCoachPinned(true);
        AppState.Changed += OnPausedChanged;
        _services.DesktopWindows.FullscreenAppChanged += OnFullscreenChanged;
        _services.DesktopWindows.DisplayChanged += OnDisplayChanged;
        _watch.Tick += (_, _) => FollowAnchor();
        _watch.Start();
        _keepOnTop.Tick += (_, _) => BringToFront();
        _keepOnTop.Start();
        _index = 0;
        _open++;
        _release?.Stop();
        // 카드 높이는 둘러보기 내내 같게 (가장 긴 단계 기준) — 넘길 때 카드 크기·자리가 안 바뀜
        // 화면에 들어가는 높이까지 (그림자 여백 + 위아래 8) — 넘으면 목록이 안에서 스크롤
        var work = _services.DesktopWindows.ResolveMonitor("").WorkArea;
        _card.Card.FixHeight(MeasureSteps().ToList(), work.Height - 16);
        Log.Info($"둘러보기 시작: {_pages.Count}장");
        ShowCurrent(animate: false);
    }

    private void Next()
    {
        if (_closed) return;
        if (_index >= _pages.Count - 1) { Close(markSeen: true, CoachEndReason.Completed); return; }
        _index++;
        ShowCurrent(animate: true);
    }

    private void Prev()
    {
        if (_closed || _index == 0) return;
        _index--;
        ShowCurrent(animate: true);
    }

    private (Rect Rect, MonitorInfo Monitor)? Resolve(CoachAnchor a)
    {
        if (a == CoachAnchor.Center) return null;
        try { return _resolve(a); }
        catch (Exception ex) { Log.Warn($"둘러보기 자리 찾기 실패: {ex.GetType().Name}"); return null; }
    }

    private void ShowCurrent(bool animate)
    {
        if (_closed || _card is null) return;
        var page = _pages[_index];
        var anchor = Resolve(page.Anchor);
        var monitor = anchor?.Monitor ?? _services.DesktopWindows.ResolveMonitor("");
        var palette = UiTheme.Palette(_services.Settings.Current);
        // 어둡게 + 구멍: 모니터가 바뀌면 창을 새로
        if (_dim is null || _monitor?.DeviceName != monitor.DeviceName)
        {
            _dim?.Close();
            _dim = new TourSpotlightWindow(_services, monitor, palette);
            _dim.Show();
            animate = false;
        }
        _monitor = monitor;
        _hole = anchor?.Rect;
        _dim.MoveTo(anchor?.Rect, Radius(page.Anchor, anchor?.Rect), animate);
        // 그림: 그 자리에서 그림 (첫 장은 지금, 나머지는 뒤에서 — 언어·테마별 메모리 캐시)
        string? key = page.Pictures;
        int count = TourScenes.Count(key);
        var frames = new List<ImageSource>();
        if (key is not null && count > 0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (TourScenes.Frame(_services, key, 0, palette) is { } first) frames.Add(first);
            if (sw.ElapsedMilliseconds > 300) Log.Info($"둘러보기 첫 장 그리기 {sw.ElapsedMilliseconds}ms ({key})");
        }
        var step = StepFor(page, frames, canTry: anchor is not null);
        _card.Card.Show(step, _index, _pages.Count);
        if (key is not null && count > 1) RenderRest(key, count, _index, palette);
        PrerenderNext(palette);
        if (_card.IsVisible) PlaceAfterRender(monitor, anchor?.Rect);
        else _card.Place(monitor, anchor?.Rect);
        if (!_card.IsVisible)
        {
            _card.Show();
            _card.Activate();
            // 카드 높이는 고정(FixHeight)이라 창 크기도 고정 — 내용을 바꿀 때 창 크기를 다시 재지 않음
            _card.SizeToContent = SizeToContent.Manual;
        }
        BringToFront();
    }

    private EventHandler? _pendingPlace;

    /// <summary>
    /// 단계가 바뀔 때 카드 옮기기는 새 내용이 화면에 그려진 뒤에 (0.5.1 깜빡임 측정 2026-10-15): 투명 창(레이어드)은 자리(SetWindowPos)가
    /// 바로 바뀌고 내용은 다음 그리기에 바뀌어, 그 사이 한 프레임 동안 카드 그림 자리가 비쳐 어둡게 번쩍였음. 그리기 두 번 뒤에 옮김.
    /// </summary>
    private void PlaceAfterRender(MonitorInfo monitor, Rect? hole)
    {
        if (_pendingPlace is not null) CompositionTarget.Rendering -= _pendingPlace;
        int frames = 0, at = _index;
        EventHandler h = null!;
        h = (_, _) =>
        {
            if (++frames < 3) return;
            CompositionTarget.Rendering -= h;
            _pendingPlace = null;
            if (!_closed && _card is not null && _index == at) _card.Place(monitor, hole);
        };
        _pendingPlace = h;
        CompositionTarget.Rendering += h;
    }

    /// <summary>카드 안 버튼 결과 (단계별): 결과 문구, 되돌리기(없으면 null).</summary>
    private readonly Dictionary<CoachPage, InlineResult> _inlineResults = new();
    private bool _inlineBusy;

    /// <summary>
    /// 카드 안 버튼 ([다운로드·휴지통 넣기]·[컴퓨터 켜면 몽독도 켜기]·[허용하기]): 실행 → 버튼 자리에 결과 한 줄 + [되돌리기].
    /// 다음 단계로는 안 넘어감. 독이 바뀌면 그린 뒤 강조 구멍을 새 자리로.
    /// </summary>
    private async void RunInline()
    {
        if (_closed || _inlineBusy || _index >= _pages.Count || _pages[_index].Inline is not { } inline) return;
        var page = _pages[_index];
        _inlineBusy = true;
        InlineResult? result;
        try { result = await inline.Run(); }
        catch (Exception ex) { Log.Error("둘러보기 카드 버튼 실패", ex); result = null; }
        finally { _inlineBusy = false; }
        if (_closed || result is null) return;
        _inlineResults[page] = result;
        RefreshAfterInline(page);
    }

    private void UndoInline()
    {
        if (_closed || _index >= _pages.Count || !_inlineResults.TryGetValue(_pages[_index], out var r) || r.Undo is null) return;
        var page = _pages[_index];
        string message;
        try { message = r.Undo(); }
        catch (Exception ex) { Log.Error("둘러보기 되돌리기 실패", ex); return; }
        _inlineResults[page] = new InlineResult(message); // 되돌린 뒤: 문구만, 버튼은 다시 (Visible 이면)
        RefreshAfterInline(page);
    }

    private void RefreshAfterInline(CoachPage page)
    {
        ShowCurrent(animate: true);
        int at = _index;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; // 독이 새 칸을 그린 뒤 구멍을 새 자리로
        t.Tick += (_, _) => { t.Stop(); if (!_closed && _index == at && _pages[at] == page) ShowCurrent(animate: true); };
        t.Start();
    }

    /// <summary>카드 높이 재기용: 버튼을 누른 뒤 모습(가장 긴 결과 + [되돌리기])도 함께 잼.</summary>
    private IEnumerable<TourStep> MeasureSteps()
    {
        foreach (var p in _pages)
        {
            yield return StepFor(p, new List<ImageSource>(), canTry: p.Anchor != CoachAnchor.Center);
            if (p.Inline is { } a)
                yield return StepFor(p, new List<ImageSource>(), canTry: p.Anchor != CoachAnchor.Center) with { InlineText = null, ResultText = a.LongestResult, CanUndo = true };
        }
    }

    private TourStep StepFor(CoachPage page, List<ImageSource> frames, bool canTry)
    {
        _inlineResults.TryGetValue(page, out var result);
        bool undoable = result?.Undo is not null;
        string? inline = page.Inline is { } a && !undoable && SafeVisible(a) ? a.Label : null;
        return new TourStep(page.Title, page.Body, frames)
        {
            Action = page.Action,
            NextText = page.NextText,
            Footnote = page.Footnote,
            CanTry = canTry,
            Groups = page.Groups,
            More = page.More,
            Link = page.ChangelogLink ? page.LinkText : null,
            FramesPending = page.Pictures is { } k && TourScenes.Count(k) > 0,
            ShowLogo = page.ShowLogo,
            InlineText = inline,
            ResultText = result?.Message,
            CanUndo = undoable,
        };
    }

    private static bool SafeVisible(InlineAction a)
    {
        try { return a.Visible(); }
        catch (Exception ex) { Log.Warn($"둘러보기 카드 버튼 확인 실패: {ex.GetType().Name}"); return false; }
    }

    /// <summary>나머지 장을 뒤에서 그려 카드에 채움 (그 사이 단계가 바뀌었으면 버림 — 캐시에는 남음).</summary>
    private void RenderRest(string key, int count, int index, UiPalette palette)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (_closed) return;
            var all = new List<ImageSource>();
            for (int i = 0; i < count; i++)
                if (TourScenes.Frame(_services, key, i, palette) is { } img) all.Add(img);
            if (!_closed && _index == index && _card is not null) _card.Card.SetFrames(all);
        });
    }

    /// <summary>다음 단계 그림을 쉬는 동안 미리 (넘기면 바로 보이게).</summary>
    private void PrerenderNext(UiPalette palette)
    {
        int next = _index + 1;
        if (next >= _pages.Count || _pages[next].Pictures is not { } key) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (_closed) return;
            for (int i = 0; i < TourScenes.Count(key); i++) TourScenes.Frame(_services, key, i, palette);
        });
    }

    /// <summary>열린 둘러보기 수 — 마지막 것이 닫히고 3초 뒤 장면 그림을 놓아줌 (바로 다른 둘러보기를 열면 그대로 씀).</summary>
    private static int _open;
    private static DispatcherTimer? _release;

    private static void ScheduleRelease()
    {
        _open = Math.Max(0, _open - 1);
        if (_open > 0) return;
        _release?.Stop();
        _release = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(3) };
        _release.Tick += (_, _) =>
        {
            _release?.Stop();
            if (_open > 0) return;
            long before = Environment.WorkingSet;
            int n = TourScenes.ReleaseCache();
            if (n > 0) Log.Info($"둘러보기 그림 정리: {n}장 (작업 집합 {before / 1048576}MB → {Environment.WorkingSet / 1048576}MB)");
        };
        _release.Start();
    }

    private double Radius(CoachAnchor a, Rect? r) => a switch
    {
        CoachAnchor.Dock => Math.Max(8, _services.Settings.Current.Dock.CornerRadius + 4),
        CoachAnchor.TopBar => 0,
        CoachAnchor.AllApps or CoachAnchor.DockStacks => 12,
        _ => r is { } rr ? Math.Min(rr.Height / 2 + 3, 10) : 10,
    };

    /// <summary>
    /// 가리키는 자리가 움직이면(자동 숨김 독이 나타남·독 확대·아이콘 수가 바뀜·알약 폭이 바뀜) 구멍·카드 자리만 다시 맞춤.
    /// 0.5.1 깜빡임 수정: 예전엔 여기서 ShowCurrent 로 카드 내용을 통째로 다시 만들어, 독 위에 마우스만 올려도(확대로 독 폭이 바뀜)
    /// 그림이 첫 장으로 돌아가고 나머지 장을 다시 채우며 번쩍였음. 자리가 생기거나 없어질 때([직접 해 보기] 표시가 바뀜)만 카드를 다시 만듦.
    /// </summary>
    private void FollowAnchor()
    {
        if (_closed || _trying || _index >= _pages.Count || _card is null || _dim is null) return;
        var page = _pages[_index];
        var found = Resolve(page.Anchor);
        var now = found?.Rect;
        bool same = now is null ? _hole is null : _hole is { } h && Math.Abs(h.Left - now.Value.Left) < 1 && Math.Abs(h.Top - now.Value.Top) < 1 && Math.Abs(h.Width - now.Value.Width) < 1 && Math.Abs(h.Height - now.Value.Height) < 1;
        if (same) return;
        if ((now is null) != (_hole is null) || found is { } f && f.Monitor.DeviceName != _monitor?.DeviceName)
        {
            Log.Info($"둘러보기 자리 {(now is null ? "없어짐" : "생김")} → 카드 다시 ({_index + 1}/{_pages.Count})");
            ShowCurrent(animate: true);
            return;
        }
        _hole = now;
        _dim.MoveTo(now, Radius(page.Anchor, now), animate: true);
        _card.Place(_monitor ?? _services.DesktopWindows.ResolveMonitor(""), now);
    }

    // ── 직접 해 보기 ──

    private void Try()
    {
        if (_closed || _trying) return;
        _trying = true;
        Log.Info($"둘러보기: 직접 해 보기 ({_index + 1}/{_pages.Count})");
        _dim?.Hide();
        _card?.Hide();
        var palette = UiTheme.Palette(_services.Settings.Current);
        var text = new TextBlock { Text = Loc.F($"둘러보기로 돌아가기 · {_index + 1}/{_pages.Count}"), FontSize = UiText.Body, FontWeight = FontWeights.SemiBold, Foreground = palette.PanelTintText, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = palette.PanelTint,
            BorderBrush = palette.PanelTintLine,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 8, 18, 9),
            Child = text,
            Cursor = Cursors.Hand,
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.2 },
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = pill,
            Title = "mongdock Tour",
        };
        pill.MouseLeftButtonUp += (_, _) => Return();
        w.SourceInitialized += (_, _) => _services.DesktopWindows.MakeOverlay(w);
        w.Loaded += (_, _) =>
        {
            var work = (_monitor ?? _services.DesktopWindows.ResolveMonitor("")).WorkArea;
            w.Left = work.Left + (work.Width - w.ActualWidth) / 2;
            // 독(아래)을 가리지 않게 화면 위쪽 가운데 (상단바 아래)
            w.Top = work.Top + 12;
        };
        _pill = w;
        w.Show();
    }

    private void Return()
    {
        if (_closed || !_trying) return;
        _trying = false;
        _pill?.Close();
        _pill = null;
        _dim?.Show();
        _card?.Show();
        _card?.Activate();
        ShowCurrent(animate: false);
    }

    /// <summary>
    /// 다른 맨 위 창(독·알림 배너 등)이 둘러보기 창 위로 올라왔을 때만 다시 올림 — 이미 맨 위면 Z 순서를 건드리지 않음
    /// (괜히 다시 올리면 레이어드 창이 다시 합성돼 깜빡일 수 있음, 2026-10-14).
    /// </summary>
    private void BringToFront()
    {
        var ours = new HashSet<IntPtr>(new Window?[] { _dim, _card, _pill }.Where(w => w is not null && w.IsVisible).Select(w => new WindowInteropHelper(w!).Handle).Where(h => h != IntPtr.Zero));
        if (ours.Count == 0) return;
        bool covered = false;
        foreach (var h in ours)
        {
            const uint GW_HWNDPREV = 3;
            for (var above = Native.User32.GetWindow(h, GW_HWNDPREV); above != IntPtr.Zero; above = Native.User32.GetWindow(above, GW_HWNDPREV))
                if (!ours.Contains(above) && Native.User32.IsWindowVisible(above)) { covered = true; break; }
            if (covered) break;
        }
        if (!covered) return;
        RaiseNow();
    }

    internal void RaiseNow()
    {
        const uint flags = 0x0001 | 0x0002 | User32Flags.NoActivate;
        foreach (Window? w in new Window?[] { _dim, _card, _pill })
        {
            if (w is null || !w.IsVisible) continue;
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero) Native.User32.SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, flags);
        }
    }

    private static class User32Flags { public const uint NoActivate = Native.User32.SWP_NOACTIVATE; }

    private void OnPausedChanged(object? sender, EventArgs e)
    {
        if (AppState.Paused) Close(markSeen: false, CoachEndReason.Paused);
    }

    private void OnFullscreenChanged(object? sender, bool any)
    {
        if (any && _services.DesktopWindows.IsFullscreenOn("")) Close(markSeen: false, CoachEndReason.Fullscreen);
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        // 배율·모니터가 바뀌면 창을 새로 (자리 잡은 뒤)
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_closed || _trying) return;
            _dim?.Close();
            _dim = null;
            ShowCurrent(animate: false);
        };
        t.Start();
    }

    public void Close(bool markSeen, CoachEndReason reason)
    {
        if (_closed) return;
        _closed = true;
        if (_pendingPlace is not null) { CompositionTarget.Rendering -= _pendingPlace; _pendingPlace = null; }
        _watch.Stop();
        _keepOnTop.Stop();
        DockState.SetCoachPinned(false);
        AppState.Changed -= OnPausedChanged;
        _services.DesktopWindows.FullscreenAppChanged -= OnFullscreenChanged;
        _services.DesktopWindows.DisplayChanged -= OnDisplayChanged;
        _pill?.Close();
        _dim?.Close();
        _card?.Close();
        _pill = null;
        _dim = null;
        _card = null;
        Log.Info($"둘러보기 끝: {reason} ({_index + 1}/{_pages.Count})");
        ScheduleRelease();
        if (markSeen)
        {
            try { CoachMarks.MarkSeen(); }
            catch (Exception ex) { Log.Error("LastSeenVersion 저장 실패", ex); }
        }
        Ended?.Invoke(reason);
    }
}

/// <summary>모니터 전체를 덮는 어둡게 + 구멍 창 (클릭 통과 — 가린 자리도 그대로 눌림).</summary>
internal sealed class TourSpotlightWindow : Window
{
    private readonly TourSpotlight _spot;
    private readonly Rect _bounds;

    public TourSpotlightWindow(AppServices services, MonitorInfo monitor, UiPalette palette)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "mongdock Tour Dim";
        _bounds = monitor.Bounds;
        Left = _bounds.Left;
        Top = _bounds.Top;
        Width = _bounds.Width;
        Height = _bounds.Height;
        _spot = new TourSpotlight(palette) { Width = _bounds.Width, Height = _bounds.Height };
        Content = _spot;
        SourceInitialized += (_, _) =>
        {
            services.DesktopWindows.MakeOverlay(this);
            var h = new WindowInteropHelper(this).Handle;
            const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            Native.User32.SetWindowLong(h, Native.User32.GWL_EXSTYLE, Native.User32.GetWindowLong(h, Native.User32.GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        Loaded += (_, _) =>
        {
            if (services.DesktopWindows.EnsureOnMonitor(this, monitor)) { Left = _bounds.Left; Top = _bounds.Top; }
        };
    }

    /// <summary>화면 좌표(DIP) 자리로 구멍 이동.</summary>
    public void MoveTo(Rect? screen, double radius, bool animate)
    {
        Rect? local = screen is { } r ? new Rect(r.X - _bounds.Left, r.Y - _bounds.Top, r.Width, r.Height) : null;
        if (!IsLoaded) { Loaded += (_, _) => _spot.MoveTo(local, radius, false); return; }
        _spot.MoveTo(local, radius, animate);
    }
}

/// <summary>가운데 카드 창 (키보드 ←→·Esc 를 받으려고 활성화되는 창).</summary>
internal sealed class TourCardWindow : Window
{
    public TourCard Card { get; }
    public event Action<Key>? KeyPressed;
    /// <summary>카드 둘레 그림자 여백 (일반 창 카드면 0 — 그림자는 DWM 이 창 밖에 그림).</summary>
    public double ShadowPad { get; }

    /// <summary>
    /// 둘러보기 카드를 투명 창(레이어드)이 아닌 일반 창으로 (윈도우 11 만): QA 측정에서 투명 카드 창이 그림이 바뀌는 순간 가끔 한 프레임
    /// 통째로 비는 것이 잡힘(2026-10-15) — 일반 창 + DWM 둥근 모서리·그림자로 바꾸면 없어질 것으로 봄. 화면 시험 전이라 아직 기본 꺼짐,
    /// 시험할 때 MONGDOCK_TOUR_CARD=plain 으로 켬.
    /// </summary>
    internal static bool PlainWindow { get; set; } =
        Environment.GetEnvironmentVariable("MONGDOCK_TOUR_CARD") == "plain" && Environment.OSVersion.Version.Build >= 22000;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    public TourCardWindow(AppServices services, UiPalette palette)
    {
        bool plain = PlainWindow;
        ShadowPad = plain ? 0 : 28;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = !plain;
        Background = plain ? palette.CardBackground : Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "mongdock Tour";
        Card = new TourCard(palette);
        if (plain) Card.UsePlainWindow();
        Content = new Border { Padding = new Thickness(ShadowPad), Child = Card };
        UiFonts.Apply(services.Settings.Current);
        SourceInitialized += (_, _) =>
        {
            services.DesktopWindows.MakeOverlay(this);
            if (plain)
            {
                // 일반 창: DWM 둥근 모서리 + 그림자(테두리 없는 팝업은 프레임을 1px 넓혀야 그림자가 생김)
                var h = new WindowInteropHelper(this).Handle;
                Native.DesktopApi.SetDwmInt(h, Native.DesktopApi.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DesktopApi.DWMWCP_ROUND);
                var m = new Margins { Bottom = 1 };
                DwmExtendFrameIntoClientArea(h, ref m);
            }
            // 가로 스크롤(WM_MOUSEHWHEEL)은 WPF 이벤트가 없어 창 메시지로 받음
            System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x020E) handled = Card.HorizontalWheel((short)((long)w >> 16));
                return IntPtr.Zero;
            });
        };
        PreviewKeyDown += (_, e) =>
        {
            // Shift+←→ 는 장 넘기기, ←→ 는 단계 넘기기
            if (e.Key is Key.Left or Key.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { e.Handled = true; Card.StepFrame(e.Key == Key.Right ? +1 : -1); }
            else if (e.Key is Key.Left or Key.Right or Key.Escape) { e.Handled = true; KeyPressed?.Invoke(e.Key); }
        };
    }

    /// <summary>모니터 가운데. 강조 자리가 아래 반이면 그 위로, 위 반이면 그 아래로 (가리지 않게).</summary>
    public void Place(MonitorInfo monitor, Rect? hole)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = root.DesiredSize.Width, h = root.DesiredSize.Height;
        var work = monitor.WorkArea;
        double left = work.Left + (work.Width - w) / 2;
        double top = work.Top + (work.Height - h) / 2;
        if (hole is { } r)
        {
            const double gap = 12; // 카드 그림자 여백(28) 밖으로 12
            bool below = r.Top + r.Height / 2 > work.Top + work.Height / 2;
            double wanted = below ? r.Top - h - gap + ShadowPad : r.Bottom + gap - ShadowPad;
            // 가운데 카드가 이미 자리를 안 가리면 가운데 그대로, 가리면 비켜 놓음
            var card = new Rect(left + ShadowPad, top + ShadowPad, w - ShadowPad * 2, h - ShadowPad * 2);
            if (card.IntersectsWith(r)) top = wanted;
            top = FitTop(top, h, work.Top, work.Bottom, ShadowPad);
        }
        Left = Math.Round(left);
        Top = Math.Round(top);
        if (SizeToContent == SizeToContent.Manual) { Width = w; Height = h; }
    }

    /// <summary>
    /// 카드 위치를 화면 안으로 (그림자 여백은 넘어도 됨). 카드가 화면보다 크면 범위가 뒤집혀 Math.Clamp 가 예외였음
    /// (오류 신고 uid 29 — '5.6' cannot be greater than -129) → 그땐 화면 가운데.
    /// </summary>
    internal static double FitTop(double top, double h, double workTop, double workBottom, double pad)
    {
        double min = workTop - pad + 8, max = workBottom - h + pad - 8;
        return max < min ? workTop + (workBottom - workTop - h) / 2 : Math.Clamp(top, min, max);
    }
}
