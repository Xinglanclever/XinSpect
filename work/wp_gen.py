# -*- coding: utf-8 -*-
"""Whitepaper appendix generator.

Everything emitted here is derived from the repository itself, so the appendix can be
regenerated and diffed.  Nothing is hand-typed prose: keys, families, rules, exemptions,
modules, tests and documents are all read out of the tree.
"""
import io, os, re, glob, sys, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, 'work', 'wp')
os.makedirs(OUT, exist_ok=True)


def read(path):
    with io.open(os.path.join(ROOT, path), encoding='utf-8') as f:
        return f.read()


def read_src(path):
    # C# sources are a mix of UTF-8 (BOM) and, in a few legacy files, cp950.
    raw = open(os.path.join(ROOT, path), 'rb').read()
    for enc in ('utf-8-sig', 'utf-8', 'cp950', 'latin-1'):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode('utf-8', 'replace')


def write(name, text):
    with io.open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
    print('%-28s %8d chars' % (name, len(text)))


# ── 1. fact keys ────────────────────────────────────────────────────────────────
cat = read_src('Services/FactKeyCatalog.cs')
keys = re.findall(r'^\s*"([^",]+)",\s*$', cat, re.M)
# de-duplicate, keep first-seen order (the catalogue is authored in sorted order)
seen, keys_ordered = set(), []
for k in keys:
    if k not in seen:
        seen.add(k)
        keys_ordered.append(k)
keys = keys_ordered

# ── 2. dynamic families ────────────────────────────────────────────────────────
dyn = read_src('Services/FactKeyDynamicCatalog.cs')
fam_block = dyn[dyn.index('Families { get; } ='):]
fam = re.findall(r'\["([^"]+)"\]\s*=\s*((?:\s*"[^"]*"\s*\+?)+)', fam_block)
families = []
for prefix, body in fam:
    reason = ' '.join(re.findall(r'"([^"]*)"', body))
    families.append((prefix, reason.strip()))

# ── 3. key -> producing module ────────────────────────────────────────────────
service_files = sorted(glob.glob(os.path.join(ROOT, 'Services', '*.cs')))
file_text = {}
for p in service_files:
    rel = 'Services/' + os.path.basename(p)
    file_text[rel] = read_src(rel)

key_literal = re.compile(r'"([a-z][a-z0-9._]*)"')
key_producers = collections.OrderedDict()
for k in keys:
    needle = '"%s"' % k
    hits = [rel for rel, text in file_text.items() if needle in text]
    key_producers[k] = hits

# ── 4. rule index ─────────────────────────────────────────────────────────────
vr = read_src('Services/VerifyRules.cs')
parts = dict(re.findall(r'public const string (Part[A-Za-z]+) = "([^"]+)";', vr))
titles = dict(re.findall(r'private const string ([A-Z0-9]+) = "([^"]+)";', vr))
rules = []
for m in re.finditer(r'new\("(R-[A-Z]+-\d+)",\s*(Part[A-Za-z]+),\s*([A-Z0-9]+),\s*\[([^\]]*)\]', vr):
    rid, part, tconst, facts = m.groups()
    req = [x.strip().split('.')[-1] for x in facts.split(',') if x.strip()]
    scope = 'Disk' if re.search(re.escape(m.group(0)) + r'[\s\S]{0,200}?VerifyScope\.Disk', vr) else 'Machine'
    rules.append((rid, parts.get(part, part), titles.get(tconst, tconst), req, scope))

# ── 5. exemptions ─────────────────────────────────────────────────────────────
fc = read_src('Services/FactCoverageReport.cs')
ex_start = fc.index('Exemptions { get; } =')
# The declaration itself opens a property brace; start scanning after the '=' so the
# 'new Dictionary<...> {' initialiser is what the depth counter tracks.
ex_start = fc.index('=', ex_start) + 1
ex_body = fc[ex_start:]
depth, end = 0, None
started = False
for i, ch in enumerate(ex_body):
    if ch == '{':
        depth += 1
        started = True
    elif ch == '}':
        depth -= 1
        if started and depth == 0:
            end = i
            break
