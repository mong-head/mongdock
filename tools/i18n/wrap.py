"""
화면 문구(한글이 든 C# 문자열 리터럴)를 Loc.T("...") / Loc.F($"...") 로 감싸는 도구 (#8, 한 번 쓰고 사람이 검토).
    python tools/i18n/wrap.py src/mongdock/Views/Foo.cs [...]
건너뜀: 주석, Log.* 가 든 줄, const 선언, case 레이블, 특성([..]), 이미 Loc.T/Loc.F 안, nameof.
보간 문자열은 구멍({..}) 안의 문자열도 재귀로 처리하고, 바깥 글자에 한글이 있으면 Loc.F($"...") 로 감싼다.
"""
import re
import sys

HANGUL = re.compile('[가-힣]')


def has_hangul(s):
    return bool(HANGUL.search(s))


def parse_string(code, i):
    """code[i] 에서 시작하는 문자열 리터럴 → (끝 인덱스, 원문, 접두사, 바깥 글자, 구멍 목록[(start,end)]) 또는 None."""
    j = i
    prefix = ''
    while j < len(code) and code[j] in '$@':
        prefix += code[j]
        j += 1
    if j >= len(code) or code[j] != '"':
        return None
    if code.startswith('"""', j):
        return None  # 원시 문자열 리터럴은 다루지 않음
    interp = '$' in prefix
    verbatim = '@' in prefix
    j += 1
    outer = []
    holes = []
    while j < len(code):
        c = code[j]
        if verbatim and c == '"' and j + 1 < len(code) and code[j + 1] == '"':
            outer.append('""')
            j += 2
            continue
        if not verbatim and c == '\\':
            outer.append(code[j:j + 2])
            j += 2
            continue
        if c == '"':
            return j + 1, code[i:j + 1], prefix, ''.join(outer), holes
        if interp and c == '{':
            if j + 1 < len(code) and code[j + 1] == '{':
                outer.append('{{')
                j += 2
                continue
            start = j + 1
            depth = 1
            k = start
            while k < len(code) and depth > 0:
                ch = code[k]
                if ch in '$@"':
                    r = parse_string(code, k)
                    if r:
                        k = r[0]
                        continue
                if ch == "'":
                    m = re.match(r"'(\\.|[^\\'])'", code[k:])
                    if m:
                        k += m.end()
                        continue
                if ch in '({[':
                    depth += 1
                elif ch in ')}]':
                    depth -= 1
                k += 1
            holes.append((start, k - 1))
            outer.append('{}')
            j = k
            continue
        if interp and c == '}' and j + 1 < len(code) and code[j + 1] == '}':
            outer.append('}}')
            j += 2
            continue
        outer.append(c)
        j += 1
    return None  # 줄 안에서 안 끝남 (여러 줄 문자열) → 손대지 않음


def process(code):
    out = []
    i = 0
    n = len(code)
    while i < n:
        c = code[i]
        if code.startswith('//', i):
            out.append(code[i:])
            break
        if c == "'":
            m = re.match(r"'(\\.|[^\\'])'", code[i:])
            if m:
                out.append(m.group(0))
                i += m.end()
                continue
        if c in '$@"':
            r = parse_string(code, i)
            if r:
                end, raw, prefix, outer, holes = r
                if holes:
                    # 구멍 안을 재귀 처리해서 다시 조립
                    rebuilt = []
                    last = i
                    for (hs, he) in holes:
                        rebuilt.append(code[last:hs])
                        rebuilt.append(process(code[hs:he]))
                        last = he
                    rebuilt.append(code[last:end])
                    raw = ''.join(rebuilt)
                before = ''.join(out)
                already = re.search(r'Loc\.[TF]\(\s*$', before) or re.search(r'nameof\(\s*$', before)
                if has_hangul(outer) and not already:
                    out.append(('Loc.F(' if '$' in prefix else 'Loc.T(') + raw + ')')
                else:
                    out.append(raw)
                i = end
                continue
        out.append(c)
        i += 1
    return ''.join(out)


SKIP_LINE = re.compile(r'^\s*(//|\[|case\s)|\bLog\.(Info|Warn|Error)\b|\bconst\s+string\b|Debug\.')


def wrap_file(path):
    raw = open(path, 'rb').read()
    bom = raw[:3] == b'\xef\xbb\xbf'
    text = raw.decode('utf-8-sig')
    crlf = '\r\n' in text
    lines = text.replace('\r\n', '\n').split('\n')
    changed = 0
    for idx, line in enumerate(lines):
        if not has_hangul(line) or SKIP_LINE.search(line):
            continue
        new = process(line)
        if new != line:
            lines[idx] = new
            changed += 1
    if changed:
        text = '\n'.join(lines)
        if crlf:
            text = text.replace('\n', '\r\n')
        open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + text.encode('utf-8'))
    return changed


if __name__ == '__main__':
    total = 0
    for p in sys.argv[1:]:
        n = wrap_file(p)
        total += n
        if n:
            print(f'{n:4} {p}')
    print('lines changed:', total)
