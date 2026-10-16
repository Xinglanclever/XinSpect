# -*- coding: utf-8 -*-
"""修正 AcpiHmatTests 的合成表：尾端要多一個 Entry Base Unit（u32）。

規格（ACPI 6.5 Table 5.36／5.37）：可變長度清單之後是
  Entry Base Unit u32、然後才是 Read/Write Bandwidth（u16×2）或 Read/Write Latency（u16×2）。
原本的合成表漏了那 4 bytes，於是解碼器讀到的位置往前偏了 4——
**測試抓到了實際的規格錯誤**（我在寫解碼器時也漏了同一項，兩邊錯得一樣才會看起來一致）。

正確的尾端佈局（以延遲階層為例）：
  [0..32)           固定標頭
  [32..32+listBytes) initiator／target 清單
  [+0..+4) Entry Base Unit（u32，量測單位，本解碼器不換算）
  [+4..+6) Read Latency（u16，ns）
  [+6..+8) Write Latency（u16，ns）
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Tests/AcpiHmatTests.cs'
t = io.open(p, encoding='utf-8').read()

# ① 延遲階層的合成表：32 + list + 12 → 32 + list + 16，並跳過 Entry Base Unit
old_lat = '''        int listBytes = appendLists ? (int)(initiators + targets) * 4 : 0;
        var b = new byte[32 + listBytes + 12];
        WriteHeader(b, 1, (uint)b.Length);
        b[6] = readOnly ? (byte)1 : (byte)0;
        BitConverter.GetBytes(initiators).CopyTo(b, 12);
        BitConverter.GetBytes(targets).CopyTo(b, 16);
        BitConverter.GetBytes((uint)hierarchy).CopyTo(b, 24);
        int t = 32 + listBytes;
        BitConverter.GetBytes(readNs).CopyTo(b, t);
        BitConverter.GetBytes(writeNs).CopyTo(b, t + 2);
        return b;'''
new_lat = '''        int listBytes = appendLists ? (int)(initiators + targets) * 4 : 0;
        var b = new byte[32 + listBytes + 16];
        WriteHeader(b, 1, (uint)b.Length);
        b[6] = readOnly ? (byte)1 : (byte)0;
        BitConverter.GetBytes(initiators).CopyTo(b, 12);
        BitConverter.GetBytes(targets).CopyTo(b, 16);
        BitConverter.GetBytes((uint)hierarchy).CopyTo(b, 24);
        int t = 32 + listBytes;
        BitConverter.GetBytes(1u).CopyTo(b, t);          // Entry Base Unit（單位基準，不換算）
        BitConverter.GetBytes(readNs).CopyTo(b, t + 4);
        BitConverter.GetBytes(writeNs).CopyTo(b, t + 6);
        return b;'''

# ② 頻寬階層同理
old_bw = '''        int listBytes = appendLists ? (int)(initiators + targets) * 4 : 0;
        var b = new byte[32 + listBytes + 12];
        WriteHeader(b, 2, (uint)b.Length);
        b[6] = readOnly ? (byte)1 : (byte)0;
        BitConverter.GetBytes(initiators).CopyTo(b, 12);
        BitConverter.GetBytes(targets).CopyTo(b, 16);
        BitConverter.GetBytes((uint)hierarchy).CopyTo(b, 24);
        int t = 32 + listBytes;
        BitConverter.GetBytes(readMb).CopyTo(b, t);
        BitConverter.GetBytes(writeMb).CopyTo(b, t + 2);
        return b;'''
new_bw = '''        int listBytes = appendLists ? (int)(initiators + targets) * 4 : 0;
        var b = new byte[32 + listBytes + 16];
        WriteHeader(b, 2, (uint)b.Length);
        b[6] = readOnly ? (byte)1 : (byte)0;
        BitConverter.GetBytes(initiators).CopyTo(b, 12);
        BitConverter.GetBytes(targets).CopyTo(b, 16);
        BitConverter.GetBytes((uint)hierarchy).CopyTo(b, 24);
        int t = 32 + listBytes;
        BitConverter.GetBytes(1u).CopyTo(b, t);          // Entry Base Unit
        BitConverter.GetBytes(readMb).CopyTo(b, t + 4);
        BitConverter.GetBytes(writeMb).CopyTo(b, t + 6);
        return b;'''

# ③ 惡意清單數量那條：表長也要跟著變成 32+16
old_evil = '''        var b = new byte[32 + 12];
        WriteHeader(b, 1, (uint)b.Length);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(b, 12);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(b, 16);
        BitConverter.GetBytes(1u).CopyTo(b, 24);'''
new_evil = '''        var b = new byte[32 + 16];
        WriteHeader(b, 1, (uint)b.Length);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(b, 12);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(b, 16);
        BitConverter.GetBytes(1u).CopyTo(b, 24);'''

n = 0
for old, new in ((old_lat, new_lat), (old_bw, new_bw), (old_evil, new_evil)):
    if old in t:
        t = t.replace(old, new)
        n += 1
    else:
        print('!! 找不到區塊：', old.split('\n')[0][:60])

io.open(p, 'w', encoding='utf-8', newline='').write(t)
print(f'修了 {n} 個合成表')
