using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 폴더에 앱 넣는 길 (spec-all-apps-panel.md "폴더에 앱 넣는 길", 2026-10-13 사용자 확정 — 끌어 놓기만으로는 어려움).
/// - 펼친 폴더 마지막 칸 [+] → 판 안에 "앱 고르기" 카드: 검색 칸 + 앱 목록(체크, 루틴 [+ 앱]과 같은 모양), 여러 개 골라 [넣기].
///   이미 든 앱은 체크된 채 흐리게. 넣으면 손댄 폴더가 되어 자동 분류·"쓰는 앱만" 규칙이 빼지 않음.
/// - [+ 새 폴더] → 같은 카드 맨 위에 이름 칸, 골라서 [만들기].
/// 오른쪽 클릭 "폴더에 넣기"는 만들지 않음 (사용자).
/// </summary>
internal sealed partial class AllAppsPanel
{
    /// <summary>앱 고르기 카드: 넣을 폴더 id (새 폴더면 null).</summary>
    private sealed class AddAppsState
    {
        public string? FolderId;
        public string Name = "";
        public readonly HashSet<string> Chosen = new(StringComparer.OrdinalIgnoreCase);
        public string Query = "";
    }

    private AddAppsState? _adding;

    /// <summary>시험·그림용: 앱 고르기 카드 열기.</summary>
    internal void OpenAddApps(string? folderId)
    {
        _adding = new AddAppsState { FolderId = folderId, Name = folderId is null ? Loc.T("새 폴더") : AllAppsCatalog.GroupName(S, folderId) };
        Rebuild();
    }

    /// <summary>펼친 폴더 마지막의 [+] 칸 (앱 칸과 같은 크기, 점선).</summary>
    private Border AddAppsCell(string folderId)
    {
        var glyph = new TextBlock { Text = "+", FontSize = 22, Foreground = P.SubText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var plate = new Border
        {
            Width = AppIcon,
            Height = AppIcon,
            CornerRadius = new CornerRadius(10),
            BorderBrush = P.SoftAccentLine,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = glyph,
        };
        var stack = new StackPanel();
        stack.Children.Add(plate);
        stack.Children.Add(new TextBlock { Text = Loc.T("앱 넣기"), FontSize = 11.5, Foreground = P.SubText, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        var cell = new Border
        {
            Width = AppCell - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 6, 2, 4),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Child = stack,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("이 폴더에 앱 넣기"),
            Uid = "m:addapps:" + folderId,
        };
        cell.MouseEnter += (_, _) => { cell.Background = P.Tile; glyph.Foreground = P.SoftAccentText; };
        cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; glyph.Foreground = P.SubText; };
        cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenAddApps(folderId); };
        return cell;
    }

