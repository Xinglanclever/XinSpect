# -*- coding: utf-8 -*-
"""I1e：域宣告項數 vs 矩陣實際分塊（正則已由 work/ultimate_probe_rows.py 實測確定）。

實測出來的列首有兩種：
    '| ★P1-001 | ...'    ← ★ 錨點，星號緊接管線
    '| P1-002 | ...'     ← 一般項
所以正確的規則是 `^\\|\\s*★?\\s*P\\d`。

前三版各敗在一個假設上，留著當教材：
    ultimate_runs.py   沒處理 `| **合計** |` → 階段一只認到 9 域
    ultimate_runs2.py  `★?` 與 ID 之間沒有 `\\s*` → 全階段 0 列
    ultimate_runs3.py  同上（沒先量位元組就寫正則）
"""
import glob
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'
ROW = re.compile(r'^\|\s*★?\s*(P\d{1,2}-\d{3})\s*\|')

print(f'{"階段":<5}{"域數":>4}{"宣告合計":>9}{"矩陣":>6}   判定')
print('-' * 46)
bad = []
for st in range(1, 11):
    path = glob.glob(f'{DIR}/STAGE-{st:02d}-*.md')[0]
    text = io.open(path, encoding='utf-8').read()
    block = text.split('## 三、域碼與項數')[1].split('##')[0]
    doms = []
    for line in block.splitlines():
        # 先去掉粗體標記再切欄：合計列寫成 `| **合計** | | **105** | | |`，
        # 不去粗體的話欄數會與域列不同，解析就會靜靜漏掉最後一個域（F10）。
        c = [x.strip().replace('**', '') for x in line.strip().strip('|').split('|')]
        if len(c) >= 3 and re.fullmatch(r'[A-Z]+\d?', c[0]) and c[2].isdigit():
            doms.append((c[0], c[1], int(c[2])))
    runs = [l for l in text.splitlines() if ROW.match(l)]
    runs = [ROW.match(l).group(1) for l in runs]
    total = sum(n for _, _, n in doms)
    ok = (total == len(runs) == 105)
    if not ok:
        bad.append(st)
    print(f'{st:<5}{len(doms):>4}{total:>9}{len(runs):>6}   {"OK" if ok else "<<< 對不上"}')
    if not ok:
        i = 0
        for code, _, n in doms:
            seg = runs[i:i + n]
            i += n
            print(f'      {code:<5}{n:>4}  {(seg[0] + ".." + seg[-1]) if seg else "-"}')
        print(f'      剩下未被任何域涵蓋：{runs[i:]}')

print()
print('★ 錨點列數：', sum(R_.group(0).count('★') for st in range(1, 11)
      for R_ in [re.match(r'^\|\s*★', l) or re.match(r'^\|', l)
                 for l in io.open(glob.glob(f"{DIR}/STAGE-{st:02d}-*.md")[0], encoding='utf-8')]
      if R_ and re.match(r'^\|\s*★\s*P\d', R_.group(0))))
print('不合規的階段：', bad if bad else '無')
