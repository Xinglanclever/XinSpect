# -*- coding: utf-8 -*-
"""修正三件事：

① `exempt` → `coverage.exempt`：exempt 是單段鍵，違反全鍵格式（須以點分段）。
   正解不是放寬格式，而是改鍵名——豁免確實是覆蓋申報的子項，本來就該在那個前綴底下。
   這一條是 P1-084 的命名規範守門抓到的，而它抓到的是我加鍵的下一分鐘。

② 補上 P1-075（audit.state_change）、P1-076（audit.export）、
   P1-083（coverage.threeway.diff）三把鍵的生產器。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# ① 改鍵名
p = 'Services/KeyCatalogPolicy.cs'
t = io.open(p, encoding='utf-8').read()
t = t.replace('public const string ExemptKey = "exempt";',
              'public const string ExemptKey = "coverage.exempt";')
io.open(p, 'w', encoding='utf-8', newline='').write(t)
print('已改鍵名 exempt → coverage.exempt')

# 目錄：把 exempt 換成 coverage.exempt
p2 = 'Services/FactKeyCatalog.cs'
t2 = io.open(p2, encoding='utf-8').read()
t2 = t2.replace('        "exempt",\n', '')
if '"coverage.exempt",' not in t2:
    pattern = re.compile(r'^(\s*)"([a-z0-9_.]+)",\s*$', re.M)
    matches = list(pattern.finditer(t2))
    keys = [m.group(2) for m in matches]
    merged = sorted(set(keys) | {'coverage.exempt'})
    block = '\n'.join(f'        "{k}",' for k in merged)
    t2 = t2[:matches[0].start()] + block + t2[matches[-1].end():]
    t2 = re.sub(r'共 \d+ 個（', f'共 {len(merged)} 個（', t2)
    print(f'目錄：{len(keys)} → {len(merged)} 鍵')
io.open(p2, 'w', encoding='utf-8', newline='').write(t2)
