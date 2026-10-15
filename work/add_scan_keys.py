# -*- coding: utf-8 -*-
"""把 6 把新鍵插進 FactKeyCatalog（維持全字串字典序），並更新成員數註解。

不用「字元類別 ＋ 量詞」以外的花招；鍵的形狀固定，逐行字面解析。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()

NEW = [
    'off.mount.kind',
    'scan.restore.ok',
    'src.available',
    'src.capability',
    'src.chain',
    'src.registry.count',
]

# 抓出所有鍵（一行一個，格式：縮排 + "key",）
pattern = re.compile(r'^(\s*)"([a-z0-9_.]+)",\s*$', re.M)
keys = [m.group(2) for m in pattern.finditer(text)]
print(f'現有 {len(keys)} 鍵')

merged = sorted(set(keys) | set(NEW))
print(f'合併後 {len(merged)} 鍵（新增 {len(merged) - len(keys)}）')

# 重建清單區塊：從第一個鍵到最後一個鍵
first = pattern.search(text)
last = list(pattern.finditer(text))[-1]
block = '\n'.join(f'        "{k}",' for k in merged)
text = text[:first.start()] + block + text[last.end():]

# 更新註解裡的成員數
text = re.sub(r'共 \d+ 個（', f'共 {len(merged)} 個（', text)

io.open(p, 'w', encoding='utf-8', newline='').write(text)
print('已寫回')
