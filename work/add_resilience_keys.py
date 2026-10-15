# -*- coding: utf-8 -*-
"""把韌性與降級八把鍵補進目錄，並把已開工範圍推進到 P1-095。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
NEW = ['key.evolution', 'coverage.gap', 'diag.count', 'src.circuit',
       'degrade.mode', 'fact.error.reason', 'src.retry', 'scan.failures']
pattern = re.compile(r'^(\s*)"([a-z0-9_.]+)",\s*$', re.M)
matches = list(pattern.finditer(text))
keys = [m.group(2) for m in matches]
merged = sorted(set(keys) | set(NEW))
block = '\n'.join(f'        "{k}",' for k in merged)
text = text[:matches[0].start()] + block + text[matches[-1].end():]
text = re.sub(r'共 \d+ 個（', f'共 {len(merged)} 個（', text)
io.open(p, 'w', encoding='utf-8', newline='').write(text)
print(f'目錄：{len(keys)} → {len(merged)} 鍵')

q = 'Tests/UltimateKeyReconciliationTests.cs'
t = io.open(q, encoding='utf-8').read()
if '        ("P1-001", "P1-084"),' in t:
    t = t.replace('        ("P1-001", "P1-084"),', '        ("P1-001", "P1-095"),')
    t = t.replace('("P1-085", "效能預算與資源上限——階段一後半段，尚未開工")',
                  '("P1-096", "發佈與建置——階段一收尾，尚未開工")')
    io.open(q, 'w', encoding='utf-8', newline='').write(t)
    print('已開工範圍推進到 P1-001–P1-095')
else:
    print('!! 找不到已開工範圍')
