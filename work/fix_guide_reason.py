# -*- coding: utf-8 -*-
"""把 contrib.guide 的豁免理由補長（原本 17 字，門檻 20 字）。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/FactCoverageReport.cs'
t = io.open(p, encoding='utf-8').read()

old = '["contrib.guide"] = "貢獻者指南：文件內容不是機器事實。",'
new = ('["contrib.guide"] = "貢獻者指南：文件內容不是機器事實，沒有第二個來源可以交叉——"\n'
       '                              + "它的正確性由「照著做能不能跑起來」驗證，那要人工執行。",')

if old in t:
    t = t.replace(old, new)
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print('已補長理由')
else:
    print('!! 找不到')
