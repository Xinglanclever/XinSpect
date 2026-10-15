# -*- coding: utf-8 -*-
"""修掉豁免表裡一個多餘的 `};`。

add_late_exemptions.py 的插入位置算錯了：它把「最後一條豁免的結尾」算成
包含 `};` 的那一行之前，於是插到了 `};` 的後面——表格已經關了，
後面又出現 `["..."] = "...",`，那是語法錯誤。

修法：把插在 `};` 之後的那一整段搬到 `};` 之前。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/FactCoverageReport.cs'
lines = io.open(p, encoding='utf-8').read().split('\n')

# 找出錯位的 `};`（第 656 行，1-based）
bad_idx = None
for i, ln in enumerate(lines):
    if ln.rstrip().endswith('};') and 'file.input.zero_egress' in ln:
        bad_idx = i
        break

if bad_idx is None:
    print('!! 找不到錯位的那一行')
    sys.exit(1)

# 該行尾的 `};` 要留著，但後面的「後半批」整段要搬到它前面
head = lines[bad_idx].rstrip()
assert head.endswith('};')
head = head[:-2].rstrip()          # 去掉 };，只留最後一條豁免

# 找結尾：下一個單獨的 `};`
tail_idx = None
for j in range(bad_idx + 1, len(lines)):
    if lines[j].strip() == '};':
        tail_idx = j
        break
assert tail_idx is not None, '找不到收尾的 };'

before = lines[:bad_idx]
late = lines[bad_idx + 1:tail_idx]
after = lines[tail_idx + 1:]

new = before + [head, ''] + late + ['        };'] + after
io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(new))
print(f'已把 {len(late)} 行搬到收尾大括號之前')
