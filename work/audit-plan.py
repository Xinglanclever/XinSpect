# -*- coding: utf-8 -*-
"""
⚠ 這一版**是錯的，留著當證據**（第 264 輪，`GATE-LOG-264` §一）：
   它的矩陣列辨識不認 `| ★P2-034 |` 這種寫法，於是數出「階段二 102 列、24 個 ID 不在矩陣裡」——
   與 FIX-REPORT 第三部那個已被推翻的指控一模一樣。
   正確的辨識式寫在 `docs/ULTIMATE-1000/README.md` 的附錄，實作在
   `Tests/PlanLibraryTests.cs`（會自動跑的那一份）。
"""
"""ULTIMATE-1000 計畫庫稽核：只做算術，不猜語意。

用途：FIX-REPORT 第三部宣稱 31 條規格缺陷，但那份稽核自己的工具被查出有問題
（Python 3.11.9 與 .NET 10 對 `[A-Z]+` 這種樣式在本機都給錯答案）——
所以「工具壞掉」與「文件壞掉」在上一輪分不開。這一支刻意只用最笨的字串比對：
不用字元類別、不用量詞猜測，ID 一律用 `P<階段>-<三位數>` 這種固定形狀去找。

輸出：每階段的
  ① 檔頭宣稱項數（「本階段 … 105 項」那一句）
  ② §五矩陣列數（`| P#-### |` 開頭的列）
  ③ 檔內全部互異 ID 數、編號是否連續 1..105
  ④ 矩陣缺的 ID，以及那些 ID 在檔案其他地方（錨點區／Gate 區）找不找得到
"""
import os, re, sys, glob

DOC = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'docs', 'ULTIMATE-1000')
ID = re.compile(r'P(\d{1,2})-(\d{3})')

def stage_no(path):
    m = re.match(r'STAGE-(\d{2})-', os.path.basename(path))
    return int(m.group(1)) if m else None

rows_total = 0
problems = []

for path in sorted(glob.glob(os.path.join(DOC, 'STAGE-*.md'))):
    st = stage_no(path)
    if st is None:
        continue
    text = open(path, encoding='utf-8').read()
    lines = text.split('\n')

    # ① 檔頭宣稱（「本階段 105 項」/「本階段共 105 項」）
    claim = None
    for l in lines[:40]:
        m = re.search(r'本階段[^\n]{0,20}?(\d{2,4})\s*項', l)
        if m:
            claim = int(m.group(1)); break

    # ② 矩陣列數與矩陣裡出現的 ID（列首就是 ID 的列）
    matrix_ids = []
    for l in lines:
        m = re.match(r'\|\s*(P\d{1,2}-\d{3})\s*\|', l)
        if m:
            matrix_ids.append(m.group(1))
    matrix_set = {i for i in matrix_ids if i.startswith(f'P{st}-')}

    # ③ 檔內全部互異 ID（含錨點區、Gate 區）
    all_ids = {}
    for i, l in enumerate(lines, 1):
        for m in ID.finditer(l):
            key = f'P{int(m.group(1))}-{m.group(2)}'
            if key.startswith(f'P{st}-'):
                all_ids.setdefault(key, i)

    nums = sorted(int(k.split('-')[1]) for k in all_ids)
    missing_nums = [n for n in range(1, 106) if n not in nums]

    # ④ 矩陣缺的 ID：在檔內其他位置出現的行號
    absent_from_matrix = sorted((k for k in all_ids if k not in matrix_set),
                               key=lambda k: int(k.split('-')[1]))
    elsewhere = {k: all_ids[k] for k in absent_from_matrix}
    nowhere = [k for k in (f'P{st}-{n:03d}' for n in missing_nums) if k not in all_ids]

    rows_total += len(matrix_set)

    print(f'--- STAGE-{st:02d}  {os.path.basename(path)}')
    print(f'    檔頭宣稱 {claim} 項 | 矩陣列 {len(matrix_set)} 列 | 檔內互異 ID {len(all_ids)} 個'
          f' | 缺號 {("無" if not missing_nums else missing_nums)}')
    if absent_from_matrix:
        det = '、'.join(f'{k}@{elsewhere[k]}行' for k in absent_from_matrix)
        print(f'    在檔內但不在矩陣：{det}')
    if nowhere:
        print(f'    ⚠ 整份檔案都找不到：{nowhere}')
    if claim is not None and claim != len(matrix_set):
        problems.append(f'STAGE-{st:02d}: 檔頭 {claim} vs 矩陣 {len(matrix_set)}')

print()
print(f'階段檔矩陣列合計：{rows_total}（十階段 × 105 ＝ 1050 才是計畫宣稱的形狀）')
print(f'檔頭與矩陣不符的階段：{problems if problems else "無"}')
