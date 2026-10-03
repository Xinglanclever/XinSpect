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

## ITER25 ・ WP3 推進批次：世代基建與掃描擴大（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP3-① | **CPU 世代判定解碼器**（`CpuGeneration`：CPUID family/model → 微架構名，只收錄有把握的子集、未收錄誠實標；＋`cpu.generation` 事實進收集） | 2763 | 7a15463 |
| R2 | WP3-② | MCHBAR 事實文字精確化：世代判定接入、不出值界線說明從「待辦」改為「**公開規格未定義（MRC 訓練結果區），維持不解碼——不出值是誠實界線，不是待辦遺漏**」 | 2763 | eab9988 |
| R3 | WP3-③ | **ECAM AER 掃描擴大**：bus 0 → segment 0 自 StartBus 起最多 32 條 bus（探頭讀取量上限保護）；逐裝置 key 帶 bus 號、上限外如實標「未掃」、掃描中止點（bus/dev.fn）如實記錄且保留已得事實 | 2765 | 6bb3c58 |
| R4 | 制度 | ITERATIONS.md 記錄本批＋記憶庫 | — | 本輪 |

**MCHBAR 誠實裁決（記錄在案）**：本機為 i9-7980XE（Skylake-X）——MCHBAR 時序暫存器屬 MRC 訓練結果區，Intel 公開 datasheet 未定義其佈局，社群逆向值不合 V7「對準規格」門檻，時序解碼維持不出值。世代判定基建已就位，未來若有出處可信的佈局文件可按世代接入。

## ITER26 ・ WP4 批次：SPI 快閃地圖與 BIOS 區雜湊（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP4-① | **`SpiFlashMap` 純解碼器**：FREG0-5 → 快閃總大小（最大上限 4KB 單位推導）＋記憶體映射基底（PCH 標準：4GB 頂端）＋BIOS 區（FREG1）位移／長度；全空或異常範圍不猜 | 2763 | 8d9aa43 |
| R2 | WP4-② | **BIOS 區 SHA-256 雜湊**（`SpiFlashHashService`）：單次大讀優先、失敗退 4KB 分塊（相容 DriverMmioReader 契約上限）、中途失敗帶中斷位移；全 F 頁與 PRx 讀保護（RPE）重疊如實標注——**雜湊＝可讀面**；BIOS 區 >64 MiB 拒讀；`SpiFlashService.ReadController` 抽出共用（SPI 事實／地圖／雜湊三服務單一發現層） | 2774 | b93e3cd |
| R3 | WP4-③ | 接線：`SpiHashFacts` 進重載＋韌體安全頁＋快照收集；對帳規則 12→**13**（`spi.hash_vs_mmio_backend` 管線資料流規則） | 2775 | 06445ca |
| R4 | 制度 | ITERATIONS.md 記錄本批＋記憶庫 | — | 本輪 |

**誠實界線（記錄在案）**：BIOS 區雜湊的當前用途是**跨時間比對（快照差分）與留存**——「原廠比對」需要原廠映像檔，本版未涵蓋，不假裝能驗正版。RPE 讀保護攔截的範圍會以全 F 呈現且被標注，雜湊不代表被擋內容。

## ITER27 ・ WP30 批次：PCI 知識層與裝置盤點（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP30-① | **`PciKnowledge` 純解碼器**：PCI-SIG 類別碼（base/subclass/progIF/rev 佈局）→ 角色名、廠商 ID → 名稱（知名子集）；未收錄如實標「未收錄」不猜；全方法 SpecRef（PCI Local Bus Spec 3.0 §6.2.1＋PCI-SIG Code & Vendor ID Spec），註冊進覆蓋檢查（解碼器 9→10） | 2780 | 8bc0843 |
| R2 | WP30-② | **Bus 0 裝置盤點**（`Bus0InventoryService`）：32 槽走訪＋多功能位元（header type bit7）決定 fn 掃描（規格行為）、逐功能事實＝規格有據角色＋原始 ID 並列、讀取錯誤計數進摘要、全失敗整組三態 | 2783 | 2885a7b |
| R3 | WP30-③ | 接線（`PciInventoryFacts` 進重載＋韌體安全頁＋快照）；對帳規則 13→**15**——SPI 控制器存在性交叉**拆兩條方向性規則**（「SPI 事實在而盤點無控制器」「控制器在而 SPI 服務稱無回應」），每條宣告保證 Present 的一側繞開引擎守衛；「非 Intel」等誠實不採用理由不誤報 | 2784 | 52e27d9 |
| R4 | 制度 | ITERATIONS.md 記錄本批＋記憶庫 | — | 本輪 |

