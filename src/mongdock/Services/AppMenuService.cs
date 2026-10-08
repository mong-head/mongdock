using System.IO;
using System.Runtime.InteropServices;
using Mongdock.Models;
using Mongdock.Native;

namespace Mongdock.Services;

/// <summary>
/// 맥처럼 상단바에 포그라운드 앱의 메뉴를 보여 주기 위한 메뉴 정의.
/// 우선순위: 1) settings.AppMenus[AUMID / exe 파일명 / exe 전체 경로] → 2) 창의 Win32 메뉴 막대(HMENU, 숨긴 것 포함)
/// → 3) 앱 전용 메뉴 규칙(menus/app-menus.json — 내장 + GitHub 갱신, <see cref="MenuRulesService"/>)
/// → 4) UI 자동화 메뉴 막대(제목만, 누르면 앱 메뉴를 펼침) → 5) 범용 내장 메뉴(Electron·기본, <see cref="BuiltinMenus"/>).
/// Payload: Win32 메뉴 항목은 <see cref="Win32Command"/>(명령 ID), 단축키 항목은 단축키 문자열(예: "Ctrl+Shift+T", "Ctrl+K Ctrl+S"),
/// 창 동작 항목은 <see cref="WindowAction"/>(WM_SYSCOMMAND).
/// 제목이 "@app" 인 메뉴는 상단바 제목이 아니라 앱 이름 메뉴에 덧붙는다(<see cref="GetAppNameItems"/>).
/// </summary>
public sealed class AppMenuService : IAppMenuService, IDisposable
{
    /// <summary>Win32 메뉴 항목의 명령 ID (Invoke 시 WM_COMMAND).</summary>
    public sealed record Win32Command(uint Id);

    /// <summary>창 동작 항목 (규칙의 "action": close/minimize/maximize/restore) — WM_SYSCOMMAND 값.</summary>
    public sealed record WindowAction(string Name, int SysCommand);

    private readonly ISettingsService _settings;
    /// <summary>변환한 내장 메뉴. 키 = 범용 종류 문자열("electron"/"generic") 또는 <see cref="MenuRule"/>. 규칙이 바뀌면 비움.</summary>
    private readonly Dictionary<object, IReadOnlyList<AppMenu>> _builtinCache = new();
    private readonly MenuRulesService _rules;
    private readonly Dictionary<string, bool> _electronCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeMenuHider _hider = new();
    private readonly UiaMenuReader _uia = new();
    private bool _hideWanted;

    public event EventHandler<IntPtr>? MenusChanged;

    public AppMenuService(ISettingsService settings)
    {
        _settings = settings;
        _hider.RecoverFromLastRun();
        _uia.Scanned += hwnd => MenusChanged?.Invoke(this, hwnd);
        _settings.SettingsChanged += OnSettingsOrPauseChanged;
        ViewModels.AppState.Changed += OnSettingsOrPauseChanged;
        _hideWanted = HideWanted();
        _rules = new MenuRulesService(settings);
        _rules.RulesChanged += OnRulesChanged;
        _rules.Start();
    }

    /// <summary>원격 규칙이 반영됨 → 변환 캐시를 비우고 지금 포그라운드 창의 메뉴를 다시 그리게 함.</summary>
    private void OnRulesChanged()
    {
        lock (_builtinCache) _builtinCache.Clear();
        MenusChanged?.Invoke(this, User32.GetForegroundWindow());
    }

    /// <summary>실험 기능 "앱 창 안 메뉴 줄 숨기기" 가 지금 동작해야 하는지.</summary>
    private bool HideWanted()
    {
        var t = _settings.Current.TopBar;
        return t.Enabled && t.ShowAppMenus && t.HideNativeMenuBars && !ViewModels.AppState.Paused;
    }

    private void OnSettingsOrPauseChanged(object? sender, EventArgs e)
    {
        bool wanted = HideWanted();
        if (_hideWanted && !wanted) _hider.RestoreAll();
        _hideWanted = wanted;
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsOrPauseChanged;
        ViewModels.AppState.Changed -= OnSettingsOrPauseChanged;
        _rules.RulesChanged -= OnRulesChanged;
        _rules.Dispose();
        _hider.Dispose(); // 숨긴 메뉴 줄 복원 (최대 1.5초 대기)
    }

