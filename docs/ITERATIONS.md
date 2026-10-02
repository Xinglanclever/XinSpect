# XinSpect 迭代帳本

> 制度依 PROGRAM-EVEREST-V7 §18.3：每輪只做一件事 → 純解碼器先 TDD（紅→綠）→ 讀不到標三態
> → 全套測試綠才算數 → commit（繁中、明確路徑、無 Co-Authored-By）→ 記錄於此（輪次、內容、測試數、commit）。
> 此檔自 2026-10-03 ITER21 批次起建立；先前的迭代史見 git log 與各 HANDOFF-*.md。

## 測試基線

| 日期 | 基線 | 說明 |
|---|---|---|
| 2026-10-03 | 2670 | ITER21 開跑前全套實跑（HANDOFF-ITER20 記 2644，其後 3 提交補至 2670） |

## ITER21 ・ V7 對齊批次：驅動回歸 WinRing0 主力（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP29-① | WinRing0 實體記憶體後端（反射 Ring0.ReadMemory）＋WinRing0MmioReader＋「主力→備援→三態」裁決鏈；本機 SPI/MCHBAR/ECAM-AER 事實翻真值 | 2675 | a5f7a0b |
| R2 | WP29-② | 後端與環境事實組（backend.mmio/msr、HVCI/Secure Boot/testsigning、PawnIO 狀態、環境矩陣裁決）；DeepAccess 文字誠實化 | 2682 | 550b0de |
| R3 | WP5-② | 對帳引擎接線進重載路徑＋「交叉對帳」分組；規則 4→6；補微碼雙來源（msr.0x8b＋reg.microcode）與 TjMax 事實鍵 | 2689 | c924ea3 |
| R4 | WP1 | IIoPortAccess seam＋POST code（I/O 0x80）唯讀事實；0xFF/0x00 不解碼 | 2692 | c68b5fb |
| R5 | WP6 | CMOS/RTC 唯讀事實（VRT 電池位、RTC 時鐘、PC-AT 校驗和）；0x70 選址保留 NMI 位元；廠商設定區刻意不解碼 | 2698 | 65ba463 |
| R6 | WP44 | SpecRef 規格引用機制＋五解碼器（SpiFlash/ChipsetSecurity/PlatformSecurity/PcieAer/AcpiTable）反射覆蓋檢查 | 2701 | 75e6c5d |
| R7 | WP45 | 獨立合成產生器（PCIe AER 能力鏈、PCH SPI 暫存器編碼）＋黃金答案向量＋全 0/全 FF/截斷邊界 | 2707 | 0e33fdc |
| R8 | WP50-① | FsCheck 性質測試：編碼解碼往返、未知位元保留、PC-AT 校驗和性質、MCFG 表長推導、揮發遮罩性質。**實績：當場抓到遮罩位元心算錯誤（0xA007 vs 0xA807）與 MCFG 尾段語義誤判** | 2714 | ea5f473 |
| R9 | P4 | 證據實驗室「原始快照」卡片（建立/載入/與目前差分）；收集掛進重載 gate；Help 條目 | 2718 | a6c5e95 |
| R10 | 制度 | docs/ITERATIONS.md 建立＋DocumentationIntegrityTests（V7 §19 封面數字推導對帳）＋V7 納入版控＋刪除 V2–V6（§0.3） | — | 本輪 |

## 待辦（下一批）

- WP3 MCHBAR 時序解碼（對準 Intel datasheet／CHIPSEC 再出貨）、WP2 SMBus 深化（TSOD/VRM 逐相）、WP7 報告與 WP32 API。
- 對帳規則 6 → ≥25（V7 目標）。
- PawnIO 模組整合（HVCI 環境備援；本機 IntelMsr 模組已知回 0 的問題要先解）。
- 發佈：等使用者明說。20 輪＋本批內容尚未折疊進 changelog（發佈前必做，FileVersion .5→.6）。
