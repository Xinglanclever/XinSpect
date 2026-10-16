# -*- coding: utf-8 -*-
"""盤點階段二 P2-019 – P2-025（SMBIOS 七項）的實作覆蓋。"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

ITEM_KEYS = {
    'P2-019 SMBIOS Type 0-3': ['sb.type0', 'sb.type1', 'sb.type2', 'sb.type3'],
    'P2-020 SMBIOS Type 4/7': ['sb.type4', 'sb.type7'],
    'P2-021 SMBIOS Type 9 插槽': ['sb.slot'],
    'P2-022 SMBIOS Type 11 OEM': ['sb.oem.strings'],
    'P2-023 SMBIOS Type 16/17 記憶體': ['sb.mem'],
    'P2-024 SMBIOS Type 19 映射': ['sb.memmap'],
    'P2-025 SMBIOS Type 32 開機': ['sb.boot'],
}

# 目錄裡的鍵
cat = io.open('Services/FactKeyCatalog.cs', encoding='utf-8').read()
catalog = set(re.findall(r'^\s*"([a-z0-9_.]+)",\s*$', cat, re.M))

# 全部原始碼裡出現的鍵
sources = {}
for root in ('Services', 'XinSpect.Decoders'):
    for d, _, files in os.walk(root):
        if 'obj' in d or 'bin' in d:
            continue
        for f in files:
            if f.endswith('.cs'):
                sources[os.path.join(d, f)] = io.open(os.path.join(d, f), encoding='utf-8').read()

print('=== 階段二 P2-019 – P2-025（SMBIOS）覆蓋盤點 ===\n')
for item, prefixes in ITEM_KEYS.items():
    in_catalog = sorted(k for k in catalog if any(k.startswith(p) for p in prefixes))
    producers = []
    for prefix in prefixes:
        for path, text in sources.items():
            if re.search(r'"' + re.escape(prefix), text):
                producers.append(os.path.basename(path))
                break
    producers = sorted(set(producers))

    verdict = '✅ 有鍵有生產者' if in_catalog and producers else \
              '⚠️ 有生產者但目錄缺鍵' if producers else \
              '❌ 完全空白'
    print(f'{item}')
    print(f'  目錄鍵：{len(in_catalog)} 把' + (f'（{", ".join(in_catalog[:4])}{"…" if len(in_catalog) > 4 else ""}）' if in_catalog else ''))
    print(f'  生產者：{", ".join(producers) if producers else "（無）"}')
    print(f'  → {verdict}\n')

# 所有 sb.* 鍵的總數與前綴分布
sb = sorted(k for k in catalog if k.startswith('sb.'))
print(f'=== 目錄裡全部 sb.* 鍵：{len(sb)} 把 ===')
from collections import Counter
prefixes = Counter('.'.join(k.split('.')[:2]) for k in sb)
for p, n in sorted(prefixes.items()):
    print(f'  {p} : {n}')
