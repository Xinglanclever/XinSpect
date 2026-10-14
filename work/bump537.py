import io, re

p = r'C:\Users\Administrator\XinSpect\Services\FactKeyCatalog.cs'
s = io.open(p, encoding='utf-8').read()
new = ['sg.audio', 'sg.usbslow']
allkeys = re.findall(r'^\s*"([a-z]{2}\.[a-z0-9._]*)",\s*$', s, re.M)
start = s.index('        "sg.')
end = s.index('\n', s.rindex('"sg.')) + 1
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
print('sg:', [k for k in after if k.startswith('sg.')])

p2 = r'C:\Users\Administrator\XinSpect\Services\FactCoverageReport.cs'
s2 = io.open(p2, encoding='utf-8').read()
old = '        ["sv.print.queues"] = "列印服務視角（SV-007）：實體與虛擬印表機分開計數——佇列存在不等於有裝置。",\n        };'
new2 = ('        ["sv.print.queues"] = "列印服務視角（SV-007）：實體與虛擬印表機分開計數——佇列存在不等於有裝置。",\n'
        '        ["sg.usbslow"] = "USB 為什麼慢（SG-008）：複用 UsbLinkDecoder.Judge 的判讀；分清楚是線材還是埠（處置不同），空清單不講成正常。",\n'
        '        ["sg.audio"] = "聲音為什麼斷（SG-010）：只並列 APO／端點／預設三個面向，不斷言元凶——排除法的起點。",\n'
        '        };')
assert old in s2
io.open(p2, 'w', encoding='utf-8').write(s2.replace(old, new2))
print('exemptions ok')
