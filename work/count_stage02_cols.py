# -*- coding: utf-8 -*-
"""在階段二矩陣加一個「實作鍵」欄位，由對照表填入。

表頭在 93 行、資料從 95 行起，共 8 欄（ID/名稱/目標/輸出鍵/敏感度/成本/依賴/…）。
等一下——表頭寫 7 欄，分隔列寫 7 個 ---，而 P2-001 那列有 7 個 |。
先數清楚再改。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
lines = io.open(p, encoding='utf-8').read().split('\n')

for i in (92, 93, 94, 95):
    cells = lines[i].split('|')
    print(f'{i}: {len(cells)} 段 → {[c.strip()[:20] for c in cells]}')
