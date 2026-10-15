using Mongdock.Models;

namespace Mongdock.Services;

/// <summary>
/// 단축키로 받을 수 있는 조합인지 (설정 → 단축키에서 키를 받을 때). spec-hotkeys.md 규칙 + PM 결정(2026-10-15):
/// - Ctrl·Alt·Win 중 하나 이상이 있어야 함 (글자만·Shift+글자는 타이핑을 막음). F13~F24 만 혼자 됨, F1~F12 는 수정자 필요.
/// - 윈도우가 쓰는 키는 막음. 다른 앱이 흔히 쓰는 키(Ctrl+C·V·S…)도 막음. Alt+글자 하나는 앱 메뉴 단축키와 겹칠 수 있어 경고만.
/// - Win+Space·Alt+Space·Ctrl+Space 는 검색 칸에서만 (예전부터 고를 수 있던 키 — 다른 칸에선 언어 전환·창 메뉴와 겹침).
/// 몽독 안 겹침(같은 키를 다른 칸이 씀)은 화면이 따로 확인.
/// </summary>
public static class HotkeyRules
{
    public enum Verdict { Ok, Warn, Block }

    /// <summary>판정 + 이유(한국어 원문 — 화면에서 Loc.T). Ok 면 이유 null.</summary>
    public readonly record struct Result(Verdict Verdict, string? Reason);

    private const int VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_HOME = 0x24, VK_PRINT = 0x2C,
        VK_F4 = 0x73, VK_OEM_COMMA = 0xBC, VK_OEM_PERIOD = 0xBE, VK_OEM_1 = 0xBA, VK_OEM_PLUS = 0xBB, VK_OEM_MINUS = 0xBD,
        VK_ADD = 0x6B, VK_SUBTRACT = 0x6D;

    private static bool IsArrow(int vk) => vk is VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN;
    private static bool IsLetter(int vk) => vk is >= 'A' and <= 'Z';
    private static bool IsDigit(int vk) => vk is >= '0' and <= '9';

    public static Result Check(HotkeyChord c, bool searchSlot)
    {
        if (!HotkeyChord.IsNamedKey(c.Vk)) return new(Verdict.Block, "이 키는 단축키로 쓸 수 없어요.");
        if (c.Vk is HotkeyChord.VK_ESCAPE or HotkeyChord.VK_BACK or HotkeyChord.VK_DELETE && !c.HasModifier)
            return new(Verdict.Block, "이 키는 단축키로 쓸 수 없어요.");

        // 수정자 필요 (F13~F24 만 예외)
        if (!c.HasModifier)
        {
            if (c.Vk is >= HotkeyChord.VK_F13 and <= HotkeyChord.VK_F24) return new(Verdict.Ok, null);
            return new(Verdict.Block, c.IsFunctionKey
                ? "F1~F12 는 다른 앱이 많이 써요. Ctrl·Alt·Win 과 함께 눌러 주세요."
                : "Ctrl·Alt·Win 중 하나와 함께 눌러 주세요. (글자만 누르면 타이핑이 막혀요)");
        }

        // 검색 칸에서만 되는 Space 조합 (예전 검색 단축키 그대로)
        if (c.Vk == HotkeyChord.VK_SPACE && c.ModifierCount == 1)
            return searchSlot ? new(Verdict.Ok, null) : new(Verdict.Block, "윈도우가 쓰는 키예요 (입력 언어 전환·창 메뉴). 검색에만 쓸 수 있어요.");

        if (WindowsUses(c)) return new(Verdict.Block, "윈도우가 쓰는 키예요.");
        if (AppsUse(c)) return new(Verdict.Block, "다른 앱이 흔히 쓰는 키예요 (복사·붙여넣기·저장 같은).");

        // Alt + 글자·숫자 하나: 앱 메뉴(파일·편집…) 단축키와 겹칠 수 있음 — 막지 않고 경고 (PM 결정)
        if (c.Alt && !c.Ctrl && !c.Win && !c.Shift && (IsLetter(c.Vk) || IsDigit(c.Vk)))
            return new(Verdict.Warn, "앱 메뉴 단축키(Alt+글자)와 겹칠 수 있어요.");
        return new(Verdict.Ok, null);
    }

