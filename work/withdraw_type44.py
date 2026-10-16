# -*- coding: utf-8 -*-
"""P2-029 的 Type 44 那半退回未開工。

理由：我把「處理器型別」放在 @0x05，而參考控制代碼是 u16@0x04
——兩者重疊，所以型別會蓋掉控制代碼的高位元組。
0x0400 被讀成 0x0300，差值固定 0x0100，那正是「被覆寫」的形狀。

真正的佈局應該是：控制代碼 u16@0x02、參考控制代碼 u16@0x04、
處理器型別 u8@0x06。**我沒有可靠依據**，所以不做。

留下的教訓：我用兩個獨立的錯誤（helper 沒傳 handle ＋ 服務位移錯）
疊出了同一個 0x0300，然後花了兩輪在 helper 的參數上找原因，
而真正的線索一直是那個數字本身。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

# ① 服務：移除 Type 44，保留 P2-030／P2-031
p = 'Services/SmbiosStructureFactsService.cs'
t = io.open(p, encoding='utf-8').read()

start = t.find('    // ── Type 44：處理器附加資訊')
end = t.find('    // ── P2-030：結構長度政策')
if start < 0 or end < 0:
    print('!! 找不到 Type 44 區塊')
    sys.exit(1)

replacement = '''    // ── Type 44：處理器附加資訊（未開工）──────────────────────────────

    // P2-029 的 Type 44 那半退回未開工。欄位佈局我沒有可靠依據：
    // 參考控制代碼是 u16，而我把「處理器型別」放在 @0x05
    // ——兩者重疊，型別會蓋掉控制代碼的高位元組。
    //
    // 徵狀是 0x0400 被讀成 0x0300（差值固定 0x0100）——那正是「被覆寫」的形狀。
    // 我花了兩輪在測試 helper 的參數上找原因，而線索一直是那個數字本身。
    //
    // 修好的路徑：找到 SMBIOS 規格 Table 7.45（Type 44）的欄位圖。
    // Type 43 的那一半（sb.tpmdev）已完成並保留。

'''

t = t[:start] + replacement + t[end:]
t = t.replace('    public const string ProcAddKey = "sb.proc_add";\n', '')
t = t.replace('        string[] keys = [ProcAddKey, TrailingKey, BoundsKey];',
              '        string[] keys = [TrailingKey, BoundsKey];')
t = t.replace('''        return
        [
            ProcAdd(at, structs),
            Trailing(at, structs),
            Bounds(at, structs),
        ];''', '''        return
        [
            Trailing(at, structs),
            Bounds(at, structs),
        ];''')
t = t.replace('''/// SMBIOS Type 44（處理器附加資訊）＋ P2-030／P2-031 的結構政策事實
/// （ULTIMATE-1000 階段二）。''',
'''/// P2-030／P2-031 的結構政策事實（ULTIMATE-1000 階段二）。
///
/// <para><b>P2-029 的 Type 44 那半不在這裡——它退回未開工</b>（欄位佈局沒有依據）。
/// Type 43 那一半已完成，在 <see cref="SmbiosTpmFactsService"/>。</para>''')
t = t.replace('''/// SMBIOS 各型別的規範最小長度（不含字串表）。
    /// 只列本專案有解碼器的型別''', '''/// SMBIOS 各型別的規範最小長度（不含字串表）。
    /// 只列本專案有解碼器的型別''')

io.open(p, 'w', encoding='utf-8', newline='').write(t)
print('服務：已移除 Type 44')

# ② 測試：移除 Type44 helper 與三條測試
p = 'Tests/SmbiosStructureFactsTests.cs'
t = io.open(p, encoding='utf-8').read()
start = t.find('    /// <summary>Type 4 的預設控制代碼')
end = t.find('    // ── 三態 ──')
if start > 0 and end > 0:
    t = t[:start] + '''    /// <summary>Type 4：插槽字串@0x04，長度 0x1A。</summary>
    private static SmbiosStruct Type4(string socket, ushort handle = 0x0400)
    {
        var d = new byte[0x1A];
        d[0x04] = 1;
        return Struct(4, d, handle, socket);
    }

''' + t[end:]
    print('測試：已精簡 helper')

for name in ['沒有Type44時要說未列是常態', '附加資訊要關聯到Type4的插槽字串',
             '懸空參照要標Unknown並說出來', '附加資訊要說出型別相依的內容未被解讀']:
    m = re.search(r'    \[Fact\]\n    public void ' + name + r'\(\)\n    \{.*?\n    \}\n\n', t, re.S)
    if m:
        t = t[:m.start()] + t[m.end():]
        print(f'測試：已移除 {name}')

t = t.replace('''    // ── Type 44 ─────────────────────────────────────────────────────────

''', '''    // ── Type 44：未開工（P2-029 的後半退回）────────────────────────────

    // 欄位佈局沒有可靠依據，所以那把鍵不產出，也沒有測試。

''')
t = t.replace('    private const ushort CpuHandle = 0x0400;\n\n', '')
io.open(p, 'w', encoding='utf-8', newline='').write(t)
print(f'測試殘留 Type44：{"Type44" in t}')

# ③ 目錄：移除 sb.proc_add
p = 'Services/FactKeyCatalog.cs'
text = io.open(p, encoding='utf-8').read()
if '"sb.proc_add",' in text:
    text = text.replace('        "sb.proc_add",\n', '')
    text = re.sub(r'共 (\d+) 個（', lambda m: f'共 {int(m.group(1)) - 1} 個（', text, count=1)
    io.open(p, 'w', encoding='utf-8', newline='').write(text)
    print('目錄：已移除 sb.proc_add')