**方法學備忘（引擎守衛補遺）**：矛盾條件涉及「A 缺席」且輸入鍵必須 Present 時，若 A/B 兩側都可能缺席，單條規則無法涵蓋兩個方向——拆成兩條方向性規則，各宣告保證 Present 的一側為輸入鍵。

**知識庫誠實界線**：本批只收錄 PCI-SIG 規格定義的類別碼與知名廠商 ID——device 型號對照（每代更新）刻意不做，待有出處化資料再補。

## ITER28 ・ WP30＋WP4 批次：BAR 資源與原廠映像比對（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP30-② | **`PciBars` 純解碼器**：type0 標頭六 BAR＋Expansion ROM——I/O／32-bit／64-bit（佔兩槽規格配對）／可預取辨識、全 0＝未配置如實計數；**唯讀界線：大小需寫入探測故不出值**；SpecRef（PCI Local Bus Spec 3.0 §6.2.5），解碼器覆蓋 10→**11** | 2788 | 1dc0e20 |
| R2 | WP30-③ | 盤點擴充：每個存在功能多一筆資源事實（`pci.res.{dev}.{fn}`）——裝置角色＋資源清單一行可稽核 | 2788 | f017c11 |
| R3 | WP4-② | **`SpiFlashCompareService` 原廠映像比對**：BIOS 區 vs 使用者供映像逐 4KB 塊比對，報差異塊數與前 8 個快閃位移；**大小不符＝誠實拒比**（不猜對齊不補值）；RPE 重疊標注「被擋範圍以全 F 呈現會計為差異」；「差異≠被改壞，判讀權在使用者」入文 | 2793 | dadd453 |
| R4 | 制度 | ITERATIONS.md 記錄本批＋記憶庫 | — | 本輪 |

**測試假件教訓（記錄在案）**：存在裝置的未實作 BAR 依規格讀 0；假件把「裝置不存在」的 0xFFFFFFFF 混用到 BAR 讀取會解出垃圾 I/O 資源——假件要照規格建模。

## ITER29 ・ 接線批次：映像比對上線與規則 17（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP4-② | **映像比對 UI 上線**：韌體安全頁「與參考映像比對」按鈕（選 .bin/.rom/.img → 背景比對 → 結果進證據列）；`EvidenceCollection.CompareFlashWithReference` 共用入口；比對結果槽「重載即清空」——資料更新後舊比對失效，不留舊結論冒充現狀 | 2794 | c6f2ff2 |
| R2 | WP32 | **CLI `--compare-flash <映像>`**：JSON 輸出、退出碼語意（0＝一致／2＝有差異或三態／1＝致命）；自動化管線可分岔 | 2796 | 57f65b1 |
| R3 | WP5 | 對帳規則 15→**17**：`spi.hash_without_map`（雜湊存在而地圖缺席＝管線矛盾）、`spi.map_vs_regions`（快閃地圖與區域事實對同一份 FREG 說不同的話） | 2797 | 9a6cf09 |
| R4 | 制度 | ITERATIONS.md 記錄本批＋記憶庫 | — | 本輪 |

**WP4 現況**：SPI 韌體稽核三層全部就緒且雙入口（UI＋CLI）——地圖（FREG→映射基底）、雜湊（可讀面 SHA-256）、比對（vs 參考映像逐塊）。

**驗收記錄（誠實）**：R4 驗收時已知 flaky `Windows電源API取樣保留快照與查詢延遲` 連續兩次全套失敗（單跑穩定過）。查證：測試檔在本批 65 提交中零觸碰（git log 驗證）、歇 30 秒後全套 2797 全綠——判定為機器負載相關的時序型 flaky（1f9b422 已緩解非根除），非回歸。**觀察**：該測試在全天高強度建置後的全套失敗率上升，下批可考慮把取樣逾時再放大或隔離到獨立 collection。

## ITER30 ・ WP5＋WP30 批次：規則 20 與有出處的名字（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP5 | 對帳規則 17→**20**：`mchbar_registers_without_mmio_backend`、`tjmax_without_msr_backend`、`mchbar_base_without_host_bridge`（管線一致性族，TryLookup 模式） | 2798 | 4f8ee45 |
| R2 | WP30-① | **Super I/O 晶片名稱對照表**（`SuperIoKnowledge`，40 條）：出處＝coreboot `util/superiotool` ite.c／nuvoton.c（GPL-2.0-or-later，2026-10-03 抓取，事實性資料＋引用標註）；探測服務把「晶片 ID＋名稱＋原始廠商 ID」並列；解碼器覆蓋 11→**12**、引用下限 32→41 | 2805 | 08b33f0 |
| R3 | 制度 | 帳本記錄＋**PCH 世代判定的誠實暫停記錄** | — | 本輪 |

