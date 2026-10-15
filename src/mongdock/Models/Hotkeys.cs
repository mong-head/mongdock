using System.Text.Json.Serialization;

namespace Mongdock.Models;

// ───────────────────────── 단축키 직접 정하기 (0.6, spec-hotkeys.md) ─────────────────────────

/// <summary>
/// 몽독 단축키 (설정 → 단축키). 기능마다 [켜기] + 키 하나. 검색만 기본 켬(지금 키 "auto"), 나머지는 꺼짐·키 없음 —
/// 업데이트한 사람의 다른 앱 단축키를 몽독이 갑자기 가로채지 않게. 루틴 키는 루틴(<see cref="RoutineDef.Hotkey"/>)에 붙어 다님.
/// 옛 설정(TopBar.SpotlightHotkey)은 처음 읽을 때 Search 로 옮기고, 0.5.x 로 되돌려도 검색 키가 남게 계속 같이 씀.
/// </summary>
public sealed class HotkeySettings
{
    public HotkeyBinding Search { get; set; } = new() { On = true, Key = HotkeyBinding.Auto };
    public HotkeyBinding AllApps { get; set; } = new();
    public HotkeyBinding ControlCenter { get; set; } = new();
    /// <summary>알림·달력 판 (상단바 시계를 누른 것과 같음).</summary>
    public HotkeyBinding Notifications { get; set; } = new();

    /// <summary>예전 검색 단축키 → 검색 칸.</summary>
    public static HotkeyBinding FromLegacy(SpotlightHotkey old) => old switch
    {
        SpotlightHotkey.None => new() { On = false, Key = HotkeyBinding.Auto },
        SpotlightHotkey.WinSpace => new() { On = true, Key = "Win+Space" },
        SpotlightHotkey.AltSpace => new() { On = true, Key = "Alt+Space" },
        SpotlightHotkey.CtrlSpace => new() { On = true, Key = "Ctrl+Space" },
        _ => new() { On = true, Key = HotkeyBinding.Auto },
    };

    /// <summary>검색 칸 → 예전 값 (0.5.x 로 되돌려도 검색 키가 남게 같이 써 둠). 직접 정한 다른 키는 자동으로.</summary>
    public static SpotlightHotkey ToLegacy(HotkeyBinding b)
    {
        if (!b.On) return SpotlightHotkey.None;
        return HotkeyChord.Parse(b.Key) switch
        {
            { Win: true, Ctrl: false, Alt: false, Shift: false, Vk: HotkeyChord.VK_SPACE } => SpotlightHotkey.WinSpace,
            { Alt: true, Ctrl: false, Win: false, Shift: false, Vk: HotkeyChord.VK_SPACE } => SpotlightHotkey.AltSpace,
            { Ctrl: true, Alt: false, Win: false, Shift: false, Vk: HotkeyChord.VK_SPACE } => SpotlightHotkey.CtrlSpace,
            _ => SpotlightHotkey.Auto,
        };
    }
}

/// <summary>기능 하나의 단축키: 켜짐 + 키 글자("Ctrl+Alt+K", 검색만 "auto"). 키가 없으면 켜져 있어도 쉼.</summary>
public sealed class HotkeyBinding
{
    /// <summary>검색 기본값: 입력 언어가 하나면 Win+Space, 여러 개면 Alt+Space.</summary>
    public const string Auto = "auto";

    public bool On { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; set; }

    public HotkeyBinding Clone() => new() { On = On, Key = Key };
}

