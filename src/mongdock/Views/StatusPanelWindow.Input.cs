using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 사운드 카드의 "입력" 구역 (맥 사운드 설정의 입력처럼): 마이크 끄기/켜기 버튼 + 입력 음량 슬라이더 + 입력 레벨 막대 + 입력 장치 목록.
/// 실제 입력 장치(루프백 제외)가 하나도 없으면 구역 전체를 숨김.
/// "스테레오 믹스" 같은 루프백 입력은 목록에서 빼되, 그게 지금 기본 장치면 맨 아래에 보여 다른 장치로 바꿀 수 있게.
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private const string MicGlyph = "\uE720";    // Microphone
    private const string MicOffGlyph = "\uF781"; // MicOff2
    /// <summary>입력 레벨 막대 갱신 주기 (패널이 열려 있고 입력 구역이 보일 때만).</summary>
    private static readonly TimeSpan InputMeterInterval = TimeSpan.FromMilliseconds(80);

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

        // 입력 레벨: 슬라이더 아래 얇은 막대. 녹음 중인 앱이 없으면 윈도우가 0 만 주므로, 한 번이라도 소리가 잡히기 전엔
        // 자리만 차지하고 숨김 (멈춘 막대가 고장처럼 보이지 않게).
        var meter = new Grid
        {
            Height = 3,
            Margin = new Thickness(36, 6, 0, 0), // 슬라이더 트랙과 같은 폭
            Visibility = Visibility.Hidden,
            ToolTip = "입력 레벨",
        };
        meter.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = _p.SliderTrack });
        var meterFill = new Border { CornerRadius = new CornerRadius(1.5), Background = _p.Accent, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        meter.Children.Add(meterFill);
        controls.Children.Add(meter);
        section.Children.Add(controls);

        var devices = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        section.Children.Add(devices);

        double shown = 0;
        var meterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = InputMeterInterval };
        meterTimer.Tick += (_, _) =>
        {
            if (_closed) { meterTimer.Stop(); return; }
            double peak = st.InputMuted ? 0 : st.GetInputPeak();
            double level = MeterLevel(peak);
            // 오를 땐 바로, 내릴 땐 천천히 (맥 레벨 표시처럼)
            shown = level >= shown ? level : Math.Max(level, shown - 0.08);
            if (level > 0 && meter.Visibility != Visibility.Visible) meter.Visibility = Visibility.Visible;
            double w = meter.ActualWidth;
            if (w > 0) meterFill.Width = Math.Round(w * shown, 1);
        };
        Closed += (_, _) => meterTimer.Stop();

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
            bool run = any && st.HasInput && IsLoaded;
            if (run && !meterTimer.IsEnabled) meterTimer.Start();
            else if (!run && meterTimer.IsEnabled) meterTimer.Stop();
            if (!any) return;

            bool muted = st.InputMuted;
            if (!slider.IsDragging) slider.Value = st.InputVolume;
            slider.Opacity = muted ? 0.4 : 1;
            meter.Opacity = muted ? 0.4 : 1;
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

    /// <summary>피크(진폭 0~1) → 막대 길이 0~1. -60dB~0dB 를 선형으로 (말소리가 막대 중간쯤에 오게).</summary>
    internal static double MeterLevel(double peak)
    {
        if (peak <= 0.001) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    private static string InputGlyph(AudioDeviceKind kind) => kind switch
    {
        AudioDeviceKind.Headphones => "\uE7F6", // Headphone (헤드셋)
        AudioDeviceKind.LineIn or AudioDeviceKind.Digital => "\uE8D6", // Audio
        AudioDeviceKind.Loopback => "\uE767", // Volume
        _ => MicGlyph,
    };
}
