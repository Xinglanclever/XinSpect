# -*- coding: utf-8 -*-
"""ULTIMATE-1000 稽核：把「宣稱」與「實測」並列，不猜。

輸出（全部可重跑）：
  1. 每階段矩陣列數、編號範圍
  2. 每階段的域碼表：域碼、宣告項數、宣告前綴
  3. 每項：ID、輸出鍵、依賴欄
  4. 三類可疑點：
     a. 依賴指向「比自己所屬階段更後面」的階段（前向依賴）
     b. 依賴指向同檔更後面的項（檔內前向）
     c. 輸出鍵的第一段前綴不在該階段的任何域前綴表內
"""
import io
import glob
import os
import re
import sys

DIR = os.path.join('docs', 'ULTIMATE-1000')
ROW = re.compile(r'^\|\s*(★?)\s*(P(\d{1,2})-(\d{3}))\s*\|(.*)$')
DOMAIN = re.compile(r'^\|\s*([A-Z]+\d?)\s*\|\s*([^|]*)\|\s*(\d+)\s*\|\s*([^|]*)\|')
KEY = re.compile(r'`([a-z][a-z0-9_]*(?:\.[a-z0-9_{}*]+)+)`')
DEP = re.compile(r'P\d{1,2}-\d{3}')


def cells(line):
    return [c.strip() for c in line.strip().strip('|').split('|')]


stages = {}
for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))):
    name = os.path.basename(path)
    stage = int(re.search(r'STAGE-(\d+)', path).group(1))
    text = io.open(path, encoding='utf-8').read()

    domains = []
    for line in text.splitlines():
        m = DOMAIN.match(line)
        if m and m.group(1) != '合計':
            prefixes = [p.strip().strip('`') for p in re.split(r'[／/、,]', m.group(4)) if p.strip()]
            domains.append((m.group(1), int(m.group(3)), prefixes))

    items = []
    for line in text.splitlines():
        m = ROW.match(line)
        if not m:
            continue
        c = cells(line)
        # 欄位：ID | 名稱 | 目標 | 輸出鍵 | 敏感度 | 成本 | 依賴
        key_cell = c[3] if len(c) > 3 else ''
        dep_cell = c[6] if len(c) > 6 else ''
        items.append((m.group(2), key_cell, dep_cell))

    stages[stage] = dict(file=name, domains=domains, items=items)

print('=' * 78)
print('每階段：矩陣列數、域碼表項數合計、域數')
print('=' * 78)
for s in sorted(stages):
    st = stages[s]
    total = sum(n for _, n, _ in st['domains'])
    nums = sorted(int(i.split('-')[1]) for i, _, _ in st['items'])
    span = f'{nums[0]}..{nums[-1]}' if nums else '-'
    flag = '' if (len(st["items"]) == 105 and total == 105) else '   <<< 對不上'
    print(f'  階段 {s:>2}  {st["file"]:<26} rows={len(st["items"]):>3}  domSum={total:>3}  '
          f'domains={len(st["domains"]):>2}  編號={span}{flag}')

print()
print('=' * 78)
print('A. 前向依賴（依賴欄指向更後面的階段）')
print('=' * 78)
fw = 0
for s in sorted(stages):
    for iid, _, dep in stages[s]['items']:
        own = int(iid.split('-')[0][1:])
        for d in DEP.findall(dep or ''):
            other = int(d.split('-')[0][1:])
            if other > own:
                fw += 1
                print(f'  {iid} (階段{own})  ->  {d} (階段{other})')
print(f'  ---- 共 {fw} 條' if fw else '  無')

print()
print('=' * 78)
print('B. 檔內前向引用（依賴同檔更後面的項）')
print('=' * 78)
n = 0
for s in sorted(stages):
    order = {iid: k for k, (iid, _, _) in enumerate(stages[s]['items'])}
    for iid, _, dep in stages[s]['items']:
        for d in DEP.findall(dep or ''):
            if d in order and order[d] > order[iid]:
                n += 1
                print(f'  {iid}  ->  {d}（同檔第 {order[d] + 1} 列，自己第 {order[iid] + 1} 列）')
print(f'  ---- 共 {n} 條' if n else '  無')

print()
print('=' * 78)
print('C. 輸出鍵前綴不在本階段任何域的前綴表內')
print('=' * 78)
n = 0
for s in sorted(stages):
    declared = [p for _, _, ps in stages[s]['domains'] for p in ps]
    for iid, key_cell, _ in stages[s]['items']:
        for k in KEY.findall(key_cell or ''):
            root = k.split('.')[0] + '.'
            if not any(root.startswith(p.rstrip('*')) or p.startswith(root) for p in declared):
                n += 1
                print(f'  {iid:<9} {k:<32} 本階段宣告前綴：{"、".join(declared)}')
print(f'  ---- 共 {n} 條' if n else '  無')
sys.exit(0)