**PCH 世代判定暫停（誠實記錄）**：已勘察 CHIPSEC（chipsec2 分支）cfg 結構——平台 XML（adl.xml 等）以 HOSTCTL DID 清單＋SKU 名組織、PCH 世代 XML（pch_5xxlp.xml 等）以 PMC/SMBUS DID 清單組織；**LPC DID→行銷名（H510/Z590）的直接對照不在單一檔案**，需逐檔交叉建表。按 V7「未對準出處就不出值」原則，本批不做半成品表；下批專門處理。

## ITER31 ・ WP5 批次：對帳規則達標 25（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP5 | 規則 21–22（UEFI 語義族）：`uefi.secureboot_vs_setupmode`（SB=1 而金鑰未部署＝狀態機警訊）、`uefi.audit_vs_deployed`（互斥狀態同時開啟） | 2806 | 05c71b5 |
| R2 | WP5 | 規則 23–24（SPI 交叉族）：`spi.service_vs_spi_bar_resource`（盤點有 BAR 而服務稱未配置 SPIBAR）、`spi.write_surface_vs_hsfsts_flockdn`（FLOCKDN 兩向交叉——同一次 SPIBAR 讀取的兩種解讀） | 2807 | 20c0c27 |
| R3 | WP5 | 規則 **25**（達標 V7 §13）：`uefi.variable_vs_registry_platform`（UEFI 變數存在而登錄檔否定 SecureBoot 狀態＝平台型別矛盾） | 2808 | ed39ae1 |
| R4 | 制度 | 帳本記錄＋記憶庫 | — | 本輪 |

**V7 §13 成功定義「對帳 ≥25 條」達成**。規則組成：硬體語義族（微碼、TjMax、SMM_BWP×SMRAM、SMRAMC 非法組合、UEFI 狀態機互斥×3）＋管線一致性族（×10＋）＋來源交叉族（Secure Boot 雙來源、平台型別）。下批規則只許往上走。

## ITER32 ・ G6 開跑：WP14 TPM 量測開機鏈（2026-10-03）

> G6 全線路線圖立檔 `docs/ROADMAP-G6.md`（ITER32–40 序列＋誠實界線：EC 直寫、Rowhammer、網路觸及、PMU 各有閘門）。

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R0+R1 | WP14-① | G6 路線圖入檔＋**`Tpm2` 純解碼器**：TPM2_PCRRead 命令建構（金標位元組）、回應解析（rc@6——回應標頭 tag/paramSize/rc）、TCG 事件 log 走訪（多演算法摘要、損毀即停標 truncated）。**坑：TPM 線上格式全大端序**（與 x86 BitConverter 相反）；AlgId 是 u16 不能用 4-byte 寫入器蓋掉 | 2813 | 4cbf171 |
| R2 | WP14-② | **`TpmFactsService`**：經 Windows TBS（tbs.dll 仲介，零核心風險）讀 PCR 0–7（SHA-256 bank）＋TCG log 摘要（事件數＋PCR 覆蓋）；TBS 錯誤／TPM_RC／無 TPM 分別如實三態；`tpm.*` 事實進韌體安全頁＋快照＋CLI evidence | 2816 | 8316ef3 |
| R3 | 制度 | 帳本記錄＋記憶庫 | — | 本輪 |

## ITER33 ・ G6：WP21＋WP22 批次（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP21 | **NUMA 拓撲事實**（`NumaTopologyService`）：GetNumaHighestNodeNumber＋GetNumaNodeProcessorMaskEx（kernel32 usermode 唯讀）；節點數＋逐節點遮罩/popcount；**刻意不走 GetLogicalProcessorInformationEx 變長結構解析**（欄位偏移隨版本演進，解析錯位＝把遮罩當事實） | 2817 | 5a322c0 |
| R2 | WP22-① | **Rowhammer 未施測誠實聲明**（`mem.rowhammer`）：讓「為什麼工具不測」有明文答案（V7 §14 風險＋ECC 見 R-MEM-05＋TRR usermode 不猜）；接線進 `LoadPlatformFacts`（NUMA＋聲明一批載入） | 2820 | 5a322c0 |
| R3 | 制度 | 帳本記錄＋記憶庫 | — | 本輪 |

