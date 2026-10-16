# -*- coding: utf-8 -*-
"""平行實作掃描：找出「同一把鍵有多個產生者」的情況。

這支腳本回答一個問題：**有沒有一個事實鍵被兩個以上的地方產生？**
那通常代表我在已有答案的地方重新發明了一遍——
就像 P2-052 的 FIT 表（SpiFitFactsService 已經有了）。

同時也列出「語意相近的鍵對」，因為鍵名不同但語意重疊
是更隱蔽的平行實作。
"""
import io
import os
import re
import sys
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8')

ROOT = r'C:\Users\Administrator\XinSpect'

# ① 掃出所有「產生事實鍵」的地方：new HardwareFact("key", ...) 與常數宣告
producers = defaultdict(list)

key_literal = re.compile(r'new\s+HardwareFact\(\s*"([a-z0-9_.]+)"')
key_const = re.compile(r'public\s+const\s+string\s+\w*Key\w*\s*=\s*"([a-z0-9_.]+)"')
key_arg = re.compile(r'new\s+HardwareFact\(\s*([A-Za-z_]\w*)\s*,')

for dirpath in ('Services', 'XinSpect.Decoders'):
    full = os.path.join(ROOT, dirpath)
    if not os.path.isdir(full):
        continue
    for fn in os.listdir(full):
        if not fn.endswith('.cs'):
            continue
        path = os.path.join(full, fn)
        text = io.open(path, encoding='utf-8', errors='replace').read()
        for m in key_literal.finditer(text):
            producers[m.group(1)].append(f'{dirpath}/{fn}')
        # 常數宣告：把常數名 → 鍵值記下來，供後面解析 new HardwareFact(ConstName, ...)
        consts = {m2.group(0).split()[-3]: m2.group(1) for m2 in
                  [re.match(r'.*?const\s+string\s+(\w+)', line) for line in text.split('\n')]
                  if m2}

print('=== 同一把鍵有多個產生者 ===')
dup = {k: v for k, v in producers.items() if len(set(v)) > 1}
if dup:
    for k, v in sorted(dup.items()):
        print(f'  {k}')
        for p in sorted(set(v)):
            print(f'      {p}')
else:
    print('  （無）')

# ② 語意相近的鍵對：前綴相同但最後一段不同，且分屬不同檔案
print()
print('=== 語意相近但分屬不同檔案的鍵（前綴相同的家族） ===')
families = defaultdict(lambda: defaultdict(set))
for k, files in producers.items():
    if '.' not in k:
        continue
    prefix = k.rsplit('.', 1)[0]
    for f in set(files):
        families[prefix][f].add(k)

suspicious = 0
for prefix, byfile in sorted(families.items()):
    if len(byfile) > 1:
        suspicious += 1
        print(f'  {prefix}.*')
        for f, keys in sorted(byfile.items()):
            print(f'      {f}：{", ".join(sorted(keys))}')
if suspicious == 0:
    print('  （無）')

print()
print(f'=== 統計 ===')
print(f'  鍵總數（有明確字面值的）：{len(producers)}')
print(f'  多產生者的鍵：{len(dup)}')
print(f'  跨檔案共享前綴的家族：{suspicious}')
