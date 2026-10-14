using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 잠자는 앱 [Windows에서 제거…] → 몽독 확인 카드 → 제거 프로그램을 차례로 (Services/AppUninstall.cs).
/// 확인 카드: "n개 앱을 지울까요?" + 앱 목록(아이콘·이름·크기, 지울 수 없는 앱은 "지울 수 없음") + 안내 + [취소] [지우기].
/// 진행: 막대 + "2/3 · 한컴오피스 2020 제거 중…" + [나머지 취소]. 끝나면 목록을 새로 세고 "n개 지움 · m개 남음" 한 줄.
/// </summary>
internal sealed partial class AllAppsPanel
{
    private List<AppUninstall.Job>? _removeJobs;
    private bool _removeRunning;
    private (int I, int N, string Name) _removeProgress;
    private CancellationTokenSource? _removeCancel;
    /// <summary>끝난 뒤 잠자는 앱 목록 위에 한 번 보이는 한 줄.</summary>
    private string? _removeSummary;

    /// <summary>시험 그림에서만: 진행 화면 (이 자리·이름으로 멈춘 상태).</summary>
    internal void ShowRemoveForTest(bool running)
    {
        if (_unused is null) return;
        _removeJobs = AppUninstall.Plan(_unused.Where(r => _unusedChecked.Contains(r.App.Key)));
        _removeRunning = running;
        if (running && _removeJobs.FirstOrDefault(j => j.What.Cannot is null) is { } first) _removeProgress = (2, 3, first.Name);
    }

    /// <summary>[Windows에서 제거…]: 고른 앱으로 확인 카드.</summary>
    private void RemoveChecked()
    {
        if (_unused is null || AppUninstall.Running) return;
        var rows = _unused.Where(r => _unusedChecked.Contains(r.App.Key)).ToList();
        if (rows.Count == 0) return;
        _removeJobs = AppUninstall.Plan(rows);
        _removeRunning = false;
        _removeSummary = null;
        Log.Info($"잠자는 앱: 지우기 확인 ({rows.Count}개 고름, 지울 단위 {_removeJobs.Count}개)");
        Rebuild();
    }

