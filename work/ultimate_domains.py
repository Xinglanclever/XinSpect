# -*- coding: utf-8 -*-
"""ULTIMATE-1000 稽核：域歸屬（三支腳本中最關鍵的一支）。

work/ultimate_audit2.py 只做兩件事：數列、比對宣告項數合計。兩者都對，
卻完全看不到「105 項被切給 9 個域、每域宣告幾項」是否真的分得開。

實際上階段一的域碼表宣告 F1..F10 共 <b>95</b> 項，矩陣卻有 105 列——
多的 10 項< b>沒有任何域認領</b>，而照直覺把它們塞給報表那個域（例如把
P1-046/047/051/054 塞進 F5「匿名化與資料邊界」）之後，號稱 10 項的域
會變成 14 項。這就是 work/ultimate_audit2.py 的「domSum=95」真正的意思：
不是表格算錯，是<b>有 10 項還沒被分配</b>。

這支腳本把每一項的輸出鍵前綴與每個域宣告的前綴做比對，列出「落在哪個域」
與「哪個域都不屬於」，讓分配決定可以是被人看見的一步，而不是被隱含假設。
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


def cells(line):
    return [c.strip() for c in line.strip().strip('|').split('|')]


def prefix_of(key):
    return key.split('.')[0] + '.'


for path in sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md'))):
    stage = int(re.search(r'STAGE-(\d+)', path).group(1))
    text = io.open(path, encoding='utf-8').read()
    domains = []
    for line in text.splitlines():
        m = DOMAIN.match(line)
        if m and m.group(1) != '合計':
            pre = [p.strip().strip('`').rstrip('*') for p in re.split(r'[／/、,]', m.group(4)) if p.strip()]
            domains.append((m.group(1), m.group(2), int(m.group(3)), pre))

    items = []
    for line in text.splitlines():
        m = ROW.match(line)
        if not m:
            continue
        c = cells(line)
        keys = KEY.findall(c[3]) if len(c) > 3 else []
        items.append((m.group(2), keys))

    print('=' * 78)
    print(f'階段 {stage}　{os.path.basename(path)}')
    print(f'  域碼表：{len(domains)} 域，宣告合計 {sum(d[2] for d in domains)} 項；矩陣 {len(items)} 列')
    print('=' * 78)

    # 每個域的「認領數」＝輸出鍵前綴落在該域宣告前綴裡的項數
    claimed = {d[0]: [] for d in domains}
    orphans = []
    for iid, keys in items:
        hit = None
        for code, _, _, pres in domains:
            if any(prefix_of(k).startswith(p) for k in keys for p in pres):
                hit = code
                break
        if hit:
            claimed[hit].append(iid)
        else:
            orphans.append((iid, keys))

    print('  域碼  宣告  認領  差')
    for code, label, n, _ in domains:
        got = len(claimed[code])
        flag = '' if got == n else ('   <<<' if got < n else '   >>>')
        print(f'  {code:<5} {n:>4} {got:>5} {got - n:>+4}{flag}   {label}')

    print(f'\n  沒有任何域認領的項（{len(orphans)} 條）：')
    for iid, keys in orphans:
        print(f'    {iid:<9} {"、".join(keys) if keys else "（測試）"}')
    print()
