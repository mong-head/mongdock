using System.Windows;
using System.Windows.Media;
using Mongdock.Converters;
using Mongdock.Services;

namespace Mongdock.Views;

/// <summary>
/// 카메라·마이크 사용 중 점 (맥 메뉴 막대처럼): 카메라를 쓰는 앱이 있으면 초록, 마이크만이면 주황, 없으면 숨김(자리 차지 없음).
/// 나타날 때 짧은 페이드. 누르면 어떤 앱이 쓰는지 작은 카드 (StatusPanelWindow.Privacy.cs).
/// 감지는 공유 PrivacyUsageService (모니터별 상단바가 여러 개여도 감시 스레드 하나).
/// </summary>
public partial class TopBarWindow
{
    internal static readonly Brush PrivacyCameraBrush = BrushParser.Frozen(BrushParser.Hex("#FF30D158"));
    internal static readonly Brush PrivacyMicBrush = BrushParser.Frozen(BrushParser.Hex("#FFFF9F0A"));

    private bool _privacyWatch;

    private void SetPrivacyWatch(bool on)
    {
        if (on == _privacyWatch) return;
        _privacyWatch = on;
        var svc = PrivacyUsageService.Shared;
        try
        {
            if (on)
            {
                svc.Changed += OnPrivacyChanged;
                svc.Acquire();
            }
            else
            {
                svc.Changed -= OnPrivacyChanged;
                svc.Release();
            }
        }
        catch (Exception ex) { Log.Error("카메라·마이크 사용 감시 설정 실패", ex); }
        UpdatePrivacy();
    }

    private void OnPrivacyChanged(object? sender, EventArgs e) => UpdatePrivacy();

    private void UpdatePrivacy()
    {
        if (_closed) return;
        var svc = PrivacyUsageService.Shared;
        bool camera = _privacyWatch && svc.CameraInUse;
        bool mic = _privacyWatch && svc.MicrophoneInUse;
        bool show = camera || mic;

        if (show)
        {
            var fill = camera ? PrivacyCameraBrush : PrivacyMicBrush;
            if (!ReferenceEquals(PrivacyDot.Fill, fill)) PrivacyDot.Fill = fill;
            PrivacyButton.ToolTip = camera && mic ? Loc.T("카메라·마이크 사용 중") : camera ? Loc.T("카메라 사용 중") : Loc.T("마이크 사용 중");
        }

        var vis = show ? Visibility.Visible : Visibility.Collapsed;
        if (PrivacyButton.Visibility == vis) return;
        PrivacyButton.Visibility = vis;
        // 점이 사라져도 열린 카드는 그대로 ("쓰는 앱이 없어요" 로 바뀜) — 링크를 누르려는 순간 닫혀 클릭이 허공에 떨어지지 않게
        if (show) Anim.Appear(PrivacyDot, 220, fromScale: 0.4, origin: new Point(0.5, 0.5));
        Remeasure(RightSection);
    }

    private void OnPrivacyClick(object sender, RoutedEventArgs e) => TogglePanel(StatusPanelKind.Privacy, PrivacyButton);
}
