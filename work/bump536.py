import io, re

p = r'C:\Users\Administrator\XinSpect\Services\FactKeyCatalog.cs'
s = io.open(p, encoding='utf-8').read()
new = ['sv.file.shares', 'sv.print.queues']
allkeys = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
start = s.index('        "sv.')
end = s.index('\n', s.rindex('"sv.')) + 1
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
print('sv:', [k for k in after if k.startswith('sv.')])

p2 = r'C:\Users\Administrator\XinSpect\Services\FactCoverageReport.cs'
s2 = io.open(p2, encoding='utf-8').read()
old = '        ["uv.usb.trim"] = "USB 隨身碟 TRIM（UV-009）：裝置宣告不等於執行；沒插隨身碟是不適用不是 0。",\n        };'
new2 = ('        ["uv.usb.trim"] = "USB 隨身碟 TRIM（UV-009）：裝置宣告不等於執行；沒插隨身碟是不適用不是 0。",\n'
        '        ["sv.file.shares"] = "檔案共用視角（SV-006）：路徑只歸類不揭露、ACL 只報廣度不列帳號；共用存在不等於網路可及。",\n'
        '        ["sv.print.queues"] = "列印服務視角（SV-007）：實體與虛擬印表機分開計數——佇列存在不等於有裝置。",\n'
        '        };')
assert old in s2
io.open(p2, 'w', encoding='utf-8').write(s2.replace(old, new2))
print('exemptions ok')
