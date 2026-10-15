# -*- coding: utf-8 -*-
"""把「依賴欄裡的前向引用」改寫成「參照：Px-xxx」。

規則：依賴欄寫的是建置前置；指向更後面階段的引用不是前置，而是參照。
兩者混用是本計畫的措辭缺陷。修法是改措辭，不是砍掉那條連結——
連結本身有價值（可對帳），砍掉反而讓兩項之間的關係消失。

只動依賴欄（矩陣第 7 欄），其餘欄位一字不動。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'

changed = 0
for st in range(1, 11):
    path = [f'{DIR}/{n}' for n in os.listdir(DIR) if n.startswith(f'STAGE-{st:02d}-')][0]
    lines = io.open(path, encoding='utf-8').read().split('\n')
    out = []
    for line in lines:
        t = line.strip()
        if not t.startswith('|'):
            out.append(line)
            continue
        cells = [c.strip() for c in t.strip('|').split('|')]
        if len(cells) < 7 or '---' in t:
            out.append(line)
            continue
        ident = cells[0].lstrip('★')
        if not ident.startswith(f'P{st}-') or len(ident) != len(f'P{st}-') + 3:
            out.append(line)
            continue
        dep = cells[6]
        # 找出前向引用
        fwd = [m for m in re.findall(r'P(\d{1,2})-\d{3}', dep) if int(m) > st]
        if not fwd:
            out.append(line)
            continue
        # 依賴欄拆成「本階段的前置」與「跨階段參照」兩部分
        local = re.findall(rf'P{st}-\d{{3}}', dep)
        others = [m for m in re.findall(r'P\d{{1,2}}-\d{{3}}', dep) if int(m.split('-')[0][1:]) < st]
        fwd_ids = [m for m in re.findall(r'P\d{1,2}-\d{3}', dep) if int(m.split('-')[0][1:]) > st]
        parts = []
        if local or others:
            parts.append('、'.join(local + others))
        parts.append('參照：' + '、'.join(fwd_ids))
        newdep = '；'.join(parts)
        cells[6] = newdep
        out.append('| ' + ' | '.join(cells) + ' |')
        changed += 1
    io.open(path, 'w', encoding='utf-8', newline='').write('\n'.join(out))

print(f'改寫 {changed} 列')
