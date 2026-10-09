"""
코드의 Loc.T("...") / Loc.F($"...") 에서 번역 키(런타임 문자열 그대로, 보간은 "{0}" 서식)를 뽑아
src/mongdock/i18n/en.json 과 비교한다.
    python tools/i18n/extract.py            → 빠진 키·안 쓰는 키 개수
    python tools/i18n/extract.py --missing  → 빠진 키를 JSON 으로 출력 (번역할 목록)
    python tools/i18n/extract.py --check    → 빠진 키가 있으면 종료 코드 1 (빌드 전 확인용)
"""
import glob
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(__file__))
from wrap import parse_string  # noqa: E402

ROOT = os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'mongdock')
EN = os.path.join(ROOT, 'i18n', 'en.json')


def unescape(body, verbatim):
    if verbatim:
        return body.replace('""', '"')
    out = []
    i = 0
    while i < len(body):
        c = body[i]
        if c == '\\' and i + 1 < len(body):
            n = body[i + 1]
            if n == 'n':
                out.append('\n')
            elif n == 't':
                out.append('\t')
            elif n == 'r':
                out.append('\r')
            elif n == '0':
                out.append('\0')
            elif n == 'u':
                out.append(chr(int(body[i + 2:i + 6], 16)))
                i += 6
                continue
            else:
                out.append(n)
            i += 2
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def split_hole(hole):
    """구멍 내용 → (식, 정렬·서식 꼬리). 괄호·문자열 밖의 첫 ',' 또는 ':' 부터가 꼬리."""
    depth = 0
    i = 0
    while i < len(hole):
        c = hole[i]
        if c in '$@"':
            r = parse_string(hole, i)
            if r:
                i = r[0]
                continue
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        elif depth == 0 and c in ',:':
            if c == ':' and i + 1 < len(hole) and hole[i + 1] == ':':  # global::
                i += 2
                continue
            return hole[:i], hole[i:]
        elif depth == 0 and c == '?' and ':' in hole[i:]:
            # 괄호 없는 삼항은 C# 에서 허용되지 않으므로 여기 오면 서식이 아님 — 그대로 진행
            pass
        i += 1
    return hole, ''


def format_key(code, i):
    """code[i] 의 리터럴 → 런타임 키."""
    r = parse_string(code, i)
    if not r:
        return None, i + 1
    end, raw, prefix, outer, holes = r
    verbatim = '@' in prefix
    if '$' not in prefix:
        body = raw[len(prefix) + 1:-1]
        return unescape(body, verbatim), end
    # 보간: 바깥 글자 + 구멍 → {n[,align][:fmt]}
    parts = []
    start = i + len(prefix) + 1
    pos = start
    idx = 0
    for hs, he in holes:
        seg = code[pos:hs - 1]
        parts.append(unescape(seg, verbatim).replace('{{', '{{').replace('}}', '}}'))
        _, tail = split_hole(code[hs:he])
        parts.append('{' + str(idx) + tail + '}')
        idx += 1
        pos = he + 1
    parts.append(unescape(code[pos:end - 1], verbatim))
    return ''.join(parts), end


def collect():
    keys = []
    seen = set()
    for f in sorted(glob.glob(os.path.join(ROOT, '**', '*.cs'), recursive=True)):
        text = open(f, encoding='utf-8-sig').read()
        for m in re.finditer(r'Loc\.[TF]\(\s*', text):
            key, _ = format_key(text, m.end())
            if key is not None and key not in seen:
                seen.add(key)
                keys.append(key)
    return keys


PLACEHOLDER = re.compile(r'\{([^{}:,]+)[^{}]*\}')  # 이름만 비교 ({0:M월 d일} 과 {0:MMM d} 는 같음 — 날짜 서식은 언어마다 다름)


def main():
    """
    영어(en.json)와 다른 언어(i18n/*.json)를 코드의 키와 비교.
      --missing [코드]  : 그 언어(기본 en)의 빠진 키를 JSON 으로 (번역 채울 때)
      --check           : 언어마다 빠진 것·자리표시자({0}·{hotkey}) 어긋남이 있으면 종료 코드 1
    en.json 에만 있는 동적 키(공휴일·색 이름처럼 Loc.T(변수))는 다른 언어에도 있어야 함 (en 의 키 전체가 기준).
    """
    keys = collect()
    en = json.load(open(EN, encoding='utf-8')) if os.path.exists(EN) else {}
    if '--missing' in sys.argv:
        i = sys.argv.index('--missing')
        code = sys.argv[i + 1] if i + 1 < len(sys.argv) and not sys.argv[i + 1].startswith('--') else 'en'
        table = en if code == 'en' else json.load(open(os.path.join(ROOT, 'i18n', code + '.json'), encoding='utf-8'))
        want = keys if code == 'en' else list(dict.fromkeys(keys + list(en)))
        print(json.dumps({k: '' for k in want if not table.get(k)}, ensure_ascii=False, indent=1))
        return 0
    bad = False
    missing = [k for k in keys if not en.get(k)]
    unused = [k for k in en if k not in set(keys)]
    print(f'en: keys {len(keys)}, translated {len(keys) - len(missing)}, missing {len(missing)}, unused {len(unused)}')
    if missing:
        bad = True
        for k in missing[:30]:
            print('  missing:', k.replace('\n', '\\n'))
    want = list(dict.fromkeys(keys + list(en)))
    for f in sorted(glob.glob(os.path.join(ROOT, 'i18n', '*.json'))):
        code = os.path.splitext(os.path.basename(f))[0]
        if code == 'en':
            continue
        table = json.load(open(f, encoding='utf-8'))
        miss = [k for k in want if not table.get(k)]
        ph = [k for k in want if table.get(k) and sorted(PLACEHOLDER.findall(k)) != sorted(PLACEHOLDER.findall(table[k]))]
        extra = [k for k in table if k not in set(want)]
        print(f'{code}: translated {len(want) - len(miss)}/{len(want)}, missing {len(miss)}, placeholder mismatch {len(ph)}, extra {len(extra)}')
        for k in (miss + ph)[:10]:
            print('  ' + ('missing' if k in miss else 'placeholder') + ':', k.replace('\n', '\\n')[:80])
        if miss or ph:
            bad = True
    return 1 if '--check' in sys.argv and bad else 0


if __name__ == '__main__':
    sys.exit(main())
