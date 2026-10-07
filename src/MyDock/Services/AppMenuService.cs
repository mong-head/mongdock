using System.IO;
using System.Runtime.InteropServices;
using MyDock.Models;
using MyDock.Native;

namespace MyDock.Services;

/// <summary>
/// 맥처럼 상단바에 포그라운드 앱의 메뉴를 보여 주기 위한 메뉴 정의.
/// 우선순위: 1) 창의 Win32 메뉴 막대(HMENU, 읽기만) → 2) settings.AppMenus[AUMID / exe 파일명 / exe 전체 경로] → 3) 내장 기본 메뉴.
/// Payload: Win32 메뉴 항목은 <see cref="Win32Command"/>(명령 ID), 단축키 항목은 단축키 문자열(예: "Ctrl+Shift+T").
/// </summary>
public sealed class AppMenuService : IAppMenuService
{
    /// <summary>Win32 메뉴 항목의 명령 ID (Invoke 시 WM_COMMAND).</summary>
    public sealed record Win32Command(uint Id);

    private readonly ISettingsService _settings;
    private readonly Dictionary<string, IReadOnlyList<AppMenu>> _builtinCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _electronCache = new(StringComparer.OrdinalIgnoreCase);

    public AppMenuService(ISettingsService settings)
    {
        _settings = settings;
    }

    // ───────────────────────── 메뉴 얻기 ─────────────────────────

    public IReadOnlyList<AppMenu> GetMenus(AppWindowInfo window)
    {
        if (window is null) return Array.Empty<AppMenu>();
        try
        {
            var win32 = ReadWin32Menu(window.Hwnd);
            if (win32.Count > 0) return win32;

            var user = FindUserMenus(window);
            if (user is not null) return Convert(user);

            return GetBuiltin(window);
        }
        catch (Exception ex)
        {
            Log.Error("앱 메뉴 조회 실패", ex);
            return Array.Empty<AppMenu>();
        }
    }

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

    /// <summary>창의 메뉴 막대를 재귀로 읽음. WM_INITMENUPOPUP 은 보내지 않으므로 상태는 앱이 마지막으로 정한 그대로.</summary>
    internal static IReadOnlyList<AppMenu> ReadWin32Menu(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Array.Empty<AppMenu>();
        IntPtr bar = MenuApi.GetMenu(hwnd);
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
            string? keys = string.IsNullOrWhiteSpace(it.Keys) ? null : it.Keys.Trim();
            bool enabled = children is not null || (keys is not null && KeyParser.TryParse(keys, out _));
            list.Add(new AppMenuItem(it.Text, false, enabled, false, keys, children, children is null ? keys : null));
        }
        return list;
    }

    // ───────────────────────── 내장 기본 메뉴 ─────────────────────────

    private IReadOnlyList<AppMenu> GetBuiltin(AppWindowInfo w)
    {
        string exe = Path.GetFileName(w.ProcessPath).ToLowerInvariant();
        string cls = User32.GetClassNameOf(w.Hwnd);
        // 콘솔/터미널: Ctrl+C 등이 다른 의미라 메뉴를 보여 주지 않음
        if (cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "VirtualConsoleClass" or "PuTTY"
            || exe is "windowsterminal.exe" or "wt.exe" or "conhost.exe" or "openconsole.exe" or "mintty.exe" or "alacritty.exe" or "wezterm-gui.exe")
            return Array.Empty<AppMenu>();
        string kind = exe switch
        {
            "chrome.exe" or "msedge.exe" or "whale.exe" => exe,
            "firefox.exe" => "firefox.exe",
            "explorer.exe" when cls == "CabinetWClass" => "explorer",
            "notion.exe" => "notion",
            _ => IsElectron(w.ProcessPath) ? "electron" : "generic",
        };
        lock (_builtinCache)
        {
            if (_builtinCache.TryGetValue(kind, out var cached)) return cached;
            var menus = Convert(BuiltinMenus.For(kind));
            _builtinCache[kind] = menus;
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
                case string keys:
                    if (!KeyParser.TryParse(keys, out var vks))
                    {
                        Log.Warn($"단축키 해석 실패: '{keys}'");
                        return;
                    }
                    _ = SendKeysToWindowAsync(hwnd, keys, vks);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"앱 메뉴 실행 실패: {item?.Text}", ex);
        }
    }

    /// <summary>창을 포그라운드로 확인/전환 → 수식키가 모두 떼어질 때까지 잠깐 기다림 → 단축키 SendInput.</summary>
    private static async Task SendKeysToWindowAsync(IntPtr hwnd, string text, ushort[] vks)
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
            // 기다리는 사이 사용자가 다른 창으로 옮겼을 수 있음 → 보내기 직전 다시 확인
            if (!IsTargetFocused(hwnd))
            {
                Log.Warn($"단축키 보내기 직전 포그라운드가 바뀜 → '{text}' 취소");
                return;
            }
            KeyChord.Send(text, vks);
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

/// <summary>"Ctrl+Shift+T" 같은 단축키 문자열 → 가상 키 배열 (수식키 먼저, 마지막이 주 키).</summary>
internal static class KeyParser
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0xA2, ["Control"] = 0xA2, ["Shift"] = 0xA0, ["Alt"] = 0xA4, ["Win"] = 0x5B, ["Windows"] = 0x5B,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Space"] = 0x20, ["Backspace"] = 0x08, ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Insert"] = 0x2D, ["Ins"] = 0x2D,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PgUp"] = 0x21, ["PageDown"] = 0x22, ["PgDn"] = 0x22,
        ["="] = 0xBB, ["Plus"] = 0xBB, ["-"] = 0xBD, ["Minus"] = 0xBD, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF,
        [";"] = 0xBA, ["`"] = 0xC0, ["["] = 0xDB, ["\\"] = 0xDC, ["]"] = 0xDD, ["'"] = 0xDE,
    };

    private static readonly HashSet<ushort> Modifiers = new() { 0xA2, 0xA0, 0xA4, 0x5B };

    public static bool TryParse(string text, out ushort[] keys)
    {
        keys = Array.Empty<ushort>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        var parts = new List<string>();
        // "Ctrl++" 처럼 '+' 키 자체는 마지막 토큰
        if (s.EndsWith("++")) { parts.AddRange(s[..^2].Split('+', StringSplitOptions.RemoveEmptyEntries)); parts.Add("="); }
        else parts.AddRange(s.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
        if (parts.Count == 0) return false;

        var result = new List<ushort>();
        foreach (string p in parts)
        {
            if (!TryKey(p, out ushort vk)) return false;
            result.Add(vk);
        }
        // 주 키는 정확히 하나 (마지막), 나머지는 수식키
        if (result.Count(k => !Modifiers.Contains(k)) > 1) return false;
        var ordered = result.Where(Modifiers.Contains).Concat(result.Where(k => !Modifiers.Contains(k))).ToArray();
        if (ordered.Length == 0) return false;
        keys = ordered;
        return true;
    }

    private static bool TryKey(string p, out ushort vk)
    {
        vk = 0;
        if (Named.TryGetValue(p, out vk)) return true;
        if (p.Length == 1)
        {
            char c = char.ToUpperInvariant(p[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
            return false;
        }
        if ((p[0] == 'F' || p[0] == 'f') && int.TryParse(p[1..], out int n) && n is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + n - 1);
            return true;
        }
        return false;
    }
}
