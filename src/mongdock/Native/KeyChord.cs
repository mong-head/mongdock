using Mongdock.Services;

namespace Mongdock.Native;

/// <summary>단축키 전송 공통 헬퍼 (User32.SendChord + 실패 로그). 가상 데스크톱/셸 단축키/시작 메뉴/한영이 공유.</summary>
internal static class KeyChord
{
    public static void Send(string name, params ushort[] keys)
    {
        try
        {
            if (!User32.SendChord(keys))
                Log.Error($"단축키 전송 실패({name}) err={User32.LastSendError}");
        }
        catch (Exception ex)
        {
            Log.Error($"단축키 전송 예외({name})", ex);
        }
    }
}
