# -*- coding: utf-8 -*-
"""修掉 AcpiHmatTests 裡 Assert.Equal(常數, uint) 的多載解析問題。

Assert.Equal(3, l.Hierarchy) 這種寫法會讓 xUnit 選到 DateTime 的多載
（因為兩個引數都能隱式轉成 DateTime 的候選），要加上型別標註。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Tests/AcpiHmatTests.cs'
t = io.open(p, encoding='utf-8').read()

fixes = [
    ('Assert.Equal(3, l.Hierarchy);', 'Assert.Equal((byte)3, l.Hierarchy);'),
    ('Assert.Equal(120, l.ReadLatencyNs);', 'Assert.Equal(120u, l.ReadLatencyNs);'),
    ('Assert.Equal(140, l.WriteLatencyNs);', 'Assert.Equal(140u, l.WriteLatencyNs);'),
    ('Assert.Equal(5, b.Hierarchy);', 'Assert.Equal((byte)5, b.Hierarchy);'),
    ('Assert.Equal(88, l.ReadLatencyNs);', 'Assert.Equal(88u, l.ReadLatencyNs);'),
    ('Assert.Equal(99, l.WriteLatencyNs);', 'Assert.Equal(99u, l.WriteLatencyNs);'),
]
n = 0
for old, new in fixes:
    if old in t:
        t = t.replace(old, new)
        n += 1
    else:
        print('!! 找不到：', old)
io.open(p, 'w', encoding='utf-8', newline='').write(t)
print(f'修了 {n} 處')
