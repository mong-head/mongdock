using MyDock.Native;

namespace MyDock.Services;

/// <summary>원격(StarDesk)에서 Win 단축키가 안 넘어가므로 로컬에서 SendInput 으로 대신 보냄.</summary>
public sealed class ShellActions : IShellActions
{
    public void OpenStartMenu() => KeyChord.Send("Win", User32.VK_LWIN);

    public void OpenSearch() => KeyChord.Send("Win+S", User32.VK_LWIN, User32.VK_S);

    public void OpenQuickSettings() => KeyChord.Send("Win+A", User32.VK_LWIN, User32.VK_A);

    public void OpenNotificationCenter() => KeyChord.Send("Win+N", User32.VK_LWIN, User32.VK_N);

    public void OpenTaskView() => KeyChord.Send("Win+Tab", User32.VK_LWIN, User32.VK_TAB);
}
