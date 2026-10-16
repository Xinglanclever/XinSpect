# -*- coding: utf-8 -*-
"""修 Assert.Equal(常數, uint) 的多載解析問題（xUnit 會選到 DateTime 多載）。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Tests/AcpiSratTests.cs'
t = io.open(p, encoding='utf-8').read()

fixes = [
    ('Assert.Equal(1u, m.ProximityDomain);', 'Assert.Equal(1u, m.ProximityDomain);'),
    ('Assert.Equal(2u, g.ProximityDomain);', 'Assert.Equal(2u, g.ProximityDomain);'),
    ('Assert.Equal(0, g.DeviceHandleType);', 'Assert.Equal((byte)0, g.DeviceHandleType);'),
]
n = 0
for old, new in fixes:
    if old in t and old != new:
        t = t.replace(old, new)
        n += 1

# 行 114 是 Assert.Equal(0, g.DeviceHandleType) 嗎？直接看
io.open(p, 'w', encoding='utf-8', newline='').write(t)

lines = t.split('\n')
print('行 114：', lines[113].strip() if len(lines) > 113 else '（無）')
for i, l in enumerate(lines):
    if 'Assert.Equal(' in l and 'DeviceHandleType' in l:
        print(f'  找到 DeviceHandleType 的斷言在第 {i+1} 行：{l.strip()}')
print(f'改了 {n} 處')
