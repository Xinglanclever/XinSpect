# -*- coding: utf-8 -*-
"""ULTIMATE-1000 對帳（2026-10-16 稽核）。

work/ultimate_lint.py 的 ROW 正則要求列首是 `| P2-063 |`（管線後直接接 ID），
但十份階段檔的矩陣列格式是 `| P2-063 | 標題 | ... |`——中間隔著空白，
所以那個檢查其實<b>一列都沒匹配到</b>：stage row totals 是空的 {}、
total items 是 0、然後印出「no problems」。

一個把 0 列當成 0 問題的檢查，正是本專案最忌的那種「看起來成功的值」。
這支腳本不假設列格式，改為在檔案內文中掃描所有 P<stage>-<nnn>，
並把「每個階段應該有幾項」與實際數到的項數分開列出來。
"""
import io
import glob
import os
import re
import sys

DIR = os.path.join('docs', 'ULTIMATE-1000')
ID = re.compile(r'P(\d{1,2})-(\d{3})')
ROW = re.compile(r'^\|\s*★?\s*(P\d{1,2}-\d{3})\s*\|')

problems = []
stage_ids = {}

for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))):
    name = os.path.basename(path)
    text = io.open(path, encoding='utf-8').read()
    stage = int(re.search(r'STAGE-(\d+)', path).group(1))
    ids = sorted({m.group(0) for m in ID.finditer(text)})
    row_ids = sorted({m.group(1) for m in (ROW.match(l) for l in text.splitlines()) if m})
    stage_ids[stage] = set(ids)
    declared = re.search(r'本階段\s*\*{0,2}(\d+)\*{0,2}\s*項', text)
    print('%s  stage=%2d  distinct IDs=%3d  matrix rows=%3d  declared=%s'
          % (name, stage, len(ids), len(row_ids), declared.group(1) if declared else '?'))

all_ids = set()
for s in stage_ids.values():
    all_ids |= s

print()
print('distinct IDs all stages :', len(all_ids))

# 每個 ID 的重複定義（同一 ID 出現在多個階段檔）。
# 注意：階段檔會<b>引用</b>別的階段的 ID（依賴欄、Gate 條款），所以「出現」不等於「定義」。
# 定義＝矩陣列（| Pn-nnn | ...）。這裡只把矩陣列當成定義來查重。
seen = {}
for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))):
    for line in io.open(path, encoding='utf-8'):
        m = ROW.match(line)
        if m:
            seen.setdefault(m.group(1), []).append(os.path.basename(path))
dupes = {i: s for i, s in seen.items() if len(s) > 1}
if dupes:
    problems.append('同一 ID 有多個矩陣列：%s' % dupes)

# 編號連續性：每個階段應是 1..105 無缺
for stage in range(1, 11):
    ids = stage_ids.get(stage, set())
    own = sorted(int(i.split('-')[1]) for i in ids if int(i.split('-')[0][1:]) == stage)
    expected = list(range(1, 106))
    if own != expected:
        missing = sorted(set(expected) - set(own))[:20]
        extra = sorted(set(own) - set(expected))[:20]
        problems.append('階段 %d：編號不是 1..105（缺 %s；超出 %s）' % (stage, missing, extra))

# 跨階段依賴引用是否可解析
for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))):
    for n, line in enumerate(io.open(path, encoding='utf-8'), 1):
        for m in ID.finditer(line):
            if m.group(0) not in all_ids:
                problems.append('%s:%d 懸空引用 %s' % (os.path.basename(path), n, m.group(0)))

print()
if problems:
    print('PROBLEMS (%d):' % len(problems))
    for p in problems:
        print('  ', p)
else:
    print('no problems')
sys.exit(1 if problems else 0)
