# -*- coding: utf-8 -*-
"""把寫入閘門與審計鏈的九把鍵補進目錄，並把已開工範圍推進到 P1-074。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
NEW = ['guard.inventory', 'write.count', 'write.ledger', 'write.allowlist', 'write.rollback',
       'audit.head', 'audit.verify', 'ev.manifest', 'ev.chain.verify']
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
old_range = '        ("P1-001", "P1-064"),'
new_range = '        ("P1-001", "P1-074"),'
old_nyi = '("P1-065", "寫入閘門與審計鏈——階段一後半段，尚未開工")'
new_nyi = '("P1-075", "深測與證據鏈——階段一後半段，尚未開工")'
changed = False
if old_range in t:
    t = t.replace(old_range, new_range); changed = True
if old_nyi in t:
    t = t.replace(old_nyi, new_nyi); changed = True
if changed:
    io.open(q, 'w', encoding='utf-8', newline='').write(t)
    print('已開工範圍推進到 P1-001–P1-074')
else:
    print('!! 找不到目標字串')