    /// <summary>윈도우·탐색기가 쓰는 조합.</summary>
    private static bool WindowsUses(HotkeyChord c)
    {
        int vk = c.Vk;
        // Win + 키 하나 (다른 수정자 없이): 거의 다 윈도우 몫 (글자·숫자·Tab·방향키·기호·Home·PrtSc·Esc·Enter·Space)
        if (c.Win && !c.Ctrl && !c.Alt && !c.Shift) return true;
        if (c.Win && c.Shift && !c.Ctrl && !c.Alt && (vk is 'S' or 'M' or 'C' or 'V' or 'R' || IsArrow(vk) || IsDigit(vk) || vk == HotkeyChord.VK_RETURN)) return true;
        if (c.Win && c.Ctrl && !c.Alt && !c.Shift && (vk is 'D' or 'F' or 'C' or 'O' or 'Q' or 'N' or 'S' or 'T' or VK_F4 or HotkeyChord.VK_RETURN || IsArrow(vk) || IsDigit(vk))) return true;
        if (c.Win && c.Ctrl && c.Shift && !c.Alt && vk is 'B') return true;
        if (c.Win && c.Alt && !c.Ctrl && !c.Shift && (vk is 'B' or 'D' or 'G' or 'K' or 'R' or 'T' or VK_PRINT or HotkeyChord.VK_RETURN || IsArrow(vk))) return true;
        if (c.Alt && !c.Ctrl && !c.Win && vk is HotkeyChord.VK_TAB or HotkeyChord.VK_ESCAPE or VK_F4 or VK_PRINT) return true; // Shift 섞여도
        if (c.Ctrl && !c.Alt && !c.Win && vk == HotkeyChord.VK_ESCAPE) return true; // Ctrl+Esc, Ctrl+Shift+Esc
        if (c.Ctrl && c.Alt && !c.Win && vk is HotkeyChord.VK_DELETE or HotkeyChord.VK_TAB) return true;
        if (c.Win && (vk is VK_OEM_PLUS or VK_OEM_MINUS or VK_ADD or VK_SUBTRACT)) return true; // 돋보기
        if (vk == VK_HOME && c.Win) return true;
        if (vk is VK_OEM_COMMA or VK_OEM_PERIOD or VK_OEM_1 && c.Win) return true; // 바탕 화면 보기·이모지
        return false;
    }

    /// <summary>앱들이 흔히 쓰는 조합 (막음).</summary>
    private static bool AppsUse(HotkeyChord c)
    {
        int vk = c.Vk;
        if (c.Ctrl && !c.Alt && !c.Win && !c.Shift)
        {
            if (IsLetter(vk) && "ABCDFHIKLNOPQRSTUVWXYZ".Contains((char)vk)) return true; // 모두 선택·복사·찾기·새로·열기·인쇄·저장·닫기…
            if (vk is HotkeyChord.VK_TAB or HotkeyChord.VK_RETURN || IsArrow(vk) || IsDigit(vk) && vk != '0') return true; // 탭 넘기기·단어 이동·탭 n
        }
        if (c.Ctrl && c.Shift && !c.Alt && !c.Win && (vk is 'T' or 'N' or 'Z' or 'V' or 'C' or 'S' or 'P' or 'W' or HotkeyChord.VK_TAB || IsArrow(vk))) return true;
        if (c.Alt && !c.Ctrl && !c.Win && (IsArrow(vk) || vk == HotkeyChord.VK_RETURN)) return true; // 뒤로·앞으로, 속성·전체 화면
        if (c.Shift && !c.Ctrl && !c.Alt && !c.Win) return true; // Shift+키 = 대문자·선택 (수정자 확인에서 이미 걸러지지만 한 번 더)
        return false;
    }
}
