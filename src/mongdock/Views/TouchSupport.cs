using System.Windows;
using System.Windows.Input;
using Mongdock.Native;

namespace Mongdock.Views;

/// <summary>
/// 터치스크린(2-in-1) 보조. WPF 는 터치를 마우스 이벤트로 승격하므로 클릭은 그대로 동작하고, 여기서는
/// 터치에서만 다른 부분(길게 누르기, 손 뗀 뒤 호버 해제, 패널 행 높이)을 판단한다.
///
/// 길게 누르기 정리:
/// - 독 아이콘·⌃ 카드의 트레이 아이콘·메뉴: 윈도우 기본 "누르고 있기" 제스처(손 떼면 오른쪽 클릭으로 승격)를 그대로 쓴다.
///   다만 그 전에 왼쪽 누름이 먼저 승격된 경우를 대비해 <see cref="HoldMs"/> 이상 누른 터치는 왼쪽 클릭으로 치지 않는다.
/// - 상단바 오른쪽 아이콘: 0.4초 길게 누르기 = 순서 바꾸기라 시스템 제스처를 끄고(Stylus.IsPressAndHoldEnabled=False),
///   길게 누른 채 움직이지 않고 떼면 그 아이콘의 오른쪽 클릭으로 처리 (TopBarWindow.Reorder).
/// </summary>
internal static class TouchSupport
{
    private const int SM_MAXIMUMTOUCHES = 95;

    /// <summary>이 이상 누른 터치는 "누르고 있기"(오른쪽 클릭)로 보고 왼쪽 클릭을 내보내지 않음.</summary>
    public const int HoldMs = 600;

    private static bool? _hasTouch;

    /// <summary>터치 입력 장치가 있는지 (GetSystemMetrics(SM_MAXIMUMTOUCHES) &gt; 0). 처음 한 번만 확인.</summary>
    public static bool HasTouch
    {
        get
        {
            if (_hasTouch is bool b) return b;
            try { _hasTouch = User32.GetSystemMetrics(SM_MAXIMUMTOUCHES) > 0; }
            catch { _hasTouch = false; }
            return _hasTouch.Value;
        }
    }

    /// <summary>패널·메뉴 행 높이에 더할 값 (터치 장치가 있으면 +4, 아니면 0).</summary>
    public static double RowExtra => HasTouch ? 4 : 0;

    /// <summary>행 버튼 위아래 패딩에 각각 더할 값.</summary>
    public static double RowPad => RowExtra / 2;

    /// <summary>터치에서 승격된 마우스 이벤트인지 (펜은 아님 — 펜은 호버가 있어 마우스처럼 둔다).</summary>
    public static bool IsTouch(MouseEventArgs e)
    {
        try { return e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch; }
        catch { return false; }
    }

    /// <summary>앱 리소스 "MenuRowMinHeight"(Themes/Menus.xaml 메뉴 행 최소 높이)를 터치 장치에 맞춤. 여러 번 불러도 됨.</summary>
    public static void ApplyResources()
    {
        if (Application.Current is not { } app) return;
        double want = 26 + RowExtra;
        if (app.Resources["MenuRowMinHeight"] is double cur && Math.Abs(cur - want) < 0.1) return;
        app.Resources["MenuRowMinHeight"] = want;
    }
}
