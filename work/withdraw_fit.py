# -*- coding: utf-8 -*-
"""把 FIT 的兩把鍵從目錄移除（P2-052 退回未開工）。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/FactKeyCatalog.cs'
t = io.open(p, encoding='utf-8').read()

removed = 0
for k in ('"fit.present",', '"fit.count",'):
    if k in t:
        t = t.replace('        ' + k + '\n', '')
        removed += 1
        print(f'移除 {k}')

m = re.search(r'共 (\d+) 個（', t)
if m and removed:
    t = t[:m.start()] + f'共 {int(m.group(1)) - removed} 個（' + t[m.end():]
    print(f'目錄：{m.group(1)} → {int(m.group(1)) - removed} 鍵')

io.open(p, 'w', encoding='utf-8', newline='').write(t)
