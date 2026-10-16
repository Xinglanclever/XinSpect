# -*- coding: utf-8 -*-
"""把 DMAR 登記進解碼器覆蓋申報。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'XinSpect.Decoders/AcpiDeep.cs'
t = io.open(p, encoding='utf-8').read()

old = '''        // ULTIMATE-1000 階段二 P2-006：中斷控制器與處理器清單（AcpiMadt）。
        "MADT",
    ];'''
new = '''        // ULTIMATE-1000 階段二：P2-006 中斷控制器與處理器清單（AcpiMadt）、
        // P2-010 IOMMU 範圍與例外（AcpiDmar）。
        "MADT", "DMAR",
    ];'''

if old in t:
    t = t.replace(old, new)
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print('已把 DMAR 登記進覆蓋申報')
else:
    print('!! 找不到目標；實際內容：')
    i = t.find('DecodedSignatures')
    print(t[i:i + 400])
