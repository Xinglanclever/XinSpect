# -*- coding: utf-8 -*-
"""把壓縮支援的兩把鍵補進目錄。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
NEW = ['uefi.comp.supported', 'uefi.comp.decompressed']
pat = re.compile(r'^(\s*)"([a-z0-9_.]+)",\s*$', re.M)
ms = list(pat.finditer(text))
keys = [m.group(2) for m in ms]
merged = sorted(set(keys) | set(NEW))
block = '\n'.join(f'        "{k}",' for k in merged)
text = text[:ms[0].start()] + block + text[ms[-1].end():]
text = re.sub(r'共 \d+ 個（', f'共 {len(merged)} 個（', text)
io.open(p, 'w', encoding='utf-8', newline='').write(text)
print(f'目錄：{len(keys)} → {len(merged)} 鍵')
