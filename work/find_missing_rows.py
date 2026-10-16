# -*- coding: utf-8 -*-
"""找出階段二矩陣裡「被我的解析漏掉」的列。

我的 `StartsWith("| P2-")` 會漏掉任何格式不同的列。
README:98 說這個失效模式是「認不出來（宣告變成 95 而非 105），而矩陣列數一直數對」
—— 所以我要把所有含 P2- 的行都列出來，看哪幾行的開頭不是 `| P2-`。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
t = io.open(p, encoding='utf-8').read()
lines = t.split('\n')

# ① 所有含 P2-\d\d\d 的行
pat = re.compile(r'P2-(\d{3})')
seen = {}
for i, l in enumerate(lines):
    for m in pat.finditer(l):
        seen.setdefault(m.group(1), []).append(i)

nums = sorted(seen.keys())
print(f'檔案裡出現過的 P2 編號：{len(nums)} 個')
print(f'範圍：{nums[0]} – {nums[-1]}')

# ② 找出缺號
missing = [f'{n:03d}' for n in range(1, 106) if f'{n:03d}' not in seen]
print(f'缺號：{len(missing)} 個 → {missing}')

# ③ 對每個編號，看它在哪些行、那些行的開頭長什麼樣
print('\n=== 每個編號首次出現的行，以及該行前 30 字元 ===')
for n in [f'{i:03d}' for i in range(1, 106)]:
    if n not in seen:
        print(f'  P2-{n}  ✗ 完全沒出現')
        continue
    i = seen[n][0]
    head = lines[i][:30]
    is_row = lines[i].startswith('| P2-')
    if not is_row:
        print(f'  P2-{n}  ⚠ 行 {i} 開頭不是 | P2- → {head!r}')

# ④ 統計「哪幾行的開頭不是 | P2- 但含 P2-nnn」
print('\n=== 所有含 P2-nnn 但開頭不是 | P2- 的行 ===')
for i, l in enumerate(lines):
    if pat.search(l) and not l.startswith('| P2-'):
        print(f'  {i}: {l[:150]!r}')
