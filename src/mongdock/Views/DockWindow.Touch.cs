using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Mongdock.Views;

/// <summary>
/// 독 터치 입력 (2-in-1). 클릭·끌기는 WPF 의 터치→마우스 승격으로 그대로 동작하고 여기서는 터치만의 차이를 맞춘다.
/// - 손가락 아래 아이콘이 확대로 밀려나면 누르기가 어려우므로 터치 이동은 확대하지 않음.
/// - 터치는 손을 떼도 MouseLeave 가 오지 않고 커서가 그 자리에 남음 → 뗀 직후 확대·이름 말풍선 해제,
///   자동 숨김은 "진짜 마우스가 움직이기 전까지" 커서가 독 위에 있어도 바깥으로 본다.
/// - 길게 누르기 = 오른쪽 클릭(메뉴)은 윈도우 기본 "누르고 있기" 제스처가 처리. 그 사이 왼쪽 누름이 먼저 승격돼도
///   <see cref="TouchSupport.HoldMs"/> 이상 누른 터치는 클릭(실행)으로 치지 않는다 (DockWindow.ItemDrag OnItemDragMouseUp).
/// </summary>
public partial class DockWindow
{
    private bool _touchReleased;    // 터치를 뗀 뒤 진짜 마우스가 아직 안 움직임
    private Point? _touchUpCursor;  // 뗀 순간 커서 위치 (독 모니터 DIP)
    private bool _pressTouch;       // 지금 누른 아이콘이 터치로 눌렸는지
    private long _pressTicks;

    private void InitTouch()
    {
        Root.PreviewTouchUp += (_, _) =>
        {
            _touchReleased = true;
            try { _touchUpCursor = _services.DesktopWindows.GetCursorPosition(_monitor); }
            catch { _touchUpCursor = null; }
            // 승격된 마우스 떼기(클릭 처리)가 끝난 뒤 정리
            Dispatcher.BeginInvoke(() =>
            {
                if (_closed || _itemDragging || _dragArmed) return;
                ResetMagnification(animate: true);
                _label?.Hide();
            }, DispatcherPriority.Input);
        };
    }

    /// <summary>
    /// 터치를 뗀 뒤 진짜 마우스가 아직 안 움직였는지 (그동안은 커서 위치를 믿을 수 없음).
    /// 레이아웃 변화로 WPF 가 만드는 가짜 MouseMove 에 속지 않게 커서 좌표가 실제로 바뀌었는지로 판단.
    /// </summary>
    private bool TouchReleasedAt(Point? cursor)
    {
        if (!_touchReleased) return false;
        if (cursor is Point c && (_touchUpCursor is not Point t || Math.Abs(c.X - t.X) > 2 || Math.Abs(c.Y - t.Y) > 2))
        {
            _touchReleased = false;
            return false;
        }
        return true;
    }

    /// <summary>OnRootMouseMove 첫머리: 터치 이동이면 true (확대 안 함).</summary>
    private bool HandleTouchMove(MouseEventArgs e)
    {
        if (TouchSupport.IsTouch(e))
        {
            _touchReleased = false; // 다시 만지는 중 (뗄 때 다시 세움)
            _lastInsideTicks = Environment.TickCount64;
            return true;
        }
        return false;
    }

    private void NotePress(MouseButtonEventArgs e)
    {
        _pressTouch = TouchSupport.IsTouch(e);
        _pressTicks = Environment.TickCount64;
    }

    /// <summary>터치로 <see cref="TouchSupport.HoldMs"/> 이상 누르고 있었는지 (= 누르고 있기 → 메뉴, 클릭 아님).</summary>
    private bool WasTouchHold => _pressTouch && Environment.TickCount64 - _pressTicks >= TouchSupport.HoldMs;
}