    private UIElement BuildRemove()
    {
        var jobs = _removeJobs!;
        int removable = jobs.Count(j => j.What.Cannot is null);
        var root = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        if (_removeRunning)
        {
            var (i, n, name) = _removeProgress;
            root.Children.Add(new TextBlock { Text = Loc.T("앱 지우는 중"), FontSize = UiText.CardTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10) });
            var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = P.Tile, Margin = new Thickness(2, 0, 2, 8) };
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = P.AccentFill, HorizontalAlignment = HorizontalAlignment.Left };
            track.Child = fill;
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * Math.Clamp((i - 0.5) / Math.Max(1, n), 0, 1);
            root.Children.Add(track);
            root.Children.Add(new TextBlock { Text = Loc.F($"{i}/{n} · {name} 제거 중…"), Margin = new Thickness(2, 0, 0, 4) });
            root.Children.Add(new TextBlock { Text = Loc.T("제거 창이 열렸어요 — 그 창에서 계속해 주세요"), Margin = new Thickness(2, 6, 0, 2) });
            root.Children.Add(new TextBlock { Text = Loc.T("제거 창에서 안내대로 진행해 주세요. 창을 닫거나 취소하면 다음 앱으로 넘어가요."), FontSize = UiText.Sub, Foreground = P.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10) });
            var runButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var show = PanelButton(Loc.T("제거 창 보기"), () =>
            {
                bool found = AppUninstall.BringWindowToFront();
                Log.Info($"잠자는 앱 지우기: 제거 창 보기 ({(found ? "앞으로 가져옴" : "창 없음")})");
            });
            show.Background = P.PanelTint;
            show.Foreground = P.PanelTintText;
            runButtons.Children.Add(show);
            // 오래 걸리면(15초) [기다리지 않기] — 제거 창을 닫지 않고 다음으로
            if (AppUninstall.CurrentStarted is { } at && DateTime.Now - at > TimeSpan.FromSeconds(SkipAfterSec))
            {
                var skip = PanelButton(Loc.T("기다리지 않기"), AppUninstall.SkipWait);
                skip.Margin = new Thickness(8, 0, 0, 0);
                runButtons.Children.Add(skip);
            }
            var stop = PanelButton(Loc.T("나머지 취소"), () => { Log.Info("잠자는 앱 지우기: 나머지 취소 누름"); _removeCancel?.Cancel(); });
            stop.Margin = new Thickness(8, 0, 0, 0);
            stop.IsEnabled = _removeCancel?.IsCancellationRequested != true;
            runButtons.Children.Add(stop);
            root.Children.Add(runButtons);
            return root;
        }
        root.Children.Add(new TextBlock { Text = Loc.F($"{removable}개 앱을 지울까요?"), FontSize = UiText.CardTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 8) });
        foreach (var job in jobs)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6), Opacity = job.What.Cannot is null ? 1 : 0.55 };
            var right = new TextBlock
            {
                Text = job.What.Cannot is { } why ? Loc.T("지울 수 없음") + " · " + why : UnusedApps.SizeText(job.Bytes),
                FontSize = UiText.Sub,
                Foreground = P.SubText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(right, Dock.Right);
            line.Children.Add(right);
            var image = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            LoadIcon(image, job.Rows[0].App);
            line.Children.Add(image);
            // 묶음이면 묶음 이름 + 그 안의 앱들 ("한컴오피스 2020 · 한글, 한셀, 한쇼")
            string label = job.Rows.Count > 1 ? $"{job.Name} · {string.Join(", ", job.Rows.Select(r => r.App.Name))}" : job.Rows[0].App.Name;
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
            // 실제로 돌릴 제거 항목을 늘 보여 줌, 앱 이름과 다르면 주의 (0.5.0 검토 2번)
            if (job.What.Cannot is null && job.What.Command is { Length: > 0 })
            {
                string by = job.What.Publisher is { Length: > 0 } pub ? $"{job.What.Name} · {pub}" : job.What.Name;
                texts.Children.Add(new TextBlock { Text = Loc.F($"제거 프로그램: {by}"), FontSize = UiText.Sub, Foreground = P.SubText, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!SameProduct(job.What.Name, job.Rows.Select(r => r.App.Name)))
                    texts.Children.Add(new TextBlock { Text = "\u26A0 " + Loc.T("다른 이름의 제거 프로그램이 실행돼요"), FontSize = UiText.Sub, Foreground = P.HolidayText, TextWrapping = TextWrapping.Wrap });
            }
            line.Children.Add(texts);
            root.Children.Add(line);
        }
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("각 앱의 제거 프로그램이 차례로 열려요. 지운 앱은 되돌릴 수 없어요."),
            Foreground = P.SubText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 10),
        });
        if (jobs.Any(j => j.What.Family is not null && j.What.Cannot is null))
            root.Children.Add(new TextBlock { Text = Loc.T("스토어 앱은 Windows 설정의 그 앱 화면이 열려요. 거기서 [제거]를 눌러 주세요."), FontSize = UiText.Sub, Foreground = P.SubText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, -4, 0, 10) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 4) };
        var cancel = PanelButton(Loc.T("취소"), () => { _removeJobs = null; Rebuild(); });
        var go = PanelButton(Loc.T("지우기"), () => _ = StartRemove(jobs));
        go.Background = P.PanelTint;
        go.Foreground = P.PanelTintText;
        go.IsEnabled = removable > 0;
        go.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(go);
        root.Children.Add(buttons);
        return root;
    }

    /// <summary>제거 항목 이름이 앱 이름(들)과 같은 제품으로 보이는지: 한쪽이 다른 쪽을 품으면 같다고 봄 (대소문자·공백 무시).</summary>
    internal static bool SameProduct(string uninstallName, IEnumerable<string> appNames)
    {
        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        string u = Norm(uninstallName);
        if (u.Length == 0) return false;
        return appNames.Select(Norm).Where(a => a.Length > 0).Any(a => u.Contains(a) || a.Contains(u));
    }

    private const int SkipAfterSec = 15;

    private async Task StartRemove(List<AppUninstall.Job> jobs)
    {
        if (AppUninstall.Running) return;
        Log.Info($"잠자는 앱 지우기: [지우기] 누름 (지울 단위 {jobs.Count(j => j.What.Cannot is null)}개, 지울 수 없음 {jobs.Count(j => j.What.Cannot is not null)}개)");
        _removeRunning = true;
        // 제거 창이 판 뒤에 숨지 않게: 지우는 동안 판은 맨 위에서 내림, 제거 창이 뜨면 한 번 앞으로 가져옴 (2026-10-14 사용자: 멈춘 느낌)
        Topmost = false;
        HoldOpen = true; // 제거 창을 눌러도 판(진행 화면)은 그대로
        bool brought = false;
        DateTime? job = null;
        var watch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        watch.Tick += (_, _) =>
        {
            var started = AppUninstall.CurrentStarted;
            if (started != job) { job = started; brought = false; }
            if (started is null) return;
            if (!brought && AppUninstall.BringWindowToFront()) { brought = true; Log.Info("잠자는 앱 지우기: 제거 창을 앞으로 가져옴"); }
            // [기다리지 않기] 를 보여 줄 때가 되면 한 번 다시 그림
            double sec = (DateTime.Now - started.Value).TotalSeconds;
            if (sec > SkipAfterSec && sec < SkipAfterSec + 0.6 && !IsClosing) Rebuild();
        };
        watch.Start();
        AppUninstall.Result result;
        try
        {
            _removeCancel = new CancellationTokenSource();
            _removeProgress = (1, Math.Max(1, jobs.Count(j => j.What.Cannot is null)), jobs.FirstOrDefault(j => j.What.Cannot is null)?.Name ?? "");
            Rebuild();
            result = await AppUninstall.RunAsync(jobs, (i, n, name) =>
            {
                _removeProgress = (i, n, name);
                if (!IsClosing) Rebuild();
            }, _removeCancel.Token);
        }
        catch (Exception ex)
        {
            // 진행 중 예외: 판이 "지우는 중"에 갇히지 않게 (0.5.0 검토 8번)
            Log.Error("잠자는 앱 지우기 실패", ex);
            _removeJobs = null;
            _removeSummary = Loc.T("지우는 중 문제가 생겼어요");
            if (!IsClosing) Rebuild();
            return;
        }
        finally
        {
            watch.Stop();
            Topmost = true;
            HoldOpen = false;
            _removeRunning = false;
            _removeCancel = null;
        }
        int asked = jobs.Count;
        int left = asked - result.Removed - result.Settings;
        _removeSummary = result.Settings > 0
            ? Loc.F($"{result.Removed}개 지움 · {left}개 남음 · {result.Settings}개는 설정에서 지워 주세요")
            : Loc.F($"{result.Removed}개 지움 · {left}개 남음 (취소 또는 실패)");
        Log.Info($"잠자는 앱 지우기 끝: 지움 {result.Removed}, 실패·취소 {result.Failed}, 설정으로 {result.Settings}, 지울 수 없음 {result.Skipped}");
        _removeJobs = null;
        if (IsClosing) return;
        // 앱 목록을 새로 만들고(지운 앱은 빠짐) 잠자는 앱을 다시 셈
        _unusedChecked.Clear();
        Rebuild();
        var fresh = await Task.Run(() => AllAppsCatalog.Apps(true));
        if (IsClosing) return;
        _apps = fresh;
        _unused = null;
        _unusedAt = default;
        Rebuild();
        UnusedApps.CountInBackground(Services, _ => { if (IsClosing) return; _unused = null; _unusedAt = default; Rebuild(); });
    }
}
