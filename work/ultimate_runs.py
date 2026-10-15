# -*- coding: utf-8 -*-
"""I1：檢查每個階段「域碼表的項數」是否等於矩陣裡對應的連續列數。

矩陣是分域成塊寫的（F1 的 12 列、接著 F2 的 10 列…）。若某個域宣告 n 項，
就從游標位置取 n 列，看那些列的內容是否真的屬於該域。取完整份表格後
游標應該剛好用完 105 列；游標用不完或提前用完，就是宣告與矩陣對不上。
"""
import glob
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'
ROW = re.compile(r'^\|\s*(★?)\s*(P(\d{1,2})-(\d{3}))\s*\|(.*)$')

print(f'{"階段":<4}{"域":<6}{"宣告":>4}  區間（矩陣列序）')
print('-' * 70)
for st in range(1, 11):
    path = glob.glob(f'{DIR}/STAGE-{st:02d}-*.md')[0]
    text = io.open(path, encoding='utf-8').read()
    block = text.split('## 三、域碼與項數')[1].split('##')[0]
    doms = [(m.group(1), int(m.group(2)))
            for m in re.finditer(r'^\|\s*([A-Z]+\d?)\s*\|[^|]*\|\s*(\d+)\s*\|', block, re.M)]
    runs = re.findall(r'^\|\s*★?\s*(P\d{1,2}-\d{3})\s*\|', text, re.M)
    i = 0
    for code, n in doms:
        seg = runs[i:i + n]
        i += n
        rng = f'{seg[0]}..{seg[-1]}' if seg else '（取不到）'
        print(f'{st:<4}{code:<6}{n:>4}  {rng}')
    tail = runs[i:]
    note = 'OK' if not tail else f'!!! 游標用完後還剩 {len(tail)} 列：{tail[:6]}'
    print(f'      {"":<10}{sum(n for _, n in doms):>4}  {note}')
