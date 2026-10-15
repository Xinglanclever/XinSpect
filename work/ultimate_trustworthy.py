"""把「已提交的那兩支對帳腳本」誠實地重跑一遍，看看哪些結論站得住。

重點不是再得出一個數字，而是分辨：
  * 矩陣列數與互異 ID —— 這一項用多種寫法都得到同一個答案
  * 域碼表合計      —— 這一項的結論建立在 `[A-Z]+\\d?` 這種字元類別＋量詞上，
                       需要先知道這個引擎還能不能用
"""
import io
import glob
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
DIR = 'docs/ULTIMATE-1000'

print('== 0. 這台機器的 re 引擎自我檢查 ==')
checks = [
    ('[A-Z]   on "F"   (應 True)', r'[A-Z]', 'F'),
    ('[A-Z]   on "A1"  (應 False)', r'[A-Z]', 'A1'),
    ('[A-Z]+  on "A1"  (應 False)', r'[A-Z]+', 'A1'),
    ('[A-Z]+  on "F"   (應 True)', r'[A-Z]+', 'F'),
    ('P\\d{1,2} on "P1" (應 True)', r'P\d{1,2}', 'P1'),
    ('P. on "P1"       (應 True)', r'P.', 'P1'),
    ('(P) on "P1"      (應 False)', r'(P)', 'P1'),
]
broken = 0
for label, pat, s in checks:
    got = bool(re.fullmatch(pat, s))
    want = 'True' in label
    if got != want:
        broken += 1
    print(f'  {label:<32} -> {got}   {"!!!" if got != want else ""}')
print(f'  失效檢查數：{broken}')
print()

print('== 1. 矩陣列數（不用字元類別＋量詞，改用字面比對）==')


def rows_literal(text):
    out = []
    for line in text.splitlines():
        body = line.strip()
        if not body.startswith('|'):
            continue
        c = body.strip('|').split('|')
        if not c:
            continue
        first = c[0].strip()
        if first.startswith('★'):
            first = first[1:]
        if len(first) == 6 and first[0] == 'P' and first[2] == '-' and first[1].isdigit() \
           and first[3:].isdigit():
            out.append(first)
    return out


total = 0
allids = set()
for st in range(1, 11):
    path = glob.glob(f'{DIR}/STAGE-{st:02d}-*.md')[0]
    ids = rows_literal(io.open(path, encoding='utf-8').read())
    total += len(ids)
    allids |= set(ids)
    nums = sorted(int(i.split('-')[1]) for i in ids)
    ok = nums == list(range(1, 106))
    print(f'  階段 {st:>2}: {len(ids):>3} 列  編號連續 1..105：{"是" if ok else "否"}')
print(f'  合計 {total} 列，互異 ID {len(allids)} 個')
print()

print('== 2. 域碼表的域碼字串（改用字面比對，不用 [A-Z]+）==')
for st in range(1, 11):
    path = glob.glob(f'{DIR}/STAGE-{st:02d}-*.md')[0]
    text = io.open(path, encoding='utf-8').read()
    block = text.split('## 三、域碼與項數')[1].split('##')[0]
    doms = []
    for line in block.splitlines():
        body = line.strip()
        if not body.startswith('|'):
            continue
        c = [x.strip().replace('**', '') for x in body.strip('|').split('|')]
        if len(c) >= 3 and c[0] and all(ch.isalpha() or ch.isdigit() for ch in c[0]) \
           and c[2].isdigit():
            doms.append((c[0], int(c[2])))
    print(f'  階段 {st:>2}: {len(doms)} 域  宣告合計 {sum(n for _, n in doms)}')
