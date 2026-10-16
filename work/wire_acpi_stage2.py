# -*- coding: utf-8 -*-
"""把 MADT／DMAR／SRAT 三組 ACPI 事實接進 EvidenceLabService.AllFacts，並更新矩陣。

接線位置與其他 ACPI 事實同一處（AcpiFacts 之後），理由：
它們的來源都是同一個 IAcpiTableSource，分成兩次讀取只會多讀一次表。
"""
import io
import sys

sys.stdout.reconfigure(encoding='utf-8')

# ① 接線
p = 'Services/EvidenceLabService.cs'
t = io.open(p, encoding='utf-8').read()

old = """        AcpiFacts = AcpiService.Collect(source, DateTimeOffset.UtcNow)
            // FW-024 FPDT：同一份表來源，另外解 POST 階段時長（見 AcpiFpdtFactsService 的版面出處）。
            .Concat([AcpiFpdtFactsService.Collect(source, DateTimeOffset.UtcNow)]).ToList();"""

new = """        var acpiAt = DateTimeOffset.UtcNow;
        AcpiFacts = AcpiService.Collect(source, acpiAt)
            // FW-024 FPDT：同一份表來源，另外解 POST 階段時長（見 AcpiFpdtFactsService 的版面出處）。
            .Concat([AcpiFpdtFactsService.Collect(source, acpiAt)])
            // ULTIMATE-1000 階段二：這三張表共用同一個來源，所以接在同一次讀取上，
            // 不另外再讀一遍表列——每多讀一次就多一次「兩次讀到的表不一樣」的可能。
            //
            //   P2-006 MADT：中斷控制器與處理器清單
            //   P2-010 DMAR：IOMMU 範圍與例外（保留記憶體區域）
            //   P2-007 SRAT：處理器與記憶體的節點歸屬（補上 SLIT 缺的那一半）
            .Concat(AcpiMadtFactsService.Collect(source, acpiAt))
            .Concat(AcpiDmarFactsService.Collect(source, acpiAt))
            .Concat(AcpiSratFactsService.Collect(source, acpiAt))
            .ToList();"""

if old in t:
    t = t.replace(old, new)
    io.open(p, 'w', encoding='utf-8', newline='').write(t)
    print('已接進 EvidenceLabService.LoadAcpi（與其他 ACPI 事實同一次讀取）')
else:
    print('!! 找不到接線點；實際內容：')
    i = t.find('AcpiFacts = AcpiService.Collect')
    print(t[max(0, i - 200):i + 400])
