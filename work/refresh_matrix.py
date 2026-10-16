# -*- coding: utf-8 -*-
"""更新階段二矩陣的實作鍵欄——把已完成的項目從 todo 改成 ok。

這支腳本與 refill_impl_keys.py 同樣是「查證式」：
先看規格鍵在目錄裡找不找得到，再看有沒有已知的別名。
"""
import io
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

ALIASES = {
    'ac.table.{sig}': ('acpi.table.{sig}', 'Services/AcpiTableService.cs', 42),
    'ac.table.wpbt': ('se.wpbt', 'Services/ClosingFactsService.cs', 27),
    'ac.cedt.*': ('cxl.cfmws.count', 'Services/CxlFactsService.cs', 34),
    # SLIT 的實作叫 numa.slit.nodes——上一輪漏了這一條，
    # 而守門用「P2-007 標 todo 但 ac.srat.* 已經在目錄裡」把它逼出來。
    'ac.slit.*': ('numa.slit.nodes', 'Services/TopologyDeepenFactsService.cs', 75),
    'sb.type0.*': ('sb.type03', 'Services/SmbiosDeepFactsService.cs', 48),
    'sb.type3.*': ('sb.type03', 'Services/SmbiosDeepFactsService.cs', 48),
    'sb.type11.*': ('sb.oem.list', 'Services/SmbiosDeepFactsService.cs', 92),
    'sb.oem.strings': ('sb.oem.list', 'Services/SmbiosDeepFactsService.cs', 92),
    'sb.mem.*': ('sb.dim.*', 'Services/SmbiosDeepFactsService.cs', 65),
    'sb.psu.*': ('sb.psu', 'Services/SmbiosDeepFactsService.cs', 99),
    'sb.mc.*': ('sb.mctl', 'Services/SmbiosDeepFactsService.cs', 79),
    'sb.m2.map': ('sb.m2slot.*', 'Services/SmbiosDeepFactsService.cs', 109),
    'sb.m2slot.*': ('sb.m2slot.*', 'Services/SmbiosDeepFactsService.cs', 109),
    # P2-052 的 FIT 表：SpiFitFactsService 早就有 FitDecoder（走 SPI 快閃尾端 ＋ MMIO），
    # 而我第一版沒查就自己寫了一個平行的解碼器，然後因為「我不確定佈局」把它撤回。
    # **撤回是對的，但理由不完整**——真正的理由是「已經有實作了，我應該先查」。
    # 這與 MemoryTypeName 那個教訓同型：**在已有答案的地方重新發明。**
    'fit.present': ('fw.fit.summary', 'Services/SpiFitFactsService.cs', 17),
    # P2-053／P2-057／P2-059：規格的 bt.* 那一族（Boot Guard／信任鏈主題）
    # 對到既有的 fw.*／spi.* 實作。**這是「先 grep 再動手」的直接產出**——
    # 七列裡有三列早就做完了，而它們在矩陣上顯示為 todo。
    #
    #   bt.state        ← fw.bg.profile（SpiFitFactsService，Boot Guard 設定檔）
    #   bt.chain.list   ← fw.bootchain.summary（FirmwareAuditFactsService，開機鏈五環聚合）
    #   bt.wp           ← fw.spiperm.summary（FirmwareAuditFactsService，主控／唯讀／寫保護區域）
    #
    # 而另外四列（bt.ibb／bt.km／bt.measured_boot／bt.root）**是真的缺**——
    # 既有實作裡沒有對應的東西，所以它們留在 todo。
    #
    # ═══ 而 alias 有三種品質，不只一種 ═══
    #
    # 我第一版把 bt.state／bt.chain.list／bt.wp 三列都標成 alias，
    # 然後發現它們的真相**完全不同**：
    #
    #   bt.chain.list → fw.bootchain.summary  **有值**（Derived 聚合卡，五環）
    #                   但規格要「逐層開機元件與其量測狀態」，而既有只給彙總
    #                   → alias 成立，「逐層明細」是落差
    #
    #   bt.wp         → fw.spiperm.summary    **永遠 ReadError**：ReadSpiPerm() 直接回 null
    #                   而它的註解說「接線時只要換成 SpiFlashService 的實際讀取」
    #                   → 出處存在（spi.frap／spi.prr／spi.hsfsts）、只是那條線沒接
    #                   → alias 成立，「未接線」是待辦（而那與這一項有沒有實作無關）
    #
    #   bt.state      → fw.bg.profile         **永遠 NotSupported**
    #                   而它的理由是「需要 flash descriptor 佈局——未對準出處前不出值」
    #                   → **沒有出處可接**，那不是「未接線」而是「未開工」
    #                   → **所以它不是 alias，是 todo**
    #
    # ═══ 由此得到一條規則（alias 的第三個條件）═══
    #
    #   grep 到既有鍵**不足以**判定 alias。還要看那把鍵的 Availability 路徑：
    #     有值                                         → alias
    #     永遠 ReadError，但出處存在                    → alias（附「未接線」）
    #     **永遠 NotSupported，理由是「未對準出處」**    → **todo**（那是未開工）
    #
    # **「鍵存在」與「那一項做完了」是不同的斷言。**
    # 一個「如實說讀不到」的佔位鍵，在鍵的層面與實作完成的鍵長得一樣。
    #
    # （bt.state 因此從 alias 退回 todo：那一輪的 +3 實際上是 +2。）
    'bt.chain.list': ('fw.bootchain.summary', 'Services/FirmwareAuditFactsService.cs', 25),
    'bt.wp': ('fw.spiperm.summary', 'Services/FirmwareAuditFactsService.cs', 18),
    # P2-039／P2-047：SPI 快閃地圖的兩半，而它們**都有值**（通過 alias 的第三個條件）。
    #
    #   bios.region.size ← spi.flash_map（SpiFlashHashService:21）
    #       它的值含「BIOS 區快閃位移 0x…-0x…（大小）」——那正是「BIOS 區有多大」。
    #
    #   spi.map.*        ← spi.regions（SpiFlashService:184）
    #       把六個 FREG 區域逐項攤開（描述符／BIOS／Intel ME／GbE／平台資料／EC），
    #       而那正是「SPI 區域劃分與用途」。
    #
    # 注意 spi.map.* 的家族前綴「spi.map.」在目錄裡**不存在**——
    # 實作叫 spi.regions（不同前綴）。這與 ac.slit.* → numa.slit.nodes 同形。
    'bios.region.size': ('spi.flash_map', 'Services/SpiFlashHashService.cs', 21),
    'spi.map.*': ('spi.regions', 'Services/SpiFlashService.cs', 184),
    # P2-085：規格的 ev.testsigning 對到既有的 platform.testsigning。
    #
    # 而它不只給值，還說明了「開著意味著什麼」：
    #   「測試簽章模式開啟（**允許未經微軟簽署的核心驅動載入**）」
    # ——那比規格要的更多，而規格 P2-085 的標題正是「不安全開機旗標」。
    #
    # 通過 alias 的三個條件：鍵存在、會給值（1／0，Measured）、
    # 有出處（NtQuerySystemInformation(103) CodeIntegrityOptions）。
    'ev.testsigning': ('platform.testsigning', 'Services/BackendEnvironmentService.cs', 80),
    # P2-078／P2-084：ev.* 那一族的另一半——而它們在 uefi.* 底下。
    #
    #   ev.secureboot.*   ← uefi.secure_boot ＋ **uefi.setup_mode**
    #
    #       規格要四態（存在／啟用／已設定／雜湊世代），而：
    #         存在／啟用 → uefi.secure_boot（三態：讀不到／變數不存在／0 或 1）
    #         **已設定** → **uefi.setup_mode**（金鑰未部署／已部署）
    #
    #       **而 setup_mode 比直接讀 PK 更好**——Setup Mode 是韌體自己報的狀態，
    #       比「PK 變數存不存在」更直接。
    #       （「雜湊世代」那一半由既有的 secure.dbx 負責。）
    #
    #   ev.bootorder.var  ← uefi.boot_order_count（＋ uefi.boot_entries.count）
    #
    # 兩把都通過 alias 的三個條件：鍵存在、會給值、有出處
    # （UEFI 變數，經 GetFirmwareEnvironmentVariableEx）。
    # 而它們的權限三態也如實：拿不到 SeSystemEnvironmentPrivilege 就整組
    # InsufficientPrivilege，不推測。
    'ev.secureboot.*': ('uefi.secure_boot', 'Services/UefiBootFactsService.cs', 30),
    'ev.bootorder.var': ('uefi.boot_order_count', 'Services/UefiBootFactsService.cs', 38),
}

