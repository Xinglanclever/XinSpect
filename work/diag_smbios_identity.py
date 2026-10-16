# -*- coding: utf-8 -*-
"""診斷：Type19 與 Type32 的 helper 為什麼爆 Destination array。

懷疑點：Type32 只給 0x0B 長度，但 Boot() 讀到 0x0E；
Type19 的 0x0F 長度與 MemMapDetail 讀到 0x0E。
把每個 helper 的長度與讀取位移列出來比對。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

svc = io.open('Services/SmbiosIdentityFactsService.cs', encoding='utf-8').read()

# 列出每個方法裡 ByteAt/WordAt/DwordAt 的最大位移
print('=== 服務裡每個方法的讀取位移 ===')
for m in re.finditer(r'private static HardwareFact (\w+)\(', svc):
    name = m.group(1)
    start = m.end()
    # 找方法結尾（下一個 private static 或 檔案結尾）
    nxt = svc.find('private static', start)
    body = svc[start:nxt if nxt > 0 else len(svc)]
    offs = set()
    for om in re.finditer(r'(?:ByteAt|WordAt|DwordAt)\(0x([0-9A-Fa-f]+)\)', body):
        offs.add(int(om.group(1), 16))
    for om in re.finditer(r'ByteAt\((0x[0-9A-Fa-f]+) \+ i\)', body):
        offs.add(int(om.group(1), 16) + 5)
    if offs:
        print(f'  {name}: 讀到最大位移 0x{max(offs):X}（至少需要 {max(offs)+1} bytes）')

print()
print('=== 測試 helper 給的長度 ===')
t = io.open('Tests/SmbiosIdentityFactsTests.cs', encoding='utf-8').read()
for name in ['Type0', 'Type1', 'Type2', 'Type3', 'Type19', 'Type32']:
    m = re.search(r'private static SmbiosStruct ' + name + r'\([^)]*\)\s*\n?\s*\{', t)
    if not m:
        print(f'  {name}: 找不到')
        continue
    seg = t[m.end():m.end() + 900]
    lm = re.search(r'new byte\[(0x[0-9A-Fa-f]+|\d+)\]', seg)
    if lm:
        n = int(lm.group(1), 16) if lm.group(1).startswith('0x') else int(lm.group(1))
        print(f'  {name}: 配置 {n} bytes（0x{n:X}）')
    else:
        print(f'  {name}: 找不到長度')
