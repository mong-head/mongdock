"""
Loc.T/Loc.F 로 감싸지 않은 한글 문자열 리터럴을 찾아 보여 줌 (주석·로그 줄 제외). 남겨도 되는 것(로그·진단·비교 키)은 사람이 판단.
    python tools/i18n/leftover.py [폴더=src/mongdock]
"""
import glob
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')  # 한국어(cp949) 콘솔에서도 끝까지
sys.path.insert(0, os.path.dirname(__file__))
from wrap import parse_string, has_hangul, SKIP_LINE  # noqa: E402

root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), '..', '..', 'src', 'mongdock')
count = 0
for f in sorted(glob.glob(os.path.join(root, '**', '*.cs'), recursive=True)):
    for no, line in enumerate(open(f, encoding='utf-8-sig'), 1):
        if not has_hangul(line) or SKIP_LINE.search(line):
            continue
        i = 0
        while i < len(line):
            if line.startswith('//', i):
                break
            if line[i] in '$@"':
                r = parse_string(line, i)
                if r:
                    end, raw, prefix, outer, holes = r
                    before = line[:i]
                    if has_hangul(outer) and not re.search(r'Loc\.[TF]\(\s*$', before):
                        count += 1
                        print(f'{os.path.relpath(f, root)}:{no}: {raw[:90]}')
                    i = end
                    continue
            i += 1
print('unwrapped:', count)
