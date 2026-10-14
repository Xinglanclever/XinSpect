import io, re

p = r'C:\Users\Administrator\XinSpect\Services\FactKeyCatalog.cs'
s = io.open(p, encoding='utf-8').read()


def insert_block(s, prefix, new_keys):
    allkeys = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
    start = s.index('        "' + prefix + '.')
    end = s.index('\n', s.rindex('"' + prefix + '.')) + 1
    block_keys = re.findall(r'^\s*"([a-z0-9._]+)",\s*$', s[start:end], re.M)
    merged = sorted(set(block_keys) | set(new_keys))
    outside = list(allkeys)
    for k in block_keys:
        outside.remove(k)
    dupes = [k for k in merged if k in outside]
    assert not dupes, f'{prefix} collision: {dupes}'
    s = s[:start] + '\n'.join(f'        "{k}",' for k in merged) + '\n' + s[end:]
    return s, merged


s, st = insert_block(s, 'st', ['st.iscsi.paths', 'st.iscsi.sessions'])
s, dv = insert_block(s, 'dv', ['dv.vm.count', 'dv.vm.inventory'])
io.open(p, 'w', encoding='utf-8').write(s)

after = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
print('total', len(after), 'dups', len(after) - len(set(after)))
print('inversions', [(a, b) for a, b in zip(after, after[1:]) if a > b][:5])
print('st:', [k for k in after if k.startswith('st.')])
print('dv:', [k for k in after if k.startswith('dv.')])
