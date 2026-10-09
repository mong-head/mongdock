"""
wrap.py 가 잘못 감싼 것(로그·비교용 키)을 되돌림: 지정한 한국어 조각이 든 Loc.T("...") / Loc.F($"...") 를 원래 리터럴로.
    python tools/i18n/unwrap.py <파일> <조각1> [<조각2> ...]
"""
import re
import sys


def unwrap(path, needles):
    raw = open(path, 'rb').read()
    bom = raw[:3] == b'\xef\xbb\xbf'
    s = raw.decode('utf-8-sig')
    count = 0
    for needle in needles:
        # Loc.T("...needle...") 또는 Loc.F($"...needle...") — 리터럴 안에 괄호·따옴표 짝이 단순한 경우
        pat = re.compile(r'Loc\.[TF]\((\$?@?"(?:[^"\\]|\\.)*' + re.escape(needle) + r'(?:[^"\\]|\\.)*")\)')
        s, n = pat.subn(lambda m: m.group(1), s)
        count += n
    open(path, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
    return count


if __name__ == '__main__':
    print(unwrap(sys.argv[1], sys.argv[2:]))
