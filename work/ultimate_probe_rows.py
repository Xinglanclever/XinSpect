# -*- coding: utf-8 -*-
"""I1d：找出真正的列首（先看位元組，不要先寫正則）。

前三版都敗在同一件事上：我從「列首長什麼樣」的假設出發寫正則，
而不是先看實際位元組。這一版先把前三種候選寫法各列幾條出來，
確定真正的形狀之後才開始數。

（這正是計畫裡「不以推測的位移硬做」那條紀律的實踐。）
"""
import glob
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'
path = glob.glob(f'{DIR}/STAGE-01-*.md')[0]
text = io.open(path, encoding='utf-8').read()
lines = text.split('\n')

cands = {
    'A 管線後直接 ★ID': re.compile(r'^\|★P\d'),
    'B 管線後空白★ID': re.compile(r'^\|\s*★P\d'),
    'C 管線後空白ID': re.compile(r'^\|\s+P\d'),
    'D 管線後ID': re.compile(r'^\|P\d'),
    'E 管線後空白★?空格ID': re.compile(r'^\|\s*★?\s*P\d'),
}
for name, rx in cands.items():
    n = sum(1 for l in lines if rx.match(l))
    print(f'{name:<22} {n:>4}')

print()
print('矩陣區塊的前 3 列原始位元組：')
try:
    start = next(i for i, l in enumerate(lines) if l.startswith('## 五、矩陣'))
except StopIteration:
    start = 0
shown = 0
for l in lines[start:]:
    if re.search(r'P\d{1,2}-\d{3}', l) and l.startswith('|'):
        print('   ', ascii(l))
        shown += 1
        if shown >= 3:
            break
