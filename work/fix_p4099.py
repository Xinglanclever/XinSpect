# -*- coding: utf-8 -*-
"""把 P4-099 補上階段內前置（RPT 域的第一項，前一項屬前一域）。

`P4-099 儲存深測條件格式` 是 RPT 域的第一列，而前一列 P4-098 屬 LIFE 域，
所以前一版的「取同域前一項」規則在這裡取不到——它落在域邊界上。
這種情況取「矩陣中的前一列」即可：那是保守且可稽核的選擇。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'docs/ULTIMATE-1000/STAGE-04-STORAGE.md'
lines = io.open(p, encoding='utf-8').read().split('\n')
changed = False
for i, line in enumerate(lines):
    if not line.strip().startswith('|'):
        continue
    c = [x.strip() for x in line.strip().strip('|').split('|')]
    if len(c) < 7 or '---' in line:
        continue
    if c[0].lstrip('★') == 'P4-099' and c[6] == '參照：P8-001':
        c[6] = 'P4-098；參照：P8-001'
        lines[i] = '| ' + ' | '.join(c) + ' |'
        changed = True
if changed:
    io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
print('P4-099 已補階段內前置' if changed else '未找到目標列')