ex_body = ex_body[:end] if end else ex_body
exemptions = []
for m in re.finditer(r'\["([^"]+)"\]\s*=\s*((?:\s*"[^"]*"\s*\+?)+)', ex_body):
    prefix, body = m.groups()
    reason = ' '.join(re.findall(r'"([^"]*)"', body))
    exemptions.append((prefix, re.sub(r'\s+', ' ', reason).strip()))

# ── 6. modules / tests / docs ─────────────────────────────────────────────────
modules = []
for rel, text in file_text.items():
    lines = text.count('\n') + 1
    mcls = re.search(r'public (?:static |sealed |abstract |partial )*(?:class|record|struct|enum)\s+(\w+)', text)
    owned = sum(1 for k, hits in key_producers.items() if rel in hits)
    modules.append((rel, mcls.group(1) if mcls else '—', lines, owned))
modules.sort(key=lambda r: (-r[3], r[0]))
module_owner = {rel: owned for rel, _, _, owned in modules}

test_files = sorted(glob.glob(os.path.join(ROOT, 'Tests', '*.cs')))
tests = []
total_test_methods = 0
for p in test_files:
    rel = 'Tests/' + os.path.basename(p)
    text = read_src(rel)
    facts = len(re.findall(r'\[Fact\]', text))
    theories = len(re.findall(r'\[Theory\]', text))
    total_test_methods += facts + theories
    tests.append((rel, facts, theories, text.count('\n') + 1))
tests.sort(key=lambda r: -(r[1] + r[2]))

doc_files = sorted(glob.glob(os.path.join(ROOT, 'docs', '**', '*.md'), recursive=True))
docs = []
for p in doc_files:
    rel = os.path.relpath(p, ROOT).replace('\\', '/')
    text = read_src(rel)
    title = '—'
    for line in text.split('\n'):
        if line.startswith('# '):
            title = line[2:].strip()
            break
    docs.append((rel, title, len(text)))

# ── emissions ─────────────────────────────────────────────────────────────────
def group_tables(items, keyfn):
    groups = collections.OrderedDict()
    for it in items:
        groups.setdefault(keyfn(it), []).append(it)
    return groups


# A. full key catalogue, with producing module
lines = ['<!-- GENERATED by work/wp_gen.py — do not edit by hand -->', '']
lines.append('全目錄共 **%d** 個靜態事實鍵，另有 **%d** 個動態鍵家族（見附錄 B）。'
             % (len(keys), len(families)))
lines.append('')
by_prefix = group_tables(keys, lambda k: k.split('.')[0])
lines.append('| 前綴 | 鍵數 | 主要產生模組 |')
lines.append('|---|---:|---|')
lines2 = []
for prefix, ks in sorted(by_prefix.items()):
    tally = collections.Counter()
    for k in ks:
        for h in key_producers.get(k, []):
            tally[h] += 1
    top = '、'.join('`%s`' % h.replace('Services/', '') for h, _ in tally.most_common(2)) or '—'
    lines.append('| `%s.` | %d | %s |' % (prefix, len(ks), top))
lines.append('')
for prefix, ks in sorted(by_prefix.items()):
    lines2.append('### `%s` — %d 鍵' % (prefix, len(ks)))
    lines2.append('')
    lines2.append('| 事實鍵 | 產生模組 |')
    lines2.append('|---|---|')
    for k in ks:
        hits = key_producers.get(k, [])
        shown = '、'.join('`%s`' % h.replace('Services/', '') for h in hits) if hits else '—'
        lines2.append('| `%s` | %s |' % (k, shown))
    lines2.append('')
write('a_keys.md', '\n'.join(lines + lines2))

