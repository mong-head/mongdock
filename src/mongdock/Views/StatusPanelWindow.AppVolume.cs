using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 소리 판의 앱별 볼륨 (#21, 0.8): 지금 출력 장치에 소리 세션이 있는 앱마다 아이콘·이름·볼륨 막대·음소거.
/// 판이 열려 있는 동안 1초마다(판의 갱신 타이머) 스레드 풀에서 세션을 읽어 바뀐 것만 고침 — 판이 닫혀 있으면 아무 일도 안 함.
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private const string SpeakerGlyph = ""; // Volume
    private const string SpeakerOffGlyph = ""; // Mute

    private sealed class AppVolumeRow
    {
        public required string Key;
        public required PillSlider Slider;
        public required Button Mute;
        public required TextBlock MuteGlyph;
        public bool Muted;
    }

    /// <summary>시험 그림용: 앱 목록을 바로 채움 (뒤에서 읽기를 기다리지 않게).</summary>
    private Action<IReadOnlyList<AppVolume>>? _applyAppVolumeForTest;

    private UIElement BuildAppVolumeSection()
    {
        var section = new StackPanel { Visibility = Visibility.Collapsed };
        var title = Sub(Loc.T("앱 볼륨"));
        title.Margin = new Thickness(0, 10, 0, 4);
        section.Children.Add(title);
        var rows = new StackPanel();
        section.Children.Add(rows);
        var byKey = new Dictionary<string, AppVolumeRow>();
        string signature = "";
        bool reading = false;

        void Apply(IReadOnlyList<AppVolume> all)
        {
            var list = all.Take(AppVolumeService.MaxRows).ToList();
            section.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            string sig = string.Join("|", list.Select(a => a.Key));
            if (sig != signature)
            {
                signature = sig;
                rows.Children.Clear();
                byKey.Clear();
                foreach (var a in list) rows.Children.Add(AppVolumeLine(a, byKey));
            }
            foreach (var a in list)
            {
                if (!byKey.TryGetValue(a.Key, out var r)) continue;
                if (!r.Slider.IsDragging) r.Slider.Value = a.Muted ? 0 : a.Volume;
                SetAppMuted(r, a.Muted);
            }
        }

        async void Refresh()
        {
            if (reading || _closed) return;
            reading = true;
            try
            {
                var list = await Task.Run(AppVolumeService.List);
                if (!_closed) Apply(list);
            }
            catch (Exception ex) { Log.Warn($"앱 볼륨 읽기 실패: {ex.GetType().Name}"); }
            finally { reading = false; }
        }
        _refreshers.Add(Refresh);
        _applyAppVolumeForTest = Apply;
        return section;
    }

    private UIElement AppVolumeLine(AppVolume a, Dictionary<string, AppVolumeRow> byKey)
    {
        var line = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 3), ToolTip = a.Name };
        // 아이콘 (시스템 소리는 스피커 모양)
        FrameworkElement icon;
        var img = AppVolumeIcon(a);
        if (img is not null) icon = new Image { Source = img, Width = 20, Height = 20 };
        else icon = new TextBlock { Text = SpeakerGlyph, FontFamily = IconFont, FontSize = 14, Foreground = _p.SubText, Width = 20, TextAlignment = TextAlignment.Center };
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 8, 0);
        line.Children.Add(icon);
        var name = new TextBlock
        {
            Text = a.Name,
            Width = 88,
            FontSize = 13,
            Foreground = _p.Text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        line.Children.Add(name);
        // 오른쪽 음소거 버튼
        var glyph = new TextBlock { Text = SpeakerGlyph, FontFamily = IconFont, FontSize = 13, Foreground = _p.SubText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var mute = new Button
        {
            Style = (Style)FindStyle("CardButton"),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Width = 26,
            Height = 26,
            Content = glyph,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(mute, Dock.Right);
        line.Children.Add(mute);
        var slider = new PillSlider(_p, showValue: false) { WheelAdjusts = false, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(slider);

        var row = new AppVolumeRow { Key = a.Key, Slider = slider, Mute = mute, MuteGlyph = glyph };
        byKey[a.Key] = row;
        string key = a.Key;
        // 끄는 동안 값이 자주 오므로 마지막 값만 보냄 (보내는 중이면 끝난 뒤 한 번 더)
        double? pending = null;
        bool sending = false;
        async void Send()
        {
            if (sending) return;
            sending = true;
            try
            {
                while (pending is { } v)
                {
                    pending = null;
                    await Task.Run(() => AppVolumeService.Set(key, v, v > 0 ? false : null));
                }
            }
            catch (Exception ex) { Log.Warn($"앱 볼륨 바꾸기 실패: {ex.GetType().Name}"); }
            finally { sending = false; }
        }
        slider.UserChanged += (_, v) =>
        {
            pending = v;
            if (v > 0) SetAppMuted(row, false);
            Send();
        };
        mute.Click += async (_, _) =>
        {
            bool to = !row.Muted;
            SetAppMuted(row, to);
            try { await Task.Run(() => AppVolumeService.Set(key, null, to)); }
            catch (Exception ex) { Log.Warn($"앱 음소거 바꾸기 실패: {ex.GetType().Name}"); }
            RefreshAll();
        };
        return line;
    }

    private void SetAppMuted(AppVolumeRow r, bool muted)
    {
        r.Muted = muted;
        r.MuteGlyph.Text = muted ? SpeakerOffGlyph : SpeakerGlyph;
        r.MuteGlyph.Foreground = muted ? _p.HolidayText : _p.SubText;
        r.Mute.ToolTip = muted ? Loc.T("소리 켜기") : Loc.T("음소거");
        r.Slider.Opacity = muted ? 0.45 : 1;
    }

    /// <summary>앱 아이콘 (몽독 아이콘 캐시 — 실행 파일 또는 스토어 앱). 없으면 null.</summary>
    private ImageSource? AppVolumeIcon(AppVolume a)
    {
        try
        {
            var style = _services.Settings.Current.Dock.IconStyle;
            if (a.Aumid is { Length: > 0 } aumid) return _services.Icons.GetIcon(new PinItem { Kind = PinKind.Aumid, Target = aumid }, style);
            if (a.ExePath is { Length: > 0 } path) return _services.Icons.GetIcon(new PinItem { Kind = PinKind.Exe, Target = path }, style);
        }
        catch (Exception ex) { Log.Warn($"앱 볼륨 아이콘 실패: {ex.GetType().Name}"); }
        return null;
    }
}