    /// <summary>앱 고르기 카드 (판 본문 자리).</summary>
    private UIElement BuildAddApps()
    {
        var st = _adding!;
        bool create = st.FolderId is null;
        var already = create ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : (_folders.FirstOrDefault(f => f.Id == st.FolderId)?.Apps.Select(a => a.Key) ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        root.Children.Add(new TextBlock
        {
            Text = create ? Loc.T("새 폴더 만들기") : Loc.F($"'{st.Name}'에 앱 넣기"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 8),
        });
        TextBox? nameBox = null;
        if (create)
        {
            nameBox = MongField.Apply(new TextBox { Text = st.Name, FontSize = 13, MaxLength = 24, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6, 3, 6, 3) }, P);
            nameBox.TextChanged += (_, _) => st.Name = nameBox.Text;
            root.Children.Add(nameBox);
        }
        var search = MongField.Apply(new TextBox { Text = st.Query, FontSize = 13, Padding = new Thickness(6, 3, 6, 3) }, P);
        var searchHost = new Grid();
        var hint = new TextBlock { Text = Loc.T("앱 검색"), Foreground = P.Disabled, Margin = new Thickness(9, 4, 0, 0), IsHitTestVisible = false, Visibility = st.Query.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        searchHost.Children.Add(search);
        searchHost.Children.Add(hint);
        root.Children.Add(searchHost);

        // 목록은 판의 남은 높이를 다 채우고(제목·검색은 위, [취소][넣기]는 아래에 고정), 판이 넓으면(1000 DIP 이상) 2열 (2026-10-13 사용자: 목록이 너무 좁음)
        int cols = _panelWidth >= 1000 ? 2 : 1;
        var list = new System.Windows.Controls.Primitives.UniformGrid { Columns = cols, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) };
        var listScroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        MongScroll.Apply(listScroll, P);

        var add = new Button
        {
            Style = (Style)Application.Current.FindResource("CardButton"),
            Background = P.SoftAccent,
            Foreground = P.SoftAccentText,
            Height = 28,
            Padding = new Thickness(18, 0, 18, 0),
            Margin = new Thickness(8, 0, 0, 0),
        };
        void UpdateAdd()
        {
            int n = st.Chosen.Count(k => !already.Contains(k));
            add.Content = new TextBlock { Text = create ? (n > 0 ? Loc.F($"만들기 ({n})") : Loc.T("만들기")) : (n > 0 ? Loc.F($"넣기 ({n})") : Loc.T("넣기")), FontWeight = FontWeights.SemiBold };
            add.IsEnabled = create || n > 0;
        }
        void Fill()
        {
            list.Children.Clear();
            string q = st.Query.Trim();
            var apps = _apps.Where(a => !S.Hidden.Contains(a.Key, StringComparer.OrdinalIgnoreCase))
                .Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(a => already.Contains(a.Key) ? 1 : 0) // 이미 든 앱은 아래로
                .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(q.Length == 0 ? 400 : 120).ToList();
            if (apps.Count == 0) list.Children.Add(Muted(Loc.T("찾는 앱이 없어요")));
            foreach (var app in apps)
            {
                var a = app;
                bool inside = already.Contains(a.Key);
                var check = DockMenus.Check(P, null, inside || st.Chosen.Contains(a.Key));
                check.IsEnabled = !inside;
                check.VerticalAlignment = VerticalAlignment.Center;
                check.Margin = new Thickness(4, 0, 10, 0);
                check.Checked += (_, _) => { st.Chosen.Add(a.Key); UpdateAdd(); };
                check.Unchecked += (_, _) => { st.Chosen.Remove(a.Key); UpdateAdd(); };
                var img = new Image { Width = 30, Height = 30, Margin = new Thickness(0, 4, 12, 4) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                LoadIcon(img, a);
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(check);
                line.Children.Add(img);
                line.Children.Add(new TextBlock { Text = a.Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                if (inside) line.Children.Add(new TextBlock { Text = Loc.T("이미 들어 있어요"), FontSize = 11.5, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                var row = new Border { CornerRadius = new CornerRadius(6), Child = line, Opacity = inside ? 0.5 : 1, Background = Brushes.Transparent, Cursor = inside ? Cursors.Arrow : Cursors.Hand };
                if (!inside)
                {
                    row.MouseEnter += (_, _) => row.Background = P.Hover;
                    row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                    row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) check.IsChecked = check.IsChecked != true; };
                }
                list.Children.Add(row);
            }
        }
        search.TextChanged += (_, _) => { st.Query = search.Text; hint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };
        Fill();
        UpdateAdd();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 4) };
        var cancel = new Button { Style = (Style)Application.Current.FindResource("CardButton"), Content = new TextBlock { Text = Loc.T("취소") }, Background = P.Tile, Foreground = P.Text, Height = 28, Padding = new Thickness(18, 0, 18, 0) };
        cancel.Click += (_, _) => { _adding = null; Rebuild(); };
        add.Click += (_, _) => ApplyAddApps(st, already);
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);
        // 위(제목·이름·검색) / 가운데(목록 — 남은 높이 전부) / 아래(버튼)
        var page = new System.Windows.Controls.Grid { Height = Math.Max(240, _bodyMaxHeight - 8), Margin = root.Margin };
        root.Margin = new Thickness(0);
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.Children.Add(root);
        System.Windows.Controls.Grid.SetRow(listScroll, 1);
        page.Children.Add(listScroll);
        System.Windows.Controls.Grid.SetRow(buttons, 2);
        page.Children.Add(buttons);

        // 열리면 바로 입력: 새 폴더는 이름 칸(전체 선택), 넣기는 검색 칸
        Dispatcher.BeginInvoke(() =>
        {
            if (nameBox is not null) { nameBox.Focus(); nameBox.SelectAll(); }
            else search.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
        return page;
    }

    private void ApplyAddApps(AddAppsState st, HashSet<string> already)
    {
        var settings = Services.Settings.Current;
        var keys = st.Chosen.Where(k => !already.Contains(k)).ToArray();
        string id;
        if (st.FolderId is null)
        {
            string name = st.Name.Trim();
            id = AppFolders.Create(settings, _apps, name.Length > 0 ? name : Loc.T("새 폴더"), keys);
        }
        else
        {
            id = st.FolderId;
            foreach (var k in keys) AppFolders.AddTo(settings, _apps, id, k); // 손댄 폴더가 됨 → 자동 분류·"쓰는 앱만" 규칙이 빼지 않음
        }
        Log.Info($"앱 모음 판: {(st.FolderId is null ? "새 폴더 만들기" : "폴더에 앱 넣기")} — 앱 {keys.Length}개");
        _adding = null;
        _expanded = id;
        Save();
    }
}
