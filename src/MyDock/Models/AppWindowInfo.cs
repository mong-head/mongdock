namespace MyDock.Models;

/// <summary>독에 표시할 수 있는 최상위 앱 창 하나.</summary>
public sealed record AppWindowInfo(
    IntPtr Hwnd,
    string Title,
    // 프로세스 exe 전체 경로 (알 수 없으면 "").
    string ProcessPath,
    // 창의 AppUserModelID (스토어 앱 등). 없으면 null.
    string? Aumid,
    bool IsMinimized,
    // 이 창이 있는 가상 데스크톱 번호 (1부터, 모르면 0) / 현재 데스크톱에 있는지.
    int DesktopIndex = 0,
    bool OnCurrentDesktop = true);
