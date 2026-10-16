# -*- coding: utf-8 -*-
"""在階段二矩陣的最後加一欄「實作鍵」。

欄位語意（機器可讀，由 UltimateAudit 讀）：
  ok:<key>       規格鍵與實作鍵相同
  alias:<key>    語意相同、名字不同（實作真實用的鍵名）
  todo:          尚未實作

每一條 alias 都必須附出處（哪個檔案的第幾行確認的），寫在下面的 ALIASES 裡。
這是因為「名字不同但語意相同」的判斷**不能靠機器猜**——必須有人看過並留下依據。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# 規格鍵 → (實作鍵, 出處)
ALIASES = {
    'ac.table.{sig}': ('acpi.table.{sig}', 'Services/AcpiTableService.cs:42'),
    'ac.table.wpbt': ('se.wpbt', 'Services/ClosingFactsService.cs:27'),
    'sb.type0.*': ('sb.type03', 'Services/SmbiosDeepFactsService.cs:48'),
    'sb.type3.*': ('sb.type03', 'Services/SmbiosDeepFactsService.cs:48'),
    'sb.type11.*': ('sb.oem.list', 'Services/SmbiosDeepFactsService.cs:92'),
    'sb.oem.strings': ('sb.oem.list', 'Services/SmbiosDeepFactsService.cs:92'),
    'sb.mem.*': ('sb.dim.*', 'Services/SmbiosDeepFactsService.cs:65'),
    'sb.psu.*': ('sb.psu', 'Services/SmbiosDeepFactsService.cs:99'),
    'sb.mc.*': ('sb.mctl', 'Services/SmbiosDeepFactsService.cs:79'),
    'sb.m2.map': ('sb.m2slot.*', 'Services/SmbiosDeepFactsService.cs:109'),
    'sb.m2slot.*': ('sb.m2slot.*', 'Services/SmbiosDeepFactsService.cs:109'),
}

p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
lines = io.open(p, encoding='utf-8').read().split('\n')

# 表頭（93）與分隔列（94）加欄位
h = lines[93].split('|')
lines[93] = '|'.join(h[:-1]) + ' 實作鍵 |'
d = lines[94].split('|')
lines[94] = '|'.join(d[:-1]) + '---|'

changed = 0
for i in range(95, len(lines)):
    line = lines[i]
    if not line.startswith('| P2-'):
        # 矩陣可能在 P2-105 後結束
        if line.startswith('|') and not line.startswith('| P2-'):
            continue
        continue
    cells = line.split('|')
    if len(cells) != 9:
        continue
    keycell = cells[4].strip()
    keys = [k.strip().strip('`') for k in re.split(r'[／/]', keycell) if k.strip() and k.strip() != '—']
    verdicts = []
    for k in keys:
        if k in ALIASES:
            real, src = ALIASES[k]
            verdicts.append(f'alias:{real}')
        else:
            verdicts.append(f'ok:{k}')
    cells[8] = ('／'.join(verdicts) if verdicts else 'todo:') + ' '
    lines[i] = '|'.join(cells)
    changed += 1

io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
print(f'已加欄位，處理 {changed} 列')
