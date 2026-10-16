# -*- coding: utf-8 -*-
"""修掉 AcpiDmar 的 DmarAtsr 建構錯誤。

錯誤訊息：引數 2 無法從 'ushort' 轉換成 'byte'——但第 91 行是 Drhds 的區塊？
實際上是 record 的宣告順序與建構子呼叫的順序不一致。

DmarAtsr 宣告：(byte Flags, ushort Segment, DeviceScopes)
呼叫：        (table[off+4]        → byte ✓
               BitConverter.UInt16 → ushort ✓
               DeviceScopes(...))  ✓

看起來沒問題。真正錯的是 DmarDrhd 的宣告——
  record DmarDrhd(byte Flags, byte Segment, ulong RegisterBase, bool CoversWholeMachine, scopes)
  Segment 在 DRHD 裡是 u16@6，不是 byte。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'XinSpect.Decoders/AcpiDmar.cs'
t = io.open(p, encoding='utf-8').read()

old = '''public readonly record struct DmarDrhd(
    byte Flags, byte Segment, ulong RegisterBase,
    bool CoversWholeMachine, IReadOnlyList<(ushort Start, ushort End)> DeviceScopes);'''
new = '''public readonly record struct DmarDrhd(
    byte Flags, ushort Segment, ulong RegisterBase,
    bool CoversWholeMachine, IReadOnlyList<(ushort Start, ushort End)> DeviceScopes);'''

if old in t:
    t = t.replace(old, new)
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print('已修 DmarDrhd.Segment 型別 byte → ushort')
else:
    print('!! 找不到')
