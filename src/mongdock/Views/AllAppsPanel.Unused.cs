using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// "모든 앱" 머리 칩 [전체] [최근 사용] [안 쓰는 앱 n] (spec-all-apps-panel.md 마지막 절).
/// 안 쓰는 앱: 체크·아이콘·이름·"n개월 전/쓴 적 없음"·용량, 정렬(오래된 순/용량 큰 순/이름순), 체크하면 떠 있는 선택 막대 [숨기기] [Windows에서 제거…] [선택 해제].
/// 몽독은 직접 지우지 않고 윈도우 앱 설정만 연다. "자주 쓰는 앱 기록"이 꺼져 있으면 칩을 보이지 않음(근거가 없어서).
/// </summary>
internal sealed partial class AllAppsPanel
{
    private enum AllFilter { All, Recent, Unused }
    private enum UnusedSort { Oldest, Largest, Name }

    private AllFilter _allFilter = AllFilter.All;
    private UnusedSort _unusedSort = UnusedSort.Oldest;
    private List<UnusedApps.Row>? _unused;
    private DateTime _unusedAt;
    private bool _unusedLoading;
    private readonly HashSet<string> _unusedChecked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>시험 그림에서만: 안 쓰는 앱 목록 (이 PC 기록 대신).</summary>
    internal static Func<List<AppEntry>, List<UnusedApps.Row>>? UnusedOverride { get; set; }

    /// <summary>시험 그림(report-test --allapps)에서만: 앱 몇 개를 안 쓰는 앱으로, [안 쓰는 앱] 보기를 펼친 상태로.</summary>
    internal void ShowUnusedForTest()
    {
        var now = DateTime.Now;
        UnusedOverride = visible => visible.Where(a => !UnusedApps.IsBuiltIn(a)).Take(7)
            .Select((a, i) => i is 1 or 2
                ? new UnusedApps.Row(a, now.AddDays(-90 - 30 * i), 1_930_000_000, "uninstall:test", "한컴오피스 2020") // 같은 설치 묶음 둘 (크기는 묶음 전체)
                : new UnusedApps.Row(a, i % 3 == 2 ? null : now.AddDays(-70 - 45 * i), i == 4 ? null : (long)(i + 1) * 180_000_000 % 2_400_000_000))
            .ToList();
        _allOpen = true;
        _allFilter = AllFilter.Unused;
        _unusedChecked.Clear();
        _unused = null;
        // 체크 2개(묶음 앱 둘 — 합계는 묶음 한 번만) + 하나 → 선택 막대가 보이는 상태
        var rows = UnusedOverride(_apps.Where(a => !S.Hidden.Contains(a.Key, StringComparer.OrdinalIgnoreCase)).ToList());
        foreach (var r in rows.Skip(1).Take(2)) _unusedChecked.Add(r.App.Key);
    }

