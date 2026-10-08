using System.Windows;
using System.Windows.Controls;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 사운드 카드의 "입력" 구역 (맥 사운드 설정의 입력처럼): 마이크 끄기/켜기 버튼 + 입력 음량 슬라이더 + 입력 장치 목록.
/// (말할 때 움직이는 레벨 막대는 정신없다는 의견으로 넣지 않음.)
/// 실제 입력 장치(루프백 제외)가 하나도 없으면 구역 전체를 숨김.
/// "스테레오 믹스" 같은 루프백 입력은 목록에서 빼되, 그게 지금 기본 장치면 맨 아래에 보여 다른 장치로 바꿀 수 있게.
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private const string MicGlyph = "\uE720";    // Microphone
    private const string MicOffGlyph = "\uF781"; // MicOff2
    private UIElement BuildInputSection()
    {
        var st = _services.Status;
        var section = new StackPanel();
        section.Children.Add(Divider());
        var title = Sub("입력");
        title.Margin = new Thickness(0, 0, 0, 6);
        section.Children.Add(title);

        // [마이크 버튼] [슬라이더]
        var controls = new StackPanel();
        var row = new DockPanel { LastChildFill = true };
        var micCircle = Circle(MicGlyph, true, 26, 13);
        var micButton = new Button
        {
            Style = (Style)FindStyle("CardButton"),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = _p.Text,
            Padding = new Thickness(0),
            Content = micCircle,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        micButton.Click += (_, _) =>
        {
            try
            {
                bool mute = !st.InputMuted;
                st.SetInputMuted(mute);
                if (!mute && st.InputVolume <= 0) st.SetInputVolume(0.5); // 음량 0 인 채로 켜면 안 들리니 올림
            }
            catch (Exception ex) { Log.Error("마이크 켜기/끄기 실패", ex); }
            RefreshAll();
        };
        row.Children.Add(micButton);
        var slider = new PillSlider(_p) { WheelAdjusts = false, VerticalAlignment = VerticalAlignment.Center };
        slider.UserChanged += (_, v) =>
        {
            try
            {
                if (st.InputMuted && v > 0) st.SetInputMuted(false);
                st.SetInputVolume(v);
            }
            catch (Exception ex) { Log.Error("입력 음량 설정 실패", ex); }
        };
        row.Children.Add(slider);
        controls.Children.Add(row);

        section.Children.Add(controls);

        var devices = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        section.Children.Add(devices);

        string signature = "";
        _refreshers.Add(() =>
        {
            var all = st.InputDevices ?? Array.Empty<AudioDevice>();
            var visible = all.Where(d => d.Kind != AudioDeviceKind.Loopback || d.IsDefault)
                .OrderBy(d => d.Kind == AudioDeviceKind.Loopback ? 1 : 0)
                .ToList();
            bool any = all.Any(d => d.Kind != AudioDeviceKind.Loopback);
            section.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            controls.Visibility = st.HasInput ? Visibility.Visible : Visibility.Collapsed;
            if (!any) return;

            bool muted = st.InputMuted;
            if (!slider.IsDragging) slider.Value = st.InputVolume;
            slider.Opacity = muted ? 0.4 : 1;
            SetCircle(micCircle, muted ? MicOffGlyph : MicGlyph, !muted);
            micButton.ToolTip = muted ? "마이크 켜기" : "마이크 끄기";

            string sig = string.Join("|", visible.Select(d => $"{d.Id}:{d.IsDefault}:{d.Name}:{d.Kind}"));
            if (sig == signature) return;
            signature = sig;
            devices.Children.Clear();
            foreach (var d in visible)
            {
                var id = d.Id;
                devices.Children.Add(DeviceRow(InputGlyph(d.Kind), d.IsDefault, d.Name,
                    d.Kind == AudioDeviceKind.Loopback ? "재생 소리 녹음" : null,
                    d.IsDefault ? null : () => st.SetDefaultInput(id)));
            }
            devices.Visibility = visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        return section;
    }

    private static string InputGlyph(AudioDeviceKind kind) => kind switch
    {
        AudioDeviceKind.Headphones => "\uE7F6", // Headphone (헤드셋)
        AudioDeviceKind.LineIn or AudioDeviceKind.Digital => "\uE8D6", // Audio
        AudioDeviceKind.Loopback => "\uE767", // Volume
        _ => MicGlyph,
    };
}
