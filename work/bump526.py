import io, re

p = r'C:\Users\Administrator\XinSpect\Services\FactKeyCatalog.cs'
s = io.open(p, encoding='utf-8').read()

new = ['st.fs.refs', 'st.fs.volumes', 'st.fs.vss']
allkeys = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)

start = s.index('        "st.')
end = s.index('\n', s.rindex('"st.')) + 1
block = s[start:end]
block_keys = re.findall(r'^\s*"([a-z0-9._]+)",\s*$', block, re.M)
print('existing st block:', block_keys)

merged = sorted(set(block_keys) | set(new))
assert len(merged) == len(set(merged))

outside = list(allkeys)
for k in block_keys:
    outside.remove(k)
dupes = [k for k in merged if k in outside]
assert not dupes, f'collision: {dupes}'

s = s[:start] + '\n'.join(f'        "{k}",' for k in merged) + '\n' + s[end:]
io.open(p, 'w', encoding='utf-8').write(s)

after = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
print('total', len(after), 'dups', len(after) - len(set(after)))
print('inversions', [(a, b) for a, b in zip(after, after[1:]) if a > b][:5])
print('st block:', [k for k in after if k.startswith('st.')])
