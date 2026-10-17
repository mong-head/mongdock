using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mongdock.Models;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 소리 판의 앱별 볼륨 (#21, 0.8): 지금 출력 장치에 소리 세션이 있는 앱마다 아이콘·이름·볼륨 막대·음소거.
/// 판이 열려 있는 동안 1초마다(판의 갱신 타이머) 스레드 풀에서 세션을 읽어 바뀐 것만 고침 — 판이 닫혀 있으면 아무 일도 안 함.
/// 0.8.2: 첫 화면엔 지금 소리 내는 앱 1~2줄, 모든 앱은 「앱별 볼륨 n개 ›」 세부 화면 (StatusPanelWindow.Sound.cs).
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

    /// <summary>둘러보기 장면: 예시 앱 목록으로 바로 채움 (장면 아이콘은 <see cref="SceneIconForApp"/>).</summary>
    internal void FillAppVolumeForScene(IReadOnlyList<AppVolume> apps) => _applyAppVolumeForTest?.Invoke(apps);

    /// <summary>둘러보기 장면: 예시 앱 아이콘 (실제 실행 파일 대신). null 이면 보통처럼.</summary>
    internal static Func<AppVolume, ImageSource?>? SceneIconForApp { get; set; }

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
        if (SceneIconForApp is { } scene) return scene(a);
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
