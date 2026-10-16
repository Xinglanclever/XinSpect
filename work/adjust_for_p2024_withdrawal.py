# -*- coding: utf-8 -*-
"""測試與目錄也要跟著 P2-024 的撤回一起調整。"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# ① 測試：移除 Type19 相關的四條與 helper
p = 'Tests/SmbiosIdentityFactsTests.cs'
t = io.open(p, encoding='utf-8').read()

# 移除 helper
start = t.find('    private static SmbiosStruct Type19(')
if start > 0:
    end = t.find('    private static SmbiosStruct Type32(')
    t = t[:start] + t[end:]
    print('已移除 Type19 helper')

# 移除四條測試
for name in ['記憶體映射要算出來並標型別', '記憶體映射要說它與UEFI的分類不一定一致',
             '沒有Type19時要說UEFI映射是另一個來源']:
    m = re.search(r'    \[Fact\]\n    public void ' + name + r'\(\)\n    \{.*?\n    \}\n\n',
                  t, re.S)
    if m:
        t = t[:m.start()] + t[m.end():]
        print(f'已移除測試：{name}')

# 移除所有 Type19(...) 的呼叫
t = re.sub(r'\s*Type19\([^)]*\),', '', t)
t = re.sub(r',\s*Type19\([^)]*\)', '', t)

io.open(p, 'w', encoding='utf-8', newline='').write(t)
leftover = 'Type19' in t or 'Type 19' in t
print(f'測試殘留 Type19：{leftover}')

# ② 目錄：移除 sb.memmap 兩把（它們改回 todo）
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
for k in ('"sb.memmap",', '"sb.memmap.count",'):
    if k in text:
        text = text.replace('        ' + k + '\n', '')
        print(f'目錄移除 {k}')
text = re.sub(r'共 (\d+) 個（', lambda m: f'共 {int(m.group(1)) - 2} 個（', text, count=1)
io.open(p, 'w', encoding='utf-8', newline='').write(text)
