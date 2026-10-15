# -*- coding: utf-8 -*-
"""把這一輪新產生的 8 把鍵補進 FactKeyCatalog，維持字典序。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()

NEW = [
    'scan.skeleton.adoption',
    'scan.step.timeout',
    'scan.partial.count',
    'file.input.zero_egress',
    'state.lattice',
    'state.count',
    'fact.readerror.reason',
    'fact.why_unreadable',
]

pattern = re.compile(r'^(\s*)"([a-z0-9_.]+)",\s*$', re.M)
matches = list(pattern.finditer(text))
keys = [m.group(2) for m in matches]
print(f'現有 {len(keys)} 鍵')

missing = [k for k in NEW if k not in keys]
print(f'缺 {len(missing)} 把：{missing}')

merged = sorted(set(keys) | set(NEW))
first, last = matches[0], matches[-1]
block = '\n'.join(f'        "{k}",' for k in merged)
text = text[:first.start()] + block + text[last.end():]
text = re.sub(r'共 \d+ 個（', f'共 {len(merged)} 個（', text)

io.open(p, 'w', encoding='utf-8', newline='').write(text)
print(f'已寫回，共 {len(merged)} 鍵')
