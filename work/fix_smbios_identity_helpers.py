# -*- coding: utf-8 -*-
"""修正測試 helper 的長度，讓它符合服務實際讀取的位移 ＋ 寬度。

服務讀取的最大位移（含寬度）：
  Type0  @0x14 DwordAt → 需 0x18（設定 0x14 而不是 0x20 是為了測 DwordAt 路徑）
  Type1  @0x08–0x17 → 需 0x18
  Type19 @0x04 DwordAt、@0x0C DwordAt、@0x0E → 需 0x0F ✓
  Type32 @0x06、@0x08–0x0D → 需 0x0E
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Tests/SmbiosIdentityFactsTests.cs'
t = io.open(p, encoding='utf-8').read()

fixes = [
    # Type0：ROM 大小走 0xFF 分支時要 DwordAt(0x14)，長度需 0x18
    ('        var d = new byte[0x14];\n        d[0x04] = 1; d[0x05] = 2; d[0x08] = 3;',
     '        // 0x18 而非 0x14：ROM 大小走 0xFF 分支時讀 DwordAt(0x14)，\n'
     '        // 那需要索引 0x14..0x17 都有效＝長度至少 0x18。\n'
     '        var d = new byte[0x18];\n        d[0x04] = 1; d[0x05] = 2; d[0x08] = 3;'),
    # Type32：POST 結果讀 0x08..0x0D，長度需 0x0E
    ('    private static SmbiosStruct Type32(byte status)\n    {\n        var d = new byte[0x0B];',
     '    private static SmbiosStruct Type32(byte status)\n    {\n'
     '        // 0x0E 而非 0x0B：POST 結果讀 0x08..0x0D。\n'
     '        var d = new byte[0x0E];'),
]

n = 0
for old, new in fixes:
    if old in t:
        t = t.replace(old, new)
        n += 1
    else:
        print(f'!! 找不到：{old[:60]!r}')

io.open(p, 'w', encoding='utf-8', newline='').write(t)
print(f'修了 {n} 處')
