using Microsoft.Win32;

namespace Mongdock.Services;

/// <summary>
/// 윈도우 업데이트가 설치를 마치려고 다시 시작을 기다리는지 (0.6.2 — 로고 메뉴 「업데이트 후 다시 시작·시스템 종료」, 시작 메뉴와 같게).
/// 읽기만: HKLM\...\WindowsUpdate\Auto Update\RebootRequired 키, ...\Component Based Servicing\RebootPending 키.
/// (Orchestrator\ShutdownFlyoutOptions 는 뜻이 문서에 없어 로그에만 남김.) HKLM 이라 패키지 가상화 영향 없음.
/// 시험: MONGDOCK_FAKE_UPDATE_PENDING=1 이면 대기 중으로.
/// </summary>
public static class WindowsUpdateState
{
    private static string? _lastLogged;

    public static bool RestartPending()
    {
        if (Environment.GetEnvironmentVariable("MONGDOCK_FAKE_UPDATE_PENDING") == "1") return true;
        bool wu = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        bool cbs = KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        object? flyout = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\Orchestrator");
            flyout = k?.GetValue("ShutdownFlyoutOptions");
        }
        catch { /* 읽기 실패는 없음으로 */ }
        string state = $"WU {wu}, CBS {cbs}, flyout {flyout ?? "-"}";
        if (state != _lastLogged)
        {
            _lastLogged = state;
            Log.Info($"윈도우 업데이트 다시 시작 대기: {(wu || cbs ? "있음" : "없음")} ({state})");
        }
        return wu || cbs;
    }

    private static bool KeyExists(string path)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            return k is not null;
        }
        catch { return false; }
    }
}
