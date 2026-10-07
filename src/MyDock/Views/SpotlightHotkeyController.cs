using MyDock.Models;
using MyDock.Services;
using MyDock.ViewModels;

namespace MyDock.Views;

/// <summary>
/// 설정(TopBar.SpotlightHotkey)·일시 정지 상태에 맞춰 Spotlight 전역 단축키 훅을 켜고 끈다.
/// 설정이 바뀌면 즉시 재적용, 일시 정지 중엔 꺼짐, Dispose 시 해제. UI 스레드에서 만들 것.
/// </summary>
internal sealed class SpotlightHotkeyController : IDisposable
{
    private readonly AppServices _services;
    private readonly SpotlightHotkeyService _hotkey = new();
    private bool _disposed;

    public SpotlightHotkeyController(AppServices services)
    {
        _services = services;
        _hotkey.Pressed += OnPressed;
        _services.Settings.SettingsChanged += OnChanged;
        AppState.Changed += OnChanged;
        _services.DesktopWindows.DisplayChanged += OnDisplayChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Sync();
    }

    /// <summary>LL 훅은 시간 초과 등으로 조용히 빠질 수 있음 → 디스플레이 변경·세션 잠금 해제 때 재설치.</summary>
    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (!_disposed) _hotkey.Reinstall("DisplayChanged");
    }

    // SystemEvents 스레드에서 올 수 있음 — Reinstall 은 스레드 무관
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (_disposed) return;
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect
            or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            _hotkey.Reinstall($"세션 {e.Reason}");
    }

    private void OnChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        if (_disposed) return;
        var mode = AppState.Paused ? SpotlightHotkey.None : Resolve(_services.Settings.Current.TopBar.SpotlightHotkey);
        _hotkey.Apply(mode);
    }

    /// <summary>Auto: 키보드 입력 언어가 하나면 Win+Space, 여러 개면 Win+Space 가 언어 전환이므로 Alt+Space.</summary>
    private static SpotlightHotkey Resolve(SpotlightHotkey mode)
    {
        if (mode != SpotlightHotkey.Auto) return mode;
        int layouts = GetKeyboardLayoutList(0, null);
        return layouts > 1 ? SpotlightHotkey.AltSpace : SpotlightHotkey.WinSpace;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, IntPtr[]? lpList);

    private void OnPressed(object? sender, EventArgs e)
    {
        if (_disposed || AppState.Paused) return;
        // 키보드 입력 직후라 포그라운드 잠금은 보통 풀려 있음. 활성화는 SpotlightWindow 의 ForceForeground 가 처리.
        SpotlightWindow.Toggle(_services);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.Settings.SettingsChanged -= OnChanged;
        AppState.Changed -= OnChanged;
        _services.DesktopWindows.DisplayChanged -= OnDisplayChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hotkey.Pressed -= OnPressed;
        _hotkey.Dispose();
    }
}
