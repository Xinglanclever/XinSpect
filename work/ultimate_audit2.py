# -*- coding: utf-8 -*-
"""ULTIMATE-1000 稽核（修正版）：把「宣稱」與「實測」並列。

與 work/ultimate_audit.py 的差別：前一版把「檔內較後面的項」一律當成前向引用，
但矩陣本來就是<b>宣告順序</b>不是<b>執行順序</b>——真正的問題是
「依賴比自己所屬階段更後面的階段」與「依賴欄寫了卻指不到任何一項」。

四類輸出：
  A. 前向依賴：依賴欄指向比自己階段更後面的階段（可能合理，需逐條判定）或同階段的後續項
  B. 依賴欄有 ID 格式錯誤或指不到任何項
  C. 依賴欄的 ID 不存在（懸空）
  D. 輸出鍵前綴不在本階段任何域的前綴表內（候選，需要逐條看）
"""
import io
import glob
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = os.path.join('docs', 'ULTIMATE-1000')
ROW = re.compile(r'^\|\s*(★?)\s*(P(\d{1,2})-(\d{3}))\s*\|(.*)$')
DOMAIN = re.compile(r'^\|\s*([A-Z]+\d?)\s*\|\s*([^|]*)\|\s*(\d+)\s*\|\s*([^|]*)\|')
KEY = re.compile(r'`([a-z][a-z0-9_]*(?:\.[a-z0-9_{}*]+)*)`')
DEP = re.compile(r'P\d{1,2}-\d{3}')
# 依賴欄裡允許的非 ID 值
DEP_PLACEHOLDER = {'—', '-', '無', '', '（無）'}


def cells(line):
    return [c.strip() for c in line.strip().strip('|').split('|')]


stages = {}
for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))):
    stage = int(re.search(r'STAGE-(\d+)', path).group(1))
    text = io.open(path, encoding='utf-8').read()
    domains = []
    for line in text.splitlines():
        m = DOMAIN.match(line)
        if m and m.group(1) != '合計':
            pre = [p.strip().strip('`') for p in re.split(r'[／/、,]', m.group(4)) if p.strip()]
            domains.append((m.group(1), int(m.group(3)), pre))
    items = []
    for line in text.splitlines():
        m = ROW.match(line)
        if not m:
            continue
        c = cells(line)
        items.append(dict(id=m.group(2), star=bool(m.group(1)),
                          key=c[3] if len(c) > 3 else '',
                          dep=c[6] if len(c) > 6 else ''))
    stages[stage] = dict(file=os.path.basename(path), domains=domains, items=items)

all_ids = {it['id'] for st in stages.values() for it in st['items']}

print('=' * 78)
print('每階段：矩陣列數／域碼表項數合計')
print('=' * 78)
for s in sorted(stages):
    tot = sum(n for _, n, _ in stages[s]['domains'])
    ok = 'OK' if (len(stages[s]['items']) == 105 and tot == 105) else '<<< 對不上'
    print(f'  階段 {s:>2} {stages[s]["file"]:<26} rows={len(stages[s]["items"]):>3} domSum={tot:>3} {ok}')

print()
print('=' * 78)
print('A. 依賴欄指向「更後面的階段」')
print('=' * 78)
n = 0
for s in sorted(stages):
    for it in stages[s]['items']:
        for d in DEP.findall(it['dep']):
            other = int(d.split('-')[0][1:])
            if other > s:
                n += 1
                print(f'  {it["id"]} (階段{s}) -> {d} (階段{other})')
print(f'  ---- {n} 條')

print()
print('=' * 78)
print('B. 依賴欄有內容但不是可解析的 ID（格式異常／佔位）')
print('=' * 78)
n = 0
for s in sorted(stages):
    for it in stages[s]['items']:
        dep = it['dep'].strip()
        if dep in DEP_PLACEHOLDER or re.fullmatch(r'P\d{1,2}-\d{3}(\s*[、,]\s*P\d{1,2}-\d{3})*', dep):
            continue
        n += 1
        print(f'  {it["id"]:<9} 依賴欄：{dep!r}')
print(f'  ---- {n} 條')

print()
print('=' * 78)
print('C. 依賴欄指向不存在的 ID（含格式不合 P{d}-{nnn} 者）')
print('=' * 78)
n = 0
for s in sorted(stages):
    for it in stages[s]['items']:
        for tok in re.findall(r'P\d{1,2}-\d+', it['dep']):
            if tok not in all_ids:
                n += 1
                print(f'  {it["id"]:<9} -> {tok}（不存在或格式不合）')
print(f'  ---- {n} 條')

print()
print('=' * 78)
print('D. 輸出鍵前綴不在本階段任何域的前綴表內（候選，需逐條判定）')
print('=' * 78)
n = 0
for s in sorted(stages):
    declared = [p.rstrip('*') for _, _, ps in stages[s]['domains'] for p in ps]
    for it in stages[s]['items']:
        for k in KEY.findall(it['key']):
            root = k.split('.')[0] + '.'
            if not any(root.startswith(p) or p.startswith(root) for p in declared):
                n += 1
                print(f'  {it["id"]:<9} {k:<30} 宣告前綴：{"、".join(declared)}')
print(f'  ---- {n} 條')
