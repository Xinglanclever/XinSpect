# -*- coding: utf-8 -*-
"""診斷：印出動態家族與豁免表裡「不符門檻」的條目。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# 1) 動態家族：哪些沒寫出「由什麼決定」
t = io.open('Services/FactKeyDynamicCatalog.cs', encoding='utf-8').read()
start = t.index('Families { get; } =')
end = t.index('\n        };', start)
block = t[start:end]

entries = re.findall(r'\["([^"]+)"\]\s*=\s*\n?\s*((?:"[^"]*"\s*\+?\s*)+)', block)
print(f'找到 {len(entries)} 個家族')
markers = ['由', '成員', '決定']
for prefix, raw in entries:
    reason = ' '.join(re.findall(r'"([^"]*)"', raw))
    if not any(m in reason for m in markers):
        print(f'  X {prefix}：{reason[:70]}')

# 2) 豁免表：理由不是「無法交叉」那一類的
e = io.open('Services/FactCoverageReport.cs', encoding='utf-8').read()
s = e.index('Exemptions { get; } =')
en = e.index('\n        };', s)
block2 = e[s:en]
entries2 = re.findall(r'\["([^"]+)"\]\s*=\s*\n?\s*((?:"[^"]*"\s*\+?\s*)+)', block2)
print(f'\n找到 {len(entries2)} 條豁免')
allowed = ['無法交叉', '沒有第二', '單一來源', '同一份資料', '自己對自己', '無從量測']
pending = ['待辦', '未寫', '尚未', '先放', '不重要', 'TODO', '之後再']
for key, raw in entries2:
    reason = ' '.join(re.findall(r'"([^"]*)"', raw))
    if any(p.lower() in reason.lower() for p in pending):
        print(f'  [待辦] {key}：{reason[:60]}')
    elif not any(a in reason for a in allowed):
        print(f'  [其他] {key}：{reason[:60]}')
