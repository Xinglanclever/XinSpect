import io, re

p = r'C:\Users\Administrator\XinSpect\Services\FactKeyCatalog.cs'
s = io.open(p, encoding='utf-8').read()
new = ['mu.drm.components', 'mu.spatial.formats']
allkeys = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
start = s.index('        "mu.')
end = s.index('\n', s.rindex('"mu.')) + 1
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
print('mu:', [k for k in after if k.startswith('mu.')])

p2 = r'C:\Users\Administrator\XinSpect\Services\FactCoverageReport.cs'
s2 = io.open(p2, encoding='utf-8').read()
old = '        ["uf.retry."] = "更新失敗重試鏈（UF-002）：wu.history 的聚合視角；重試不等於失敗，門檻看失敗次數不是嘗試次數。",\n        };'
new2 = ('        ["uf.retry."] = "更新失敗重試鏈（UF-002）：wu.history 的聚合視角；重試不等於失敗，門檻看失敗次數不是嘗試次數。",\n'
        '        ["mu.drm.components"] = "DRM 元件盤點（MU-003）：固定候選機碼清單的存在性與版本；元件存在不等於可播，不讀授權、金鑰或內容識別碼。",\n'
        '        ["mu.spatial.formats"] = "空間音訊格式（MU-006）：格式註冊名稱清單；支援格式與目前使用中是兩件事，格式名不在地化。",\n'
        '        };')
assert old in s2
io.open(p2, 'w', encoding='utf-8').write(s2.replace(old, new2))
print('exemptions ok')
