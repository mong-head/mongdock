using System.Windows;

namespace Mongdock.Views;

/// <summary>
/// 자동 숨김 독의 「커서 정지」 판정 (#39, 사용자 요청: 영상 볼 때 커서를 가장자리에 밀어 둔 채 두면 독이 계속 나와 있음).
/// 독이 나와 있고 커서가 독 영역 안에서 <see cref="IdleMs"/> 동안 한 픽셀도 안 움직이면 숨김 — 아이콘 위·이름표가 떠 있어도 (사용자 결정).
/// 메뉴·폴더 판·창 고르기·끌기·알림 peek 동안은 DockWindow 가 판정을 쉬고 <see cref="Reset"/> 함.
/// 숨긴 뒤에는 커서가 그 자리 그대로면 다시 꺼내지 않음. 조금이라도 움직이거나, 화면 끝에 붙은 채 독 쪽으로 밀면(마우스 장치의 상대 이동,
/// <see cref="EdgePushWatch"/>) 풀리고 평소 가장자리 규칙대로 나옴. 창·입력이 없는 순수 상태라 시험에서 가짜 커서·시각으로 확인함 (report-test --idlehide).
/// </summary>
internal sealed class CursorIdle
{
    /// <summary>커서가 이만큼(ms) 안 움직이면 숨김.</summary>
    public const long IdleMs = 5000;

    private Point? _last;
    private long _since;
    private Point? _hiddenAt;

    /// <summary>
    /// 독이 나와 있을 때 매 확인마다 (60ms). 커서가 독 영역 안이고 같은 자리에서 IdleMs 이상이면 true — 숨길 때.
    /// </summary>
    public bool ShouldHide(Point? cursor, bool insideDock, long now)
    {
        if (cursor is not Point c)
        {
            _last = null;
            return false;
        }
        if (_last != c)
        {
            _last = c;
            _since = now;
            return false;
        }
        if (!insideDock) { _since = now; return false; }
        if (now - _since < IdleMs) return false;
        _hiddenAt = c;
        return true;
    }

    /// <summary>독 위에서 누르거나 떼거나 휠을 굴림 — 커서는 그대로여도 쓰는 중이므로 정지 시간을 처음부터 (0.8.4 화면 확인: 아이콘을 연달아 누르는 중에 숨었음).</summary>
    public void NoteActivity(long now) => _since = now;

    /// <summary>커서 정지로 숨겨 두고 있는지 (화면 끝 밀기 감시를 켤지).</summary>
    public bool Holding => _hiddenAt is not null;

    /// <summary>
    /// 숨은 동안: 커서 정지로 숨긴 그 자리에 커서가 그대로이고 독 쪽으로 밀지도 않았으면 가장자리에 있어도 다시 꺼내지 않음.
    /// 움직이거나 밀면 풀림.
    /// </summary>
    public bool HoldsHidden(Point? cursor, bool pushedTowardDock)
    {
        if (_hiddenAt is not Point h) return false;
        if (!pushedTowardDock && cursor is Point c && c == h) return true;
        _hiddenAt = null;
        _last = null; // 풀린 다음 판정에서 정지 시간을 처음부터 (옛 정지 시각으로 다음 틱에 바로 다시 숨지 않게)
        return false;
    }

    /// <summary>독이 나타날 때와 판정을 쉬는 동안 (메뉴·끌기·폴더 판·peek·둘러보기) — 지난 정지 기록을 버림. 닫힌 뒤엔 다시 5초를 기다림.</summary>
    public void Reset(long now)
    {
        _last = null;
        _since = now;
        _hiddenAt = null;
    }
}
