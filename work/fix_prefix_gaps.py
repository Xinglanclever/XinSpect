# -*- coding: utf-8 -*-
"""把缺的輸出鍵前綴補進各階段的域碼表（第四欄「主要輸出前綴」）。

為什麼選「補前綴」而不是「改鍵名」：
  鍵名多半已經反映語意（`irq.`／`fan.`／`smb.` 比硬塞進 `numa.`／`power.` 清楚）。
  前綴表是索引，索引缺頁要補頁，不是把書的內容改掉。

做法：逐一階段把「該階段各域實際用到的前綴」與「已宣告的前綴」比對，
      只補「該域自己的矩陣列用到、但沒宣告」的那些，不動別的域。
      合併後同一域內去重、保持原順序（已宣告的在前，新補的依後）。
"""
import io
import os
import re
import sys
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'


def cells(line):
    t = line.strip()
    if not t.startswith('|'):
        return None
    return [x.strip() for x in t.strip('|').split('|')]


total_added = 0
for st in range(1, 11):
    path = [f'{DIR}/{n}' for n in os.listdir(DIR) if n.startswith(f'STAGE-{st:02d}-')][0]
    text = io.open(path, encoding='utf-8').read()
    lines = text.split('\n')

    # 1. 找域碼表區塊
    start = next(i for i, l in enumerate(lines) if l.startswith('## 三、域碼與項數'))
    end = next(i for i in range(start + 1, len(lines)) if lines[i].startswith('## '))

    # 2. 解析域碼表
    domains = []           # (line_index, code, label, count, [prefixes])
    for i in range(start + 1, end):
        c = cells(lines[i])
        if not c or len(c) < 5:
            continue
        code = c[0].replace('**', '').strip()
        if not code or not all(ch.isalnum() for ch in code):
            continue
        if not c[2].replace('**', '').strip().isdigit():
            continue
        prefixes = [p.strip().strip('`').rstrip('*') for p in re.split(r'[／/、,]', c[3]) if p.strip()]
        prefixes = [p if p.endswith('.') else p + '.' for p in prefixes if p]
        domains.append([i, code, c[1], int(c[2].replace('**', '')), prefixes])

    # 3. 掃矩陣列（依域順序切塊），收集每塊實際用到的前綴
    rows = []
    for line in lines:
        c = cells(line)
        if not c or len(c) < 7 or '---' in line:
            continue
        ident = c[0].lstrip('★')
        if ident.startswith(f'P{st}-') and len(ident) == len(f'P{st}-') + 3:
            rows.append(c[3])

    cursor = 0
    for d in domains:
        seg = rows[cursor:cursor + d[3]]
        cursor += d[3]
        used = []
        for keycell in seg:
            for m in re.finditer(r'`([a-z][a-z0-9_.{}*]*)`', keycell):
                key = m.group(1)
                if '.' not in key:
                    continue
                root = key.split('.')[0] + '.'
                if root not in used:
                    used.append(root)
        # 只補「這一塊用到、但沒宣告」的
        for root in used:
            if not any(root.startswith(p) or p.startswith(root) for p in d[4]):
                if root not in d[4]:
                    d[4].append(root)
                    total_added += 1

    # 4. 寫回
    for i, code, label, count, prefixes in domains:
        c = cells(lines[i])
        c[3] = '／'.join(f'`{p}`' for p in prefixes)
        lines[i] = '| ' + ' | '.join(c) + ' |'
    io.open(path, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
    print(f'階段 {st:>2}: {len(domains)} 域，補了 {sum(1 for d in domains for _ in [0]) and total_added} 條累計')

print(f'\n合計補上 {total_added} 個前綴宣告')