cat = io.open('Services/FactKeyCatalog.cs', encoding='utf-8').read()
catalog = set(re.findall(r'^\s*"([a-z0-9_.]+)",\s*$', cat, re.M))
dyn = io.open('Services/FactKeyDynamicCatalog.cs', encoding='utf-8').read()
dynamic = set(re.findall(r'\["([a-z0-9_.]+)"\]\s*=', dyn))

def exists(pattern):
    pattern = pattern.strip().strip('`')
    if not pattern or pattern in ('—', '（測試）'):
        return False
    if '*' in pattern or '{' in pattern:
        fam = pattern.split('*')[0].split('{')[0].rstrip('.')
        return any(k.startswith(fam + '.') for k in catalog) or fam + '.' in dynamic or fam in dynamic
    return pattern in catalog

ROW = re.compile(r'^\|\s*★?\s*P2-\d{3}\s*\|')
p = 'docs/ULTIMATE-1000/STAGE-02-FIRMWARE.md'
lines = io.open(p, encoding='utf-8').read().split('\n')

stats = {'ok': 0, 'alias': 0, 'todo': 0}
done = []
per_row = []
for i, line in enumerate(lines):
    if not ROW.match(line):
        continue
    cells = line.split('|')
    if len(cells) != 9:
        continue

    declared = cells[4].strip()
    keys = [k.strip().strip('`') for k in re.split(r'[／/]', declared)
            if k.strip() and k.strip() not in ('—', '（測試）')]
    if not keys:
        cells[8] = 'todo: '
        stats['todo'] += 1
        per_row.append((cells[1].strip(), 'todo', ['todo']))
        lines[i] = '|'.join(cells)
        continue

    parts, kinds = [], []
    for k in keys:
        if k in ALIASES:
            real, src, ln = ALIASES[k]
            if exists(real):
                parts.append(f'alias:{real}@{src}:{ln}')
                kinds.append('alias')
            else:
                parts.append('todo:')
                kinds.append('todo')
        elif exists(k):
            parts.append(f'ok:{k}')
            kinds.append('ok')
        else:
            parts.append('todo:')
            kinds.append('todo')

    cells[8] = '／'.join(parts) + ' '
    lines[i] = '|'.join(cells)
    top = 'todo' if 'todo' in kinds else ('alias' if 'alias' in kinds else 'ok')
    stats[top] += 1
    per_row.append((cells[1].strip(), top, kinds))
    if top != 'todo':
        done.append((cells[1].strip(), top))

