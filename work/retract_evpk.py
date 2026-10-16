# -*- coding: utf-8 -*-
"""撤回 ev.pk.count（簽章清單那一半已經有實作）。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
before = len(re.findall(r'^\s*"[a-z0-9_.]+",\s*$', text, re.M))
text = text.replace('        "ev.pk.count",\n', '')
after = len(re.findall(r'^\s*"[a-z0-9_.]+",\s*$', text, re.M))
text = re.sub(r'共 \d+ 個（', f'共 {after} 個（', text)
io.open(p, 'w', encoding='utf-8', newline='').write(text)
print(f'目錄：{before} → {after} 鍵（移除 ev.pk.count）')
