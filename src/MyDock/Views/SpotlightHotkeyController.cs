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
        Sync();
    }

    private void OnChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        if (_disposed) return;
        var mode = AppState.Paused ? SpotlightHotkey.None : _services.Settings.Current.TopBar.SpotlightHotkey;
        _hotkey.Apply(mode);
    }

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
        _hotkey.Pressed -= OnPressed;
        _hotkey.Dispose();
    }
}
