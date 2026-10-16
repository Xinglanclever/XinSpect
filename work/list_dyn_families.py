# -*- coding: utf-8 -*-
"""列出動態鍵家族，並確認 uefi.section.tree 該怎麼登記。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

p = r'C:\Users\Administrator\XinSpect\Services\FactKeyDynamicCatalog.cs'
t = io.open(p, encoding='utf-8').read()
fams = re.findall(r'^\s*\["([^"]+)"\]', t, re.M)
print(f'{len(fams)} 個家族：')
for f in fams:
    print(f'  {f}')
print()
print('含 fv 或 uefi 的：', [f for f in fams if 'fv' in f or 'uefi' in f])
print('家族數上限：23（FactKeyCatalogTests 的 FamilyCap）')
