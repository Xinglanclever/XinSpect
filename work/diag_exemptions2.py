# -*- coding: utf-8 -*-
"""診斷：印出豁免表裡「不符門檻」的條目。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

e = io.open('Services/FactCoverageReport.cs', encoding='utf-8').read()
s = e.index('Exemptions { get; } =')
# 找對應的收尾：從 s 往後數大括號
depth = 0
i = e.index('{', s)
j = i
while j < len(e):
    if e[j] == '{':
        depth += 1
    elif e[j] == '}':
        depth -= 1
        if depth == 0:
            break
    j += 1
block = e[i:j]

entries = re.findall(r'\["([^"]+)"\]\s*=\s*\n?\s*((?:"[^"]*"\s*\+?\s*)+)', block)
print(f'找到 {len(entries)} 條豁免')
allowed = ['無法交叉', '沒有第二', '單一來源', '同一份資料', '自己對自己', '無從量測']
pending = ['待辦', '未寫', '尚未', '先放', '不重要', 'TODO', '之後再']
n_bad = 0
for key, raw in entries:
    reason = ' '.join(re.findall(r'"([^"]*)"', raw))
    if any(p.lower() in reason.lower() for p in pending):
        print(f'  [待辦] {key}：{reason[:70]}')
        n_bad += 1
    elif not any(a in reason for a in allowed):
        print(f'  [其他] {key}：{reason[:70]}')
        n_bad += 1
print(f'不合門檻 {n_bad} 條')
