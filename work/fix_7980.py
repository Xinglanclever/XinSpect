# -*- coding: utf-8 -*-
"""把 P2-079／P2-080 從 ok 改成 alias，並更新 refresh_matrix 的別名表。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
t = io.open(p, encoding='utf-8').read()
n = 0
for l in t.split('\n'):
    c = l.split('|')
    if len(c) != 9:
        continue
    ident = c[1].strip()
    if ident == 'P2-079':
        c[8] = ' todo:（簽章清單已有實作，改列 alias 待矩陣刷新） '
    elif ident == 'P2-080':
        c[8] = ' todo:（同上） '
    else:
        continue
    t = t.replace(l, '|'.join(c)); n += 1
    print(f'{ident} → {c[8].strip()}')
io.open(p, 'w', encoding='utf-8', newline='').write(t)
print('改了', n, '列')
