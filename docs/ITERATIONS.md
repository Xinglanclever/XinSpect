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

## ITER22 ・ G2 收尾批次（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP2-① | TSOD 記憶體溫度感測器（TSE2004 解碼器、SMBus Word Data 交易、白名單唯讀 0x18–0x1F、逐顆事實；iMC 匯流排如實標未掃） | 2731 | fffc9f2 |
| R2 | WP6-② | UEFI 開機設定事實組（SecureBoot/SetupMode/AuditMode/DeployedMode/BootOrder 直問韌體）＋Secure Boot 雙來源（UEFI 變數 vs 登錄檔）交叉規則 | 2735 | dffd655 |
| R3 | WP5-③ | 對帳規則 7→12：管線一致性族（BIOS_CNTL↔綜合裁決、FRAP↔暴露面、SPI↔MMIO 後端、MSR↔MSR 後端、HVCI↔環境裁決）＋SMRAMC 鎖定下開放的非法組合；SmramcText 誠實修補（D_LCK=1 且 D_OPEN=1 不再被文字吞掉） | 2737 | dfcdda2 |
| R4 | WP31-① | Super I/O 探測（0x2E/0x4E 兩種進入序列、晶片 ID／廠商 ID 原始值、**設定模式必以 finally 退出**；名稱對照表刻意未納入待知識庫） | 2744 | 3dda348 |
| R5 | 制度 | ITERATIONS.md 記錄本批＋記憶庫更新 | — | 本輪 |

**對帳規則實作方法學備忘**：「上游事實缺席正是矛盾條件」的管線規則（scan_vs_ecam、mmio_vs_spi、msr_vs_platform 三條）刻意不把後端事實列輸入鍵——引擎會把非 Present 輸入統一轉 Unverifiable，此類規則須在規則內 TryLookup 自行裁決。

## ITER23 ・ G3 批次：報告與腳本化 API（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP7-① | HTML 自足報告產生器（無外部資源、五特殊字元轉義其餘原樣可讀、讀不到／警示列樣式、尾端 SHA-256 離線可重算核驗） | 2748 | d94de8f |
| R2 | WP7-② | 「匯出 HTML 報告」按鈕接進證據實驗室（韌體安全全組＋交叉對帳） | 2748 | 02624c7 |
| R3 | WP32-① | CLI 模式：`--json evidence [--query 前綴] [--out 檔案]`，退出碼語意 0/2/1；`EvidenceCollection` 收斂啟動序列與 CLI 的單一後端組合點；App.xaml.cs 以 AttachConsole 接回主控台 | 2753 | a668931 |
| R4 | WP32-② | `XinSpect.psm1` PowerShell 模組（Get-XinSpectEvidence）＋UTF-8 BOM 機器檢查（PS 5.1 無 BOM 讀中文必炸） | 2755 | 9e7155c |
| R5 | 制度 | ITERATIONS.md 記錄本批＋記憶庫 | — | 本輪 |

**里程碑達成**：G3（WP7 報告＋WP32 API）落地——「交得出可驗證報告（HTML＋SHA-256 離線核驗）、能進 CI（CLI＋退出碼）」。**G1–G3＝世界級的工具。**

## ITER24 ・ G4 推進批次：測試方法學（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP44-① | SpecRef 覆蓋推廣 5→**9 解碼器**（＋Cmos／Tsod／SuperIo／PlatformTrustDecoder，引用 MC146818／JEDEC TSE2004／coreboot superiotool／SDM Vol.3／Win32_DeviceGuard）；引用下限 15→32 | 2755 | 6f65ad1 |
| R2 | WP45-② | 產生器擴大：TSE2004 溫度、IA32_FEATURE_CONTROL／DEBUG_INTERFACE、BIOS_CNTL／SMRAMC、ACPI 表頭（含校驗和）＋MCFG 條目——獨立編碼＋金標＋往返 | 2759 | 9e38df8 |
| R3 | WP50-③ | **差分測試**：同一份 SMBIOS 表、兩個獨立實作——本專案解碼器（smbios-real.bin）vs 微軟 WMI 提供者（Win32_PhysicalMemory）。真機實測 3/3 一致（模組數／總容量／標稱＋實際速度） | 2762 | 292c10f |
| R4 | WP50-② | **Stryker 試跑（time-boxed，誠實結局：未能執行）**：工具 5.0.0 安裝成功，但建置分析器（Buildalyzer）過不了本專案的 WPF＋巢狀 net48 橋接自訂 Target（`BuildAndEmbedXtuBridge`），「simulated build failed」。設定檔 `stryker-config.json` 已備妥（Tsod/Cmos/SuperIo 三檔、PerTest 覆蓋分析、門檻 90/70/70），待解碼器抽成獨立程式庫或工具跟上即可重跑。**副作用教訓**：Stryker 的建置模擬會弄壞 `Bridge\bin\Debug\net48\XtuBridge.exe` 產出狀態，之後測試會報「橋接建置後仍找不到產出」——跑一次正常 `dotnet build XinSpect.csproj -c Debug` 即還原 | — | 本輪 |
| R5 | 制度 | ITERATIONS.md 記錄本批＋記憶庫 | 2762 | 本輪 |

**誠實聲明**：V7 的 Mutation score 目標（關鍵解碼器 > 90%）本批**未達成**——不是測試不夠，是工具進不來。目前解碼器的突變防護由「獨立產生器往返＋金標向量」承擔（T1＋M2），缺的是系統性突變掃描。

- G4 收尾：WP44 SpecRef 覆蓋推廣（目前九解碼器）、WP50 **Stryker mutation（工具限制待解：解碼器抽成獨立程式庫，或等 Buildalyzer 支援此專案形態）**、differential 擴大（SPD fixture vs WMI？edid？）。
- WP3 MCHBAR 時序解碼（對準 Intel datasheet／CHIPSEC 再出貨）、WP4 SPI 韌體 hash 比對。
- WP30 CHIPSEC 知識庫移植（含 Super I/O 名稱對照表的出處化）。
- 對帳規則 12 → ≥25：後續規則依賴新事實來源（SPD↔TSOD、SMART、儲存面），隨 WP 推進補。
- CLI 擴充候選：全機快照（需 headless 化 WPF 服務層）、批次清單檔。
- PawnIO 模組整合（HVCI 環境備援；本機 IntelMsr 模組已知回 0 的問題要先解）。
- 發佈：等使用者明說。20 輪＋ITER21–24 內容尚未折疊進 changelog（發佈前必做，FileVersion .5→.6）。
