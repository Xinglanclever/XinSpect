import io, re, glob

root = r'C:\Users\Administrator\XinSpect'

cats = [
    root + r'\docs\INDUSTRIAL-CATALOG-2026-10-10.md',
    root + r'\docs\INDUSTRIAL-CATALOG-VOL2-2026-10-11.md',
]
corpus = ''
for c in cats:
    corpus += io.open(c, encoding='utf-8').read() + '\n'

# catalog rows: | ID | name | declared keys | ...
row_re = re.compile(r'^\|\s*([A-Z]{2}-\d{3})\s*\|([^|]*)\|([^|]*)\|', re.M)
rows = row_re.findall(corpus)

# implemented keys: every "xx.something" string literal in Services/*.cs
key_re = re.compile(r'"([a-z]{2}\.[a-z0-9._]+)"')
impl = set()
for f in glob.glob(root + r'\Services\*.cs'):
    try:
        t = io.open(f, encoding='utf-8').read()
    except Exception:
        continue
    impl |= set(key_re.findall(t))

# Declared keys live inside backticks. Take whatever is between backticks and validate the
# shape separately -- anchoring [a-z]{2} directly after the backtick silently skips names
# like `tpm.replay.*` (three letters before the first dot), which is a real key in this
# project, not a typo. That bug hid FW-002 from the gap list.
tick_re = re.compile(r'`([^`]+)`')
shape_re = re.compile(r'^[a-z]{2}\.[a-z0-9._*]+$')

dyn = io.open(root + r'\Services\FactKeyDynamicCatalog.cs', encoding='utf-8').read()
fams = [x.rstrip('.') for x in re.findall(r'\["([^"]+)"\]\s*=', dyn)]


def covered(keytext):
    if not keytext.strip():
        return True
    declared = [m for m in tick_re.findall(keytext) if shape_re.match(m)]
    if not declared:
        return True
    for k in declared:
        if k.endswith('*'):
            base = k[:-1].rstrip('.')
            if any(x.startswith(base) for x in impl):
                continue
            if any(f.startswith(base) or base.startswith(f) for f in fams):
                continue
            return False
        if k.rstrip('.') in impl or k in impl:
            continue
        if any(f.startswith(k.rstrip('.')) or k.rstrip('.').startswith(f) for f in fams):
            continue
        return False
    return True


by_domain = {}
for iid, _name, keys in rows:
    by_domain.setdefault(iid.split('-')[0], []).append((iid, keys))

print(f'{"domain":6} {"items":>5} {"covered":>7} {"missing":>7}')
tot_items = tot_missing = 0
for dom in sorted(by_domain):
    rs = by_domain[dom]
    miss = [r for r in rs if not covered(r[1])]
    tot_items += len(rs)
    tot_missing += len(miss)
    if miss:
        print(f'{dom:6} {len(rs):5} {len(rs)-len(miss):7} {len(miss):7}  ' + ','.join(m[0] for m in miss))
print('TOTAL', tot_items, 'missing', tot_missing)
print('impl keys:', len(impl), 'families:', len(fams))