/// <summary>
/// 키 조합 하나 (수정자 + 키 하나). 저장 글자는 "Ctrl+Alt+Shift+Win+K" 순서 — 언어와 상관없이 같음.
/// 화면에 보일 때는 " + " 로 띄움 (<see cref="Display"/>).
/// </summary>
public readonly record struct HotkeyChord(bool Ctrl, bool Alt, bool Shift, bool Win, int Vk)
{
    public const int VK_SPACE = 0x20, VK_ESCAPE = 0x1B, VK_BACK = 0x08, VK_DELETE = 0x2E, VK_TAB = 0x09, VK_RETURN = 0x0D,
        VK_F1 = 0x70, VK_F12 = 0x7B, VK_F13 = 0x7C, VK_F24 = 0x87;

    public bool HasModifier => Ctrl || Alt || Win;
    public int ModifierCount => (Ctrl ? 1 : 0) + (Alt ? 1 : 0) + (Shift ? 1 : 0) + (Win ? 1 : 0);

    /// <summary>키 이름 표 (VK → 저장·표시 이름). 여기에 없는 키는 단축키로 받지 않음.</summary>
    private static readonly Dictionary<int, string> Names = BuildNames();
    private static readonly Dictionary<string, int> ByName = Names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<int, string> BuildNames()
    {
        var d = new Dictionary<int, string>();
        for (int c = 'A'; c <= 'Z'; c++) d[c] = ((char)c).ToString();
        for (int c = '0'; c <= '9'; c++) d[c] = ((char)c).ToString();
        for (int i = 0; i < 24; i++) d[VK_F1 + i] = "F" + (i + 1);
        for (int i = 0; i < 10; i++) d[0x60 + i] = "Num" + i;
        d[VK_SPACE] = "Space";
        d[VK_RETURN] = "Enter";
        d[VK_TAB] = "Tab";
        d[VK_ESCAPE] = "Esc";
        d[VK_BACK] = "Backspace";
        d[VK_DELETE] = "Delete";
        d[0x2D] = "Insert";
        d[0x24] = "Home";
        d[0x23] = "End";
        d[0x21] = "PageUp";
        d[0x22] = "PageDown";
        d[0x25] = "Left";
        d[0x26] = "Up";
        d[0x27] = "Right";
        d[0x28] = "Down";
        d[0x2C] = "PrintScreen";
        d[0x6A] = "Num*";
        d[0x6B] = "Num+";
        d[0x6D] = "Num-";
        d[0x6F] = "Num/";
        // 기호 키 (미국 배열 이름 — 한국어 배열도 같은 자리)
        d[0xBA] = ";";
        d[0xBB] = "=";
        d[0xBC] = ",";
        d[0xBD] = "-";
        d[0xBE] = ".";
        d[0xBF] = "/";
        d[0xC0] = "`";
        d[0xDB] = "[";
        d[0xDC] = "\\";
        d[0xDD] = "]";
        d[0xDE] = "'";
        return d;
    }

    public static bool IsNamedKey(int vk) => Names.ContainsKey(vk);

    /// <summary>수정자 키 자체(Shift·Ctrl·Alt·Win 왼쪽/오른쪽)인지 — 키 받을 때 이것만 누른 것은 아직 조합이 아님.</summary>
    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    public bool IsFunctionKey => Vk >= VK_F1 && Vk <= VK_F24;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Names.TryGetValue(Vk, out var n) ? n : $"0x{Vk:X2}");
        return string.Join("+", parts);
    }

    /// <summary>화면용 "Ctrl + Alt + K".</summary>
    public string Display => ToString().Replace("+", " + ").Replace("Num + ", "Num+");

    /// <summary>"Ctrl+Alt+K" → 조합. "auto"·빈 글자·모르는 키 → null. 순서·대소문자 무관.</summary>
    public static HotkeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals(HotkeyBinding.Auto, StringComparison.OrdinalIgnoreCase)) return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        int vk = 0;
        // "Num+" 처럼 '+' 로 끝나는 이름이 있어 마지막 조각은 따로
        string t = text.Trim();
        var pieces = new List<string>();
        int start = 0;
        for (int i = 0; i < t.Length; i++)
            if (t[i] == '+' && i > start) { pieces.Add(t[start..i]); start = i + 1; }
        if (start < t.Length) pieces.Add(t[start..]);
        else if (pieces.Count > 0) pieces[^1] += "+"; // "Ctrl+Num+" → 마지막 "Num+"
        foreach (var raw in pieces)
        {
            string p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win": case "windows": win = true; break;
                default:
                    if (vk != 0) return null;
                    if (ByName.TryGetValue(p, out int v)) vk = v;
                    else if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[2..], System.Globalization.NumberStyles.HexNumber, null, out int hex)) vk = hex;
                    else return null;
                    break;
            }
        }
        return vk == 0 ? null : new HotkeyChord(ctrl, alt, shift, win, vk);
    }
}
