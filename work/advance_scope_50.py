# -*- coding: utf-8 -*-
"""把已開工範圍推進到 P1-050，並列出新出現的待辦鍵。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
q = 'Tests/UltimateKeyReconciliationTests.cs'
t = io.open(q, encoding='utf-8').read()
if '        ("P1-001", "P1-040"),' in t:
    t = t.replace('        ("P1-001", "P1-040"),', '        ("P1-001", "P1-050"),')
    t = t.replace('("P1-041", "知識表內容與敏感度——階段一後半段，尚未開工")',
                  '("P1-051", "掃描守門模式與完整性網——階段一後半段，尚未開工")')
    io.open(q, 'w', encoding='utf-8', newline='').write(t)
    print('已開工範圍推進到 P1-001–P1-050')
else:
    print('!! 找不到已開工範圍')
