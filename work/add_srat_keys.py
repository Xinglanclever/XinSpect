# -*- coding: utf-8 -*-
"""把 SRAT 登記進覆蓋申報，並補七把 ac.srat.* 鍵進目錄。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# ① DecodedSignatures 加 SRAT
p = 'XinSpect.Decoders/AcpiDeep.cs'
t = io.open(p, encoding='utf-8').read()
old = '''        // ULTIMATE-1000 階段二：P2-006 中斷控制器與處理器清單（AcpiMadt）、
        // P2-010 IOMMU 範圍與例外（AcpiDmar）。
        "MADT", "DMAR",'''
new = '''        // ULTIMATE-1000 階段二：P2-006 中斷控制器與處理器清單（AcpiMadt）、
        // P2-010 IOMMU 範圍與例外（AcpiDmar）、P2-007 資源親和（AcpiSrat）。
        "MADT", "DMAR", "SRAT",'''
if old in t:
    io.open(p, 'w', encoding='utf-8', newline='').write(t.replace(old, new))
    print('已把 SRAT 登記進覆蓋申報')
else:
    print('!! DecodedSignatures 找不到目標')

# ② 目錄加鍵
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
NEW = ['ac.srat.summary', 'ac.srat.proc', 'ac.srat.x2apic', 'ac.srat.mem',
       'ac.srat.generic', 'ac.srat.domain', 'ac.srat.unrecorded']
pattern = re.compile(r'^(\s*)"([a-z0-9_.]+)",\s*$', re.M)
matches = list(pattern.finditer(text))
keys = [m.group(2) for m in matches]
merged = sorted(set(keys) | set(NEW))
block = '\n'.join(f'        "{k}",' for k in merged)
text = text[:matches[0].start()] + block + text[matches[-1].end():]
text = re.sub(r'共 \d+ 個（', f'共 {len(merged)} 個（', text)
io.open(p, 'w', encoding='utf-8', newline='').write(text)
print(f'目錄：{len(keys)} → {len(merged)} 鍵')
