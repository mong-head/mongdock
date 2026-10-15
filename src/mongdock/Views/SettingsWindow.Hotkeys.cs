using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 설정 → 단축키 (0.6, spec-hotkeys.md 사용자 결정): 기능마다 [켜기] 스위치 + 키 칸. 검색만 기본 켬, 나머지는 꺼짐·키 없음.
/// 키 칸을 누르면 "키를 누르세요…" → 몽독 훅이 다음 조합 하나를 받아 옴 (Win 조합도 받으려고 창 키 이벤트가 아니라 훅에서).
/// 막는 키(윈도우·흔한 앱 키)는 이유를 빨간 글자로, Alt+글자는 경고만. 몽독 안에서 이미 쓰는 키면 [바꾸기]로 그쪽을 비우고 이쪽에.
/// 루틴은 루틴마다 한 줄 (키는 루틴에 저장 — 내보내기·가져오기·루틴 지우기에 같이 따라감).
/// </summary>
internal sealed partial class SettingsWindow
{
    /// <summary>지금 키를 받는 칸 (null = 없음).</summary>
    private string? _recordingSlot;
    /// <summary>칸별 안내 한 줄: 막힘(빨강)·경고·겹침([바꾸기]).</summary>
    private readonly Dictionary<string, HotkeyNote> _hotkeyNotes = new();

    private sealed record HotkeyNote(string Text, bool Error, string? TakeFrom = null, HotkeyChord? Chord = null);

    private const string SlotSearch = HotkeyController.Search, SlotAllApps = HotkeyController.AllApps,
        SlotControlCenter = HotkeyController.ControlCenter, SlotNotifications = HotkeyController.Notifications, SlotRoutine = HotkeyController.RoutinePrefix;

