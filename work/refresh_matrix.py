# -*- coding: utf-8 -*-
"""更新階段二矩陣的實作鍵欄——把已完成的項目從 todo 改成 ok。

這支腳本與 refill_impl_keys.py 同樣是「查證式」：
先看規格鍵在目錄裡找不找得到，再看有沒有已知的別名。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

ALIASES = {
    'ac.table.{sig}': ('acpi.table.{sig}', 'Services/AcpiTableService.cs', 42),
    'ac.table.wpbt': ('se.wpbt', 'Services/ClosingFactsService.cs', 27),
    'ac.cedt.*': ('cxl.cfmws.count', 'Services/CxlFactsService.cs', 34),
    # SLIT 的實作叫 numa.slit.nodes——上一輪漏了這一條，
    # 而守門用「P2-007 標 todo 但 ac.srat.* 已經在目錄裡」把它逼出來。
    'ac.slit.*': ('numa.slit.nodes', 'Services/TopologyDeepenFactsService.cs', 75),
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
done = []
per_row = []
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
        per_row.append((cells[1].strip(), 'todo', ['todo']))
        lines[i] = '|'.join(cells)
        continue

    parts, kinds = [], []
    for k in keys:
        if k in ALIASES:
            real, src, ln = ALIASES[k]
            if exists(real):
                parts.append(f'alias:{real}@{src}:{ln}')
                kinds.append('alias')
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
    per_row.append((cells[1].strip(), top, kinds))
    if top != 'todo':
        done.append((cells[1].strip(), top))

io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))

print('=== 結果 ===')
print(f'  ok    ：{stats["ok"]} 列')
print(f'  alias ：{stats["alias"]} 列')
print(f'  todo  ：{stats["todo"]} 列')
print(f'  合計  ：{sum(stats.values())} 列\n')

# 統計以「列」為單位——全鍵完成才算完成。
# 有些列的規格鍵不只一把（例如 P2-029 是 sb.tpmdev／sb.proc_add），
# 那種列只要有一把沒做，整列就歸 todo。
# **這是保守的算法：寧可少報完成度，不要多報。**
# 但保守的算法必須說出自己是保守的，否則「已完成 30 列」會被讀成
# 「30 列的全部鍵都完成了」——而那對部分完成的列來說是錯的。
partial = [pid for pid, kind, kinds in per_row
           if kind == 'todo' and ('ok' in kinds or 'alias' in kinds)]
if partial:
    print(f'=== 部分完成的列（{len(partial)} 列）===')
    print('  這些列的規格鍵有一部分已實作、一部分未做；')
    print('  因為統計以「列」為單位（全鍵完成才算），它們被歸在 todo 裡。')
    for pid in partial:
        print(f'    {pid}')
    print()
print(f'=== 已完成（{len(done)} 列）===')
print('  ' + '、'.join(f'{p}({k})' for p, k in done))
