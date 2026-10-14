# -*- coding: utf-8 -*-
"""Assemble the XinSpect whitepaper editions.

Authored parts live in work/wp/ alongside the fragments emitted by work/wp_gen.py.
The whole document is normalised to Traditional Chinese with OpenCC (the source
tree mixes Traditional and Simplified in comments), then a Simplified edition is
produced by a second conversion pass.
"""
import io, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WP = os.path.join(ROOT, 'work', 'wp')

import opencc
s2t = opencc.OpenCC('s2t')
t2sp = opencc.OpenCC('tw2sp')   # Taiwan Traditional -> mainland Simplified, with phrase conversion

APPENDIX_HEADINGS = {
    'a_keys.md': '# 附錄 A、事實鍵全目錄',
    'b_families.md': '# 附錄 B、動態鍵家族',
    'c_rules.md': '# 附錄 C、對帳規則索引',
    'd_exempt.md': '# 附錄 D、明文豁免清單',
    'e_modules.md': '# 附錄 E、服務模組索引',
    'f_tests.md': '# 附錄 F、測試檔索引',
    'g_docs.md': '# 附錄 G、文件索引',
    '95_glossary.md': '# 附錄 H、詞彙表',
}

# Bodies are numbered fragments and sort into the intended reading order; the appendices
# follow them. Deriving the order from the directory keeps newly added chapters from being
# silently dropped from the build.
ORDER = [(n, None) for n in sorted(os.listdir(WP)) if re.match(r'^\d\d.*\.md$', n)]
for name, heading in APPENDIX_HEADINGS.items():
    if name not in [n for n, _ in ORDER]:
        ORDER.append((name, heading))
ORDER = [(n, h or APPENDIX_HEADINGS.get(n)) for n, h in ORDER]
ORDER.sort(key=lambda e: (0, e[0]) if e[0] not in APPENDIX_HEADINGS else (1, e[0]))


def read(path):
    with io.open(path, encoding='utf-8') as f:
        return f.read()


missing = [n for n, _ in ORDER if not os.path.exists(os.path.join(WP, n))]
if missing:
    sys.exit('missing fragments: ' + ', '.join(missing))

chunks = []
for name, heading in ORDER:
    text = read(os.path.join(WP, name)).strip('\n')
    if heading:
        chunks.append(heading + '\n\n' + text)
    else:
        chunks.append(text)

doc = '\n\n'.join(chunks) + '\n'

# Normalise the mixed-script source comments to Traditional.
doc = s2t.convert(doc)
# Collapse 3+ blank lines that concatenation can produce.
doc = re.sub(r'\n{4,}', '\n\n\n', doc)

with io.open(os.path.join(ROOT, 'WHITEPAPER.md'), 'w', encoding='utf-8', newline='\n') as f:
    f.write(doc)

cn = t2sp.convert(doc)
# Mainland convention writes 於 as 于; the phrase tables leave a couple of
# occurrences behind (不屬於 -> 不属於), so finish the job character-wise.
cn = cn.replace('於', '于')
with io.open(os.path.join(ROOT, 'WHITEPAPER.zh-CN.md'), 'w', encoding='utf-8', newline='\n') as f:
    f.write(cn)


def stats(text):
    cjk = len(re.findall(r'[\u3400-\u4dbf\u4e00-\u9fff]', text))
    return dict(chars=len(text), cjk=cjk, wordcount=cjk + len(re.findall(r'[A-Za-z0-9_.-]+', text)), lines=text.count('\n') + 1)


print('WHITEPAPER.md        ', stats(doc))
print('WHITEPAPER.zh-CN.md  ', stats(cn))

# Character-level purity check. The Traditional-only set is derived rather than hand-listed:
# a hand-written probe is unreliable because 器, 覆 and 薄 look Traditional but are identical
# in both standards, which made the old probe report false leaks.
tw_only = set(c for c in set(doc) if '一' <= c <= '鿿' and t2sp.convert(c) != c)
# A character counts as a leak only if the converter would still change it in context: 著 is
# Traditional-only in isolation but correct in 顯著/著重, which the phrase tables already keep.
# Convert the window back to Traditional before asking, otherwise the phrase tables misfire on
# Simplified input: 显著 (correct mainland) would be reported as 著的残留.
leaks = set()
for i, ch in enumerate(cn):
    if ch in tw_only:
        window = s2t.convert(cn[max(0, i - 2):i + 3])
        if ch not in t2sp.convert(window):
            leaks.add(ch)
print('zh-CN traditional-only leaks:', ''.join(sorted(leaks)) if leaks else 'none')
