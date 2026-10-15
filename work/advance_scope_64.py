# -*- coding: utf-8 -*-
"""把已開工範圍推進到 P1-064（守門方法論區完成）。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
q = 'Tests/UltimateKeyReconciliationTests.cs'
t = io.open(q, encoding='utf-8').read()

old_range = '        ("P1-001", "P1-050"),'
new_range = '        ("P1-001", "P1-064"),'
old_nyi = '("P1-051", "掃描守門模式與完整性網——階段一後半段，尚未開工")'
new_nyi = '("P1-065", "寫入閘門與審計鏈——階段一後半段，尚未開工")'

changed = False
if old_range in t:
    t = t.replace(old_range, new_range)
    changed = True
if old_nyi in t:
    t = t.replace(old_nyi, new_nyi)
    changed = True

if changed:
    io.open(q, 'w', encoding='utf-8', newline='').write(t)
    print('已開工範圍推進到 P1-001–P1-064')
else:
    print('!! 找不到目標字串')
