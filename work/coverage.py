import io, re, glob

root = r'C:\Users\Administrator\XinSpect'
cat1 = io.open(root + r'\docs\INDUSTRIAL-CATALOG-2026-10-10.md', encoding='utf-8').read()
cat2 = io.open(root + r'\docs\INDUSTRIAL-CATALOG-VOL2-2026-10-11.md', encoding='utf-8').read()
corpus = cat1 + '\n' + cat2

items = re.findall(r'\|\s*([A-Z]{2}-\d{3})\s*\|\s*([^|]*)\|\s*([^|]*)\|', corpus)
by_domain = {}
for iid, name, keys in items:
    by_domain.setdefault(iid.split('-')[0], []).append((iid, name.strip(), keys.strip()))

impl = set()
for f in glob.glob(root + r'\Services\*.cs'):
    try:
        t = io.open(f, encoding='utf-8').read()
    except Exception:
        continue
    impl |= set(re.findall(r'"([a-z]{2}\.[a-z0-9._]*)"', t))
dyn = io.open(root + r'\Services\FactKeyDynamicCatalog.cs', encoding='utf-8').read()
# family prefixes keep their trailing dot, e.g. "sa.ifeo." -> "sa.ifeo"
fams = [f.rstrip('.') for f in re.findall(r'\["([^"]+)"\]\s*=', dyn)]


def covered(keytext):
    if not keytext:
        return True
    # NOTE: the wildcard dot must be captured, so the char class keeps '.'
    keys = re.findall(r'`([a-z]{2}\.[a-z0-9._]*)`', keytext)
    if not keys:
        keys = re.findall(r'([a-z]{2}\.[a-z0-9._]*\*)', keytext)
    if not keys:
        return True   # catalog declares no key (e.g. deliberately not implemented)
    for k in keys:
        if k.endswith('*'):
            base = k[:-1].rstrip('.').rstrip('*')  # 'st.nvme.*' -> 'st.nvme'
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


print(f'{"domain":6} {"items":>5} {"covered":>7} {"missing":>7}')
tot_items = tot_missing = 0
for dom in sorted(by_domain):
    rows = by_domain[dom]
    miss = [r for r in rows if not covered(r[2])]
    tot_items += len(rows); tot_missing += len(miss)
    if miss:
        print(f'{dom:6} {len(rows):5} {len(rows)-len(miss):7} {len(miss):7}  ' + ','.join(m[0] for m in miss))
print('TOTAL', tot_items, 'missing', tot_missing)
