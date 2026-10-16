# -*- coding: utf-8 -*-
"""修 Type19 helper 的長度：@0x0C CopyTo 4 bytes 需要 0x10。"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Tests/SmbiosIdentityFactsTests.cs'
t = io.open(p, encoding='utf-8').read()

old = '''    private static SmbiosStruct Type19(uint start, uint size, byte memType)
    {
        var d = new byte[0x0F];'''
new = '''    private static SmbiosStruct Type19(uint start, uint size, byte memType)
    {
        // 0x10 而非 0x0F：@0x0C 的 CopyTo 要寫 4 個位元組（0x0C..0x0F）。
        var d = new byte[0x10];'''

if old in t:
    t = t.replace(old, new)
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print('已修 Type19 的長度 0x0F → 0x10')
else:
    print('!! 找不到 Type19 helper')
