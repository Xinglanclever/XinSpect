# -*- coding: utf-8 -*-
"""把 P1-075／P1-076／P1-083 三把鍵補進目錄。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
NEW = ['audit.state_change', 'audit.export', 'coverage.threeway.diff']
pattern = re.compile(r'^(\s*)"([a-z0-9_.]+)",\s*$', re.M)
matches = list(pattern.finditer(text))
keys = [m.group(2) for m in matches]
merged = sorted(set(keys) | set(NEW))
block = '\n'.join(f'        "{k}",' for k in merged)
text = text[:matches[0].start()] + block + text[matches[-1].end():]
text = re.sub(r'共 \d+ 個（', f'共 {len(merged)} 個（', text)
io.open(p, 'w', encoding='utf-8', newline='').write(text)
print(f'目錄：{len(keys)} → {len(merged)} 鍵')
