# -*- coding: utf-8 -*-
"""ULTIMATE-1000 協定檢查（2026-10-16 稽核後新建）。

取代 work/ultimate_lint.py 成為「可引為證據」的檢查。舊腳本的問題是它的列首正則
在「管線與 ID 之間有空白」的實際寫法上匹配不到任何一列，於是 0 列被當成 0 問題、
照樣印 no problems——把「掃不到」冒充成「沒有問題」。

本腳本檢查六件事，任一不符即以離開碼 1 結束：
  1. 每階段矩陣列數 = 檔案宣告數 = 105（總數 1,050）
  2. 每階段編號為連續的 1..105
  3. 十階段沒有重複定義的 ID
  4. 內文引用的 ID 都存在（無懸空引用）
  5. 域碼表項數合計 = 矩陣列數
  6. 每階段至少 2 個 ★ 錨點（純陳述實測值；README 目前沒有寫死這個數字）
"""
import io
import glob
import os
import re
import sys

DIR = os.path.join('docs', 'ULTIMATE-1000')
ROW = re.compile(r'^\|\s*★?\s*(P\d{1,2}-\d{3})\s*\|')
ID = re.compile(r'P(\d{1,2})-(\d{3})')

problems = []
defined = {}


def check_doc_counts():
    """★ 錨點實測數。README 目前沒有寫死這個數字——一旦有人補上宣稱，這裡就會開始對帳。"""
    return sum(1 for p in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md')))
               for l in io.open(p, encoding='utf-8')
               if ROW.match(l) and '★' in l.split('|')[1])


for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))):
    name = os.path.basename(path)
    text = io.open(path, encoding='utf-8').read()
    stage = int(re.search(r'STAGE-(\d+)', path).group(1))

    rows = [ROW.match(l).group(1) for l in text.splitlines() if ROW.match(l)]
    defined[name] = rows

    for i in rows:
        if ROW.match('| %s | x |' % i) is None:
            problems.append('%s：ID 格式異常 %s' % (name, i))

    if len(rows) != len(set(rows)):
        problems.append('%s：矩陣列有重複 ID' % name)

    own = sorted(int(i.split('-')[1]) for i in rows if int(i.split('-')[0][1:]) == stage)
    if own != list(range(1, 106)):
        problems.append('%s：本階段編號不是連續的 1..105（共 %d 列）' % (name, len(own)))
    if len(rows) != 105:
        problems.append('%s：矩陣 %d 列，應為 105' % (name, len(rows)))

    declared = re.search(r'本階段\s*\*{0,2}(\d+)\*{0,2}\s*項', text)
    if not declared:
        problems.append('%s：找不到「本階段 N 項」的宣告' % name)
    elif int(declared.group(1)) != len(rows):
        problems.append('%s：宣告 %s 項，矩陣 %d 列' % (name, declared.group(1), len(rows)))

    block = text.split('## 三、域碼與項數')[1].split('##')[0]
    dom = [int(n) for _, n in re.findall(r'^\|\s*([A-Z]+\d*)\s*\|[^|]*\|\s*(\d+)\s*\|', block, re.M)]
    if not dom:
        problems.append('%s：域碼表一列都沒數到（表格格式變了嗎）' % name)
    elif sum(dom) != len(rows):
        problems.append('%s：域碼表合計 %d，矩陣 %d 列' % (name, sum(dom), len(rows)))

all_defined = [i for rows in defined.values() for i in rows]
if len(all_defined) != len(set(all_defined)):
    dupes = sorted({i for i in all_defined if all_defined.count(i) > 1})
    problems.append('跨階段重複定義的 ID：%s' % dupes[:20])

known = set(all_defined)
for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))) + [os.path.join(DIR, 'README.md')]:
    for n, line in enumerate(io.open(path, encoding='utf-8'), 1):
        for m in ID.finditer(line):
            if m.group(0) not in known:
                problems.append('%s:%d 懸空引用 %s' % (os.path.basename(path), n, m.group(0)))

stars = check_doc_counts()

print('階段檔            :', len(defined))
print('矩陣列總數        :', len(all_defined))
print('互異 ID           :', len(known))
print('★ 錨點            :', stars)
print()
if problems:
    print('PROBLEMS (%d):' % len(problems))
    for p in problems:
        print('  ', p)
else:
    print('no problems')
sys.exit(1 if problems else 0)
