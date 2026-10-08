using System.Windows.Controls;

namespace MyDock.Views;

/// <summary>
/// 코치마크: 말풍선이 가리키는 상단바 요소를 사용자가 직접 누르면 평소처럼 패널·메뉴가 열리고,
/// 코치마크는 그동안 숨었다가 그것이 닫히면 다시 나타난다 — 그 "열려 있음" 판정.
/// </summary>
public partial class TopBarWindow
{
    private ContextMenu? _appNameMenu;

    /// <summary>이 상단바에서 연 패널(달력·트레이·상태)이나 메뉴(로고·앱 이름·앱 메뉴·오른쪽 클릭)가 열려 있는지.</summary>
    internal bool CoachPopupOpen() =>
        !_closed && (_panel is { IsClosing: false }
                     || _logoMenu?.IsOpen == true
                     || _appNameMenu?.IsOpen == true
                     || _openAppMenu?.IsOpen == true
                     || ContextMenu?.IsOpen == true);
}
