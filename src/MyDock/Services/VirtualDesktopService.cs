using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 가상 데스크톱 이동/생성 단축키(Ctrl+Win+←/→/D)를 SendInput 으로 로컬에서 전송.
/// 원격 접속(StarDesk 등)에서 단축키가 전달되지 않아도 클릭만으로 동작하게 하기 위함.
/// (Rainmeter DesktopSwitcher\Switch.ps1 의 keybd_event 순서를 SendInput 한 번으로 옮김. 키 전송은 Native.KeyChord 공유)
/// </summary>
public sealed class VirtualDesktopService : IVirtualDesktopService
{
    public void Previous() => KeyChord.Send("prev", User32.VK_LCONTROL, User32.VK_LWIN, User32.VK_LEFT);

    public void Next() => KeyChord.Send("next", User32.VK_LCONTROL, User32.VK_LWIN, User32.VK_RIGHT);

    public void New() => KeyChord.Send("new", User32.VK_LCONTROL, User32.VK_LWIN, User32.VK_D);
}
