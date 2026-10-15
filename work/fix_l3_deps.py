# -*- coding: utf-8 -*-
"""把依賴欄裡的「L3 禁項」補成可對帳的形式。

原文 `L3 禁項` 有三個問題：
  1. 它不是 ID，稽核工具無法驗證它指到哪裡；
  2. 它其實指的是「P1-048 L3 禁項掃描守門」（階段一的守門項）；
  3. 五處寫法一致但都沒有解釋——讀者得自己猜。

修法：明確寫成 `P1-048（L3 禁項掃描守門）`，保留原本的說明文字，但讓它可解析。
"""
import io
import os
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'

targets = {'P2-070', 'P3-028', 'P6-071', 'P7-022', 'P7-048'}
changed = []

for st in range(1, 11):
    path = [f'{DIR}/{n}' for n in os.listdir(DIR) if n.startswith(f'STAGE-{st:02d}-')][0]
    lines = io.open(path, encoding='utf-8').read().split('\n')
    out = []
    for line in lines:
        t = line.strip()
        if not t.startswith('|'):
            out.append(line)
            continue
        cells = [c.strip() for c in t.strip('|').split('|')]
        if len(cells) < 7 or '---' in t:
            out.append(line)
            continue
        ident = cells[0].lstrip('★')
        if ident not in targets or cells[6] != 'L3 禁項':
            out.append(line)
            continue
        cells[6] = 'P1-048（L3 禁項掃描守門）'
        out.append('| ' + ' | '.join(cells) + ' |')
        changed.append(ident)
    io.open(path, 'w', encoding='utf-8', newline='').write('\n'.join(out))

print('改寫：', '、'.join(changed) if changed else '（無）')
print('未處理：', '、'.join(sorted(targets - set(changed))) if targets - set(changed) else '（無）')