io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))

print('=== 結果 ===')
print(f'  ok    ：{stats["ok"]} 列')
print(f'  alias ：{stats["alias"]} 列')
print(f'  todo  ：{stats["todo"]} 列')
print(f'  合計  ：{sum(stats.values())} 列\n')

# 統計以「列」為單位——全鍵完成才算完成。
# 有些列的規格鍵不只一把（例如 P2-029 是 sb.tpmdev／sb.proc_add），
# 那種列只要有一把沒做，整列就歸 todo。
# **這是保守的算法：寧可少報完成度，不要多報。**
# 但保守的算法必須說出自己是保守的，否則「已完成 30 列」會被讀成
# 「30 列的全部鍵都完成了」——而那對部分完成的列來說是錯的。
partial = [pid for pid, kind, kinds in per_row
           if kind == 'todo' and ('ok' in kinds or 'alias' in kinds)]
if partial:
    print(f'=== 部分完成的列（{len(partial)} 列）===')
    print('  這些列的規格鍵有一部分已實作、一部分未做；')
    print('  因為統計以「列」為單位（全鍵完成才算），它們被歸在 todo 裡。')
    for pid in partial:
        print(f'    {pid}')
    print()
print(f'=== 已完成（{len(done)} 列）===')
print('  ' + '、'.join(f'{p}({k})' for p, k in done))
