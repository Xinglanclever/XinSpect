# -*- coding: utf-8 -*-
"""列出階段二剩下的 todo 列。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')

p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
t = io.open(p, encoding='utf-8').read()

rows = []
for line in t.split('\n'):
    c = line.split('|')
    if len(c) != 9:
        continue
    ident = c[1].strip()
    if not ident.lstrip('★').startswith('P2-0'):
        continue
    state = c[8].strip()
    if not state.startswith('todo'):
        continue
    rows.append((ident, c[2].strip(), c[4].strip(), c[6].strip()))

print(f'剩下 {len(rows)} 列 todo\n')
order = {'S': 0, 'M': 1, 'L': 2}
rows.sort(key=lambda r: (order.get(r[3], 9), r[0]))
for ident, name, key, cost in rows:
    print(f'  [{cost}] {ident:10s} {name:24s} {key}')
