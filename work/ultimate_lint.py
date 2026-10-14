# -*- coding: utf-8 -*-
"""Verify the ULTIMATE-1000 plan.

Checks, per stage file:
  * the matrix row count matches the count declared in the header and in the domain table
  * item IDs are a gap-free 1..N sequence within the stage
  * every referenced ID (in the dependency column or in prose) is defined somewhere
"""
import io
import glob
import os
import re
import sys

DIR = os.path.join('docs', 'ULTIMATE-1000')
ROW = re.compile(r'^\|\s*★?\s*(P(\d{1,2})-(\d{3}))\s*\|')
ID = re.compile(r'\bP(\d{1,2})-(\d{3})\b')


def rows(path):
    out = []
    for line in io.open(path, encoding='utf-8'):
        m = ROW.match(line)
        if m:
            out.append((m.group(1), line.rstrip()))
    return out


defined = {}
problems = []
stage_totals = {}

paths = sorted(glob.glob(os.path.join(DIR, 'STAGE-*.md')))
for path in paths:
    text = io.open(path, encoding='utf-8').read()
    rs = rows(path)
    stage = int(re.search(r'STAGE-(\d+)', path).group(1))
    ids = [r[0] for r in rs]
    defined[path] = (stage, ids)

    nums = [int(i.split('-')[1]) for i in ids]
    expected = list(range(1, len(nums) + 1))
    if nums != expected:
        missing = sorted(set(expected) - set(nums))
        dupes = sorted({n for n in nums if nums.count(n) > 1})
        problems.append('%s: ID sequence broken (missing %s, dupes %s)' % (os.path.basename(path), missing, dupes))

    declared = re.findall(r'本階段\s*\*{0,2}(\d+)\*{0,2}\s*項', text)
    if len(set(declared)) != 1 or int(declared[0]) != len(rs):
        problems.append('%s: declared %s vs actual %d' % (os.path.basename(path), set(declared), len(rs)))

    stage_totals[stage] = len(rs)

all_ids = set()
for path, (stage, ids) in defined.items():
    all_ids.update(ids)

# cross references: any P<stage>-<nnn> appearing outside the ID column
for path in paths:
    for n, line in enumerate(io.open(path, encoding='utf-8'), 1):
        for m in ID.finditer(line):
            ref = m.group(0)
            if ROW.match(line) and line.startswith('| ★' + ref) or line.startswith('| ' + ref):
                continue
            if ref not in all_ids:
                problems.append('%s:%d: dangling reference %s' % (os.path.basename(path), n, ref))

# domain tables must sum to the stage row count
for path in paths:
    text = io.open(path, encoding='utf-8').read()
    stage = int(re.search(r'STAGE-(\d+)', path).group(1))
    block = text.split('## 三、域碼與項數')[1].split('##')[0]
    nums = [int(x) for x in re.findall(r'^\|\s*[A-Z]+\d*\s*\|[^|]*\|\s*(\d+)\s*\|', block, re.M)]
    if nums and sum(nums) != stage_totals[stage]:
        problems.append('%s: domain table sums to %d, matrix has %d' % (os.path.basename(path), sum(nums), stage_totals[stage]))

print('stage row totals:', {k: stage_totals[k] for k in sorted(stage_totals)})
print('total items      :', sum(stage_totals.values()))
print('distinct IDs     :', len(all_ids))
if problems:
    print('PROBLEMS (%d):' % len(problems))
    for p in problems:
        print('  ', p)
else:
    print('no problems')
sys.exit(1 if problems else 0)