    private void BuildHotkeys(Panel body)
    {
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("몽독 기능을 키 하나로 열어요. 켜고 키 칸을 누른 뒤 쓰고 싶은 조합을 누르세요. 전체 화면 게임·영상 중에는 모든 단축키가 쉬어요."),
            Foreground = _p.SubText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 0, 12),
        });
        body.Children.Add(Group(
            HotkeyRow(SlotSearch, Loc.T("검색"), Loc.T("몽독 검색창 열기·닫기")),
            HotkeyRow(SlotAllApps, Loc.T("앱 모음 판"), Loc.T("모든 앱 판 열기·닫기")),
            HotkeyRow(SlotControlCenter, Loc.T("제어 센터"), Loc.T("와이파이·소리·밝기 판 (상단바가 켜져 있을 때)")),
            HotkeyRow(SlotNotifications, Loc.T("알림·달력"), Loc.T("시계를 누른 것과 같은 판 (상단바가 켜져 있을 때)"))));

        body.Children.Add(SectionTitle(Loc.T("루틴")));
        var routines = _services.Settings.Current.Routines;
        if (routines.Count == 0)
            body.Children.Add(Group(Row(Loc.T("루틴이 아직 없어요"), Loc.T("독이나 앱 모음 판에서 루틴을 만들면 여기서 루틴마다 키를 정할 수 있어요."), new Border())));
        else
            body.Children.Add(Group(routines.Select(r => (UIElement)HotkeyRow(SlotRoutine + r.Id, r.Name, Loc.T("이 루틴 열기"))).ToArray()));

        body.Children.Add(new TextBlock
        {
            Text = Loc.T("Ctrl·Alt·Win 중 하나와 함께 누르세요 (예: Ctrl + Alt + K). 윈도우나 다른 앱이 쓰는 키는 고를 수 없어요. 키 칸에서 Esc 는 취소, Backspace 는 지우기예요."),
            FontSize = 11.5,
            Foreground = _p.SubText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 0, 10),
        });
        var reset = ActionButton(Loc.T("기본값으로 되돌리기"), ResetHotkeys);
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(reset);
    }

    // ───────────────────────── 칸 ─────────────────────────

    private HotkeyBinding GetBinding(string slot)
    {
        var s = _services.Settings.Current;
        return slot switch
        {
            SlotSearch => s.Hotkeys.Search,
            SlotAllApps => s.Hotkeys.AllApps,
            SlotControlCenter => s.Hotkeys.ControlCenter,
            SlotNotifications => s.Hotkeys.Notifications,
            _ => RoutineUi.Find(_services, slot[SlotRoutine.Length..])?.Hotkey ?? new HotkeyBinding(),
        };
    }

    private void SetBinding(string slot, HotkeyBinding b)
    {
        var s = _services.Settings.Current;
        switch (slot)
        {
            case SlotSearch: s.Hotkeys.Search = b; break;
            case SlotAllApps: s.Hotkeys.AllApps = b; break;
            case SlotControlCenter: s.Hotkeys.ControlCenter = b; break;
            case SlotNotifications: s.Hotkeys.Notifications = b; break;
            default:
                if (RoutineUi.Find(_services, slot[SlotRoutine.Length..]) is { } r) r.Hotkey = b.On || b.Key is not null ? b : null;
                break;
        }
    }

    private string SlotTitle(string slot) => slot switch
    {
        SlotSearch => Loc.T("검색"),
        SlotAllApps => Loc.T("앱 모음 판"),
        SlotControlCenter => Loc.T("제어 센터"),
        SlotNotifications => Loc.T("알림·달력"),
        _ => RoutineUi.Find(_services, slot[SlotRoutine.Length..])?.Name ?? Loc.T("루틴"),
    };

    /// <summary>칸의 실제 키 (검색 "auto" 는 이 PC 키로). 키가 없으면 null.</summary>
    private static HotkeyChord? ChordOf(string slot, HotkeyBinding b) =>
        slot == SlotSearch ? (b.Key is null || b.Key == HotkeyBinding.Auto ? HotkeyController.AutoSearchChord() : HotkeyChord.Parse(b.Key)) : HotkeyChord.Parse(b.Key);

    private IEnumerable<string> AllSlots() =>
        new[] { SlotSearch, SlotAllApps, SlotControlCenter, SlotNotifications }.Concat(_services.Settings.Current.Routines.Select(r => SlotRoutine + r.Id));

    private Grid HotkeyRow(string slot, string title, string sub)
    {
        var b = GetBinding(slot);
        bool recording = _recordingSlot == slot;

        // 키 칸: 지금 키 / "키 정하기" / 받는 중 "키를 누르세요…"
        string keyText = recording ? Loc.T("키를 누르세요…")
            : slot == SlotSearch && (b.Key is null || b.Key == HotkeyBinding.Auto) ? Loc.F($"자동 ({HotkeyController.AutoSearchChord().Display})")
            : HotkeyChord.Parse(b.Key) is { } c ? c.Display
            : Loc.T("키 정하기");
        var keyBox = new Button
        {
            Style = (Style)FindResource("CardButton"),
            Background = recording ? _p.SoftAccent : _p.Tile,
            Foreground = recording ? _p.SoftAccentText : b.On ? _p.Text : _p.SubText,
            BorderBrush = recording ? _p.SoftAccentLine : Brushes.Transparent,
            BorderThickness = new Thickness(recording ? 1 : 0),
            MinWidth = 130,
            Content = keyText,
            ToolTip = recording ? Loc.T("Esc 취소 · Backspace 지우기") : Loc.T("눌러서 키 바꾸기"),
        };
        keyBox.Click += (_, _) =>
        {
            if (_recordingSlot == slot) StopRecording(rebuild: true);
            else StartRecording(slot);
        };

        var toggle = Toggle(b.On, on =>
        {
            Commit(() =>
            {
                var cur = GetBinding(slot).Clone();
                cur.On = on;
                SetBinding(slot, cur);
            }, rebuild: true);
            // 켰는데 키가 없으면 바로 키 받기 (검색 "auto" 는 키가 있는 것으로 봄)
            if (on && slot != SlotSearch && HotkeyChord.Parse(GetBinding(slot).Key) is null) StartRecording(slot);
            else if (!on && _recordingSlot == slot) StopRecording(rebuild: true);
        });
        toggle.Margin = new Thickness(10, 0, 0, 0);
        toggle.ToolTip = Loc.T("켜기");

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        // 검색: 직접 정한 키면 "자동으로" 되돌리기
        if (slot == SlotSearch && b.Key is not null && b.Key != HotkeyBinding.Auto && !recording)
        {
            var auto = LinkButton(Loc.T("자동으로"), () =>
            {
                _hotkeyNotes.Remove(slot);
                Commit(() => SetBinding(slot, new HotkeyBinding { On = true, Key = HotkeyBinding.Auto }), rebuild: true);
            }, HorizontalAlignment.Right);
            auto.Margin = new Thickness(0, 0, 6, 0);
            auto.VerticalAlignment = VerticalAlignment.Center;
            controls.Children.Add(auto);
        }
        controls.Children.Add(keyBox);
        controls.Children.Add(toggle);

        var row = Row(title, sub, controls);
        // 안내 한 줄 (막힘·경고·겹침) — 제목 아래에
        if (_hotkeyNotes.TryGetValue(slot, out var note) && row.Children.OfType<StackPanel>().FirstOrDefault() is { } texts)
        {
            var line = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            line.Children.Add(new TextBlock
            {
                Text = (note.Error ? "" : "⚠ ") + note.Text,
                FontSize = 11.5,
                Foreground = note.Error ? _p.HolidayText : _p.SubText,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (note.TakeFrom is { } other && note.Chord is { } chord)
            {
                var take = LinkButton(Loc.T("바꾸기"), () => TakeHotkey(slot, other, chord), HorizontalAlignment.Left);
                take.Margin = new Thickness(6, 0, 0, 0);
                take.Padding = new Thickness(4, 0, 4, 0);
                line.Children.Add(take);
            }
            texts.Children.Add(line);
        }
        return row;
    }

    // ───────────────────────── 키 받기 ─────────────────────────

    private void StartRecording(string slot)
    {
        if (HotkeyController.Instance is not { } hk)
        {
            _hotkeyNotes[slot] = new HotkeyNote(Loc.T("몽독이 일시 정지 중이라 키를 받을 수 없어요."), Error: true);
            QueueRebuild();
            return;
        }
        _recordingSlot = slot;
        _hotkeyNotes.Remove(slot);
        hk.Record((kind, chord) => Dispatcher.BeginInvoke(() => OnRecorded(slot, kind, chord)));
        QueueRebuild();
    }

    private void StopRecording(bool rebuild)
    {
        if (_recordingSlot is null) return;
        _recordingSlot = null;
        HotkeyController.Instance?.Record(null);
        if (rebuild && !_closed) QueueRebuild();
    }

    private void OnRecorded(string slot, HotkeyService.RecordKind kind, HotkeyChord chord)
    {
        if (_closed || _recordingSlot != slot) return;
        _recordingSlot = null;
        switch (kind)
        {
            case HotkeyService.RecordKind.Cancel:
                QueueRebuild();
                return;
            case HotkeyService.RecordKind.Clear:
                _hotkeyNotes.Remove(slot);
                Commit(() => SetBinding(slot, slot == SlotSearch
                    ? new HotkeyBinding { On = GetBinding(slot).On, Key = HotkeyBinding.Auto } // 검색은 지우면 자동으로
                    : new HotkeyBinding { On = false, Key = null }), rebuild: true);
                Log.Info($"단축키 지움: {(slot.StartsWith(SlotRoutine, StringComparison.Ordinal) ? "루틴" : slot)}");
                return;
        }
        if (HotkeyChord.IsModifierKey(chord.Vk)) { QueueRebuild(); return; }
        var verdict = HotkeyRules.Check(chord, slot == SlotSearch);
        if (verdict.Verdict == HotkeyRules.Verdict.Block)
        {
            _hotkeyNotes[slot] = new HotkeyNote(Loc.F($"{chord.Display} — {Loc.T(verdict.Reason!)}"), Error: true);
            Log.Info($"단축키 막음: {chord} ({verdict.Reason})");
            QueueRebuild();
            return;
        }
        // 몽독 안 겹침
        string? other = AllSlots().FirstOrDefault(o => o != slot && GetBinding(o) is { On: true } ob && ChordOf(o, ob) == chord);
        if (other is not null)
        {
            _hotkeyNotes[slot] = new HotkeyNote(Loc.F($"{chord.Display} — 이미 「{SlotTitle(other)}」에 쓰고 있어요."), Error: true, other, chord);
            QueueRebuild();
            return;
        }
        Apply(slot, chord, verdict);
    }

    private void Apply(string slot, HotkeyChord chord, HotkeyRules.Result verdict)
    {
        if (verdict.Verdict == HotkeyRules.Verdict.Warn) _hotkeyNotes[slot] = new HotkeyNote(Loc.T(verdict.Reason!), Error: false);
        else _hotkeyNotes.Remove(slot);
        Commit(() => SetBinding(slot, new HotkeyBinding { On = true, Key = chord.ToString() }), rebuild: true);
        Log.Info($"단축키 정함: {(slot.StartsWith(SlotRoutine, StringComparison.Ordinal) ? "루틴" : slot)} = {chord}");
    }

    /// <summary>[바꾸기]: 다른 칸에서 이 키를 빼고(그 칸은 꺼짐·키 없음, 검색은 자동으로) 이 칸에.</summary>
    private void TakeHotkey(string slot, string from, HotkeyChord chord)
    {
        Commit(() => SetBinding(from, from == SlotSearch
            ? new HotkeyBinding { On = false, Key = HotkeyBinding.Auto }
            : new HotkeyBinding { On = false, Key = null }));
        _hotkeyNotes[from] = new HotkeyNote(Loc.F($"{chord.Display} 를 「{SlotTitle(slot)}」로 옮겼어요."), Error: false);
        Apply(slot, chord, HotkeyRules.Check(chord, slot == SlotSearch));
    }

    private void ResetHotkeys()
    {
        StopRecording(rebuild: false);
        _hotkeyNotes.Clear();
        Commit(() =>
        {
            var s = _services.Settings.Current;
            s.Hotkeys = new HotkeySettings();
            foreach (var r in s.Routines) r.Hotkey = null;
        }, rebuild: true);
        Log.Info("단축키 기본값으로");
    }
}