    /// <summary>"모든 앱"을 펼쳤을 때의 내용 (칩 + 고른 보기).</summary>
    private void AddAllApps(StackPanel root, List<AppEntry> visible)
    {
        bool chips = S.ShowSuggestions;
        if (!chips) _allFilter = AllFilter.All;
        if (chips)
        {
            EnsureUnused(visible);
            string unusedLabel = _unused is { } u ? Loc.F($"잠자는 앱 {u.Count}") : Loc.T("잠자는 앱"); // 아직 못 셌으면 숫자 없이 (점 세 개 안 씀)
            var row = new WrapPanel { Margin = new Thickness(4, 2, 0, 8) };
            row.Children.Add(Chip(Loc.T("전체"), _allFilter == AllFilter.All, () => SetFilter(AllFilter.All)));
            row.Children.Add(Chip(Loc.T("최근 사용"), _allFilter == AllFilter.Recent, () => SetFilter(AllFilter.Recent)));
            var sleeping = Chip(unusedLabel, _allFilter == AllFilter.Unused, () => SetFilter(AllFilter.Unused));
            if (_fadeChipNumber && _unused is not null) { _fadeChipNumber = false; FadeIn(sleeping.Child); } // 숫자가 처음 붙음
            row.Children.Add(sleeping);
            root.Children.Add(row);
        }
        switch (_allFilter)
        {
            case AllFilter.Recent:
            {
                var cutoff = DateTime.Now.AddDays(-30);
                var recent = visible.Select(a => (a, last: AppUsage.LastUsed(AllAppsCatalog.Identities(a))))
                    .Where(x => x.last is { } l && l >= cutoff).OrderByDescending(x => x.last).Select(x => x.a).ToList();
                if (recent.Count == 0) { root.Children.Add(Note(Loc.T("최근 30일 동안 연 앱이 없어요."))); break; }
                var wrap = new WrapPanel { Width = Cols * AppCell };
                foreach (var app in recent) wrap.Children.Add(AppCellView(app));
                root.Children.Add(wrap);
                break;
            }
            case AllFilter.Unused:
                root.Children.Add(UnusedView());
                break;
            default:
            {
                var all = new WrapPanel { Width = Cols * AppCell };
                foreach (var app in visible.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)) all.Children.Add(AppCellView(app));
                root.Children.Add(all);
                break;
            }
        }
    }

    private void SetFilter(AllFilter f)
    {
        _allFilter = f;
        if (f == AllFilter.Unused && !Services.Settings.Current.AllAppsUnusedViewOpened)
        {
            Services.Settings.Current.AllAppsUnusedViewOpened = true; // 통계: 하루에 한 번이라도 열었는지
            Services.Settings.Save();
        }
        Rebuild();
    }

    /// <summary>
    /// 잠자는 앱 목록: 미리 세어 둔 것(UnusedApps.Latest — 몽독 시작 3분 뒤·하루 한 번)을 바로 씀 (숨긴 앱·지금 실행 중인 앱만 빼고).
    /// 미리 센 게 없거나 하루 넘었으면 백그라운드에서 다시 세고, 끝나면 숫자가 흐릿→보임으로 붙음.
    /// </summary>
    private void EnsureUnused(List<AppEntry> visible)
    {
        if (UnusedOverride is { } fake) { if (_unused is null) { _unused = fake(visible); _unusedAt = DateTime.Now; } return; }
        if (_unused is not null && DateTime.Now - _unusedAt < TimeSpan.FromMinutes(10)) return;
        if (UnusedApps.Latest is { } latest)
        {
            var running = Services.Windows.Windows.Select(w => System.IO.Path.GetFileNameWithoutExtension(w.ProcessPath).ToLowerInvariant())
                .Where(e => e.Length > 0).Select(e => "exe:" + e).ToHashSet();
            var keys = visible.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _unused = latest.Rows.Where(r => keys.Contains(r.App.Key) && !AllAppsCatalog.Identities(r.App).Any(running.Contains)).ToList();
            _unusedAt = latest.At;
            if (DateTime.Now - latest.At < TimeSpan.FromDays(1)) return;
        }
        if (_unusedLoading) return;
        _unusedLoading = true;
        UnusedApps.CountInBackground(Services, _ =>
        {
            _unusedLoading = false;
            bool hadNumber = _unused is not null;
            _unused = null; // 위 Latest 경로로 다시 거름
            _unusedAt = default;
            if (IsVisible && _allOpen) { _fadeChipNumber = !hadNumber; Rebuild(); }
        });
    }

    /// <summary>숫자가 처음 붙을 때 칩 숫자를 흐릿→보임으로.</summary>
    private bool _fadeChipNumber;

    private UIElement UnusedView()
    {
        var box = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        if (_unused is null) return box; // 아직 못 셈 — 세고 나면 목록이 나타남 (점 세 개·"찾는 중" 안 씀)
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var sort = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (s, label) in new[] { (UnusedSort.Oldest, Loc.T("오래된 순")), (UnusedSort.Largest, Loc.T("용량 큰 순")), (UnusedSort.Name, Loc.T("이름순")) })
        {
            var link = SmallLink(label, () => { _unusedSort = s; Rebuild(); });
            link.Margin = new Thickness(10, 0, 0, 0);
            if (s == _unusedSort) { link.FontWeight = FontWeights.SemiBold; link.Foreground = P.Text; }
            sort.Children.Add(link);
        }
        DockPanel.SetDock(sort, Dock.Right);
        head.Children.Add(sort);
        head.Children.Add(new TextBlock { Text = Loc.T("몽독이 본 기록 기준이에요 (60일 넘게 안 연 앱)"), FontSize = 11.5, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center });
        box.Children.Add(head);
        if (_unused.Count == 0) { box.Children.Add(Note(Loc.T("잠자는 앱이 없어요."))); return box; }

        var rows = _unusedSort switch
        {
            UnusedSort.Largest => _unused.OrderByDescending(r => r.Bytes ?? -1).ThenBy(r => r.App.Name, StringComparer.CurrentCultureIgnoreCase),
            UnusedSort.Name => _unused.OrderBy(r => r.App.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => _unused.OrderBy(r => r.Last ?? DateTime.MinValue).ThenBy(r => r.App.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        _unusedChecked.IntersectWith(_unused.Select(r => r.App.Key));
        double sizeCol = _unused.Any(r => r.BundleKey is not null) ? 210 : 80; // 묶음 표시가 있으면 크기 열을 넓게 (모든 줄 같은 폭 — 열이 어긋나지 않게)
        foreach (var r in rows) box.Children.Add(UnusedRowView(r, UpdateSelectionBar, sizeCol));
        // 목록 끝 버튼 대신 떠 있는 선택 막대 — 마지막 줄을 가리지 않게 막대 높이만큼 아래 여백
        box.Margin = new Thickness(4, 0, 4, SelectionBarSpace);
        _unusedViewShown = true;
        return box;
    }

    private UIElement UnusedRowView(UnusedApps.Row r, Action changed, double sizeCol)
    {
        var app = r.App;
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(sizeCol) }); // 묶음이면 "1.8 GB · 한컴오피스 2020 전체"
        var check = DockMenus.Check(P, null, _unusedChecked.Contains(app.Key));
        check.VerticalAlignment = VerticalAlignment.Center;
        check.Margin = new Thickness(4, 0, 10, 0);
        check.Checked += (_, _) => { _unusedChecked.Add(app.Key); changed(); };
        check.Unchecked += (_, _) => { _unusedChecked.Remove(app.Key); changed(); };
        grid.Children.Add(check);
        var image = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 3, 10, 3) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        LoadIcon(image, app);
        System.Windows.Controls.Grid.SetColumn(image, 1);
        grid.Children.Add(image);
        var name = new TextBlock { Text = app.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        System.Windows.Controls.Grid.SetColumn(name, 2);
        grid.Children.Add(name);
        var last = new TextBlock { Text = LastText(r.Last), FontSize = 12, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Controls.Grid.SetColumn(last, 3);
        grid.Children.Add(last);
        var size = new TextBlock { Text = UnusedApps.RowSizeText(r), FontSize = 12, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 6, 0) };
        System.Windows.Controls.Grid.SetColumn(size, 4);
        grid.Children.Add(size);
        var row = new Border { CornerRadius = new CornerRadius(6), Child = grid, ContextMenu = LazyMenu(m => FillAppMenu(m, app)), Uid = "m:u:" + app.Key };
        MongToolTip.IfTrimmed(row, name, app.Name); // 잘렸을 때만 전체 이름
        row.MouseEnter += (_, _) => row.Background = P.Hover;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        // 줄을 누르면 체크 (실행은 오른쪽 클릭 메뉴 — 정리 화면에서 실수로 켜지 않게)
        row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) check.IsChecked = check.IsChecked != true; };
        return row;
    }

    // ───────── 떠 있는 선택 막대 (2026-10-13 사용자: 체크해도 버튼이 목록 맨 아래라 한참 스크롤해야 보임) ─────────

    private const double SelectionBarSpace = 64; // 막대 높이(약 44) + 아래 띄움(12) + 여유
    private bool _unusedViewShown;
    private Border? _selBar;
    private TextBlock? _selText;
    private bool _selShown;

    /// <summary>
    /// 하나라도 체크하면 판 아래쪽에 떠 있는 막대가 아래에서 미끄러져 올라옴(150ms): "n개 선택됨 · 합계" [숨기기] [Windows에서 제거…] [선택 해제].
    /// 둥근 12, 은은한 그림자, 목록 위에 고정(스크롤과 상관없음). 0개가 되면 내려가며 사라짐. 합계는 같은 설치 묶음을 한 번만 셈.
    /// </summary>
    private Border BuildSelectionBar()
    {
        _selText = new TextBlock { FontSize = UiText.Button, FontWeight = FontWeights.SemiBold, Foreground = P.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 14, 0) };
        var hide = PanelButton(Loc.T("숨기기"), HideChecked);
        var remove = PanelButton(Loc.T("Windows에서 제거…"), RemoveChecked);
        var clear = PanelButton(Loc.T("선택 해제"), () => { _unusedChecked.Clear(); Rebuild(); });
        clear.Background = Brushes.Transparent;
        clear.Foreground = P.SubText;
        remove.Margin = clear.Margin = new Thickness(6, 0, 0, 0);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_selText);
        row.Children.Add(hide);
        row.Children.Add(remove);
        row.Children.Add(clear);
        _selBar = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = P.CardBackground,
            BorderBrush = P.Divider,
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(12, 7, 7, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.18 },
            RenderTransform = new TranslateTransform(),
            Visibility = Visibility.Collapsed,
            Child = row,
        };
        return _selBar;
    }

    /// <summary>체크가 바뀌거나 판을 다시 그린 뒤: 막대 글자와 보이기·숨기기.</summary>
    private void UpdateSelectionBar()
    {
        if (_selBar is null || _selText is null) return;
        bool want = _unusedViewShown && _unusedChecked.Count > 0 && _unused is not null;
        if (want)
        {
            string text = Loc.F($"{_unusedChecked.Count}개 선택됨");
            if (UnusedApps.TotalBytes(_unused!.Where(r => _unusedChecked.Contains(r.App.Key))) is { } total) text += " · " + UnusedApps.SizeText(total);
            _selText.Text = text;
        }
        if (want == _selShown) return;
        _selShown = want;
        var tt = (TranslateTransform)_selBar.RenderTransform;
        int ms = Anim.Enabled && IsVisible ? 150 : 0;
        if (want) _selBar.Visibility = Visibility.Visible;
        if (ms == 0)
        {
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            _selBar.BeginAnimation(OpacityProperty, null);
            tt.Y = 0;
            _selBar.Opacity = 1;
            if (!want) _selBar.Visibility = Visibility.Collapsed;
            return;
        }
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = want ? System.Windows.Media.Animation.EasingMode.EaseOut : System.Windows.Media.Animation.EasingMode.EaseIn };
        var dur = TimeSpan.FromMilliseconds(ms);
        tt.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(want ? 24 : 0, want ? 0 : 24, dur) { EasingFunction = ease });
        var fade = new System.Windows.Media.Animation.DoubleAnimation(want ? 0 : 1, want ? 1 : 0, dur) { EasingFunction = ease };
        if (!want) fade.Completed += (_, _) => { if (!_selShown) _selBar.Visibility = Visibility.Collapsed; };
        _selBar.BeginAnimation(OpacityProperty, fade);
    }

    private void HideChecked()
    {
        int n = 0;
        foreach (var key in _unusedChecked.ToList())
            if (!S.Hidden.Contains(key, StringComparer.OrdinalIgnoreCase)) { S.Hidden.Add(key); n++; }
        _unusedChecked.Clear();
        _unused = _unused?.Where(r => !S.Hidden.Contains(r.App.Key, StringComparer.OrdinalIgnoreCase)).ToList();
        Services.Settings.Save();
        Log.Info($"잠자는 앱: {n}개 숨김");
        Rebuild();
    }

    /// <summary>윈도우 앱 설정 열기 — 스토어 앱 하나면 그 앱 페이지, 아니면 설치된 앱 목록. 몽독은 직접 지우지 않음.</summary>
    private void RemoveChecked()
    {
        var apps = _unused?.Where(r => _unusedChecked.Contains(r.App.Key)).Select(r => r.App).ToList() ?? new();
        string uri = apps.Count == 1 && apps[0].Family is { Length: > 0 } fam ? "ms-settings:appsfeatures-app?" + fam : "ms-settings:appsfeatures";
        try { using (Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })) { } }
        catch (Exception ex) { Log.Warn($"앱 설정 열기 실패: {ex.GetType().Name}"); }
        Log.Info($"잠자는 앱: Windows 앱 설정 열기 ({apps.Count}개 고름)");
    }

    private Border Chip(string text, bool on, Action click)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 3, 12, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Background = on ? P.Accent : P.Tile,
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = on ? P.AccentText : P.Text },
        };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    private Button PanelButton(string text, Action click)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("CardButton"),
            Content = text,
            Background = P.Tile,
            Foreground = P.Text,
            Padding = new Thickness(12, 4, 12, 5),
        };
        b.Click += (_, _) => click();
        return b;
    }

    private TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = P.SubText, Margin = new Thickness(6, 4, 0, 8), TextWrapping = TextWrapping.Wrap };
}
