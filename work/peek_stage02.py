# -*- coding: utf-8 -*-
"""先看清楚矩陣的表頭與分隔列長什麼樣，再決定怎麼加欄位。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
t = io.open('docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md', encoding='utf-8').read()
lines = t.split('\n')
for i, l in enumerate(lines[:30]):
    if l.startswith('|') or l.startswith('#'):
        print(f'{i}: {l[:160]}')
