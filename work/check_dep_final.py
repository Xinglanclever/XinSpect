# -*- coding: utf-8 -*-
"""把所有「依賴欄的最終狀態」列出來，供人工複核。

稽核工具的計數只能證明「沒有一格是空的、沒有懸空 ID、沒有未標示的前向引用」，
不能證明「這一條前置在語意上真的是前置」。這份清單就是給人看的那一步。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'

pure_ref = 0
both = 0
local_only = 0
for st in range(1, 11):
    path = [f'{DIR}/{n}' for n in os.listdir(DIR) if n.startswith(f'STAGE-{st:02d}-')][0]
    for line in io.open(path, encoding='utf-8').read().split('\n'):
        t = line.strip()
        if not t.startswith('|'):
            continue
        c = [x.strip() for x in t.strip('|').split('|')]
        if len(c) < 7 or '---' in t:
            continue
        ident = c[0].lstrip('★')
        if not ident.startswith(f'P{st}-') or len(ident) != len(f'P{st}-') + 3:
            continue
        dep = c[6]
        has_ref = '參照' in dep
        has_local = bool(re.search(rf'P{st}-\d{{3}}', dep))
        if has_ref and not has_local:
            pure_ref += 1
            print(f'!! 只有參照沒有階段內前置：{ident} -> {dep}')
        elif has_ref:
            both += 1
        else:
            local_only += 1

print(f'統計：只有階段內前置 {local_only} 列、前置＋參照 {both} 列、只有參照 {pure_ref} 列')
