import io, re

p = r'C:\Users\Administrator\XinSpect\Services\FactKeyCatalog.cs'
s = io.open(p, encoding='utf-8').read()
new = ['uv.cdat', 'uv.usb.trim']
allkeys = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
start = s.index('        "uv.')
end = s.index('\n', s.rindex('"uv.')) + 1
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
print('uv:', [k for k in after if k.startswith('uv.')])

p2 = r'C:\Users\Administrator\XinSpect\Services\FactCoverageReport.cs'
s2 = io.open(p2, encoding='utf-8').read()
old = '        ["mu.spatial.formats"] = "空間音訊格式（MU-006）：格式註冊名稱清單；支援格式與目前使用中是兩件事，格式名不在地化。",\n        };'
new2 = ('        ["mu.spatial.formats"] = "空間音訊格式（MU-006）：格式註冊名稱清單；支援格式與目前使用中是兩件事，格式名不在地化。",\n'
        '        ["uv.cdat"] = "CDAT 結構表（UV-005）：只到版本與條目數，**不逐條解讀**——規範版本與條目語意是外部規格，沒對準出處就不出值。",\n'
        '        ["uv.usb.trim"] = "USB 隨身碟 TRIM（UV-009）：裝置宣告不等於執行；沒插隨身碟是不適用不是 0。",\n'
        '        };')
assert old in s2
io.open(p2, 'w', encoding='utf-8').write(s2.replace(old, new2))
print('exemptions ok')
