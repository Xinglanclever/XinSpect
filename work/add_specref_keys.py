# -*- coding: utf-8 -*-
"""把出處鏈的六把鍵補進 FactKeyCatalog，並把已開工範圍推進到 P1-032。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# 1) 目錄
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
NEW = [
    'spec.ref', 'spec.ref.granularity', 'spec.ref.coverage',
    'spec.ref.version', 'spec.ref.unit', 'fact.provenance',
]
pattern = re.compile(r'^(\s*)"([a-z0-9_.]+)",\s*$', re.M)
matches = list(pattern.finditer(text))
keys = [m.group(2) for m in matches]
missing = [k for k in NEW if k not in keys]
merged = sorted(set(keys) | set(NEW))
block = '\n'.join(f'        "{k}",' for k in merged)
text = text[:matches[0].start()] + block + text[matches[-1].end():]
text = re.sub(r'共 \d+ 個（', f'共 {len(merged)} 個（', text)
io.open(p, 'w', encoding='utf-8', newline='').write(text)
print(f'目錄：{len(keys)} → {len(merged)} 鍵（新增 {missing}）')

# 2) 已開工範圍
q = 'Tests/UltimateKeyReconciliationTests.cs'
t = io.open(q, encoding='utf-8').read()
old = '        ("P1-001", "P1-022"),'
new = '        ("P1-001", "P1-032"),'
if old not in t:
    print('!! 找不到已開工範圍那一行')
else:
    t = t.replace(old, new)
    t = t.replace('("P1-024", "規格引用註記、出處鏈與知識包——階段一後半段，尚未開工")',
                  '("P1-033", "知識包框架與匿名化——階段一後半段，尚未開工")')
    io.open(q, 'w', encoding='utf-8', newline='').write(t)
    print('已開工範圍推進到 P1-001–P1-032')