    // ───────────────────────── 메뉴 얻기 ─────────────────────────

    public IReadOnlyList<AppMenu> GetMenus(AppWindowInfo window)
    {
        if (window is null) return Array.Empty<AppMenu>();
        try
        {
            var user = FindUserMenus(window);
            if (user is not null)
            {
                var own = TitlesOnly(Convert(user));
                if (own.Count > 0) return own; // "@app" 만 정의했으면 아래 순서대로
            }

            var win32 = ReadWin32Menu(window.Hwnd);
            if (win32.Count > 0)
            {
                if (_hideWanted) _hider.TryHide(window.Hwnd, window.ProcessPath);
                return win32;
            }

            string exe = Path.GetFileName(window.ProcessPath).ToLowerInvariant();
            string cls = User32.GetClassNameOf(window.Hwnd);
            if (IsConsole(exe, cls)) return Array.Empty<AppMenu>(); // Ctrl+C 등이 다른 의미

            var rule = _rules.Current.Find(exe, cls, window.Aumid);
            if (rule is not null) return TitlesOnly(GetBuiltin(rule));

            if (UiaMenuReader.IsCandidate(window.Hwnd, cls))
            {
                var titles = _uia.GetTitles(window.Hwnd); // null = 조회 중 → 일단 대체 메뉴, 끝나면 MenusChanged
                if (titles is { Count: > 0 })
                    return titles.Select(t => new AppMenu(t, Array.Empty<AppMenuItem>())).ToList();
            }
            return TitlesOnly(GetBuiltin(IsElectron(window.ProcessPath) ? "electron" : "generic"));
        }
        catch (Exception ex)
        {
            Log.Error("앱 메뉴 조회 실패", ex);
            return Array.Empty<AppMenu>();
        }
    }

    public IReadOnlyList<AppMenuItem> GetAppNameItems(AppWindowInfo window)
    {
        if (window is null) return Array.Empty<AppMenuItem>();
        try
        {
            IReadOnlyList<AppMenu> menus;
            var user = FindUserMenus(window);
            if (user is not null) menus = Convert(user);
            else
            {
                string exe = Path.GetFileName(window.ProcessPath).ToLowerInvariant();
                string cls = User32.GetClassNameOf(window.Hwnd);
                var rule = _rules.Current.Find(exe, cls, window.Aumid);
                if (rule is null) return Array.Empty<AppMenuItem>();
                menus = GetBuiltin(rule);
            }
            return menus.FirstOrDefault(m => m.Title == BuiltinMenus.AppNameMenuTitle)?.Items ?? Array.Empty<AppMenuItem>();
        }
        catch (Exception ex)
        {
            Log.Error("앱 이름 메뉴 항목 조회 실패", ex);
            return Array.Empty<AppMenuItem>();
        }
    }

