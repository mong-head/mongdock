using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 편집 창 "▸ 더 보기" (spec-routines §14): 기본 화면은 이름·앱 목록뿐, 나머지는 여기 접어 둠 — 모두 기본 꺼짐.
/// ① 시작 조건 ② 끝 조건 ③ 함께 바꿀 것 ④ 방해 금지 예외 앱 ⑤ 비슷하게 열면 물어보기 ⑥ 상단바에 머문 시간.
/// 묶음마다 스위치, 켜면 그 아래에 세부.
/// </summary>
internal sealed partial class RoutineEditorWindow
{
    private readonly StackPanel _more = new() { Margin = new Thickness(0, 6, 0, 0) };
    private bool _moreOpen;

    /// <summary>시험 그림에서만: 더 보기를 펼친 채로.</summary>
    internal void OpenMoreForTest()
    {
        _moreOpen = true;
        RebuildMore();
    }

    private RoutineMore More => _r.More ??= new RoutineMore();

    private bool AnyMore(RoutineMore? m) => m is not null && (m.Start.Count > 0 || m.End.AudioRemoved is not null || m.End.Time is not null || m.End.AllAppsClosed
        || m.Change.Dnd || m.Change.DockHide || m.Change.OutputDevice is not null || m.Change.Volume is not null || m.AskSimilar || m.ShowTime);

    private UIElement BuildMore()
    {
        _moreOpen = AnyMore(_r.More);
        RebuildMore();
        return _more;
    }