# B. dynamic families
b = ['<!-- GENERATED by work/wp_gen.py -->', '',
     '編譯期數不出成員的鍵家族共 **%d** 個。每一筆都要說明「為什麼成員是執行期決定的」，'
     '這是申報，不是免死金牌：登記了卻沒有任何服務生產，`FactKeyCatalogTests` 會判為殭屍前綴。' % len(families), '',
     '| # | 前綴 | 成員由什麼決定 |', '|---:|---|---|']
for i, (prefix, reason) in enumerate(families, 1):
    b.append('| %d | `%s` | %s |' % (i, prefix, reason.replace('|', '\\|')))
b.append('')
write('b_families.md', '\n'.join(b))

# C. rules
c = ['<!-- GENERATED by work/wp_gen.py -->', '',
     '對帳規則共 **%d** 條。判定只有三種：相符、矛盾、無法判定；不給分數、不下真偽結論。' % len(rules), '',
     '| 規則 | 分區 | 標題 | 需要的事實 | 作用域 |', '|---|---|---|---|---|']
for rid, part, title, req, scope in rules:
    c.append('| `%s` | %s | %s | %s | %s |' % (rid, part, title,
             '、'.join('`%s`' % r for r in req), '逐碟' if scope == 'Disk' else '整機'))
c.append('')
write('c_rules.md', '\n'.join(c))

# D. exemptions
d = ['<!-- GENERATED by work/wp_gen.py -->', '',
     '明文豁免共 **%d** 條。豁免的門檻是「這類事實沒有第二個來源可以交叉」，不是「懶得寫規則」。'
     % len(exemptions), '',
     '| 前綴 | 不對帳的理由 |', '|---|---|']
for prefix, reason in exemptions:
    d.append('| `%s` | %s |' % (prefix, reason.replace('|', '\\|')))
d.append('')
write('d_exempt.md', '\n'.join(d))

# E. modules
e = ['<!-- GENERATED by work/wp_gen.py -->', '',
     '`Services/` 下共 **%d** 個模組。擁有的鍵數為該檔在 `FactKeyCatalog` 中負責產生的事實鍵數量。'
     % len(modules), '',
     '| 模組 | 型別 | 行數 | 擁有鍵數 |', '|---|---|---:|---:|']
for rel, cls, ln, owned in modules:
    e.append('| `%s` | %s | %d | %d |' % (rel.replace('Services/', ''), cls, ln, owned))
e.append('')
write('e_modules.md', '\n'.join(e))

# F. tests
f = ['<!-- GENERATED by work/wp_gen.py -->', '',
     '測試檔共 **%d** 個，`[Fact]`／`[Theory]` 標記合計 **%d** 處。'
     % (len(tests), total_test_methods), '',
     '| 測試檔 | Fact | Theory | 行數 |', '|---|---:|---:|---:|']
for rel, facts, theories, ln in tests:
    f.append('| `%s` | %d | %d | %d |' % (rel.replace('Tests/', ''), facts, theories, ln))
f.append('')
write('f_tests.md', '\n'.join(f))

# G. docs
g = ['<!-- GENERATED by work/wp_gen.py -->', '',
     '`docs/` 下共 **%d** 份文件。' % len(docs), '',
     '| 文件 | 標題 | 位元組 |', '|---|---|---:|']
for rel, title, size in docs:
    g.append('| `%s` | %s | %d |' % (rel, title.replace('|', '\\|'), size))
g.append('')
write('g_docs.md', '\n'.join(g))

# stats for the front matter
stats = {
    'keys': len(keys),
    'families': len(families),
    'rules': len(rules),
    'exemptions': len(exemptions),
    'modules': len(modules),
    'tests_files': len(tests),
    'test_markers': total_test_methods,
    'docs': len(docs),
    'prefixes': len(by_prefix),
    'keys_without_producer': sum(1 for k in keys if not key_producers.get(k)),
}
import json
write('stats.json', json.dumps(stats, ensure_ascii=False, indent=2))
print(json.dumps(stats, ensure_ascii=False))
