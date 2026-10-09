using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 사운드 카드의 "입력" 구역 (맥 사운드 설정의 입력처럼): 마이크 끄기/켜기 버튼 + 입력 음량 슬라이더 + 입력 장치 목록.
/// 입력 레벨은 맥 사운드 설정처럼 점 10개 — 정신없지 않게 0.4초 동안의 최댓값을 0.2초마다 한 칸 단위로만 바꿈, 옅은 글자색.
/// 실제 입력 장치(루프백 제외)가 하나도 없으면 구역 전체를 숨김.
/// "스테레오 믹스" 같은 루프백 입력은 목록에서 빼되, 그게 지금 기본 장치면 맨 아래에 보여 다른 장치로 바꿀 수 있게.
/// </summary>
internal sealed partial class StatusPanelWindow
{
    private const string MicGlyph = "\uE720";    // Microphone
    private const string MicOffGlyph = "\uF781"; // MicOff2
    /// <summary>레벨 샘플 주기 (패널이 열려 있고 입력 구역이 보일 때만).</summary>
    private static readonly TimeSpan InputMeterSample = TimeSpan.FromMilliseconds(50);
    /// <summary>표시를 바꾸는 주기 — 이 사이의 최댓값을 보여 줌 (출렁임을 줄임).</summary>
    private const int InputMeterShowEvery = 4;
    private const int InputMeterDots = 10;

    private UIElement BuildInputSection()
    {
        var st = _services.Status;
        var section = new StackPanel();
        section.Children.Add(Divider());
        var title = Sub(Loc.T("입력"));
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

        // 입력 레벨: 슬라이더 아래 작은 점 10개. 녹음 중인 앱이 없으면 윈도우가 0 만 주므로 그땐 모두 꺼진 점.
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(38, 7, 0, 0), ToolTip = Loc.T("입력 레벨") };
        var dotList = new List<Ellipse>();
        for (int i = 0; i < InputMeterDots; i++)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 6, 0), Fill = _p.SubText, Opacity = 0.18 };
            dotList.Add(dot);
            dots.Children.Add(dot);
        }
        controls.Children.Add(dots);
        section.Children.Add(controls);

        int lit = 0, sampleCount = 0;
        double windowMax = 0;
        var meterTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = InputMeterSample };
        meterTimer.Tick += (_, _) =>
        {
            if (_closed) { meterTimer.Stop(); return; }
            double peak = st.InputMuted ? 0 : st.GetInputPeak();
            windowMax = Math.Max(windowMax, MeterLevel(peak));
            if (++sampleCount < InputMeterShowEvery) return;
            int target = (int)Math.Round(windowMax * InputMeterDots);
            sampleCount = 0;
            windowMax = 0;
            // 오를 땐 바로, 내릴 땐 한 번에 한 칸씩 (뚝뚝 끊기지 않게)
            int next = target >= lit ? target : lit - 1;
            if (next == lit) return;
            lit = next;
            for (int i = 0; i < dotList.Count; i++) dotList[i].Opacity = i < lit ? 0.65 : 0.18;
        };
        Closed += (_, _) => meterTimer.Stop();

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
            bool run = any && st.HasInput && IsLoaded;
            if (run && !meterTimer.IsEnabled) meterTimer.Start();
            else if (!run && meterTimer.IsEnabled) meterTimer.Stop();
            if (!any) return;

            bool muted = st.InputMuted;
            if (!slider.IsDragging) slider.Value = st.InputVolume;
            slider.Opacity = muted ? 0.4 : 1;
            dots.Opacity = muted ? 0.4 : 1;
            SetCircle(micCircle, muted ? MicOffGlyph : MicGlyph, !muted);
            micButton.ToolTip = muted ? Loc.T("마이크 켜기") : Loc.T("마이크 끄기");

            // 장치마다 지금 녹음 중인 앱 ("사용 중: Discord") — 슬라이더는 기본 장치만 바꾸므로, 앱이 다른 마이크를 쓰면 알 수 있게
            var inUse = InputUsageByDevice();
            string sig = string.Join("|", visible.Select(d => $"{d.Id}:{d.IsDefault}:{d.Name}:{d.Kind}:{(inUse.TryGetValue(d.Id, out var u) ? u : "")}"));
            if (sig == signature) return;
            signature = sig;
            devices.Children.Clear();
            foreach (var d in visible)
            {
                var id = d.Id;
                string? sub = inUse.TryGetValue(d.Id, out var apps) ? Loc.T("사용 중: ") + apps
                    : d.Kind == AudioDeviceKind.Loopback ? Loc.T("재생 소리 녹음") : null;
                devices.Children.Add(DeviceRow(InputGlyph(d.Kind), d.IsDefault, d.Name, sub,
                    d.IsDefault ? null : () => st.SetDefaultInput(id)));
            }
            devices.Visibility = visible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        return section;
    }

    /// <summary>입력 장치 id → 그 장치로 지금 녹음 중인 앱 이름들 ("Discord, 녹음기"). 실패하면 빈 사전.</summary>
    private static Dictionary<string, string> InputUsageByDevice()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (id, pids) in Native.AudioSessions.ActiveCaptureByDevice())
            {
                var names = pids.Select(pid =>
                {
                    var (path, _) = Native.Kernel32.QueryProcess(pid);
                    return string.IsNullOrEmpty(path) ? null
                        : ViewModels.AppNames.Get(new Models.AppWindowInfo(IntPtr.Zero, "", path, null, false));
                }).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                if (names.Count > 0) result[id] = string.Join(", ", names);
            }
        }
        catch (Exception ex) { Log.Warn($"입력 장치 사용 앱 조회 실패: {ex.Message}"); }
        return result;
    }

    /// <summary>피크(진폭 0~1) → 0~1. -60dB~0dB 를 선형으로 (말소리가 중간쯤에 오게).</summary>
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
