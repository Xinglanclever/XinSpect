# -*- coding: utf-8 -*-
"""Table-integrity lint for the whitepaper editions.

Checks every markdown table block for a separator row and uniform cell counts, honouring
escaped pipes. Also verifies that the chapter numbering is a gap-free 1..N sequence.
"""
import io
import re
import sys


def cells(line):
    s = line.strip()
    if s.startswith('|'):
        s = s[1:]
    if s.endswith('|') and not s.endswith('\\|'):
        s = s[:-1]
    out = []
    cur = []
    i = 0
    while i < len(s):
        ch = s[i]
        if ch == '\\' and i + 1 < len(s):
            cur.append(s[i + 1])
            i += 2
            continue
        if ch == '|':
            out.append(''.join(cur))
            cur = []
        else:
            cur.append(ch)
        i += 1
    out.append(''.join(cur))
    return out


def lint(path):
    lines = io.open(path, encoding='utf-8').read().split('\n')
    tables = rows = bad = 0
    problems = []
    i = 0
    while i < len(lines):
        if lines[i].lstrip().startswith('|'):
            start = i
            block = []
            while i < len(lines) and lines[i].lstrip().startswith('|'):
                block.append(lines[i])
                i += 1
            tables += 1
            rows += len(block)
            widths = [len(cells(l)) for l in block]
            if len(block) < 2 or not re.match(r'^\s*\|[\s:\-|]+\|\s*$', block[1]):
                problems.append((start + 1, 'no separator row', block[0][:48]))
                bad += 1
                continue
            if len(set(widths)) != 1:
                problems.append((start + 1, 'ragged widths %s' % widths, block[0][:48]))
                bad += 1
        else:
            i += 1
    print('%-22s tables=%-4d rows=%-6d malformed=%d' % (path, tables, rows, bad))
    for line_no, why, text in problems[:8]:
        print('    line %d: %s | %s' % (line_no, why, text))
    return bad


CN = '零一二三四五六七八九'


def cn_num(w):
    w = w.replace('\u3000', '')
    w = re.sub(r'[^\u4e00-\u9fff]', '', w)
    total = 0
    for part in w:
        if part == '十':
            total = (total or 1) * 10 if total < 10 else total + 10
        else:
            total += CN.index(part)
    return total


def chapters(path):
    txt = io.open(path, encoding='utf-8').read()
    if path.endswith('.en.md'):
        nums = [int(m) for m in re.findall(r'^## (\d+)\.', txt, re.M)]
    else:
        nums = []
        for m in re.findall(r'^## 第(.+?)章', txt, re.M):
            try:
                nums.append(cn_num(m))
            except ValueError:
                nums.append(-1)
    ok = nums == list(range(1, len(nums) + 1))
    print('%-22s chapters=%d sequence_ok=%s' % (path, len(nums), ok))
    return 0 if ok else 1


if __name__ == '__main__':
    bad = 0
    for p in ['WHITEPAPER.md', 'WHITEPAPER.zh-CN.md', 'WHITEPAPER.en.md']:
        bad += lint(p)
    bad += chapters('WHITEPAPER.md')
    bad += chapters('WHITEPAPER.en.md')
    print('TOTAL PROBLEMS:', bad)
    sys.exit(1 if bad else 0)
