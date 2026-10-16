# -*- coding: utf-8 -*-
"""補上帶 ★ 錨記的三列的第 9 欄（實作鍵）。

產生這份資料的腳本（work/add_impl_key_column.py）用了 `line.startswith('| P2-')`，
也就是我剛剛才在守門裡修掉的那個缺陷——**同一個缺陷在兩個地方出現**：
守門修了，產生資料的工具沒修。所以三列 ★ 到現在還是 8 欄。

這支腳本用修正後的規則（`|` 後可有可無的 ★ 再接 P2-nnn）來補。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

ALIASES = {
    'ac.table.{sig}': 'acpi.table.{sig}',
    'ac.table.wpbt': 'se.wpbt',
    'sb.type0.*': 'sb.type03',
    'sb.type3.*': 'sb.type03',
    'sb.type11.*': 'sb.oem.list',
    'sb.oem.strings': 'sb.oem.list',
    'sb.mem.*': 'sb.dim.*',
    'sb.psu.*': 'sb.psu',
    'sb.mc.*': 'sb.mctl',
    'sb.m2.map': 'sb.m2slot.*',
    'sb.m2slot.*': 'sb.m2slot.*',
}

ROW = re.compile(r'^\|\s*★?\s*P2-\d{3}\s*\|')

p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
lines = io.open(p, encoding='utf-8').read().split('\n')

fixed = 0
for i, line in enumerate(lines):
    if not ROW.match(line):
        continue
    cells = line.split('|')
    if len(cells) >= 10:
        continue                      # 已經有第 9 欄
    if len(cells) != 9:
        print(f'  !! 行 {i} 欄位數異常：{len(cells)} → {line[:80]!r}')
        continue

    keycell = cells[4].strip()
    keys = [k.strip().strip('`') for k in re.split(r'[／/]', keycell)
            if k.strip() and k.strip() not in ('—', '（測試）')]
    if not keys:
        cells[8] = 'todo: '
    else:
        parts = []
        for k in keys:
            parts.append(f'alias:{ALIASES[k]}' if k in ALIASES else f'ok:{k}')
        cells[8] = '／'.join(parts) + ' '
    lines[i] = '|'.join(cells)
    fixed += 1
    print(f'  行 {i} 補上：{cells[1].strip()} → {cells[8].strip()}')

io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
print(f'\n補了 {fixed} 列')

# 驗證：所有矩陣列的欄位數
t = io.open(p, encoding='utf-8').read().split('\n')
counts = {}
for line in t:
    if ROW.match(line):
        n = len(line.split('|'))
        counts[n] = counts.get(n, 0) + 1
print(f'矩陣列欄位數分布：{counts}')
