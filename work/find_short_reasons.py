# -*- coding: utf-8 -*-
"""找出豁免表裡「理由太短」的條目（門檻 20 字）。

用 C# 端的 ExemptionsWithoutReasons() 才是權威，但這裡先用 Python 掃出候選，
再看 C# 的判斷有沒有別的原因（待補／TODO）。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
t = io.open('Services/FactCoverageReport.cs', encoding='utf-8').read()
i = t.index('Exemptions { get; } =')
j = t.index('\n        };', i)
block = t[i:j]

# 逐行解析：一條豁免可能是多行續接
entries = []
cur_key = None
cur_parts = []
for line in block.split('\n'):
    m = re.match(r'\s*\["([^"]+)"\]\s*=\s*(.*)$', line)
    if m:
        if cur_key is not None:
            entries.append((cur_key, ' '.join(cur_parts)))
        cur_key = m.group(1)
        cur_parts = re.findall(r'"([^"]*)"', m.group(2))
    elif cur_key is not None:
        cur_parts += re.findall(r'"([^"]*)"', line)
if cur_key is not None:
    entries.append((cur_key, ' '.join(cur_parts)))

print(f'共 {len(entries)} 條豁免')
short = [(k, r) for k, r in entries if len(r.strip()) < 20 or '待補' in r or 'TODO' in r.upper()]
print(f'理由太短或含待補的：{len(short)} 條')
for k, r in short:
    print(f'  {k}（{len(r.strip())} 字）：{r.strip()}')
