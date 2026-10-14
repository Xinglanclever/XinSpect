import io, re

p = r'C:\Users\Administrator\XinSpect\Services\FactKeyCatalog.cs'
s = io.open(p, encoding='utf-8').read()
new = ['uf.retry.count', 'uf.retry.summary']
allkeys = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
start = s.index('        "uf.')
end = s.index('\n', s.rindex('"uf.')) + 1
block_keys = re.findall(r'^\s*"([a-z0-9._]+)",\s*$', s[start:end], re.M)
merged = sorted(set(block_keys) | set(new))
outside = list(allkeys)
for k in block_keys:
    outside.remove(k)
dupes = [k for k in merged if k in outside]
assert not dupes, dupes
s = s[:start] + '\n'.join(f'        "{k}",' for k in merged) + '\n' + s[end:]
io.open(p, 'w', encoding='utf-8').write(s)
after = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
print('total', len(after), 'dups', len(after) - len(set(after)))
print('inversions', [(a, b) for a, b in zip(after, after[1:]) if a > b][:3])
print('uf:', [k for k in after if k.startswith('uf.')])

p2 = r'C:\Users\Administrator\XinSpect\Services\FactCoverageReport.cs'
s2 = io.open(p2, encoding='utf-8').read()
old = '        ["gp.pollrate"] = "滑鼠輪詢率（GP-004）：Raw Input 取樣量測；取樣窗內無輸入是不適用不是 0 Hz，且取樣期間必須標註。",\n        };'
new2 = ('        ["gp.pollrate"] = "滑鼠輪詢率（GP-004）：Raw Input 取樣量測；取樣窗內無輸入是不適用不是 0 Hz，且取樣期間必須標註。",\n'
        '        ["uf.retry."] = "更新失敗重試鏈（UF-002）：wu.history 的聚合視角；重試不等於失敗，門檻看失敗次數不是嘗試次數。",\n'
        '        };')
assert old in s2
io.open(p2, 'w', encoding='utf-8').write(s2.replace(old, new2))
print('exemptions ok')