## ITER34 ・ G6：WP13＋WP23 批次（2026-10-03，接 TASK-GAP-6 指令書前收尾）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP13-① | **網路卸載實際啟用狀態**（`NetOffloadFactsService`）：WMI MSFT_NetAdapterChecksumOffload/RSS，防禦式屬性讀取（讀不到標「屬性未提供」不冒充停用）；三態呈現開/停(支援)/未提供 | 2820 | be01f2c |
| R2 | WP23-① | **音訊端點混合格式**（`AudioEndpointFactsService`＋`AudioFormatDecoder`）：MMDevice COM 列舉渲染＋擷取端點、WAVEFORMATEX/EXTENSIBLE 解碼（垃圾不解碼）；自訂最小 PROPVARIANT（.NET ComTypes 無此型）。**Wi-Fi RSSI 延後至後續批次**（wlanapi P/Invoke 另輪） | 2828 | 30268db |
| R3 | 制度 | 帳本記錄；**接 TASK-GAP-6-2026-10-03.md 指令書**（A45 規則引擎最優先） | — | 本輪 |

**誠實疏失記錄**：R2 曾把紅測試提交（`dotnet test | grep && git commit` 串接、grep 有輸出即過關——交接文件警告過的老坑復發）→ 立即修測試預期（全零緩衝聲道 0 → 不解碼垃圾）＋amend 救回 `30268db`。**教訓重申：測試與 commit 絕不串一行**。

**命名坑**：新類不可叫 `WaveFormat`——與 DeepBench/NAudio 的 `WaveFormat` 型別衝突（CS0722 靜態類型不可作回傳類型）；改名 `AudioFormatDecoder`。

## ITER35 ・ GAP6-A45 規則引擎（2026-10-03）

> 接 TASK-GAP-6-2026-10-03.md 指令書，A45 最優先落地。

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | A45-① | **`RuleDefinition` 模型＋`RuleInterpreter` 條件直譯器**：12 種述詞（keyMissing/Available/Unavailable、valueEquals/EqualsKey/StartsWith、textContains、reasonContains、numericPresent/EqualsKey/Between）＋not/anyOf 組合＋樣板插值（{key}/{key:hex}/{key:reason}）；`ExternalRuleEngine` 守衛語意與內建引擎對齊 | — | 9338d7f |
| R2 | A45-② | **`RuleLoader`**（JSON，System.Text.Json 零依賴；指令書允許 YAML/JSON，選 JSON 免新套件）＋**`RuleValidator`**（強制 SpecRef＋誤報條件＋鍵名登記檢查＋預設分支位置）＋**`RuleExplainer`**（為什麼觸發＋規格＋誤報） | — | 9338d7f |
| R3 | A45-③ | **`Rules/builtin.json` 26 條全數外部化**（指令書說 25——實際盤點是 **26 條**，帳本 ITER31 記 25 是數錯，本批更正）；每條附 SpecRef（出處化引用）與誤報條件 | — | 9338d7f |
| R4 | A45-④ | **行為等價測試**：68 個情境（每條規則每個分支）——同一情境下內建 C# 規則與外部 JSON 規則的 **FactRelation 逐條一致**；id 集合一條不漏。**等價定義（已向使用者宣告）：Relation 層一致；Reason 允許語義等價不逐字相同** | 2896 | 9338d7f |
| R5 | 制度 | 帳本記錄＋記憶庫 | — | 本輪 |

**界線聲明（照指令書）**：不做規則市集、不下載遠端規則、不改內建 25＋1 條判決邏輯、不碰 FactRelationEngine 公開介面。本批產出是「載入與驗證層」——內建規則仍是判決的引擎，JSON 是可分享的外部形式。

## 待辦（下一批）

- **主線：TASK-GAP-6 六項**——A45 ✅（本批）→ **A47 審計日誌（下一批）** → A44 查詢語言 → A54 效能預算 → A18/A23 純解碼器；G6 路線圖順延。
- **帳本更正：對帳規則實際為 26 條**（ITER31 記 25 是手寫數錯——機器檢查再次抓到手寫漂移）。
- Wi-Fi RSSI（wlanapi P/Invoke）併入後續 G6 批次。
- WP30 知識庫續推：PCH 世代判定（CHIPSEC cfg 逐檔交叉建 LPC DID→世代名對照；勘察記錄見 ITER30）、device 型號對照的出處化資料源。
- 對帳規則已達 25（V7 目標達成）；後續隨新事實來源繼續擴（SPD↔TSOD、SMART、儲存面）。
- WP50 Stryker（解碼器抽成獨立程式庫，或等 Buildalyzer 支援）、differential 擴大。
- 對帳規則 15 → ≥25：隨新事實來源（SPD↔TSOD、SMART、儲存面）補。
- CLI 擴充候選：全機快照（需 headless 化 WPF 服務層）、批次清單檔。
- PawnIO 模組整合（HVCI 環境備援；本機 IntelMsr 模組已知回 0 的問題要先解）。
- 發佈：等使用者明說。20 輪＋ITER21–28 內容尚未折疊進 changelog（發佈前必做，FileVersion .5→.6）。
