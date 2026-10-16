# -*- coding: utf-8 -*-
"""階段二（P2-001 – P2-025）規格宣告鍵 vs 實作實際鍵 的完整對帳。

這一支不是「猜哪個沒做」，而是把規格每一列的輸出鍵欄位拆開，
逐個拿去目錄與原始碼裡找它們的實際對應——
找不到就問「有沒有一個語意相同但名字不同的鍵」。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# ── 讀規格矩陣 ──────────────────────────────────────────────────────────
spec = io.open('docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md', encoding='utf-8').read()
rows = []
for line in spec.split('\n'):
    m = re.match(r'^\|\s*(P2-\d+)\s*\|([^|]*)\|([^|]*)\|([^|]*)\|', line)
    if m:
        rows.append((m.group(1), m.group(2).strip(), m.group(3).strip(), m.group(4).strip()))

# ── 讀目錄 ──────────────────────────────────────────────────────────────
cat = io.open('Services/FactKeyCatalog.cs', encoding='utf-8').read()
catalog = set(re.findall(r'^\s*"([a-z0-9_.]+)",\s*$', cat, re.M))
# 動態家族前綴
dyn = io.open('Services/FactKeyDynamicCatalog.cs', encoding='utf-8').read()
dyn_prefixes = set(re.findall(r'\["([a-z0-9_.]+)"\]\s*=', dyn))

# ── 讀生產者 ────────────────────────────────────────────────────────────
produced = set()
for root in ('Services', 'XinSpect.Decoders'):
    for d, _, files in os.walk(root):
        if 'obj' in d or 'bin' in d:
            continue
        for f in files:
            if f.endswith('.cs'):
                text = io.open(os.path.join(d, f), encoding='utf-8').read()
                produced |= set(re.findall(r'"(ac\.[a-z0-9_.]+|acpi\.[a-z0-9_.]+|sb\.[a-z0-9_.]+|tpm\.[a-z0-9_.]+)"', text))

print(f'規格矩陣 {len(rows)} 列；目錄 {len(catalog)} 鍵；動態家族 {len(dyn_prefixes)} 個\n')
print('=' * 78)

def keys_of(cell):
    """把規格欄位裡的鍵樣式拆出來，含 {sig} 這類佔位符。"""
    return [k.strip().strip('`') for k in re.split(r'[／/、]', cell) if k.strip()]

issues = []
for pid, title, _desc, keycell in rows:
    decls = keys_of(keycell)
    if not decls or decls == ['—']:
        continue
    verdicts = []
    for decl in decls:
        # 展開常見的簡寫：ac.bgrt → ac.bgrt；ac.spcr／ac.dbg2.* 已拆開
        base = decl.rstrip('.*')
        if '{' in decl:                       # 例如 ac.table.{sig}
            family = decl.split('{')[0].rstrip('.')
            hit = any(k.startswith(family + '.') for k in catalog | produced) or family in dyn_prefixes
            verdicts.append((decl, 'families' if hit else 'MISSING'))
        elif decl.endswith('.*'):
            hit = any(k.startswith(base + '.') for k in catalog | produced) or base in dyn_prefixes
            verdicts.append((decl, 'family' if hit else 'MISSING'))
        else:
            exact = decl in catalog
            prod = decl in produced
            if exact and prod:
                verdicts.append((decl, 'exact'))
            elif exact:
                verdicts.append((decl, 'in-catalog-only'))
            elif prod:
                verdicts.append((decl, 'produced-not-in-catalog'))
            else:
                verdicts.append((decl, 'MISSING'))
    bad = [(d, v) for d, v in verdicts if v == 'MISSING']
    flag = 'MISSING' if bad else ('PARTIAL' if any(v != 'exact' for _, v in verdicts) else 'OK')
    if flag != 'OK':
        issues.append((pid, title, verdicts))
        print(f'{pid} [{flag}] {title}')
        for d, v in verdicts:
            mark = {'exact': '✓', 'family': '✓(家族)', 'families': '✓(家族)',
                    'in-catalog-only': '⚠ 只在目錄', 'produced-not-in-catalog': '⚠ 只在生產碼',
                    'MISSING': '✗ 找不到'}[v]
            print(f'    {mark:16s} {d}')

print('=' * 78)
print(f'\n有問題的列：{len(issues)} / {len(rows)}')

ok = len(rows) - len(issues)
print(f'完全相符：{ok} 列')
