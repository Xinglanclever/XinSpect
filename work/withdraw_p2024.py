# -*- coding: utf-8 -*-
"""P2-024（Type 19 記憶體映射）退回未開工。

理由與 P2-008 HMAT 相同：欄位佈局我沒有可靠依據。
  * size 是 u32@0x0C 還是 end address u32@0x08？
  * 型別是 u8@0x0C 還是 u8@0x0E？
專案裡 SmbiosService 沒有解 Type 19，所以沒有可對照的權威。

一個位址位移不確定的解碼器會給出**看起來合理的錯誤位址**——
那比沒有更糟。

同時刪掉我自己寫的 MemoryTypeName：
SmbiosService.MemoryTypeName 已經存在（十六進位代碼），
而我寫了一份十進位的。同一份資料兩個解讀處——這次是我自己製造的。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')
p = 'Services/SmbiosIdentityFactsService.cs'
t = io.open(p, encoding='utf-8').read()

# ① 移除 MemMap 與 MemMapDetail 兩個方法（含註解區塊）
start = t.find('    // ── Type 19：記憶體映射')
end = t.find('    // ── Type 32：開機資訊')
if start < 0 or end < 0:
    print('!! 找不到 Type 19 區塊')
    sys.exit(1)

replacement = '''    // ── Type 19：記憶體映射（未開工）────────────────────────────────────

    // P2-024 退回未開工。欄位佈局我沒有可靠依據：
    //   * size 是 u32@0x0C，還是 end address u32@0x08？
    //   * 型別是 u8@0x0C，還是 u8@0x0E？
    // 專案裡 SmbiosService 沒有解 Type 19，所以沒有可對照的權威。
    //
    // 一個位址位移不確定的解碼器會給出**看起來合理的錯誤位址**
    // ——那比沒有更糟，理由與 P2-008 HMAT 的撤回相同。
    //
    // 修好的路徑：找到 SMBIOS 規格 Table 7.20（Type 19）的欄位圖，
    // 或找到一份可信的開源實作（dmidecode 的 dmi_memory_array_mapped_address）
    // 可以逐欄位對照。在那之前，sb.memmap 這兩把鍵不產出。

'''
t = t[:start] + replacement + t[end:]

# ② 移除我寫的 MemoryTypeName（改用既有的 SmbiosService.MemoryTypeName）
ms = t.find('    private static string MemoryTypeName(byte code) => code switch')
if ms > 0:
    me = t.find('    };', ms)
    if me > 0:
        t = t[:ms] + t[me + len('    };\n\n'):]
        print('已移除自寫的 MemoryTypeName（改用 SmbiosService.MemoryTypeName）')

# ③ Collect 不再產出那兩把鍵
t = t.replace('            MemMap(at, s19),\n            MemMapDetail(at, s19),\n', '')
t = t.replace('        var s19 = byType.TryGetValue(19, out var list19) ? list19 : [];\n', '')

# ④ Na 的鍵清單移除那兩把
t = t.replace('            MemMapCountKey, MemMapKey, BootKey, BootStatusKey,\n',
              '            BootKey, BootStatusKey,\n')

# ⑤ 移除未使用的常數
t = t.replace('    public const string MemMapKey = "sb.memmap";\n', '')
t = t.replace('    public const string MemMapCountKey = "sb.memmap.count";\n', '')

# ⑥ 類別註解改掉 P2-024 的宣告
t = t.replace(
    '/// SMBIOS Type 0–3 的全欄位事實（ULTIMATE-1000 階段二 P2-019）、\n'
    '/// Type 19 記憶體映射（P2-024）、Type 32 開機資訊（P2-025）。',
    '/// SMBIOS Type 0–3 的全欄位事實（ULTIMATE-1000 階段二 P2-019）\n'
    '/// 與 Type 32 開機資訊（P2-025）。\n'
    '///\n'
    '/// <para><b>P2-024（Type 19 記憶體映射）不在這裡——它退回未開工。</b>\n'
    '/// 欄位佈局沒有可靠依據，見檔尾的說明。</para>')

io.open(p, 'w', encoding='utf-8', newline='').write(t)
print('已把 P2-024 退回未開工')

# 驗證殘留
leftover = [m for m in ('MemMap', 'MemoryTypeName', 's19', 'list19') if m in t]
print(f'殘留檢查：{leftover if leftover else "（乾淨）"}')
