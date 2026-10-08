namespace MyDock.Services;

/// <summary>
/// "Ctrl+Shift+T" 같은 단축키 문자열 → 가상 키 배열 (수식키 먼저, 마지막이 주 키).
/// 공백으로 나눈 "Ctrl+K Ctrl+S" / "Ctrl+K S" 는 차례로 누르는 연속 입력(<see cref="TryParseSequence"/>).
/// </summary>
internal static class KeyParser
{
    /// <summary>공백으로 나눈 입력 여러 개 (최대 4개). 하나라도 해석 못 하면 false.</summary>
    public static bool TryParseSequence(string text, out IReadOnlyList<ushort[]> chords)
    {
        chords = Array.Empty<ushort[]>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 4) return false;
        var list = new List<ushort[]>(parts.Length);
        foreach (var p in parts)
        {
            if (!TryParse(p, out var keys)) return false;
            list.Add(keys);
        }
        chords = list;
        return true;
    }

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

    /// <summary>"NumPad0"~"NumPad9" / "Num0"~"Num9" → VK_NUMPAD0(0x60)~.</summary>
    private static bool TryNumPad(string p, out ushort vk)
    {
        vk = 0;
        string digits = p.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) ? p[6..]
            : p.StartsWith("Num", StringComparison.OrdinalIgnoreCase) ? p[3..] : "";
        if (digits.Length != 1 || digits[0] is < '0' or > '9') return false;
        vk = (ushort)(0x60 + (digits[0] - '0'));
        return true;
    }

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
        if (TryNumPad(p, out vk)) return true;
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
