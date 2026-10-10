using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 루틴 편집 창 (#24-A, spec-routines §3): 위는 크게(이름·아이콘·어디서·항목 목록), 세부는 항목의 ▸ 안에 접어서
/// (함께 열 것·고급 실행 옵션, 모니터, 창 위치, 다음 항목까지 기다리기, 이미 켜져 있으면).
/// ≡ 를 끌어 여는 순서, × 로 빼기. [+ 앱] [+ 웹사이트] [+ 파일·폴더] [지금 화면에서 다시 읽기], [루틴 지우기] [취소] [저장].
/// 고치는 동안은 사본 — [저장]을 눌러야 반영.
/// </summary>
internal sealed class RoutineEditorWindow : RoutineCardWindow
{
    private static RoutineEditorWindow? _open;

    private readonly RoutineDef? _original;
    private bool _pinToDock;
    private readonly RoutineDef _r;
    private readonly TextBox _name;
    private readonly Image _iconImage = new() { Width = 28, Height = 28 };
    private readonly StackPanel _list = new();
    private readonly Border _picker = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _status;
    private readonly WrapPanel _addButtons = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly List<FrameworkElement> _rowHeads = new();
    private int _expanded = -1;
    private bool _advancedOpen;

    public static void Open(AppServices services, RoutineDef routine, bool focusName = false)
    {
        _open?.Close();
        var w = new RoutineEditorWindow(services, routine, Copy(routine), focusName);
        Show(w);
    }

    /// <summary>새 루틴 (빈 목록, 또는 "루틴에 넣기 ▸ 새 루틴…"의 첫 항목).</summary>
    public static void OpenNew(AppServices services, RoutineItem? first = null, bool pinToDock = false)
    {
        _open?.Close();
        if (!RoutineUi.CanAdd(services))
        {
            ConfirmCardWindow.Ask(services, Loc.T("루틴이 가득 찼어요"), Loc.F($"루틴은 {RoutineDef.MaxRoutines}개까지 만들 수 있어요. 안 쓰는 루틴을 지운 뒤 다시 해 주세요."), Loc.T("확인"), () => { });
            return;
        }
        var r = new RoutineDef { Name = RoutineUi.NextName(services) };
        if (first is not null) r.Items.Add(first);
        Show(new RoutineEditorWindow(services, null, r, focusName: true) { _pinToDock = pinToDock });
    }

    private static void Show(RoutineEditorWindow w)
    {
        _open = w;
        w.Closed += (_, _) => { if (_open == w) _open = null; };
        w.Show();
        w.Activate();
    }

    private static RoutineDef Copy(RoutineDef r) => JsonSerializer.Deserialize<RoutineDef>(JsonSerializer.Serialize(r)) ?? new RoutineDef();

    /// <summary>시험 그림에서만: 이 항목의 세부를 펼친 채로.</summary>
    internal void ExpandForTest(int index, bool advanced = false)
    {
        _expanded = index;
        _advancedOpen = advanced;
        Rebuild();
    }

    internal RoutineEditorWindow(AppServices services, RoutineDef? original, RoutineDef working, bool focusName) : base(services, 600, "mongdock Routine")
    {
        _original = original;
        _r = working;
        Body.Children.Add(Heading(original is null ? Loc.T("새 루틴") : Loc.T("루틴 편집")));

        // 이름 + 아이콘
        _name = Field(_r.Name);
        _name.MaxLength = 30;
        var nameRow = new DockPanel { LastChildFill = true };
        var iconButton = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = P.Tile,
            Padding = new Thickness(4, 2, 10, 2),
            Margin = new Thickness(10, 0, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = true,
            ToolTip = Loc.T("아이콘 바꾸기"),
        };
        var iconStack = new StackPanel { Orientation = Orientation.Horizontal };
        RenderOptions.SetBitmapScalingMode(_iconImage, BitmapScalingMode.HighQuality);
        iconStack.Children.Add(_iconImage);
        iconStack.Children.Add(new TextBlock { Text = Loc.T("아이콘"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), FontSize = 12 });
        iconButton.Child = iconStack;
        iconButton.MouseLeftButtonUp += (_, _) => PickIcon();
        iconButton.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; PickIcon(); } };
        DockPanel.SetDock(iconButton, Dock.Right);
        nameRow.Children.Add(iconButton);
        nameRow.Children.Add(_name);
        Body.Children.Add(LabeledRow(Loc.T("이름"), nameRow));

        // 어디서 (데스크톱)
        var desktopRow = new StackPanel { Orientation = Orientation.Horizontal };
        var number = MenuPicker(Enumerable.Range(1, 9).Select(n => n.ToString()).ToList(), Math.Clamp(_r.Desktop.Index, 1, 9) - 1, i => _r.Desktop.Index = i + 1, 52);
        number.IsEnabled = _r.Desktop.Mode == RoutineDesktopMode.Index;
        number.Opacity = number.IsEnabled ? 1 : 0.45;
        int mode = _r.Desktop.Mode switch { RoutineDesktopMode.Current => 1, RoutineDesktopMode.Index => 2, _ => 0 };
        desktopRow.Children.Add(Segments(new[] { Loc.T("새 데스크톱 만들어서 (기본)"), Loc.T("지금 데스크톱"), Loc.T("데스크톱") }, mode, i =>
        {
            _r.Desktop.Mode = i switch { 1 => RoutineDesktopMode.Current, 2 => RoutineDesktopMode.Index, _ => RoutineDesktopMode.New };
            number.IsEnabled = i == 2;
        }));
        desktopRow.Children.Add(number);
        Body.Children.Add(LabeledRow(Loc.T("어디서"), desktopRow, bottom: 4));

        // 항목
        var items = new StackPanel();
        items.Children.Add(Scroll(_list));
        items.Children.Add(_addButtons);
        items.Children.Add(_picker);
        Body.Children.Add(LabeledRow(Loc.T("항목"), items, bottom: 4));
        _status = Muted("", 11.5);
        _status.Margin = new Thickness(76, 0, 0, 0);
        Body.Children.Add(_status);

        // 아래 버튼
        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        if (original is not null)
        {
            var delete = CardButton(Loc.T("루틴 지우기"), danger: true);
            delete.Click += async (_, _) =>
            {
                Topmost = false;
                if (await ConfirmCardWindow.AskAsync(Services, Loc.T("루틴 지우기"), Loc.F($"'{original.Name}' 루틴을 지울까요? 앱과 파일은 그대로예요."), Loc.T("지우기")))
                {
                    RoutineUi.Delete(Services, original);
                    Close();
                }
                else Topmost = true;
            };
            DockPanel.SetDock(delete, Dock.Left);
            bottom.Children.Add(delete);
        }
        var save = CardButton(Loc.T("저장"), primary: true);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) => Save();
        var cancel = CardButton(Loc.T("취소"));
        cancel.Click += (_, _) => Close();
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        bottom.Children.Add(save);
        bottom.Children.Add(cancel);
        Body.Children.Add(bottom);
        Body.Children.Add(Muted(Loc.F($"루틴 하나에 항목은 {RoutineDef.MaxItems}개까지예요."), 11));

        BuildAddButtons();
        RefreshIcon();
        Rebuild();
        Loaded += (_, _) =>
        {
            if (!focusName) return;
            _name.Focus();
            Keyboard.Focus(_name);
            _name.SelectAll();
        };
    }

    // ───────────────────────── 위쪽 ─────────────────────────

    private IconStyle IconStyleNow => Services.Settings.Current.Dock.IconStyle;

    private void RefreshIcon()
    {
        try { _iconImage.Source = RoutineIcons.Icon(Services, _r, IconStyleNow); }
        catch (Exception ex) { Log.Warn($"루틴 아이콘 미리 보기 실패: {ex.GetType().Name}"); }
    }

    private void PickIcon()
    {
        IconPickerWindow.Open(Services, _r.Icon, keepDefaultGlyph: false, Loc.T("담긴 항목 아이콘 4개를 모아요."),
            preview: icon => PinIconRenderer.Render(icon) ?? RoutineIcons.Icon(Services, new RoutineDef { Items = _r.Items }, IconStyleNow),
            done: icon =>
            {
                _r.Icon = icon;
                RefreshIcon();
            });
    }

    private void BuildAddButtons()
    {
        _addButtons.Children.Clear();
        bool full = _r.Items.Count >= RoutineDef.MaxItems;
        Button Small(string text, Action run, bool enabled = true)
        {
            var b = CardButton(text);
            b.Height = 28;
            b.MinWidth = 0;
            b.Padding = new Thickness(12, 0, 12, 0);
            b.Margin = new Thickness(0, 0, 6, 6);
            b.IsEnabled = enabled;
            b.Click += (_, _) => run();
            return b;
        }
        _addButtons.Children.Add(Small("+ " + Loc.T("앱"), ShowAppPicker, !full));
        _addButtons.Children.Add(Small("+ " + Loc.T("웹사이트"), AddWebsite, !full));
        var file = Small("+ " + Loc.T("파일·폴더"), () => { }, !full);
        file.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = file, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            menu.Items.Add(DockMenus.Item(Loc.T("파일…"), () => AddPath(folder: false)));
            menu.Items.Add(DockMenus.Item(Loc.T("폴더…"), () => AddPath(folder: true)));
            menu.IsOpen = true;
        };
        _addButtons.Children.Add(file);
        _addButtons.Children.Add(Small(Loc.T("지금 화면에서 다시 읽기"), Reread, _r.Items.Count > 0));
    }

    // ───────────────────────── 항목 목록 ─────────────────────────

    private void Rebuild()
    {
        _list.Children.Clear();
        _rowHeads.Clear();
        if (_r.Items.Count == 0)
            _list.Children.Add(Muted(Loc.T("아직 항목이 없어요. 아래 버튼으로 앱·웹사이트·파일을 넣어 주세요."), 12.5));
        for (int i = 0; i < _r.Items.Count; i++)
        {
            _list.Children.Add(Row(i));
            if (i == _expanded) _list.Children.Add(Detail(_r.Items[i]));
        }
        BuildAddButtons();
        RefreshIcon();
    }

    private UIElement Row(int index)
    {
        var item = _r.Items[index];
        bool missing = RoutineIcons.Missing(item);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        foreach (var w in new[] { 18.0, 32.0, double.NaN, -1, 22, 24 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : w < 0 ? GridLength.Auto : new GridLength(w) });

        var handle = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeNS, ToolTip = Loc.T("끌어서 순서 바꾸기") };
        HookReorder(handle, index);
        grid.Children.Add(handle);

        var icon = new Image { Width = 22, Height = 22, Source = RoutineIcons.ItemIcon(Services, item, IconStyleNow), Opacity = missing ? 0.4 : 1, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = missing ? 0.5 : 1 };
        var pill = Pill("");
        pill.Margin = new Thickness(8, 0, 6, 0);
        void Header()
        {
            name.Inlines.Clear();
            string title = item.Kind == RoutineItemKind.Url && item.Target.Length == 0 ? Loc.T("웹사이트") : RoutineService.ItemName(item);
            name.Inlines.Add(new System.Windows.Documents.Run(title));
            string? extra = item.Kind switch
            {
                RoutineItemKind.App => item.Open,
                RoutineItemKind.Path => item.Target,
                _ => null,
            };
            if (missing) extra = Loc.T("이 PC에 없음 — 건너뛰어요");
            if (!string.IsNullOrEmpty(extra)) name.Inlines.Add(new System.Windows.Documents.Run("  " + extra) { Foreground = P.SubText, FontSize = 11.5 });
            ((TextBlock)pill.Child).Text = RoutineIcons.PlacementText(item);
        }
        Header();
        name.Tag = (Action)Header;
        _rowHeads.Add(name);
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);
        Grid.SetColumn(pill, 3);
        grid.Children.Add(pill);

        var chevron = new TextBlock { Text = index == _expanded ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = index == _expanded ? P.Accent : P.SubText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = Loc.T("세부") };
        Grid.SetColumn(chevron, 4);
        grid.Children.Add(chevron);

        var remove = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = Loc.T("빼기") };
        remove.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveAt(index); };
        Grid.SetColumn(remove, 5);
        grid.Children.Add(remove);

        var row = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(4, 4, 2, 4), Child = grid, Background = index == _expanded ? P.Tile : Brushes.Transparent, Focusable = true, BorderBrush = P.Accent, BorderThickness = new Thickness(0) };
        row.MouseEnter += (_, _) => { if (index != _expanded) row.Background = P.Hover; };
        row.MouseLeave += (_, _) => { if (index != _expanded) row.Background = Brushes.Transparent; };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            _expanded = _expanded == index ? -1 : index;
            Rebuild();
        };
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { e.Handled = true; _expanded = _expanded == index ? -1 : index; Rebuild(); }
            else if (e.Key == Key.Delete) { e.Handled = true; RemoveAt(index); }
            else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; Move(index, index - 1); }
            else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt) { e.Handled = true; Move(index, index + 1); }
        };
        var menu = new ContextMenu();
        menu.Items.Add(DockMenus.Item(Loc.T("위로 이동"), () => Move(index, index - 1), enabled: index > 0));
        menu.Items.Add(DockMenus.Item(Loc.T("아래로 이동"), () => Move(index, index + 1), enabled: index < _r.Items.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockMenus.Item(Loc.T("빼기"), () => RemoveAt(index)));
        row.ContextMenu = menu;
        row.Tag = index;
        return row;
    }

    private void RemoveAt(int index)
    {
        if (index < 0 || index >= _r.Items.Count) return;
        _r.Items.RemoveAt(index);
        if (_expanded == index) _expanded = -1;
        else if (_expanded > index) _expanded--;
        Rebuild();
    }

    private void Move(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= _r.Items.Count || to >= _r.Items.Count) return;
        var item = _r.Items[from];
        bool open = _expanded == from;
        _r.Items.RemoveAt(from);
        _r.Items.Insert(to, item);
        if (open) _expanded = to;
        else if (_expanded >= 0)
        {
            if (from < _expanded && to >= _expanded) _expanded--;
            else if (from > _expanded && to <= _expanded) _expanded++;
        }
        Rebuild();
        if (_list.Children.OfType<Border>().FirstOrDefault(b => b.Tag is int i && i == to) is { } moved) moved.Focus();
    }

    /// <summary>≡ 를 눌러 위아래로 끌면 놓을 자리 위에 선, 놓으면 그 자리로.</summary>
    private void HookReorder(FrameworkElement handle, int index)
    {
        Border? marked = null;
        int target = index;
        handle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            handle.CaptureMouse();
        };
        handle.MouseMove += (_, e) =>
        {
            if (!handle.IsMouseCaptured) return;
            var rows = _list.Children.OfType<Border>().Where(b => b.Tag is int).ToList();
            double y = e.GetPosition(_list).Y;
            target = rows.Count - 1;
            foreach (var r in rows)
            {
                var top = r.TranslatePoint(new Point(0, 0), _list).Y;
                if (y < top + r.ActualHeight / 2) { target = (int)r.Tag; break; }
            }
            if (marked is not null) marked.BorderThickness = new Thickness(0);
            marked = rows.FirstOrDefault(r => (int)r.Tag == target);
            if (marked is not null && target != index) marked.BorderThickness = target > index ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (!handle.IsMouseCaptured) return;
            e.Handled = true;
            handle.ReleaseMouseCapture();
            if (marked is not null) marked.BorderThickness = new Thickness(0);
            Move(index, target);
        };
    }

    // ───────────────────────── 세부 ─────────────────────────

    private UIElement Detail(RoutineItem item)
    {
        var box = new StackPanel();
        var frame = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC3, 0xB3, 0xFF)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(14, 8, 0, 4),
            Margin = new Thickness(26, 0, 0, 8),
            Child = box,
        };
        void Changed() { foreach (var h in _rowHeads) (h.Tag as Action)?.Invoke(); }
        const double lw = 88;

        switch (item.Kind)
        {
            case RoutineItemKind.App:
            {
                int kind = OpenKind(item.Open);
                var field = Field(item.Open ?? "");
                var pick = CardButton(Loc.T("고르기…"));
                pick.Height = 28;
                pick.MinWidth = 0;
                pick.Margin = new Thickness(6, 0, 0, 0);
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                DockPanel.SetDock(pick, Dock.Right);
                line.Children.Add(pick);
                line.Children.Add(Hinted(field, Loc.T("파일·폴더 경로 또는 웹 주소")));
                void ShowField() { line.Visibility = kind == 0 ? Visibility.Collapsed : Visibility.Visible; pick.Visibility = kind is 1 or 2 ? Visibility.Visible : Visibility.Collapsed; }
                field.TextChanged += (_, _) => { item.Open = string.IsNullOrWhiteSpace(field.Text) ? null : field.Text.Trim(); Changed(); };
                pick.Click += (_, _) =>
                {
                    if (PickPath(kind == 2) is { } path) field.Text = path;
                };
                var open = new StackPanel();
                open.Children.Add(Segments(new[] { Loc.T("없음"), Loc.T("파일"), Loc.T("폴더"), Loc.T("웹 주소") }, kind, i =>
                {
                    kind = i;
                    if (i == 0) field.Text = "";
                    ShowField();
                }));
                open.Children.Add(line);
                ShowField();
                // 고급: 실행 옵션 (접힘)
                var argsField = Field(item.Args ?? "");
                argsField.FontFamily = new FontFamily("Consolas");
                argsField.TextChanged += (_, _) => item.Args = string.IsNullOrWhiteSpace(argsField.Text) ? null : argsField.Text.Trim();
                argsField.Visibility = _advancedOpen ? Visibility.Visible : Visibility.Collapsed;
                var advanced = new TextBlock { Text = Loc.T("고급: 실행 옵션") + (_advancedOpen ? " ▾" : " ▸"), FontSize = 12, Foreground = P.SubText, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 4) };
                advanced.MouseLeftButtonUp += (_, _) =>
                {
                    _advancedOpen = !_advancedOpen;
                    argsField.Visibility = _advancedOpen ? Visibility.Visible : Visibility.Collapsed;
                    advanced.Text = Loc.T("고급: 실행 옵션") + (_advancedOpen ? " ▾" : " ▸");
                };
                open.Children.Add(advanced);
                open.Children.Add(argsField);
                box.Children.Add(LabeledRow(Loc.T("함께 열 것"), open, lw, 6));
                break;
            }
            case RoutineItemKind.Url:
            {
                var field = Field(item.Target);
                field.TextChanged += (_, _) => { item.Target = field.Text.Trim(); Changed(); };
                box.Children.Add(LabeledRow(Loc.T("주소"), Hinted(field, "https://"), lw, 8));
                if (item.Target.Length == 0) Dispatcher.BeginInvoke(() => { field.Focus(); Keyboard.Focus(field); }, System.Windows.Threading.DispatcherPriority.Input);
                break;
            }
            default:
            {
                var field = Field(item.Target);
                var pick = CardButton(Loc.T("고르기…"));
                pick.Height = 28;
                pick.MinWidth = 0;
                pick.Margin = new Thickness(6, 0, 0, 0);
                pick.Click += (_, _) => { if (PickPath(Directory.Exists(item.Target)) is { } path) field.Text = path; };
                field.TextChanged += (_, _) => { item.Target = field.Text.Trim(); item.Name = null; Changed(); };
                var line = new DockPanel();
                DockPanel.SetDock(pick, Dock.Right);
                line.Children.Add(pick);
                line.Children.Add(field);
                box.Children.Add(LabeledRow(Loc.T("파일·폴더"), line, lw, 8));
                break;
            }
        }

        // 모니터: 그대로 / 주 모니터 / 1, 2, …
        var monitors = Monitors.GetAll().Select(m => m.Number).Where(n => n > 0).OrderBy(n => n).ToList();
        if (item.Monitor is { Mode: RoutineMonitorMode.Index } saved && !monitors.Contains(saved.Index)) monitors.Add(saved.Index);
        var monitorLabels = new List<string> { Loc.T("그대로"), Loc.T("주 모니터") };
        monitorLabels.AddRange(monitors.Select(n => n.ToString()));
        int monitorAt = (item.Monitor?.Mode ?? RoutineMonitorMode.Keep) switch
        {
            RoutineMonitorMode.Primary => 1,
            RoutineMonitorMode.Index => 2 + Math.Max(0, monitors.IndexOf(item.Monitor!.Index)),
            _ => 0,
        };
        box.Children.Add(LabeledRow(Loc.T("모니터"), Segments(monitorLabels, monitorAt, i =>
        {
            item.Monitor = i switch
            {
                0 => null,
                1 => new RoutineMonitor { Mode = RoutineMonitorMode.Primary },
                _ => new RoutineMonitor { Mode = RoutineMonitorMode.Index, Index = monitors[i - 2], DeviceName = Monitors.GetAll().FirstOrDefault(m => m.Number == monitors[i - 2])?.DeviceName },
            };
            Changed();
        }), lw, 2));

        // 창 위치
        var modes = new[] { RoutinePlacementMode.Keep, RoutinePlacementMode.Max, RoutinePlacementMode.Left, RoutinePlacementMode.Right, RoutinePlacementMode.Top, RoutinePlacementMode.Bottom, RoutinePlacementMode.Saved, RoutinePlacementMode.Min };
        int placeAt = Array.IndexOf(modes, item.Placement?.Mode ?? RoutinePlacementMode.Keep);
        box.Children.Add(LabeledRow(Loc.T("창 위치"), Segments(modes.Select(RoutineIcons.PlacementName).ToList(), Math.Max(0, placeAt), i =>
        {
            var rect = item.Placement?.Rect;
            item.Placement = modes[i] == RoutinePlacementMode.Keep ? null : new RoutinePlacement { Mode = modes[i], Rect = modes[i] == RoutinePlacementMode.Saved ? rect ?? new[] { 0.1, 0.1, 0.8, 0.8 } : null };
            Changed();
        }), lw, 2));

        // 다음 항목까지 기다리기 0~10초
        var wait = MenuPicker(Enumerable.Range(0, 11).Select(s => Loc.F($"{s}초")).ToList(), Math.Clamp((int)Math.Round(item.DelayMs / 1000.0), 0, 10), i => item.DelayMs = i * 1000);
        var waitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        waitRow.Children.Add(wait);
        waitRow.Children.Add(new TextBlock { Text = Loc.T("다음 항목까지 · 무거운 앱 뒤에 써요"), Foreground = P.SubText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        box.Children.Add(LabeledRow(Loc.T("기다리기"), waitRow, lw, 4));

        // 이미 켜져 있으면 (앱만)
        if (item.Kind == RoutineItemKind.App)
            box.Children.Add(LabeledRow(Loc.T("켜져 있으면"), Segments(new[] { Loc.T("앞으로 가져오기"), Loc.T("하나 더 열기") }, item.IfRunning == RoutineIfRunning.New ? 1 : 0,
                i => item.IfRunning = i == 1 ? RoutineIfRunning.New : RoutineIfRunning.Focus), lw, 0));
        return frame;
    }

    /// <summary>함께 열 것의 종류 (0 없음, 1 파일, 2 폴더, 3 웹 주소).</summary>
    private static int OpenKind(string? open)
    {
        if (string.IsNullOrWhiteSpace(open)) return 0;
        if (open.Contains("://") || open.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return 3;
        try { return Directory.Exists(Environment.ExpandEnvironmentVariables(open)) ? 2 : 1; }
        catch (ArgumentException) { return 1; }
    }

    private string? PickPath(bool folder)
    {
        bool top = Topmost;
        Topmost = false; // 파일 고르기 창이 뒤로 가지 않게
        try
        {
            if (folder)
            {
                var d = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("폴더 고르기") };
                return d.ShowDialog(this) == true ? d.FolderName : null;
            }
            var f = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("파일 고르기"), CheckFileExists = true };
            return f.ShowDialog(this) == true ? f.FileName : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"루틴 파일 고르기 실패: {ex.GetType().Name}");
            return null;
        }
        finally { Topmost = top; }
    }

    // ───────────────────────── 넣기 ─────────────────────────

    private void AddItem(RoutineItem item, bool expand = false)
    {
        if (_r.Items.Count >= RoutineDef.MaxItems) return;
        _r.Items.Add(item);
        if (expand) _expanded = _r.Items.Count - 1;
        Rebuild();
    }

    private void AddWebsite() => AddItem(new RoutineItem { Kind = RoutineItemKind.Url, Target = "" }, expand: true);

    private void AddPath(bool folder)
    {
        if (PickPath(folder) is not { } path) return;
        AddItem(new RoutineItem { Kind = RoutineItemKind.Path, Target = path, Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } n ? n : path });
    }

    private void Reread()
    {
        int n = RoutineService.RereadPlacements(_r.Items);
        _status.Text = n == 0 ? Loc.T("지금 화면에 이 루틴의 창이 없어요") : Loc.F($"{n}개 항목의 모니터·위치를 지금 화면으로 채웠어요");
        Rebuild();
    }

    /// <summary>[+ 앱]: 설치된 앱 목록에서 체크 (검색 칸 포함) → [넣기].</summary>
    private void ShowAppPicker()
    {
        if (_picker.Visibility == Visibility.Visible) { _picker.Visibility = Visibility.Collapsed; return; }
        var box = new StackPanel();
        var search = Field("");
        box.Children.Add(Hinted(search, Loc.T("앱 검색")));
        var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var scroll = Scroll(rows);
        scroll.MaxHeight = 220;
        box.Children.Add(scroll);
        var chosen = new List<AppEntry>();
        var add = CardButton(Loc.T("넣기"), primary: true);
        add.IsEnabled = false;
        var close = CardButton(Loc.T("닫기"));
        close.Margin = new Thickness(0, 0, 8, 0);
        close.Click += (_, _) => _picker.Visibility = Visibility.Collapsed;
        add.Click += (_, _) =>
        {
            foreach (var a in chosen.Take(RoutineDef.MaxItems - _r.Items.Count)) _r.Items.Add(RoutineUi.ItemFromApp(Services, a));
            _picker.Visibility = Visibility.Collapsed;
            Rebuild();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(close);
        buttons.Children.Add(add);
        box.Children.Add(buttons);
        _picker.Child = box;
        _picker.Background = P.Tile;
        _picker.CornerRadius = new CornerRadius(10);
        _picker.Padding = new Thickness(10);
        _picker.Margin = new Thickness(0, 2, 0, 0);
        _picker.Visibility = Visibility.Visible;

        IReadOnlyList<AppEntry> apps = AllAppsCatalog.Cached ?? Array.Empty<AppEntry>();
        void Fill()
        {
            rows.Children.Clear();
            string q = search.Text.Trim();
            var list = (q.Length == 0 ? apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    : apps.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).OrderBy(a => a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase)))
                .Take(40).ToList();
            if (apps.Count == 0) rows.Children.Add(Muted("…"));
            else if (list.Count == 0) rows.Children.Add(Muted(Loc.T("찾는 앱이 없어요")));
            foreach (var app in list)
            {
                var a = app;
                var check = new CheckBox { IsChecked = chosen.Contains(a), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                line.Children.Add(check);
                var img = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) };
                try { img.Source = Services.Icons.GetIcon(new PinItem { Kind = PinKind.Aumid, Target = a.Key, Name = a.Name }, IconStyleNow); } catch { /* 아이콘 없이 */ }
                line.Children.Add(img);
                line.Children.Add(new TextBlock { Text = a.Name, VerticalAlignment = VerticalAlignment.Center });
                check.Checked += (_, _) => { if (!chosen.Contains(a)) chosen.Add(a); add.IsEnabled = true; };
                check.Unchecked += (_, _) => { chosen.Remove(a); add.IsEnabled = chosen.Count > 0; };
                line.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) check.IsChecked = check.IsChecked != true; };
                rows.Children.Add(line);
            }
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        if (AllAppsCatalog.Cached is null)
            Task.Run(() => AllAppsCatalog.Apps()).ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion) Dispatcher.BeginInvoke(() => { apps = t.Result; if (IsLoaded) Fill(); });
            }, TaskScheduler.Default);
        search.Focus();
        Keyboard.Focus(search);
    }

    // ───────────────────────── 저장 ─────────────────────────

    private void Save()
    {
        string name = _name.Text.Trim();
        _r.Name = name.Length > 0 ? name : _original?.Name ?? RoutineUi.NextName(Services);
        _r.Items.RemoveAll(i => i.Kind != RoutineItemKind.App && string.IsNullOrWhiteSpace(i.Target)); // 빈 웹 주소·경로
        if (_original is null)
        {
            if (!RoutineUi.Add(Services, _r)) return;
            if (_pinToDock) RoutineUi.PinToDock(Services, _r);
        }
        else if (!Services.Settings.Current.Routines.Contains(_original))
        {
            Log.Info("루틴 편집 저장 안 함: 그새 지워짐");
        }
        else
        {
            _original.Name = _r.Name;
            _original.Icon = _r.Icon;
            _original.Desktop = _r.Desktop;
            _original.Items = _r.Items;
            foreach (var pin in Services.Settings.Current.Pins.Where(p => p.Kind == PinKind.Routine && p.Target == _original.Id)) pin.Name = _r.Name;
            Services.Settings.Save();
            Log.Info($"루틴 편집 저장: 항목 {_r.Items.Count}개");
        }
        Close();
    }
}
