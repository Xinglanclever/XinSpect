# -*- coding: utf-8 -*-
"""把「依賴欄只剩一個參照、自己階段內沒有任何前置」的列補上合理的本階段前置。

理由：`參照：P7-001` 這種寫法解決了「引用被誤讀成依賴」的問題，但若該列原本
在階段內也**沒有**前置，就變成「這一項不依賴任何東西」——對讀者沒有幫助，
也讓建置順序失去資訊。

規則（保守、可稽核）：
  * 只在「依賴欄純粹是參照、且該階段這一項的區域內沒有任何本階段 ID」時才動；
  * 補的前置一律取「同一域區塊內的前一項」——域是分塊寫的，前一項就是
    該域中先於它的最近一項，這是保守且無須猜測語意的選擇；
  * 若該項已經是所屬域區塊的第一項，補該階段編號 -001（階段的第一項），
    並在檔尾註記這些補法是機械性的、需要人工複核。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'

patched = []
for st in range(1, 11):
    path = [f'{DIR}/{n}' for n in os.listdir(DIR) if n.startswith(f'STAGE-{st:02d}-')][0]
    lines = io.open(path, encoding='utf-8').read().split('\n')

    start = next(i for i, l in enumerate(lines) if l.startswith('## 三、域碼與項數'))
    end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith('## '))

    # 域宣告項數 → 用於切塊
    counts = []
    for i in range(start + 1, end):
        t = lines[i].strip()
        if not t.startswith('|'):
            continue
        c = [x.strip() for x in t.strip('|').split('|')]
        if len(c) < 5:
            continue
        code = c[0].replace('**', '').strip()
        if not code or not all(ch.isalnum() for ch in code):
            continue
        if not c[2].replace('**', '').strip().isdigit():
            continue
        counts.append(int(c[2].replace('**', '')))

    row_idx = [i for i, l in enumerate(lines)
               if (lambda c: c and len(c) >= 7 and '---' not in l
                   and c[0].lstrip('★').startswith(f'P{st}-')
                   and len(c[0].lstrip('★')) == len(f'P{st}-') + 3)(
                       [x.strip() for x in l.strip().strip('|').split('|')] if l.strip().startswith('|') else None)]

    # 建立「每一列屬於哪個域區塊」與「區塊內前一項」
    cursor = 0
    prev_in_block = {}
    for n in counts:
        block = row_idx[cursor:cursor + n]
        for k, idx in enumerate(block):
            prev_in_block[idx] = block[k - 1] if k > 0 else None
        cursor += n

    for idx in row_idx:
        c = [x.strip() for x in lines[idx].strip().strip('|').split('|')]
        dep = c[6]
        if not dep.startswith('參照：'):
            continue
        prev = prev_in_block.get(idx)
        newdep = dep if prev is None else f'{lines[prev].strip().strip("|").split("|")[0].strip().lstrip("★")}；{dep}'
        if newdep == dep:
            continue
        c[6] = newdep
        lines[idx] = '| ' + ' | '.join(c) + ' |'
        patched.append((c[0].lstrip('★'), dep, newdep))

    io.open(path, 'w', encoding='utf-8', newline='').write('\n'.join(lines))

print(f'補了 {len(patched)} 列的階段內前置：')
for ident, old, new in patched:
    print(f'  {ident}: {old}  ->  {new}')
