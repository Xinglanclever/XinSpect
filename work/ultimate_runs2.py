# -*- coding: utf-8 -*-
"""I1b：域碼表逐列解析（含 ** 粗體與全形分隔符）並與矩陣分塊對齊。

work/ultimate_runs.py 用的正則漏掉了合計列之前那些「域碼被粗體包起來」寫法的檔案
（階段一寫的是 `| **合計** |`，其他階段不一定），所以階段一只數到 9 個域 95 項。
這一版把 ** 去掉再解析，並且列出「宣告合計 ≠ 105」的階段，讓兩者當場分開。
"""
import glob
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'
ROW = re.compile(r'^\|\s*★?\s*(P\d{1,2}-\d{3})\s*\|')

for st in range(1, 11):
    path = glob.glob(f'{DIR}/STAGE-{st:02d}-*.md')[0]
    text = io.open(path, encoding='utf-8').read()
    block = text.split('## 三、域碼與項數')[1].split('##')[0]
    doms = []
    for line in block.splitlines():
        c = [x.strip().replace('**', '') for x in line.strip().strip('|').split('|')]
        if len(c) >= 3 and re.fullmatch(r'[A-Z]+\d?', c[0]) and c[2].isdigit():
            doms.append((c[0], int(c[2])))
    runs = ROW.findall(text)
    total = sum(n for _, n in doms)
    print(f'階段 {st}: {len(doms)} 域  宣告合計={total}  矩陣={len(runs)}  '
          f'{"OK" if total == len(runs) == 105 else "<<< 對不上"}')
    if total != 105:
        i = 0
        for code, n in doms:
            seg = runs[i:i + n]
            i += n
            print(f'    {code:<5}{n:>4}  {seg[0] if seg else "-"}..{seg[-1] if seg else "-"}')
        print(f'    剩下未被任何域涵蓋：{runs[i:]}')
