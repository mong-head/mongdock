using Mongdock.Models;
using Mongdock.Services;
using Mongdock.ViewModels;

namespace Mongdock.Views;

/// <summary>
/// 설정(Hotkeys·루틴 키)·일시 정지·전체 화면 앱에 맞춰 전역 단축키 표를 켜고 끈다 (0.6 — 예전 SpotlightHotkeyController).
/// 설정이 바뀌면 바로 다시 적용, 일시 정지·전체 화면 앱 중엔 검색 포함 전부 쉼(게임 키와 겹침 사고 방지), Dispose 시 해제. UI 스레드에서 만들 것.
/// 눌린 키의 할 일(검색 열기·앱 모음 판·제어 센터·알림 판·루틴)은 App 이 넘긴 <c>run</c> 이 처리.
/// </summary>
internal sealed class HotkeyController : IDisposable
{
    public const string Search = "search", AllApps = "allApps", ControlCenter = "controlCenter", Notifications = "notifications", RoutinePrefix = "routine:";
    /// <summary>볼륨 키 (#21): 수정자 없이 누른 볼륨 올림·내림·음소거 키. 올림·내림은 누르고 있으면 반복.</summary>
    public const string VolumeUp = "volume:up", VolumeDown = "volume:down", VolumeMute = "volume:mute";
    public const int VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    /// <summary>설정 창의 키 칸이 키를 받을 때 씀 (몽독이 돌고 있으면 늘 있음).</summary>
    public static HotkeyController? Instance { get; private set; }

    private readonly AppServices _services;
    private readonly Action<string> _run;
    private readonly HotkeyService _hotkey = new();
    private bool _fullscreen;
    private bool _disposed;

    public HotkeyController(AppServices services, Action<string> run)
    {
        _services = services;
        _run = run;
        _hotkey.Pressed += OnPressed;
        _services.Settings.SettingsChanged += OnChanged;
        AppState.Changed += OnChanged;
        _services.DesktopWindows.DisplayChanged += OnDisplayChanged;
        _services.DesktopWindows.FullscreenAppChanged += OnFullscreenChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Instance = this;
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
            _hotkey.Reinstall(Loc.F($"세션 {e.Reason}"));
    }

    private void OnFullscreenChanged(object? sender, bool any)
    {
        if (_disposed || _fullscreen == any) return;
        _fullscreen = any;
        Log.Info(any ? "전체 화면 앱 — 단축키 쉼" : "전체 화면 끝 — 단축키 다시");
        Sync();
    }

    private void OnChanged(object? sender, EventArgs e) => Sync();

    private void Sync()
    {
        if (_disposed) return;
        var s = _services.Settings.Current;
        // 예전 값도 같이 맞춰 둠 (0.5.x 로 되돌려도 검색 키가 남게)
        s.TopBar.SpotlightHotkey = HotkeySettings.ToLegacy(s.Hotkeys.Search);
        _hotkey.Apply(AppState.Paused || _fullscreen ? new Dictionary<HotkeyChord, string>() : Table(s));
    }

    /// <summary>켜져 있고 키가 있는 칸만. 같은 키가 둘이면 앞의 것 (설정 화면이 겹침을 막음).</summary>
    internal static Dictionary<HotkeyChord, string> Table(Settings s)
    {
        var t = new Dictionary<HotkeyChord, string>();
        void Add(HotkeyBinding? b, string action, bool search = false)
        {
            if (b is not { On: true }) return;
            var chord = search ? SearchChord(b) : HotkeyChord.Parse(b.Key);
            if (chord is { } c && !t.ContainsKey(c)) t[c] = action;
        }
        Add(s.Hotkeys.Search, Search, search: true);
        Add(s.Hotkeys.AllApps, AllApps);
        Add(s.Hotkeys.ControlCenter, ControlCenter);
        Add(s.Hotkeys.Notifications, Notifications);
        foreach (var r in s.Routines) Add(r.Hotkey, RoutinePrefix + r.Id);
        if (s.TopBar.VolumeIndicator)
        {
            t[new HotkeyChord(false, false, false, false, VK_VOLUME_UP)] = VolumeUp;
            t[new HotkeyChord(false, false, false, false, VK_VOLUME_DOWN)] = VolumeDown;
            t[new HotkeyChord(false, false, false, false, VK_VOLUME_MUTE)] = VolumeMute;
        }
        return t;
    }

    /// <summary>검색 칸의 실제 키 ("auto" → 이 PC 에 맞는 키). 꺼져 있으면 null.</summary>
    public static HotkeyChord? SearchChord(HotkeyBinding b)
    {
        if (!b.On) return null;
        if (b.Key is null || b.Key.Equals(HotkeyBinding.Auto, StringComparison.OrdinalIgnoreCase))
            return AutoSearchChord();
        return HotkeyChord.Parse(b.Key);
    }

    public static HotkeyChord? SearchChord(Settings s) => SearchChord(s.Hotkeys.Search);

    /// <summary>자동: 키보드 입력 언어가 하나면 Win+Space, 여러 개면 Win+Space 가 언어 전환이므로 Alt+Space.</summary>
    public static HotkeyChord AutoSearchChord()
    {
        int layouts = GetKeyboardLayoutList(0, null);
        return layouts > 1
            ? new HotkeyChord(false, true, false, false, HotkeyChord.VK_SPACE)
            : new HotkeyChord(false, false, false, true, HotkeyChord.VK_SPACE);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, IntPtr[]? lpList);

    /// <summary>설정 창 키 칸: 다음 조합 하나 받기 (받는 동안 단축키는 동작 안 함). null = 그만.</summary>
    public void Record(Action<HotkeyService.RecordKind, HotkeyChord>? recorder) => _hotkey.Record(recorder);

    public bool IsRecording => _hotkey.IsRecording;

    private void OnPressed(string action)
    {
        if (_disposed || AppState.Paused || _fullscreen) return;
        Log.Info($"단축키: {(action.StartsWith(RoutinePrefix, StringComparison.Ordinal) ? "루틴" : action)}");
        _run(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Instance == this) Instance = null;
        _services.Settings.SettingsChanged -= OnChanged;
        AppState.Changed -= OnChanged;
        _services.DesktopWindows.DisplayChanged -= OnDisplayChanged;
        _services.DesktopWindows.FullscreenAppChanged -= OnFullscreenChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        _hotkey.Pressed -= OnPressed;
        _hotkey.Dispose();
    }
}
