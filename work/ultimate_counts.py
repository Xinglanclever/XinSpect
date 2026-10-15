# -*- coding: utf-8 -*-
"""ULTIMATE-1000 的項數對帳（2026-10-16 稽核的 P0）。

為什麼要這支：前一輪稽核以 30 個 agent 對十份階段檔逐列數項，結論是
「階段 2 只數到 102、階段 3 與 7 只數到 103，所以 1,050 這個總數是錯的」。

實際以列首格式（`| Pn-nnn | ...`，不含 ★ 與空白變體）重數的結果是
**每階段都是 105 列、十階段共 1,050 個互異 ID**——與計畫宣稱的一致。
錯誤不在階段檔，在<b>數法</b>：只要正則或人工挑列時漏掉 ★ 錨點列、
或把「依賴欄引用了別的階段 ID」的行也算成項，就會少算。

這支腳本把兩種數法並列印出，讓「105 是怎麼來的」當場可驗，
而不是再留一句無法對帳的斷言。
"""
import io
import glob
import os
import re
import sys

DIR = os.path.join('docs', 'ULTIMATE-1000')
ID = re.compile(r'P(\d{1,2})-(\d{3})')

# 矩陣列的寬鬆寫法：管線後可有 ★ 與空白，ID 後接空白與管線
ROW_LOOSE = re.compile(r'^\s*\|\s*★?\s*(P(\d{1,2})-(\d{3}))\s*\|')
# 嚴格寫法（＝ work/ultimate_lint.py 用的那個，順便示範它為什麼匹配不到）
ROW_STRICT = re.compile(r'^\|\s*★?\s*(P\d{1,2}-\d{3})\s*\|')

print('%-26s %6s %6s %8s %10s' % ('file', 'loose', 'strict', 'declared', 'refs(別階段)'))
print('-' * 62)

problems = []
total_loose = 0
all_own = set()

for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))):
    name = os.path.basename(path)
    text = io.open(path, encoding='utf-8').read()
    stage = int(re.search(r'STAGE-(\d+)', path).group(1))

    loose, strict, refs = [], [], 0
    for line in text.splitlines():
        m = ROW_LOOSE.match(line)
        if m:
            loose.append(m.group(1))
            strict.append(m.group(1)) if ROW_STRICT.match(line) else None
            continue
        for r in ID.finditer(line):
            if int(r.group(1)) != stage:
                refs += 1
                break

    declared = re.search(r'本階段\s*\*{0,2}(\d+)\*{0,2}\s*項', text)
    dec = declared.group(1) if declared else '?'
    print('%-26s %6d %6d %8s %10d' % (name, len(loose), len(strict), dec, refs))

    total_loose += len(loose)
    own = {i for i in loose}
    all_own |= own

    if len(loose) != len(set(loose)):
        problems.append('%s：矩陣列有重複 ID' % name)
    if dec != '?' and int(dec) != len(loose):
        problems.append('%s：宣告 %s 項，實際 %d 列' % (name, dec, len(loose)))
    nums = sorted(int(i.split('-')[1]) for i in loose)
    if nums != list(range(1, 106)):
        problems.append('%s：編號不是連續的 1..105' % name)

print()
print('矩陣列總數        :', total_loose)
print('互異 ID（全部階段）:', len(all_own))
print()
if total_loose == 1050 and len(all_own) == 1050:
    print('對帳通過：十階段各 105 項，合計 1,050，且無跨階段重複的 ID。')
else:
    problems.append('總數對不上：列 %d、互異 ID %d' % (total_loose, len(all_own)))

if problems:
    print('PROBLEMS (%d):' % len(problems))
    for p in problems:
        print('  ', p)
sys.exit(1 if problems else 0)
