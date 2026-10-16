# -*- coding: utf-8 -*-
"""掃描解碼器裡的「同前綴 ≥3 個十六進位常數」群——**包含同一行多個變數**。"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# 先抓整條 const 宣告（可能橫跨多行、可能一行多個變數），再從裡面抽「名字 = 0x值」。
decl = re.compile(r'public const \w+ ([^;]+);', re.S)
assign = re.compile(r'(\w+)\s*=\s*(0x[0-9A-Fa-f]+)')

total = 0
for fn in sorted(os.listdir('XinSpect.Decoders')):
    if not fn.endswith('.cs'):
        continue
    t = io.open(os.path.join('XinSpect.Decoders', fn), encoding='utf-8').read()
    groups = {}
    for d in decl.finditer(t):
        for m in assign.finditer(d.group(1)):
            pre = re.sub(r'\d+$', '', m.group(1))
            if pre:
                groups.setdefault(pre, []).append(m.group(1))
    tables = {k: v for k, v in groups.items() if len(v) >= 3}
    if tables:
        total += 1
        has = bool(re.search(r'spec-ref-policy|收錄政策', t))
        print(f'{fn}: {len(tables)} 組  政策={has}')
        for k, v in tables.items():
            print(f'    {k}* ({len(v)}): {", ".join(v)}')
print(f'--- 有常數表的檔案：{total} 個 ---')
