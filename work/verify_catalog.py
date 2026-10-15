# -*- coding: utf-8 -*-
"""驗證 FactKeyCatalog 的鍵集：數量、排序、唯一、以及新鍵都在。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
t = io.open('Services/FactKeyCatalog.cs', encoding='utf-8').read()
keys = [m.group(1) for m in re.finditer(r'^\s*"([a-z0-9_.]+)",\s*$', t, re.M)]

print('鍵數：', len(keys))
print('已排序：', keys == sorted(keys))
print('無重複：', len(set(keys)) == len(keys))

need = ['off.mount.kind', 'scan.restore.ok', 'src.available',
        'src.capability', 'src.chain', 'src.registry.count']
missing = [k for k in need if k not in keys]
print('新鍵齊備：', not missing, '' if not missing else missing)