    private void RebuildMore()
    {
        _more.Children.Clear();
        var toggle = new TextBlock
        {
            Text = (_moreOpen ? "▾ " : "▸ ") + Loc.T("더 보기"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = P.SoftAccentText,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 4, 0, 6),
            Focusable = true,
        };
        toggle.MouseLeftButtonUp += (_, _) => { _moreOpen = !_moreOpen; RebuildMore(); };
        toggle.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { e.Handled = true; _moreOpen = !_moreOpen; RebuildMore(); } };
        var head = new DockPanel();
        head.Children.Add(toggle);
        if (!_moreOpen)
            head.Children.Add(new TextBlock { Text = Loc.T("시작·끝 조건, 함께 바꿀 것 등 — 모두 꺼져 있어요"), FontSize = 11.5, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, -2, 0, 0) });
        _more.Children.Add(head);
        if (!_moreOpen) return;

        var box = new StackPanel();
        var frame = new Border { Background = P.Tile, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Child = box };
        box.Children.Add(StartGroup());
        box.Children.Add(Divider());
        box.Children.Add(EndGroup());
        box.Children.Add(Divider());
        box.Children.Add(ChangeGroup());
        box.Children.Add(Divider());
        box.Children.Add(ExceptionGroup());
        box.Children.Add(Divider());
        box.Children.Add(Switch(Loc.T("비슷하게 열면 물어보기"), Loc.T("어떤 데스크톱에 이 루틴의 앱이 대부분(70% 이상, 2개 이상) 떠 있으면 이 루틴인지 한 번 물어요."),
            More.AskSimilar, on => More.AskSimilar = on, null));
        box.Children.Add(Divider());
        box.Children.Add(Switch(Loc.T("상단바에 머문 시간 보이기"), Loc.T("루틴 데스크톱을 보고 있는 동안만 세요. 잠금 중이거나 5분 넘게 입력이 없으면 멈춰요."),
            More.ShowTime, on => More.ShowTime = on, null));
        _more.Children.Add(frame);
    }

    private Border Divider() => new() { Height = 1, Background = P.Divider, Margin = new Thickness(0, 8, 0, 8) };

    /// <summary>제목 + 설명 + 오른쪽 스위치. 켜져 있으면 detail 을 아래에.</summary>
    private UIElement Switch(string title, string? sub, bool on, Action<bool> set, Func<UIElement>? detail)
    {
        var box = new StackPanel();
        var row = new DockPanel { LastChildFill = true };
        var sw = new ToggleButton
        {
            Style = (Style)Application.Current.FindResource("MacSwitch"),
            Background = P.AccentFill,
            BorderBrush = P.CircleOff,
            IsChecked = on,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 2, 0, 0),
        };
        DockPanel.SetDock(sw, Dock.Right);
        row.Children.Add(sw);
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        if (sub is not null) texts.Children.Add(Muted(sub, 11.5));
        row.Children.Add(texts);
        box.Children.Add(row);
        var holder = new ContentControl { Margin = new Thickness(0, 6, 0, 0), Focusable = false };
        void Show(bool v) => holder.Content = v && detail is not null ? detail() : null;
        sw.Click += (_, _) =>
        {
            bool v = sw.IsChecked == true;
            set(v);
            Show(v);
        };
        Show(on);
        box.Children.Add(holder);
        return box;
    }

    // ───────────────────────── ① 시작 조건 ─────────────────────────

    // 묶음 스위치를 껐다 다시 켜면 끄기 전 선택으로 (저장 전 편집 중에만 — QA: 함께 바꿀 것을 껐다 켜면 볼륨이 풀림)
    private (List<RoutineStart> Start, bool AutoOpen)? _offStart;
    private RoutineEnd? _offEnd;
    private RoutineChange? _offChange;

    private UIElement StartGroup() => Switch(Loc.T("시작 조건"), Loc.T("조건이 맞으면 열지 물어봐요. 직접 누르기는 늘 돼요."),
        More.Start.Count > 0, on =>
        {
            if (!on)
            {
                _offStart = (More.Start.ToList(), More.AutoOpen);
                More.Start.Clear();
                More.AutoOpen = false;
            }
            else if (_offStart is { } prev && More.Start.Count == 0) { More.Start.AddRange(prev.Start); More.AutoOpen = prev.AutoOpen; }
            // 처음 켜면 [+ 조건 추가]로 고름 (저절로 넣지 않음)
        }, StartDetail);

    private UIElement StartDetail()
    {
        var box = new StackPanel();
        void Refill()
        {
            box.Children.Clear();
            foreach (var s in More.Start.ToList())
            {
                var start = s;
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var remove = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = P.SubText, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = Loc.T("빼기") };
                remove.MouseLeftButtonUp += (_, _) => { More.Start.Remove(start); Refill(); };
                DockPanel.SetDock(remove, Dock.Right);
                line.Children.Add(remove);
                line.Children.Add(start.Kind == RoutineStartKind.Time ? TimeRow(start) : new TextBlock { Text = RoutineTriggers.Describe(start), VerticalAlignment = VerticalAlignment.Center });
                box.Children.Add(line);
            }
            var add = CardButton("+ " + Loc.T("조건 추가"));
            add.Height = 26;
            add.MinWidth = 0;
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Background = P.CardBackground; // 회색 묶음 바탕 위에서 버튼으로 보이게
            add.Click += (_, _) =>
            {
                var menu = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
                void Add(RoutineStart s) { More.Start.Add(s); Refill(); }
                menu.Items.Add(DockMenus.Item(Loc.T("컴퓨터를 켜면"), () => Add(new RoutineStart { Kind = RoutineStartKind.Login }), enabled: More.Start.All(x => x.Kind != RoutineStartKind.Login)));
                var monitors = new MenuItem { Header = Loc.T("모니터를 연결하면") };
                monitors.Items.Add(DockMenus.Item(Loc.T("외부 모니터 아무거나"), () => Add(new RoutineStart { Kind = RoutineStartKind.Monitor })));
                foreach (var m in Monitors.GetAll().Where(m => !m.IsPrimary))
                {
                    var mon = m;
                    monitors.Items.Add(DockMenus.Item(Loc.F($"모니터 {mon.Number}"), () => Add(new RoutineStart { Kind = RoutineStartKind.Monitor, Device = mon.DeviceName, Name = Loc.F($"모니터 {mon.Number}") })));
                }
                menu.Items.Add(monitors);
                var audio = new MenuItem { Header = Loc.T("오디오 장치를 연결하면") };
                foreach (var d in Services.Status.OutputDevices)
                {
                    var dev = d;
                    audio.Items.Add(DockMenus.Item(dev.Name, () => Add(new RoutineStart { Kind = RoutineStartKind.Audio, Device = dev.Id, Name = dev.Name })));
                }
                if (audio.Items.Count == 0) audio.Items.Add(DockMenus.Item(Loc.T("연결된 장치가 없어요"), () => { }, enabled: false));
                menu.Items.Add(audio);
                menu.Items.Add(DockMenus.Item(Loc.T("시간·요일"), () => Add(new RoutineStart { Kind = RoutineStartKind.Time, Time = "09:00", Days = new List<int> { 1, 2, 3, 4, 5 } })));
                var apps = new MenuItem { Header = Loc.T("이 앱을 켜면") };
                foreach (var w in Services.Windows.Windows.GroupBy(w => AppNames.Get(w)).Select(g => g.First()).OrderBy(w => AppNames.Get(w), StringComparer.CurrentCultureIgnoreCase).Take(20))
                {
                    var win = w;
                    string key = !string.IsNullOrEmpty(win.Aumid) && AppsFolder.IsPackagedAumid(win.Aumid) ? win.Aumid! : System.IO.Path.GetFileNameWithoutExtension(win.ProcessPath).ToLowerInvariant();
                    if (key.Length == 0 || key == "mongdock") continue;
                    apps.Items.Add(DockMenus.Item(AppNames.Get(win), () => Add(new RoutineStart { Kind = RoutineStartKind.App, App = key, Name = AppNames.Get(win) })));
                }
                foreach (var item in _r.Items.Where(i => i.Kind == RoutineItemKind.App))
                {
                    var it = item;
                    string key = it.Aumid ?? System.IO.Path.GetFileNameWithoutExtension(it.Target).ToLowerInvariant();
                    if (key.Length == 0 || apps.Items.OfType<MenuItem>().Any(m => (m.Header as string) == RoutineService.ItemName(it))) continue;
                    apps.Items.Add(DockMenus.Item(RoutineService.ItemName(it), () => Add(new RoutineStart { Kind = RoutineStartKind.App, App = key, Name = RoutineService.ItemName(it) })));
                }
                if (apps.Items.Count == 0) apps.Items.Add(DockMenus.Item(Loc.T("켜져 있는 앱이 없어요 — 앱을 켠 뒤 골라 주세요"), () => { }, enabled: false));
                menu.Items.Add(apps);
                menu.IsOpen = true;
            };
            box.Children.Add(add);
            bool anyNonTime = More.Start.Any(x => x.Kind != RoutineStartKind.Time);
            var auto = DockMenus.Check(P, new TextBlock { Text = Loc.T("묻지 않고 바로 열기 (시간 조건은 늘 물어요)"), FontSize = 12 }, More.AutoOpen && anyNonTime);
            auto.IsEnabled = anyNonTime;
            auto.Margin = new Thickness(0, 8, 0, 0);
            auto.Checked += (_, _) => More.AutoOpen = true;
            auto.Unchecked += (_, _) => More.AutoOpen = false;
            box.Children.Add(auto);
        }
        Refill();
        return box;
    }

    private UIElement TimeRow(RoutineStart start)
    {
        var row = new WrapPanel();
        row.Children.Add(new TextBlock { Text = Loc.T("시간"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) });
        var field = Field(start.Time ?? "09:00", 64);
        field.MaxLength = 5;
        field.Margin = new Thickness(0, 0, 10, 6);
        field.LostKeyboardFocus += (_, _) =>
        {
            if (TimeSpan.TryParse(field.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
                start.Time = $"{t.Hours:00}:{t.Minutes:00}";
            field.Text = start.Time ?? "09:00";
        };
        row.Children.Add(field);
        row.Children.Add(DayPills(start.Days ??= new List<int>()));
        row.Children.Add(new TextBlock { Text = Loc.T("· 묻기만 해요"), FontSize = 11.5, Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 6) });
        return row;
    }

    /// <summary>요일 7칸 (여러 개 켤 수 있음, 비면 매일).</summary>
    private UIElement DayPills(List<int> days)
    {
        var names = new[] { Loc.T("일"), Loc.T("월"), Loc.T("화"), Loc.T("수"), Loc.T("목"), Loc.T("금"), Loc.T("토") };
        var wrap = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        for (int i = 0; i < 7; i++)
        {
            int d = i;
            var pill = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(1), Child = new TextBlock { Text = names[i], FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            void Paint()
            {
                bool on = days.Contains(d);
                pill.Background = on ? P.SoftAccent : P.CardBackground;
                pill.BorderBrush = on ? P.SoftAccentLine : P.Divider;
                ((TextBlock)pill.Child).Foreground = on ? P.SoftAccentText : P.SubText;
            }
            pill.MouseLeftButtonUp += (_, _) => { if (!days.Remove(d)) days.Add(d); days.Sort(); Paint(); };
            Paint();
            wrap.Children.Add(pill);
        }
        return wrap;
    }

    // ───────────────────────── ② 끝 조건 ─────────────────────────

    private UIElement EndGroup() => Switch(Loc.T("끝 조건"), Loc.T("오디오 장치를 빼거나 시간이 되면 끝낼지 물어요. 앱을 다 닫으면 묻지 않고 끝내요."),
        More.End.AudioRemoved is not null || More.End.Time is not null || More.End.AllAppsClosed, on =>
        {
            if (!on) { _offEnd = More.End; More.End = new RoutineEnd(); }
            else if (_offEnd is not null) { More.End = _offEnd; _offEnd = null; }
            if (on && More.End.AudioRemoved is null && More.End.Time is null && !More.End.AllAppsClosed) More.End.AllAppsClosed = true;
        }, EndDetail);

    private UIElement EndDetail()
    {
        var box = new StackPanel();
        var end = More.End;
        // 오디오 장치를 빼면
        var devices = Services.Status.OutputDevices.ToList();
        var audioRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var audioCheck = DockMenus.Check(P, new TextBlock { Text = Loc.T("오디오 장치를 빼면"), FontSize = 12 }, end.AudioRemoved is not null);
        audioRow.Children.Add(audioCheck);
        var picker = DevicePicker(devices, end.AudioRemoved, end.AudioName, (id, name) => { end.AudioRemoved = id; end.AudioName = name; });
        picker.Margin = new Thickness(10, 0, 0, 0);
        picker.IsEnabled = end.AudioRemoved is not null;
        audioRow.Children.Add(picker);
        audioCheck.Checked += (_, _) => { picker.IsEnabled = true; if (end.AudioRemoved is null && devices.FirstOrDefault() is { } d) { end.AudioRemoved = d.Id; end.AudioName = d.Name; } };
        audioCheck.Unchecked += (_, _) => { picker.IsEnabled = false; end.AudioRemoved = null; end.AudioName = null; };
        box.Children.Add(audioRow);
        // 시간
        var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var timeCheck = DockMenus.Check(P, new TextBlock { Text = Loc.T("시간이 되면"), FontSize = 12 }, end.Time is not null);
        var timeField = Field(end.Time ?? "18:00", 64);
        timeField.Height = 26;
        timeField.Margin = new Thickness(10, 0, 0, 0);
        timeField.IsEnabled = end.Time is not null;
        timeField.LostKeyboardFocus += (_, _) =>
        {
            if (TimeSpan.TryParse(timeField.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
                end.Time = $"{t.Hours:00}:{t.Minutes:00}";
            timeField.Text = end.Time ?? "18:00";
        };
        timeCheck.Checked += (_, _) => { timeField.IsEnabled = true; end.Time ??= "18:00"; };
        timeCheck.Unchecked += (_, _) => { timeField.IsEnabled = false; end.Time = null; };
        timeRow.Children.Add(timeCheck);
        timeRow.Children.Add(timeField);
        box.Children.Add(timeRow);
        // 앱을 다 닫으면
        var closed = DockMenus.Check(P, new TextBlock { Text = Loc.T("루틴으로 연 앱을 다 닫으면 (묻지 않고 끝내요)"), FontSize = 12 }, end.AllAppsClosed);
        closed.Checked += (_, _) => end.AllAppsClosed = true;
        closed.Unchecked += (_, _) => end.AllAppsClosed = false;
        box.Children.Add(closed);
        return box;
    }

    private Border DevicePicker(List<AudioDevice> devices, string? currentId, string? currentName, Action<string, string> pick)
    {
        var labels = devices.Select(d => d.Name).ToList();
        int at = devices.FindIndex(d => d.Id == currentId);
        if (at < 0 && currentId is not null) { labels.Insert(0, (currentName ?? currentId) + " " + Loc.T("(지금 없음)")); at = 0; devices.Insert(0, new AudioDevice(currentId, currentName ?? currentId, false, default)); }
        if (labels.Count == 0) labels.Add(Loc.T("연결된 장치가 없어요"));
        return MenuPicker(labels, Math.Max(0, at), i => { if (i < devices.Count) pick(devices[i].Id, devices[i].Name); }, 220);
    }

    // ───────────────────────── ③ 함께 바꿀 것 ─────────────────────────

    private UIElement ChangeGroup()
    {
        var c = More.Change;
        return Switch(Loc.T("함께 바꿀 것"), Loc.T("열 때 바꾸고 끝내면 열기 직전으로 되돌려요. 도중에 직접 바꾼 건 그대로 둬요."),
            c.Dnd || c.DockHide || c.OutputDevice is not null || c.Volume is not null, on =>
            {
                if (!on) { _offChange = More.Change; More.Change = new RoutineChange(); }
                else if (_offChange is not null) { More.Change = _offChange; _offChange = null; }
                if (on && !(More.Change.Dnd || More.Change.DockHide || More.Change.OutputDevice is not null || More.Change.Volume is not null)) More.Change.Dnd = true;
                Dispatcher.BeginInvoke(RebuildMore); // 방해 금지가 켜지고 꺼짐에 따라 "예외 앱" 묶음도 (QA: 껐다 켜야 활성화됨)
            }, ChangeDetail);
    }

    private UIElement ChangeDetail()
    {
        var c = More.Change;
        var box = new StackPanel();
        UIElement Check(string text, string sub, bool on, Action<bool> set)
        {
            var cb = DockMenus.Check(P, null, on);
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = text, FontSize = 12 });
            label.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = P.SubText });
            cb.Content = label;
            cb.Margin = new Thickness(0, 0, 0, 6);
            cb.Checked += (_, _) => { set(true); RebuildMore(); };
            cb.Unchecked += (_, _) => { set(false); RebuildMore(); };
            return cb;
        }
        box.Children.Add(Check(Loc.T("방해 금지"), Loc.T("몽독 알림 배너를 띄우지 않아요 · 루틴 데스크톱에 있는 동안만"), c.Dnd, v => c.Dnd = v));
        box.Children.Add(Check(Loc.T("독 자동 숨김"), Loc.T("루틴 데스크톱에 있는 동안만"), c.DockHide, v => c.DockHide = v));
        // 소리 출력 장치
        var devices = Services.Status.OutputDevices.ToList();
        var deviceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var deviceCheck = DockMenus.Check(P, new TextBlock { Text = Loc.T("소리 출력 장치"), FontSize = 12 }, c.OutputDevice is not null);
        var devicePicker = DevicePicker(devices, c.OutputDevice, c.OutputName, (id, name) => { c.OutputDevice = id; c.OutputName = name; });
        devicePicker.Margin = new Thickness(10, 0, 0, 0);
        devicePicker.IsEnabled = c.OutputDevice is not null;
        deviceCheck.Checked += (_, _) => { devicePicker.IsEnabled = true; if (c.OutputDevice is null && devices.FirstOrDefault() is { } d) { c.OutputDevice = d.Id; c.OutputName = d.Name; } };
        deviceCheck.Unchecked += (_, _) => { devicePicker.IsEnabled = false; c.OutputDevice = null; c.OutputName = null; };
        deviceRow.Children.Add(deviceCheck);
        deviceRow.Children.Add(devicePicker);
        box.Children.Add(deviceRow);
        // 볼륨
        var volumeRow = new StackPanel { Orientation = Orientation.Horizontal };
        var volumeCheck = DockMenus.Check(P, new TextBlock { Text = Loc.T("볼륨"), FontSize = 12 }, c.Volume is not null);
        var slider = new Slider
        {
            Style = (Style)Application.Current.FindResource("MacSlider"),
            Width = 180,
            Minimum = 0,
            Maximum = 100,
            Value = c.Volume ?? 50,
            Foreground = P.AccentFill,
            Background = P.SliderTrack,
            Margin = new Thickness(10, 0, 8, 0),
            IsEnabled = c.Volume is not null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var value = new TextBlock { Text = $"{(int)slider.Value}", Foreground = P.SubText, VerticalAlignment = VerticalAlignment.Center, Width = 28 };
        slider.ValueChanged += (_, _) => { if (c.Volume is not null) c.Volume = (int)Math.Round(slider.Value); value.Text = $"{(int)Math.Round(slider.Value)}"; };
        volumeCheck.Checked += (_, _) => { slider.IsEnabled = true; c.Volume = (int)Math.Round(slider.Value); };
        volumeCheck.Unchecked += (_, _) => { slider.IsEnabled = false; c.Volume = null; };
        volumeRow.Children.Add(volumeCheck);
        volumeRow.Children.Add(slider);
        volumeRow.Children.Add(value);
        box.Children.Add(volumeRow);
        return box;
    }

    // ───────────────────────── ④ 방해 금지 예외 앱 ─────────────────────────

    private UIElement ExceptionGroup()
    {
        var box = new StackPanel { IsEnabled = More.Change.Dnd, Opacity = More.Change.Dnd ? 1 : 0.5 };
        box.Children.Add(new TextBlock { Text = Loc.T("방해 금지 예외 앱"), FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        box.Children.Add(Muted(More.Change.Dnd ? Loc.T("방해 금지 중에도 이 앱의 알림 배너는 띄워요.") : Loc.T("함께 바꿀 것의 방해 금지를 켜면 고를 수 있어요."), 11.5));
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var key in More.DndExceptions.ToList())
        {
            var k = key;
            var chip = new Border { CornerRadius = new CornerRadius(10), Background = P.CardBackground, BorderBrush = P.Divider, BorderThickness = new Thickness(1), Padding = new Thickness(9, 2, 6, 3), Margin = new Thickness(0, 0, 6, 6) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = RoutineTriggers.AppLabel(Services, k), FontSize = 12 });
            var x = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9, Foreground = P.SubText, Margin = new Thickness(6, 2, 0, 0), Cursor = Cursors.Hand };
            x.MouseLeftButtonUp += (_, _) => { More.DndExceptions.Remove(k); RebuildMore(); };
            row.Children.Add(x);
            chip.Child = row;
            chips.Children.Add(chip);
        }
        var add = CardButton("+ " + Loc.T("앱"));
        add.Height = 24;
        add.MinWidth = 0;
        add.Background = P.CardBackground;
        add.Margin = new Thickness(0, 0, 0, 6);
        add.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
            foreach (var w in Services.Windows.Windows.GroupBy(w => AppNames.Get(w)).Select(g => g.First()).OrderBy(w => AppNames.Get(w), StringComparer.CurrentCultureIgnoreCase).Take(25))
            {
                string key = !string.IsNullOrEmpty(w.Aumid) ? w.Aumid! : System.IO.Path.GetFileNameWithoutExtension(w.ProcessPath).ToLowerInvariant();
                if (key.Length == 0 || More.DndExceptions.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                menu.Items.Add(DockMenus.Item(AppNames.Get(w), () => { More.DndExceptions.Add(key); RebuildMore(); }));
            }
            if (menu.Items.Count == 0) menu.Items.Add(DockMenus.Item(Loc.T("켜져 있는 앱이 없어요 — 앱을 켠 뒤 골라 주세요"), () => { }, enabled: false));
            menu.IsOpen = true;
        };
        chips.Children.Add(add);
        box.Children.Add(chips);
        return box;
    }
}
