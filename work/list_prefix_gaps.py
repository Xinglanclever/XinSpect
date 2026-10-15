# -*- coding: utf-8 -*-
"""列出「輸出鍵前綴未在任何域碼表宣告」的 75 條，並依「該補哪個域」分組。

報告說這類問題有 8 條，實測是 75 條。差距的原因是報告只看了「項目自己覺得怪」的，
而真正的規則是：**讀者應該能用「前綴 → 域」定位每一個輸出鍵**，
所以每一條落在沒有宣告的前綴上的鍵都算。

輸出格式：`階段 | 域碼 | 域 | 現有前綴 | 需要補的前綴`
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
    c = [x.strip() for x in t.strip('|').split('|')]
    return c


report = []
for st in range(1, 11):
    path = [f'{DIR}/{n}' for n in os.listdir(DIR) if n.startswith(f'STAGE-{st:02d}-')][0]
    text = io.open(path, encoding='utf-8').read()
    block = text.split('## 三、域碼與項數')[1].split('##')[0]

    domains = []
    for line in block.split('\n'):
        c = cells(line)
        if not c or len(c) < 5:
            continue
        code = c[0].replace('**', '').strip()
        if not code or not all(ch.isalnum() for ch in code):
            continue
        if not c[2].replace('**', '').strip().isdigit():
            continue
        prefixes = [p.strip().strip('`').rstrip('*')
                    for p in re.split(r'[／/、,]', c[3]) if p.strip()]
        prefixes = [p if p.endswith('.') else p + '.' for p in prefixes if p]
        domains.append((code, c[1], int(c[2].replace('**', '')), prefixes))

    # 每個域的矩陣列區間（依宣告項數順序切塊）
    rows = []
    for line in text.split('\n'):
        c = cells(line)
        if not c or len(c) < 7 or '---' in line:
            continue
        ident = c[0].lstrip('★')
        if ident.startswith(f'P{st}-') and len(ident) == len(f'P{st}-') + 3:
            rows.append((ident, c[1], c[3]))

    cursor = 0
    for code, label, n, prefixes in domains:
        seg = rows[cursor:cursor + n]
        cursor += n
        for ident, name, keycell in seg:
            for m in re.finditer(r'`([a-z][a-z0-9_.{}*]*)`', keycell):
                key = m.group(1)
                if '.' not in key:
                    continue
                root = key.split('.')[0] + '.'
                if any(root.startswith(p) or p.startswith(root) for p in prefixes):
                    continue
                report.append((st, code, label, '、'.join(prefixes), key, ident, name))

print(f'共 {len(report)} 條\n')
print('| 階段 | 域 | 鍵 | 項目 | 現有前綴 |')
print('|---:|---|---|---|---|')
for st, code, label, prefixes, key, ident, name in report:
    print(f'| {st} | {code} | `{key}` | {ident} {name} | {prefixes} |')

print('\n\n## 依階段統計需要補的前綴')
by_stage = defaultdict(set)
for st, code, label, prefixes, key, ident, name in report:
    by_stage[(st, code, label)].add(key.split('.')[0] + '.')
for (st, code, label), prefs in sorted(by_stage.items()):
    print(f'- 階段 {st} / {code} {label}：{"、".join(sorted(prefs))}')
