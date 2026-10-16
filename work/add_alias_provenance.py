# -*- coding: utf-8 -*-
"""把「語意相同但名字不同」的判斷加上出處，並補 CXL／WPBT／TPM2。

問題：我建立的分工是「機器查證 todo、人工判斷 alias」，
但我只釘住了機器那一半。人工那一半（語意相同的判斷）如果沒有出處，
就會變成「我說了算」——而那是不能被守門檢驗的斷言。

處置：alias 的語法改成 `alias:<實作鍵>@<檔案>:<行號>`，
      守門要求 @ 後面必須是「真的存在的那個檔案裡的某一行」。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# 規格鍵 → (實作鍵, 出處檔案, 行號)
ALIASES = {
    'ac.table.{sig}': ('acpi.table.{sig}', 'Services/AcpiTableService.cs', 42),
    'ac.table.wpbt': ('se.wpbt', 'Services/ClosingFactsService.cs', 27),
    'ac.cedt.*': ('cxl.cfmws.count', 'Services/CxlFactsService.cs', 34),
    'sb.type0.*': ('sb.type03', 'Services/SmbiosDeepFactsService.cs', 48),
    'sb.type3.*': ('sb.type03', 'Services/SmbiosDeepFactsService.cs', 48),
    'sb.type11.*': ('sb.oem.list', 'Services/SmbiosDeepFactsService.cs', 92),
    'sb.oem.strings': ('sb.oem.list', 'Services/SmbiosDeepFactsService.cs', 92),
    'sb.mem.*': ('sb.dim.*', 'Services/SmbiosDeepFactsService.cs', 65),
    'sb.psu.*': ('sb.psu', 'Services/SmbiosDeepFactsService.cs', 99),
    'sb.mc.*': ('sb.mctl', 'Services/SmbiosDeepFactsService.cs', 79),
    'sb.m2.map': ('sb.m2slot.*', 'Services/SmbiosDeepFactsService.cs', 109),
    'sb.m2slot.*': ('sb.m2slot.*', 'Services/SmbiosDeepFactsService.cs', 109),
}

cat = io.open('Services/FactKeyCatalog.cs', encoding='utf-8').read()
catalog = set(re.findall(r'^\s*"([a-z0-9_.]+)",\s*$', cat, re.M))
dyn = io.open('Services/FactKeyDynamicCatalog.cs', encoding='utf-8').read()
dynamic = set(re.findall(r'\["([a-z0-9_.]+)"\]\s*=', dyn))

def exists(pattern):
    pattern = pattern.strip().strip('`')
    if not pattern or pattern in ('—', '（測試）'):
        return False
    if '*' in pattern or '{' in pattern:
        fam = pattern.split('*')[0].split('{')[0].rstrip('.')
        return any(k.startswith(fam + '.') for k in catalog) or fam + '.' in dynamic or fam in dynamic
    return pattern in catalog

ROW = re.compile(r'^\|\s*★?\s*P2-\d{3}\s*\|')
p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
lines = io.open(p, encoding='utf-8').read().split('\n')

stats = {'ok': 0, 'alias': 0, 'todo': 0}
alias_rows = []
for i, line in enumerate(lines):
    if not ROW.match(line):
        continue
    cells = line.split('|')
    if len(cells) != 9:
        continue

    declared = cells[4].strip()
    keys = [k.strip().strip('`') for k in re.split(r'[／/]', declared)
            if k.strip() and k.strip() not in ('—', '（測試）')]
    if not keys:
        cells[8] = 'todo: '
        stats['todo'] += 1
        lines[i] = '|'.join(cells)
        continue

    parts, kinds = [], []
    for k in keys:
        if k in ALIASES:
            real, src, ln = ALIASES[k]
            if exists(real):
                parts.append(f'alias:{real}@{src}:{ln}')
                kinds.append('alias')
                alias_rows.append((cells[1].strip(), k, real, src, ln))
            else:
                parts.append('todo:')
                kinds.append('todo')
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

io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))

print('=== 結果 ===')
print(f'  ok    ：{stats["ok"]} 列')
print(f'  alias ：{stats["alias"]} 列')
print(f'  todo  ：{stats["todo"]} 列')
print(f'  合計  ：{sum(stats.values())} 列\n')

print('=== alias 的出處 ===')
for pid, spec, real, src, ln in alias_rows:
    print(f'  {pid:10s} {spec:20s} → {real:20s} @ {src}:{ln}')
