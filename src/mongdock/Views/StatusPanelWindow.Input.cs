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