    public void OpenNativeMenu(IntPtr hwnd, AppMenu menu, int index)
    {
        if (hwnd == IntPtr.Zero || menu is null) return;
        _ = _uia.ExpandAsync(hwnd, menu.Title, index).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !t.Result) Log.Warn($"앱 메뉴 '{menu.Title}' 를 펼치지 못함");
        }, TaskScheduler.Default);
    }

    private static IReadOnlyList<AppMenu> TitlesOnly(IReadOnlyList<AppMenu> menus) =>
        menus.Any(m => m.Title == BuiltinMenus.AppNameMenuTitle)
            ? menus.Where(m => m.Title != BuiltinMenus.AppNameMenuTitle).ToList()
            : menus;

    private static bool IsConsole(string exe, string cls) =>
        cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "VirtualConsoleClass" or "PuTTY"
        || exe is "windowsterminal.exe" or "wt.exe" or "conhost.exe" or "openconsole.exe" or "mintty.exe" or "alacritty.exe" or "wezterm-gui.exe";

    private List<AppMenuDef>? FindUserMenus(AppWindowInfo w)
    {
        var map = _settings.Current.AppMenus;
        if (map is null || map.Count == 0) return null;
        foreach (string? key in new[] { w.Aumid, Path.GetFileName(w.ProcessPath), w.ProcessPath })
        {
            if (string.IsNullOrEmpty(key)) continue;
            foreach (var kv in map)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && kv.Value is { Count: > 0 })
                    return kv.Value;
        }
        return null;
    }

    // ───────────────────────── Win32 HMENU ─────────────────────────

    /// <summary>
    /// 창의 메뉴 막대를 재귀로 읽음. 보통은 WM_INITMENUPOPUP 을 보내지 않으므로 상태는 앱이 마지막으로 정한 그대로.
    /// 메뉴 줄을 숨긴 창(<see cref="NativeMenuHider"/>)은 사용자가 앱 메뉴를 직접 열 수 없어 상태가 갱신되지 않으므로,
    /// 읽기 전에 각 하위 메뉴에 WM_INITMENUPOPUP 을 보내(응답 없으면 100ms 에 포기) 체크·비활성 상태를 맞춘다.
    /// </summary>
    private IReadOnlyList<AppMenu> ReadWin32Menu(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Array.Empty<AppMenu>();
        IntPtr bar = MenuApi.GetMenu(hwnd);
        if (bar == IntPtr.Zero)
        {
            bar = _hider.GetHiddenMenu(hwnd);
            if (bar != IntPtr.Zero) RefreshHiddenMenuState(hwnd, bar);
        }
        if (bar == IntPtr.Zero || !MenuApi.IsMenu(bar)) return Array.Empty<AppMenu>();
        var result = new List<AppMenu>();
        foreach (var top in ReadItems(bar, depth: 0))
        {
            if (top.IsSeparator || top.Text.Length == 0) continue; // MDI 자식 시스템 메뉴 비트맵 등 제외
            var children = top.Children ?? Array.Empty<AppMenuItem>();
            if (children.Count == 0) continue; // 메뉴 막대에 바로 붙은 명령은 상단바 메뉴로 표현 불가 → 제외
            result.Add(new AppMenu(top.Text, children));
        }
        return result;
    }

    private static void RefreshHiddenMenuState(IntPtr hwnd, IntPtr bar)
    {
        if (MenuApi.IsHungAppWindow(hwnd)) return;
        int count = Math.Min(MenuApi.GetMenuItemCount(bar), 30);
        for (int i = 0; i < count; i++)
        {
            IntPtr sub = MenuApi.GetSubMenu(bar, i);
            if (sub == IntPtr.Zero) continue;
            // lParam: LOWORD = 위치, HIWORD = 시스템 메뉴 아님(0)
            if (User32.SendMessageTimeout(hwnd, MenuApi.WM_INITMENUPOPUP, sub, new IntPtr(i), SmtoAbortIfHung, 100, out _) == IntPtr.Zero)
                return; // 시간 초과·실패 → 나머지도 생략
        }
    }

    private const uint SmtoAbortIfHung = 0x0002;

    private static List<AppMenuItem> ReadItems(IntPtr menu, int depth)
    {
        var list = new List<AppMenuItem>();
        if (depth > 6) return list;
        int count = MenuApi.GetMenuItemCount(menu);
        for (uint i = 0; i < count && i < 200; i++)
        {
            var mii = new MENUITEMINFO
            {
                cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MenuApi.MIIM_FTYPE | MenuApi.MIIM_STATE | MenuApi.MIIM_ID | MenuApi.MIIM_SUBMENU | MenuApi.MIIM_STRING,
            };
            if (!MenuApi.GetMenuItemInfo(menu, i, true, ref mii)) continue;
            // 문자열을 받는 두 번째 호출 전에 값을 보관 (두 번째 호출이 구조체를 다시 채움)
            uint fType = mii.fType, fState = mii.fState, id = mii.wID;
            IntPtr hSub = mii.hSubMenu;

            if ((fType & MenuApi.MFT_SEPARATOR) != 0)
            {
                if (list.Count > 0 && !list[^1].IsSeparator) list.Add(Separator);
                continue;
            }

            string raw = "";
            if (mii.cch > 0)
            {
                uint cch = mii.cch + 1;
                IntPtr buf = Marshal.AllocHGlobal((int)cch * 2);
                try
                {
                    var sm = new MENUITEMINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                        fMask = MenuApi.MIIM_STRING,
                        dwTypeData = buf,
                        cch = cch,
                    };
                    if (MenuApi.GetMenuItemInfo(menu, i, true, ref sm)) raw = Marshal.PtrToStringUni(buf) ?? "";
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            var (text, shortcut) = SplitMenuText(raw);
            if (text.Length == 0) continue; // 소유자 그리기/비트맵 전용 항목 — 텍스트를 알 수 없음

            IReadOnlyList<AppMenuItem>? children = null;
            if (hSub != IntPtr.Zero)
            {
                var sub = ReadItems(hSub, depth + 1);
                TrimSeparators(sub);
                children = sub;
            }
            bool enabled = (fState & MenuApi.MFS_DISABLED) == 0;
            bool check = (fState & MenuApi.MFS_CHECKED) != 0;
            list.Add(new AppMenuItem(text, false, enabled, check, shortcut, children,
                children is null ? new Win32Command(id) : null));
        }
        TrimSeparators(list);
        return list;
    }

    /// <summary>"열기(&amp;O)...\tCtrl+O" → ("열기(O)...", "Ctrl+O"). '&amp;&amp;' 는 '&amp;' 로.</summary>
    internal static (string Text, string? Shortcut) SplitMenuText(string raw)
    {
        string? shortcut = null;
        int tab = raw.IndexOf('\t');
        if (tab >= 0)
        {
            shortcut = raw[(tab + 1)..].Trim();
            if (shortcut.Length == 0) shortcut = null;
            raw = raw[..tab];
        }
        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '&')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '&') { sb.Append('&'); i++; }
                continue;
            }
            sb.Append(raw[i]);
        }
        // "파일(F)" 처럼 니모닉만 괄호에 남은 한국어 메뉴는 괄호 부분 제거
        string text = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s*\([A-Za-z0-9]\)", "").Trim();
        return (text, shortcut);
    }

    private static void TrimSeparators(List<AppMenuItem> list)
    {
        while (list.Count > 0 && list[^1].IsSeparator) list.RemoveAt(list.Count - 1);
        while (list.Count > 0 && list[0].IsSeparator) list.RemoveAt(0);
    }

    private static readonly AppMenuItem Separator = new("-", true, false, false, null, null, null);

    // ───────────────────────── 정의 → AppMenu ─────────────────────────

    internal static IReadOnlyList<AppMenu> Convert(List<AppMenuDef> defs) =>
        defs.Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Title))
            .Select(d => new AppMenu(d.Title, ConvertItems(d.Items)))
            .ToList();

    private static IReadOnlyList<AppMenuItem> ConvertItems(List<AppMenuItemDef>? items)
    {
        var list = new List<AppMenuItem>();
        if (items is null) return list;
        foreach (var it in items)
        {
            if (it is null) continue;
            if (it.Text == "-") { list.Add(Separator); continue; }
            var children = it.Items is { Count: > 0 } ? ConvertItems(it.Items) : null;
            if (children is null && !string.IsNullOrWhiteSpace(it.Action))
            {
                bool known = MenuRules.AllowedActions.TryGetValue(it.Action.Trim(), out int sc);
                list.Add(new AppMenuItem(it.Text, false, known, false, null, null, known ? new WindowAction(it.Action.Trim(), sc) : null));
                continue;
            }
            string? keys = string.IsNullOrWhiteSpace(it.Keys) ? null : it.Keys.Trim();
            bool enabled = children is not null || (keys is not null && KeyParser.TryParseSequence(keys, out _));
            list.Add(new AppMenuItem(it.Text, false, enabled, false, keys, children, children is null ? keys : null));
        }
        return list;
    }

    // ───────────────────────── 내장 기본 메뉴 ─────────────────────────

    /// <summary>key: <see cref="MenuRule"/>(앱 전용 규칙) 또는 "electron"/"generic"(코드의 범용 메뉴).</summary>
    private IReadOnlyList<AppMenu> GetBuiltin(object key)
    {
        lock (_builtinCache)
        {
            if (_builtinCache.TryGetValue(key, out var cached)) return cached;
            var menus = Convert(key is MenuRule rule ? rule.Menus : BuiltinMenus.For((string)key));
            _builtinCache[key] = menus;
            return menus;
        }
    }

    /// <summary>Electron 앱인지 (exe 폴더에 resources\app.asar 또는 resources\app 폴더). 결과 캐시.</summary>
    private bool IsElectron(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        lock (_electronCache)
        {
            if (_electronCache.TryGetValue(exePath, out bool v)) return v;
            bool r = false;
            try
            {
                string? dir = Path.GetDirectoryName(exePath);
                if (dir is not null)
                    r = File.Exists(Path.Combine(dir, "resources", "app.asar")) || Directory.Exists(Path.Combine(dir, "resources", "app"));
            }
            catch { }
            _electronCache[exePath] = r;
            return r;
        }
    }

    // ───────────────────────── 실행 ─────────────────────────

    public void Invoke(IntPtr hwnd, AppMenuItem item)
    {
        try
        {
            if (item is null || hwnd == IntPtr.Zero) return;
            switch (item.Payload)
            {
                case Win32Command cmd:
                    if (!User32.PostMessage(hwnd, MenuApi.WM_COMMAND, new IntPtr(cmd.Id), IntPtr.Zero))
                        Log.Warn($"WM_COMMAND 전송 실패 id={cmd.Id} err={Marshal.GetLastWin32Error()}");
                    break;
                case WindowAction act:
                    if (!User32.PostMessage(hwnd, MenuApi.WM_SYSCOMMAND, new IntPtr(act.SysCommand), IntPtr.Zero))
                        Log.Warn($"창 동작 '{act.Name}' 전송 실패 err={Marshal.GetLastWin32Error()}");
                    break;
                case string keys:
                    if (!KeyParser.TryParseSequence(keys, out var chords))
                    {
                        Log.Warn($"단축키 해석 실패: '{keys}'");
                        return;
                    }
                    _ = SendKeysToWindowAsync(hwnd, keys, chords);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"앱 메뉴 실행 실패: {item?.Text}", ex);
        }
    }

    /// <summary>
    /// 창을 포그라운드로 확인/전환 → 수식키가 모두 떼어질 때까지 잠깐 기다림 → 단축키 SendInput.
    /// "Ctrl+K Ctrl+S" 처럼 여러 입력이면 차례로(사이 40ms), 매번 대상 창이 그대로인지 확인.
    /// </summary>
    private static async Task SendKeysToWindowAsync(IntPtr hwnd, string text, IReadOnlyList<ushort[]> chords)
    {
        try
        {
            if (User32.GetForegroundWindow() != hwnd)
            {
                AppLauncher.ActivateWindow(hwnd);
                await Task.Delay(120);
                if (User32.GetForegroundWindow() != hwnd)
                {
                    Log.Warn($"단축키 대상 창을 앞으로 가져오지 못함 → '{text}' 취소");
                    return;
                }
            }
            for (int i = 0; i < 10 && AnyModifierDown(); i++) await Task.Delay(30);
            if (AnyModifierDown())
            {
                Log.Warn($"수식키가 눌려 있어 '{text}' 취소");
                return;
            }
            for (int c = 0; c < chords.Count; c++)
            {
                if (c > 0) await Task.Delay(40);
                // 기다리는 사이 사용자가 다른 창으로 옮겼을 수 있음 → 보내기 직전 다시 확인
                if (!IsTargetFocused(hwnd))
                {
                    Log.Warn($"단축키 보내기 직전 포그라운드가 바뀜 → '{text}' 취소");
                    return;
                }
                KeyChord.Send(text, chords[c]);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"단축키 전송 실패: {text}", ex);
        }
    }

    /// <summary>hwnd 가 포그라운드이고, 그 창 스레드의 활성 창도 hwnd 인지 (키 입력이 그 창으로 가는지).</summary>
    private static bool IsTargetFocused(IntPtr hwnd)
    {
        if (User32.GetForegroundWindow() != hwnd) return false;
        uint tid = User32.GetWindowThreadProcessId(hwnd, out _);
        var gti = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (tid == 0 || !User32.GetGUIThreadInfo(tid, ref gti)) return true; // 확인 불가 → 포그라운드 확인만으로 판단
        return gti.hwndActive == IntPtr.Zero || gti.hwndActive == hwnd;
    }

    private static bool AnyModifierDown() =>
        MenuApi.IsKeyDown(0x10) || MenuApi.IsKeyDown(0x11) || MenuApi.IsKeyDown(0x12) ||
        MenuApi.IsKeyDown(0x5B) || MenuApi.IsKeyDown(0x5C);
}
