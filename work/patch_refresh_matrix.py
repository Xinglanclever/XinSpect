# -*- coding: utf-8 -*-
"""在 refresh_matrix.py 裡記錄每列的逐鍵狀態，讓「部分完成」可被列出來。

原本只記 top（最弱者），所以看不出 P2-029 是「一半 ok 一半 todo」。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'work/refresh_matrix.py'
t = io.open(p, encoding='utf-8').read()

t = t.replace(
    "stats = {'ok': 0, 'alias': 0, 'todo': 0}\ndone = []",
    "stats = {'ok': 0, 'alias': 0, 'todo': 0}\ndone = []\nper_row = []")

t = t.replace(
    """    stats[top] += 1
    if top != 'todo':
        done.append((cells[1].strip(), top))""",
    """    stats[top] += 1
    per_row.append((cells[1].strip(), top, kinds))
    if top != 'todo':
        done.append((cells[1].strip(), top))""")

# 沒有 keys 的那條路徑也要記
t = t.replace(
    """        cells[8] = 'todo: '
        stats['todo'] += 1
        lines[i] = '|'.join(cells)
        continue""",
    """        cells[8] = 'todo: '
        stats['todo'] += 1
        per_row.append((cells[1].strip(), 'todo', ['todo']))
        lines[i] = '|'.join(cells)
        continue""")

# 修 partial 的判斷
t = t.replace(
    """partial = [pid for pid, kind, kinds in per_row if kind == 'todo' and 'ok' in kinds or
           (kind == 'todo' and 'alias' in kinds)]""",
    """partial = [pid for pid, kind, kinds in per_row
           if kind == 'todo' and ('ok' in kinds or 'alias' in kinds)]""")

io.open(p, 'w', encoding='utf-8', newline='').write(t)
print('已更新 refresh_matrix.py')
