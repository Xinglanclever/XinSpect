import io, re

root = r'C:\Users\Administrator\XinSpect'
cat1 = io.open(root + r'\docs\INDUSTRIAL-CATALOG-2026-10-10.md', encoding='utf-8').read()
cat2 = io.open(root + r'\docs\INDUSTRIAL-CATALOG-VOL2-2026-10-11.md', encoding='utf-8').read()
corpus = cat1 + '\n' + cat2

# Collect every catalog item id + its declared key(s)
items = re.findall(r'\|\s*([A-Z]{2}-\d{3})\s*\|\s*([^|]*)\|\s*([^|]*)\|', corpus)
by_domain = {}
for iid, name, keys in items:
    dom = iid.split('-')[0]
    by_domain.setdefault(dom, []).append((iid, name.strip(), keys.strip()))

# All implemented keys (static catalog + producers)
prod = io.open(root + r'\Services\FactKeyCatalog.cs', encoding='utf-8').read()
impl_keys = set(re.findall(r'"([a-z]{2}\.[a-z0-9._]*)"', prod))
import glob
for f in glob.glob(root + r'\Services\*.cs'):
    try:
        t = io.open(f, encoding='utf-8').read()
    except Exception:
        continue
    impl_keys |= set(re.findall(r'"([a-z]{2}\.[a-z0-9._]*)"', t))
# dynamic family prefixes
dyn = io.open(root + r'\Services\FactKeyDynamicCatalog.cs', encoding='utf-8').read()
fams = re.findall(r'\["([^"]+)"\]\s*=', dyn)

def covered(keytext):
    if not keytext:
        return False
    m = re.findall(r'`?([a-z]{2}\.[a-z0-9._]*)', keytext)
    if not m:
        return True   # 目錄沒宣告鍵（例如明文不做）
    for k in m:
        base = k.rstrip('.*')
        if k.endswith('.*'):
            if any(base.startswith(f.rstrip('.*')) or base.startswith(f) for f in fams):
                continue
            if any(x.startswith(base) for x in impl_keys):
                continue
            return False
        if k in impl_keys or any(k.startswith(f.rstrip('*')) for f in fams if f.endswith('*')) or any(k.startswith(f) for f in fams):
            continue
        # also accept prefix families without trailing star
        if any(k.startswith(f) for f in fams):
            continue
        return False
    return True

print('domain  items  covered  missing')
tot_items = tot_missing = 0
for dom in sorted(by_domain):
    rows = by_domain[dom]
    miss = [r for r in rows if not covered(r[2])]
    tot_items += len(rows); tot_missing += len(miss)
    if miss:
        print(f'{dom:6} {len(rows):5}  {len(rows)-len(miss):7}  {len(miss):5}  ' + ', '.join(m[0] for m in miss))
print('TOTAL', tot_items, 'missing', tot_missing)
