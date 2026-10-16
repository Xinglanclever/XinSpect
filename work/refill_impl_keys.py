# -*- coding: utf-8 -*-
"""重填階段二矩陣的「實作鍵」欄——改成查證式，不是我填的。

語意：
  ok:<key>      規格鍵在目錄（或動態家族）裡找得到，且以原名存在
  alias:<key>   已人工確認「語意相同、名字不同」——實作真實用的鍵名
  todo:         在目錄與動態家族裡都找不到——未實作

差別在於前兩者要**查**，第三者要**查不到**。
todo 不再是「我宣稱它沒做」，而是「機器查證後找不到」。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# 人工確認過的別名（附出處）。每一條都必須是我實際讀過程式碼確認的。
ALIASES = {
    'ac.table.{sig}': ('acpi.table.{sig}', 'AcpiTableService.cs:42'),
    'ac.table.wpbt': ('se.wpbt', 'ClosingFactsService.cs:27'),
    'sb.type0.*': ('sb.type03', 'SmbiosDeepFactsService.cs:48'),
    'sb.type3.*': ('sb.type03', 'SmbiosDeepFactsService.cs:48'),
    'sb.type11.*': ('sb.oem.list', 'SmbiosDeepFactsService.cs:92'),
    'sb.oem.strings': ('sb.oem.list', 'SmbiosDeepFactsService.cs:92'),
    'sb.mem.*': ('sb.dim.*', 'SmbiosDeepFactsService.cs:65'),
    'sb.psu.*': ('sb.psu', 'SmbiosDeepFactsService.cs:99'),
    'sb.mc.*': ('sb.mctl', 'SmbiosDeepFactsService.cs:79'),
    'sb.m2.map': ('sb.m2slot.*', 'SmbiosDeepFactsService.cs:109'),
    'sb.m2slot.*': ('sb.m2slot.*', 'SmbiosDeepFactsService.cs:109'),
}

# ── 目錄與動態家族 ──────────────────────────────────────────────────────
cat = io.open('Services/FactKeyCatalog.cs', encoding='utf-8').read()
catalog = set(re.findall(r'^\s*"([a-z0-9_.]+)",\s*$', cat, re.M))
dyn = io.open('Services/FactKeyDynamicCatalog.cs', encoding='utf-8').read()
dynamic = set(re.findall(r'\["([a-z0-9_.]+)"\]\s*=', dyn))

def exists(pattern: str) -> bool:
    pattern = pattern.strip().strip('`')
    if not pattern or pattern in ('—', '（測試）'):
        return False
    if '*' in pattern or '{' in pattern:
        family = pattern.split('*')[0].split('{')[0].rstrip('.')
        return (any(k.startswith(family + '.') for k in catalog)
                or family + '.' in dynamic or family in dynamic)
    return pattern in catalog

ROW = re.compile(r'^\|\s*★?\s*P2-\d{3}\s*\|')
p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
lines = io.open(p, encoding='utf-8').read().split('\n')

stats = {'ok': 0, 'alias': 0, 'todo': 0}
per_row = []
for i, line in enumerate(lines):
    if not ROW.match(line):
        continue
    cells = line.split('|')
    if len(cells) != 9:
        print(f'  !! 行 {i} 欄位數 {len(cells)}，跳過')
        continue

    declared = cells[4].strip()
    keys = [k.strip().strip('`') for k in re.split(r'[／/]', declared)
            if k.strip() and k.strip() not in ('—', '（測試）')]
    if not keys:
        cells[8] = 'todo: '
        stats['todo'] += 1
        per_row.append((cells[1].strip(), 'todo'))
        lines[i] = '|'.join(cells)
        continue

    parts, kinds = [], []
    for k in keys:
        if k in ALIASES and exists(ALIASES[k][0]):
            parts.append(f'alias:{ALIASES[k][0]}')
            kinds.append('alias')
        elif exists(k):
            parts.append(f'ok:{k}')
            kinds.append('ok')
        else:
            parts.append('todo:')
            kinds.append('todo')

    cells[8] = '／'.join(parts) + ' '
    lines[i] = '|'.join(cells)
    top = 'todo' if 'todo' in kinds else ('alias' if 'alias' in kinds else 'ok')
    stats[top] += 1
    per_row.append((cells[1].strip(), top))

io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))

print(f'=== 結果 ===')
print(f'  ok    （已實作、鍵名一致）：{stats["ok"]} 列')
print(f'  alias （已實作、鍵名不同）：{stats["alias"]} 列')
print(f'  todo  （查證後找不到）    ：{stats["todo"]} 列')
print(f'  合計：{sum(stats.values())} 列\n')

print('=== 有實作或別名的列 ===')
for pid, kind in per_row:
    if kind != 'todo':
        print(f'  [{kind}] {pid}')

print('\n=== 全部 todo 的列 ===')
todos = [pid for pid, k in per_row if k == 'todo']
print(f'  {len(todos)} 列：{"、".join(todos)}')
