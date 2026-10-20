using System.Windows;

namespace Mongdock.Views;

/// <summary>
/// 자동 숨김 독의 「커서 정지」 판정 (#39, 사용자 요청: 영상 볼 때 커서를 가장자리에 밀어 둔 채 두면 독이 계속 나와 있음).
/// 독이 나와 있고 커서가 독 영역 안에서 <see cref="IdleMs"/> 동안 한 픽셀도 안 움직이면 숨김. 숨긴 뒤에는 커서가 그 자리 그대로면 다시 꺼내지 않고,
/// 조금이라도 움직이면 평소 가장자리 규칙대로 나옴. 창·입력이 없는 순수 상태라 시험에서 가짜 커서·시각으로 확인함 (report-test --idlehide).
/// </summary>
internal sealed class CursorIdle
{
    /// <summary>커서가 이만큼(ms) 안 움직이면 숨김.</summary>
    public const long IdleMs = 5000;

    private Point? _last;
    private long _since;
    private Point? _hiddenAt;

    /// <summary>
    /// 독이 나와 있을 때 매 확인마다 (60ms). 커서가 독 영역 안이고, 막을 것(이름표·아이콘 위)이 없고, 같은 자리에서 IdleMs 이상이면 true — 숨길 때.
    /// </summary>
    public bool ShouldHide(Point? cursor, bool insideDock, bool blocked, long now)
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
        if (!insideDock || blocked) { _since = now; return false; } // 막힌 동안은 정지 시간을 처음부터 (이름표가 사라진 직후 바로 숨지 않게)
        if (now - _since < IdleMs) return false;
        _hiddenAt = c;
        return true;
    }

    /// <summary>숨은 동안: 커서 정지로 숨긴 그 자리에 커서가 그대로면 가장자리에 있어도 다시 꺼내지 않음. 한 번이라도 움직이면 풀림.</summary>
    public bool HoldsHidden(Point? cursor)
    {
        if (_hiddenAt is not Point h) return false;
        if (cursor is Point c && c == h) return true;
        _hiddenAt = null;
        _last = cursor;
        return false;
    }

    /// <summary>다른 이유로 숨거나 나타날 때 (가장자리 진입·설정 바뀜 등) — 지난 정지 기록을 버림.</summary>
    public void Reset(long now)
    {
        _last = null;
        _since = now;
        _hiddenAt = null;
    }
}
