# -*- coding: utf-8 -*-
"""I1c：把「域宣告項數」與「矩陣實際分塊」逐階段對齊（粗體與全形分隔符都處理）。

前兩版各踩到一個坑，都值得留著：
  * ultimate_runs.py 的列首正則沒處理 `| **合計** |`，所以階段一只數到 9 個域。
  * ultimate_runs2.py 把 ROW 寫成 `★?` 緊接 ID，漏掉 `| ★P3-031 |` 這種合法寫法，
    結果所有階段都數到 0 列。

這版兩者都修好，並用「宣告合計 == 矩陣列數 == 105」當驗收條件。
"""
import glob
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'
ROW = re.compile(r'^\|\s*★\s*(P\d{1,2}-\d{3})\s*\|')      # ★ 錨點列（星號緊接 ID）
ROW2 = re.compile(r'^\|\s{2,}(P\d{1,2}-\d{3})\s*\|')       # 非錨點列（管線後有空白）
ROW_ANY = re.compile(r'^\|\s*★?\s*(P\d{1,2}-\d{3})\s*\|')

results = []
for st in range(1, 11):
    path = glob.glob(f'{DIR}/STAGE-{st:02d}-*.md')[0]
    text = io.open(path, encoding='utf-8').read()
    block = text.split('## 三、域碼與項數')[1].split('##')[0]
    doms = []
    for line in block.splitlines():
        c = [x.strip().replace('**', '') for x in line.strip().strip('|').split('|')]
        if len(c) >= 3 and re.fullmatch(r'[A-Z]+\d?', c[0]) and c[2].isdigit():
            doms.append((c[0], c[1], int(c[2])))
    runs = ROW_ANY.findall(text)
    total = sum(n for _, _, n in doms)
    ok = (total == len(runs) == 105)
    results.append((st, len(doms), total, len(runs), ok))
    print(f'階段 {st:>2}: {len(doms)} 域  宣告合計={total:>3}  矩陣={len(runs):>3}  {"OK" if ok else "<<< 對不上"}')
    if not ok:
        i = 0
        for code, _, n in doms:
            seg = runs[i:i + n]
            i += n
            print(f'      {code:<5}{n:>4}  {(seg[0] + ".." + seg[-1]) if seg else "-"}')
        print(f'      剩下未被任何域涵蓋：{runs[i:]}')

print()
print('★ 錨點列數（ROW 專用）：', sum(len(ROW.findall(io.open(glob.glob(f"{DIR}/STAGE-{st:02d}-*.md")[0], encoding="utf-8").read())) for st in range(1, 11)))
print('非錨點列數（ROW2 專用）：', sum(len(ROW2.findall(io.open(glob.glob(f"{DIR}/STAGE-{st:02d}-*.md")[0], encoding="utf-8").read())) for st in range(1, 11)))
