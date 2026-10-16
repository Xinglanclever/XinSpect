# -*- coding: utf-8 -*-
"""修掉表頭與分隔列：我上一版把「依賴」與「實作鍵」黏在一起了。

原本：split('|') 得到 ['', 'ID', ..., '依賴', '']（9 段，頭尾是分隔用的空字串）
我寫 '|'.join(h[:-1]) + ' 實作鍵 |' → 砍掉尾端空字串後接新欄位，
結果「依賴」後面直接黏上「 實作鍵」，中間少了 '|'。

正確做法：保留頭尾，在倒數第二個位置插入新欄位。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
lines = io.open(p, encoding='utf-8').read().split('\n')

# 表頭
h = lines[93].split('|')          # ['', ID, 名稱, 目標, 輸出鍵, 敏感度, 成本, 依賴  實作鍵, '']
h[7] = ' 依賴 '
h.insert(8, ' 實作鍵 ')
lines[93] = '|'.join(h)

# 分隔列
d = lines[94].split('|')
if len(d) == 9:
    d[7] = '---'
    d.insert(8, '---')
lines[94] = '|'.join(d)

io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))

# 驗證
t = io.open(p, encoding='utf-8').read().split('\n')
for i in (93, 94, 95):
    cells = t[i].split('|')
    print(f'{i}: {len(cells)} 段 → {[c.strip()[:22] for c in cells]}')
