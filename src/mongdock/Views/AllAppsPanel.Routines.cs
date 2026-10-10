using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 앱 모음 판 맨 위 "루틴" 줄 (#24-A, spec-all-apps-panel 최종): 루틴 칸들 + 줄 끝 [+ 루틴 추가](지금 화면 그대로 저장 / 직접 만들기 / 루틴이 뭔가요?) + 오른쪽 (?).
/// 칸 클릭 = 실행, 오른쪽 클릭 = 루틴 메뉴(편집·끝내기·독에 고정·지우기), 끌어서 줄 안 순서 바꾸기·독에 놓으면 고정. 실행 중이면 아이콘 아래 점.
/// 루틴이 0개면 줄 대신 설명 카드 [지금 화면으로 만들기] [직접 만들기]. 업데이트로 처음 받은 사용자에게 제목 옆 NEW.
/// </summary>
internal sealed partial class AllAppsPanel
{
    public const string RoutineFormat = "mongdock.allapps.routine";

    private bool _routineHelp;

    private UIElement RoutineSection()
    {
        var routines = Services.Settings.Current.Routines;
        var root = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        var title = new DockPanel { LastChildFill = false };
        var name = SectionTitle(Loc.T("루틴"));
        DockPanel.SetDock(name, Dock.Left);
        title.Children.Add(name);
        if (NewBadges.Show(RoutineUi.Badge))
        {
            var badge = new Border
            {
                Background = DockMenus.NewBadgeBrush,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(0, 6, 0, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "NEW", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = DockMenus.NewBadgeText },
            };
            DockPanel.SetDock(badge, Dock.Left);
            title.Children.Add(badge);
        }
        if (routines.Count > 0)
        {
            var help = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                BorderBrush = P.SubText,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 8, 2),
                Cursor = Cursors.Hand,
                ToolTip = Loc.T("루틴이 뭔가요?"),
                Child = new TextBlock { Text = "?", FontSize = 11, Foreground = P.SubText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            help.MouseLeftButtonUp += (_, e) => { e.Handled = true; _routineHelp = !_routineHelp; Rebuild(); };
            DockPanel.SetDock(help, Dock.Right);
            title.Children.Add(help);
        }
        root.Children.Add(title);

        if (routines.Count == 0 || _routineHelp)
        {
            root.Children.Add(RoutineExplainCard(showButtons: routines.Count == 0));
            if (routines.Count == 0) return root;
        }
        var row = new WrapPanel { Width = Cols * AppCell };
        foreach (var r in routines) row.Children.Add(RoutineCell(r));
        row.Children.Add(AddRoutineCell());
        root.Children.Add(row);
        return root;
    }

    private Border RoutineCell(RoutineDef routine)
    {
        var image = new Image { Width = AppIcon, Height = AppIcon, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        try { image.Source = RoutineIcons.Icon(Services, routine, _style); }
        catch (Exception ex) { Log.Warn($"루틴 아이콘 실패: {ex.GetType().Name}"); }
        var stack = new StackPanel();
        stack.Children.Add(image);
        // 실행 중 = 아이콘 아래 점 (깜빡이지 않음)
        stack.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = RoutineService.IsRunning(routine.Id) || RoutineService.IsOpening(routine.Id) ? P.Text : Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        stack.Children.Add(new TextBlock { Text = routine.Name, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0) });
        var cell = new Border
        {
            Width = AppCell - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 6, 2, 4),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Child = stack,
            ToolTip = RoutineIcons.Tooltip(routine),
            Cursor = Cursors.Hand,
            Tag = new DropTag("routine", routine.Id),
            ContextMenu = LazyMenu(m => RoutineUi.FillMenu(m, Services, routine, CloseAnimated)),
        };
        cell.MouseEnter += (_, _) => cell.Background = P.Tile;
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        Pressable(cell, () =>
        {
            if (routine.Items.Count == 0) { CloseAnimated(); RoutineEditorWindow.Open(Services, routine); return; }
            CloseAnimated();
            RoutineUi.Run(Services, routine);
        }, new DragItem(false, routine.Id, IsRoutine: true), () =>
        {
            var data = new DataObject();
            data.SetData(RoutineFormat, routine.Id); // 독에 놓으면 고정
            return data;
        });
        return cell;
    }

    /// <summary>
    /// 줄 끝 [+ 루틴 추가]: 루틴 칸과 똑같은 자리 — 아이콘 본체(44px 그림 안의 둥근 사각 ≈ 80%)와 같은 크기·모서리의 연한 회보라 점선,
    /// 그 아래 점 자리와 이름 기준선도 같게. "+"는 회색(마우스를 올리면 연보라), 글자는 다른 칸 이름과 같은 색·굵기.
    /// 누르면 "지금 화면 그대로 저장 / 직접 만들기 / 루틴이 뭔가요?".
    /// </summary>
    private Border AddRoutineCell()
    {
        double body = Math.Round(AppIcon * 0.805); // 맥 아이콘 그리드: 256 캔버스 안 본체 206 (MacIconRenderer)
        var slot = new Grid { Width = AppIcon, Height = AppIcon, HorizontalAlignment = HorizontalAlignment.Center };
        var dashed = DashedRect(body * 0.225);
        dashed.Width = body;
        dashed.Height = body;
        dashed.HorizontalAlignment = HorizontalAlignment.Center;
        dashed.VerticalAlignment = VerticalAlignment.Center;
        slot.Children.Add(dashed);
        var glyph = PlusGlyph(14);
        slot.Children.Add(glyph);
        var stack = new StackPanel();
        stack.Children.Add(slot);
        stack.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) }); // 루틴 칸의 실행 점 자리
        stack.Children.Add(new TextBlock { Text = Loc.T("루틴 추가"), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11.5, Margin = new Thickness(0, 1, 0, 0) });
        var cell = new Border
        {
            Width = AppCell - 4,
            Margin = new Thickness(2),
            Padding = new Thickness(2, 6, 2, 4),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1.5), // 루틴 칸과 같은 테두리 자리 (위치가 어긋나지 않게)
            Child = stack,
            Cursor = Cursors.Hand,
        };
        cell.MouseEnter += (_, _) => { cell.Background = P.Tile; glyph.Foreground = P.SoftAccentText; };
        cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; glyph.Foreground = P.SubText; };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu { PlacementTarget = cell, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            FillAddRoutineMenu(menu);
            menu.IsOpen = true;
        };
        return cell;
    }

    private void FillAddRoutineMenu(ContextMenu menu)
    {
        bool can = RoutineUi.CanAdd(Services);
        menu.Items.Add(DockMenus.Item(Loc.T("지금 화면 그대로 저장"), () => { CloseAnimated(); RoutineSaveWindow.Open(Services, pinToDock: false); }, enabled: can));
        menu.Items.Add(DockMenus.Item(Loc.T("직접 만들기"), () => { CloseAnimated(); RoutineEditorWindow.OpenNew(Services); }, enabled: can));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item(Loc.T("루틴이 뭔가요?"), () => { _routineHelp = true; Rebuild(); }));
    }

    /// <summary>설명 카드: 아이콘 하나에서 창 3개가 펼쳐지는 그림 + 문구 (+ 0개일 때 [지금 화면으로 만들기] [직접 만들기]).</summary>
    private UIElement RoutineExplainCard(bool showButtons)
    {
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(BurstPicture());
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = Loc.T("루틴 — 자주 쓰는 앱들을 한 번에 열어요. 예: '업무 시작'을 누르면 아웃룩·크롬·한글이 정해 둔 모니터와 자리에 한꺼번에 열려요."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
        });
        text.Children.Add(new TextBlock
        {
            Text = Loc.T("기본은 새 데스크톱에서 열리고, 끝낼 때 루틴이 연 창만 한 번에 닫아요."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Foreground = P.SubText,
            Margin = new Thickness(0, 4, 0, 0),
        });
        if (showButtons)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            Button B(string label, bool primary, Action run)
            {
                var b = new Button
                {
                    Style = (Style)Application.Current.FindResource("CardButton"),
                    Content = new TextBlock { Text = label, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
                    Background = primary ? P.SoftAccent : P.CardBackground,
                    Foreground = primary ? P.SoftAccentText : P.Text,
                    Height = 28,
                    Padding = new Thickness(14, 0, 14, 0),
                    Margin = new Thickness(0, 0, 8, 0),
                };
                b.Click += (_, _) => run();
                return b;
            }
            buttons.Children.Add(B(Loc.T("지금 화면으로 만들기"), true, () => { CloseAnimated(); RoutineSaveWindow.Open(Services, pinToDock: false); }));
            buttons.Children.Add(B(Loc.T("직접 만들기"), false, () => { CloseAnimated(); RoutineEditorWindow.OpenNew(Services); }));
            text.Children.Add(buttons);
        }
        else
        {
            var close = SmallLink(Loc.T("닫기"), () => { _routineHelp = false; Rebuild(); });
            close.Margin = new Thickness(0, 6, 0, 0);
            close.HorizontalAlignment = HorizontalAlignment.Left;
            text.Children.Add(close);
        }
        System.Windows.Controls.Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return new Border { Background = P.Tile, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(4, 0, 4, 6), Child = grid };
    }

    /// <summary>아이콘 하나(몽독 하늘색 판) 오른쪽 위로 창 3개가 부채꼴로 펼쳐지는 작은 그림.</summary>
    private UIElement BurstPicture()
    {
        var canvas = new Canvas { Width = 112, Height = 76 };
        var colors = new[] { Color.FromRgb(0x9F, 0xD3, 0xFF), Color.FromRgb(0xC2, 0xB4, 0xFF), Color.FromRgb(0xA8, 0xE6, 0xD6) };
        for (int i = 0; i < 3; i++)
        {
            var win = new Border
            {
                Width = 46,
                Height = 32,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(colors[i]),
                BorderBrush = P.CardBackground,
                BorderThickness = new Thickness(1.5),
                Child = new Border { Height = 6, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(3, 3, 0, 0) },
            };
            Canvas.SetLeft(win, 36 + i * 22);
            Canvas.SetTop(win, 4 + i * 12);
            canvas.Children.Add(win);
        }
        var icon = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = new LinearGradientBrush(MacIconRenderer.SkyTop, MacIconRenderer.SkyBottom, 75),
            Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Canvas.SetLeft(icon, 0);
        Canvas.SetTop(icon, 32);
        canvas.Children.Add(icon);
        return canvas;
    }

    /// <summary>루틴 줄 안에서 끌어 순서 바꾸기: moved 를 before 앞으로.</summary>
    private void MoveRoutineBefore(string moved, string before)
    {
        var list = Services.Settings.Current.Routines;
        var item = list.FirstOrDefault(r => r.Id == moved);
        if (item is null || moved == before) return;
        list.Remove(item);
        int at = list.FindIndex(r => r.Id == before);
        list.Insert(at < 0 ? list.Count : at, item);
        Save(false);
    }
}
