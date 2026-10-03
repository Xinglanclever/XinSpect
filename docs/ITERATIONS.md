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

## ITER36 ・ GAP6-A47 審計日誌（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | A47 | **`AuditEntry`＋`AuditLogService`＋`AuditVerifier`**：雜湊鏈（PreviousHash→EntryHash，創世 GENESIS）、追加／載入／存檔（canonical JSON）、驗證器（序號連續＋前筆串接＋逐筆重算——**改中間一筆驗證必失敗，重算雜湊也逃不過 PreviousHash 斷裂**）；機器識別重用 `CreateAnonymousMachineId`（不可逆，序號原文不落日誌）；只記中繼資料不記內容；與 EvidenceTimeline 的關係＝並存不重疊（聲明寫進型別註解） | 2902 | 020999e |
| R2 | A47 接線 | 時間膠囊「建立／比較」動作追加審計條目（操作者＋匿名機器雜湊＋判決摘要＋快照 Integrity.Hash）；寫入失敗不影響主流程 | 2902 | 020999e |
| R3 | 制度 | 帳本記錄＋記憶庫 | — | 本輪 |

**界線（照指令書）**：不上傳雲端、不做區塊鏈存證；日誌在本機 `%ProgramData%\XinSpect\Auditudit.json`。

## ITER37 ・ GAP6-A44 查詢語言（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | A44 | **查詢三件套**：`FactQuery`（模型＋統一查詢形狀 QueryFact＋結果摘要）、`QueryParser`（五頁語法：key/category/source/value/availability/since/until × =/~/^、一行一子句、#註解、值可含空格；未知輸入丟 ParseException 帶修正指引）、`QueryExecutor`（管線逐層篩選＋單集合＋跨機器快照） | 2910 | 6d3c675 |
| R2 | A44 | **「查不到 ≠ 沒有」語意釘死**：0 筆匹配的摘要明說「可能沒有這個事實或鍵名不同」；存在但讀不到的項目是正常匹配（三態原因隨附、摘要標「不是沒有這個事實」）；時間軸（since/until）＋跨機器（結果帶匿名機器識別） | 2910 | 6d3c675 |
| R3 | 制度 | 帳本記錄＋記憶庫 | — | 本輪 |

**語法示例**：`category=韌體安全`、`key^=spi.`、`availability=read-error`、`since=2026-10-01`——一行一子句按序成管線。不做 SQL、不做跨機器寫入、不碰 FactAvailability 定義（照指令書）。CLI 接線（--query 升級為本語法）留給後續批次。

## ITER38 ・ GAP6-A54 效能預算＋自家可觀測性（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | A54 | **`PerfBudget` 預算登記**（5 項門檻釘 V7 原值 3 秒/300 MB/1%/60 秒/5 秒；**放寬即紅燈**）；**可重複量測兩項真量真擋**：報告產生（2000 列大表 Build < 5 秒）、全套掃描編排（假讀取器 20 輪 < 60 秒） | 2917 | 本輪 |
| R2 | A54 | **`SelfTelemetry` 自家可觀測性**：掃描耗時／三態率／例外數——**預設關閉**（未啟用 Record 是無聲 no-op）、匿名（只記數字與時間，測試驗證檔內無事實鍵與值）、只存本機不上傳；`EvidenceCollection.ReloadInto` 接入計時（停用時 no-op） | 2917 | 本輪 |
| R3 | 制度 | 帳本記錄＋記憶庫 | — | 本輪 |

**誠實界線**：冷啟動／常駐記憶體／CPU 閒置三項標 `RuntimeOnly`——單元測試量它們必然不可重複（違反「量測必須可重複」），由 SelfTelemetry 在應用程式內記錄；測試只釘「登記存在＋門檻未放寬」。**測試坑**：C# 識別字不可含連字號（方法名 `no-op` 編譯錯）；`ReloadInto` 接線層照慣例極薄不在單測覆蓋（真後端會碰核心）。

## ITER39 ・ GAP6-A18＋A23 收尾：帶外管理與 RAID 純解碼器（2026-10-03）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | A18 | **`IpmiDecoder` 純解碼器**：SEL 系統事件記錄（16 bytes 金標——**Generator ID 是 u16**，手算向量先錯成 1 byte 被長度檢查抓回）、FRU 板卡區（1996-01-01 起分鐘數＋ASCII TLV＋檢查和）、SDR 標頭；SpecRef 引 IPMI 2.0 §31/§34/§42 | 2929 | 56561e7 |
| R2 | A23＋A18 | **`MegaRaidDecoder`**：MFI 訊框標頭（cmd/sense_len/status/flags/data_xfer_len——跨 FreeBSD/Linux 一致欄位；**BBU/VD/PD/foreign config 佈局未驗證，刻意不解碼**）＋**`RedfishSchemaDecoder`**（@odata.id/Members/error，只解 JSON 不發請求）；**`OobFactsService`** 三態分離：無 BMC/無 RAID＝NotApplicable（SMBIOS Type 38／PCI 類別碼 0104,0107 探測）、有硬體＝NotSupported（通路未實作不出貨）＋未施測聲明進平台事實組 | 2931 | 56561e7 |
| R3 | 制度 | 帳本記錄＋記憶庫 | 2931 | 本輪 |

**GAP6 六項全數完成**：A45 ✅ A47 ✅ A44 ✅ A54 ✅ A18 ✅ A23 ✅。

**誠實界線（記錄在案）**：本工具只宣稱「支援 IPMI／MegaRAID **訊息解碼**」（SEL/FRU/SDR/MFI 純解碼器，合成向量依 spec 手算），**不宣稱支援 IPMI/MegaRAID 本身**——KCS/SSIF/NCSI/SMBus 通路無法在無 BMC、無 MegaRAID 的本機驗證，不出貨；BBU/VD/PD 佈局文獻分歧，未驗證前不解碼。

**測試坑**：git add 一個不存在的檔案會靜默失敗導致空提交（`2>/dev/null` 蓋掉錯誤）——add 後必須確認 staged 才 commit。

## ITER40 ・ 顯示層：交叉對帳判決卡（2026-10-03，使用者回饋「應該顯示比較好」）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | 顯示 | **交叉對帳判決卡**進韌體安全頁頂部：26 條規則逐條「判定徽章（一致綠/矛盾紅/無法驗證灰）＋規則名＋原因」＋摘要計數；徽章色走既有 SeverityToBrush（零硬編色）；HelpCatalog 加條目 | 2933 | 本批 |
| R2 | 實機驗證 | **打開程式抓到兩件事**：① 啟動即崩潰——`Tbsi_Context_Create` P/Invoke 簽名誤用 by-value uint（API 收指標），AV 無法被 catch 攔、行程即死——修正為 `ref uint` 並實機驗證；② 判決卡**當場在真機抓到一條真矛盾**（SMM_BWP=1 但 SMRAM 鎖定交叉不符，紅字呈現），且發現本機登錄檔微碼值為 4 位元組（解碼器如實標「格式不明」而非硬解——三態設計生效） | 2933 | 本批 |

**啟動耗時 2.7 秒**（狀態列實測）——A54 冷啟動預算 3 秒內。

## ITER41 ・ 系列更名：Everest → Olympus、版號 2.5.0（2026-10-03，使用者指示）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | 品牌 | csproj 2.1.0→**2.5.0**（FileVersion 2.5.0.0）、AppInfo 代號單一來源→**Olympus**、MainWindow 標題／彩蛋（**額菲爾士峰→奧林帕斯山**）、AboutView 版號行、ZhTerms 繁簡對（**珠穆朗玛峰→奧林匹斯山**）、README 徽章、Changelog 新增 2.5.0 Olympus 條目（歷史 2.1.0 Everest 條目原封保留）、DeepBench 釘住測試改釘新慣例、AppInfoTests/ZhTermsTests 期望值 | 2933 | 本批 |
| R2 | 實機 | 重建＋重啟截圖驗證：標題列「XinSpect v2.5 Olympus」、表頭「v2.5.0 Olympus」 | — | 本批 |

**範圍裁定**：DeviceIcons／BrandBadge／IconGalleryWindow 的「Everest 系列」徽章是 **Intel CPU 系列名**（7980XE 等真實屬該 bin 系列）——硬體事實名不隨 App 品牌更名，保留。
**ChangelogTests 版號同步**（csproj＝Latest＝AboutView）自動通過——單一來源設計的紅利。

## ITER42 ・ 使用者回饋修正（2026-10-03，接 ITER41 更名後實機檢視）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | 修正 | **深層核心存取狀態文字重複**：StatusFrom 內嵌 notes＋AppendStatusNotes 再附加一次＋Enable/Disable 又合併——同一句出現兩三遍。重構：StatusFrom 只回一句話；RefreshStatus/Enable/Disable 各自附加一次；UI 移除重複附加 | 2935 | 3e516d4 |
| R2 | 修正 | **7980XE 歸類釘住**：使用者指示 7980XE 屬至尊版 Extreme Edition（金底 XE）不屬 Everest 系列——現行 i9-extreme 規則本已正確，補釘住測試鎖死；實機截圖驗證（處理器頁金底 XE＋至尊版紅膠囊） | 2935 | 3e516d4 |
| R3 | 修正 | **深測中心儲存根預設值**：空白儲存根讓六個儲存測項全數被紅字擋下卻無入口——預設給可寫暫存子目錄（%TEMP%\XinSpectDeepBench）、XAML 加「瀏覽…」（OpenFolderDialog）、BuildOrchestrator 自動建立目錄 | 2935 | 3e516d4 |

**除錯教訓（stash 實驗法）**：測試主機 Fatal error 當機（0xC0000005 於 D3D11PresentFramePacingEngine 背景 Task）用 stash 二分定位——**建構子預設 StorageRoot 讓「不設根就 StartAsync」的單元測試誤跑真實測試陣列（含真 D3D11 引擎）**，違反「測試注入假件」鐵律。修正＝預設值移到 MainViewModel 生產接線（注意：MainViewModel 有名為 System 的屬性遮蔽 System 命名空間，需 global:: 限定）。

## ITER43 ・ Stryker 解鎖：純解碼器抽 XinSpect.Decoders 程式庫（2026-10-03，核准計畫第一段）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | 抽庫 | **XinSpect.Decoders 類別庫**（net10.0、RootNamespace XinSpect）落成：19 個純解碼器檔遷入（13 檔整搬＋5 檔混合拆分——SpiFlashService／ChipsetSecurityService／PlatformSecurityMsrFacts／AcpiTableService／SuperIoProbeService／AudioEndpointFactsService 的解碼器型別與服務分檔）；主專案六類 Remove 排除＋ProjectReference 接線；單建 0 警告、跨組件全套綠 | 2935 | 本批 |
| R2 | 突變 | **dotnet-stryker 實驗成功**（Buildalyzer 對純類別庫不再擋）——初始 Mutation score **66.63%**；新增 DecoderHardeningTests（20 測：CpuGeneration 全表／SpecRef 註冊表釘死／MegaRAID 命令碼全表＋Flags＋Redfish 結構面／IPMI 型別全表＋deassertion＋FRU 邊界／TPM 回應邊界＋TCG log 損毀分支／AudioFormat 上界）＋知識表（SuperIoKnowledge／PciKnowledge）資料檔排除 → **80.50%**（過 low 門檻 70）。**突變測試抓到真 bug：CpuGeneration.DecodeSignature 文件聲稱 extended family 進位但實作漏做**（family=0xF 時應 base+ext）——已修並補釘住測。另補提交 ITER40–41 漏入庫的已驗證變更（TBS ref 註記、語言切換標題、更名殘字） | 2955 | 本批 |

**Stryker 結論**：解鎖方案的關鍵是「純解碼器、零 WPF／零特權相依」——Buildalyzer 掃不到 WPF 專案，抽庫後直接可跑（3 分 10 秒全輪）。剩餘逃逸集中在 SpecRef 反射輔助與 PlatformTrustDecoder／PciBars 的字串分支，後續批次隨手補。主專案側仍無法突變測（預期內）。

## ITER44 ・ 微碼 4 位元組變體＋Wi-Fi 頻道＋EC 風險評估（2026-10-03，核准計畫第二段前哨）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | 微碼 | **登錄檔 Update Revision 4 位元組變體解碼**：本機實測 `06 70 00 02`＝LE DWORD 0x02007006（恰等於 MSR 0x8B 高 32 位）——照 BiosMeDecoder 口徑以 LE DWORD 解碼、畫面註明「4 位元組變體」不冒充標準 8 位元組佈局；其他長度仍如實標格式不明 | 2956 | 本批 |
| R2 | Wi-Fi | **WLAN_BSS_ENTRY 純解碼器**（WifiBssDecoder 進 Decoders 庫＋SpecRef 註冊）：中心頻率（kHz）→頻道查表公式（2.4/4.9/5/6 GHz 等差＋ch14 特例，換不出回 null 不猜）、佈局依 wlanapi.h 手算（sizeof=360）；WifiSignalService 接 BSS list 取頻道與 BSSID；**未連線介面如實列一列**（此前「沒連線」被混報成「沒介面」）；UI 加頻道欄與三段式狀態文字。本機 Intel AC 9260 無線電軟體關閉——連線態欄位（RSSI／頻道）實測待使用者連線後補驗 | 2973 | 本批 |
| R3 | WP15 | **EC 唯讀風險評估記錄**（docs/EC-RISK-ASSESSMENT.md）：「唯讀」實含 RD_EC 命令埠寫入、與 acpi.sys 電池/熱輪詢交易交錯＝資料錯位、burst 破壞、症狀延遲顯現——**結案裁定：不實作任何 EC 埠存取**，重開條件（核心合作通路＋測試機＋作者同意）成文；G6 路線圖同步 | 2973 | 8ecdde9 |

## ITER45 ・ WP15 系統與軟體層（2026-10-03，核准計畫第二段）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP15-① | **Windows Update 歷史**（WindowsUpdateHistoryService）：WUA COM「Microsoft.Update.Session」（dynamic、零相依、零特權）——最新一筆（標題＋安裝日期）、總筆數、近 30 天安裝數、失敗／中止計數含最近一次標題；無日期不猜、空歷史是「0 筆」不是讀不到、COM 不可用三態。**實機 COM 通路驗證通**（單測過程即讀到真機歷史） | 2979 | 本批 |
| R2 | WP15-② | **服務盤點**（ServiceInventoryService）：WMI Win32_Service——總數／執行中／自動／停用／**非系統目錄服務**（PathName 引號感知解析＋不在 \Windows\ 下，例舉前三名，不下安全結論）；空清單與讀不到分得清楚 | 2982 | 本批 |
| R3 | WP15-③ | **事件記錄摘要**（EventLogSummaryService）：System log 反向走訪 7 天內嚴重＋錯誤、最常見「來源 事件ID×次數」前 3；ExportJson（canonical camelCase、只含探測給的欄位）。讀到 7 天外即停不整表掃 | 2982 | 本批 |
| R4 | WP15-④ | **稽核政策＋機器原則**（AuditPolicyService）：LSA LsaQueryInformationPolicy（PolicyAuditEventsInformation，唯讀）——九類別等級逐類繁中描述（0 未設定不列、規範外等級如實標）；x64 struct 手算偏移（指標欄對齊 8）；機器原則以 Registry.pol 存在＋寫入時間為指紋（**不解析二進位**、無檔＝NotSupported 非錯誤）；四組全部接線 LoadSoftwareFacts（StartupSequence 放 Task.Run，COM/WMI 數秒不卡啟動） | 2986 | 本批 |

**範圍裁定**：UAC（EnableLUA）與 LsaProtection（RunAsPPL）已由 OsAnalysisService／SecurityPostureService 涵蓋——WP15 不重複收錄；排程工作已有 ScheduledTaskStartup（工作定義 XML 解析）涵蓋登入/開機觸發面。

## ITER46 ・ WP16＋WP14：虛擬化元件＋核心模組＋開機參數（2026-10-03，核准計畫第二段）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP16-① | **Windows 選用功能狀態**（OptionalFeatureService）：WMI Win32_OptionalFeature——Hyper-V Hypervisor／虛擬機器平台／WSL／容器四目標的啟用／停用／不存在（照抄系統 InstallState 口徑）；**WMI 沒回報的功能如實標「未回報」，不當成停用**。hypervisor 存在位與 VBS 已由 PlatformTrustService 涵蓋不重複 | 2988 | 本批 |
| R2 | WP14-① | **核心模組載入清單**（KernelModuleService）：psapi EnumDeviceDrivers——\Windows\ 下模組只計數（簽章面由 DriverAudit 的 Win32_PnPSignedDriver 涵蓋不重複驗）；**非系統目錄的載入模組逐檔 wintrust Authenticode（DRIVER_ACTION_VERIFY）**——未通過帶原始 NTSTATUS 碼、檔案不存在等無法驗證者如實列名，通過者只計數 | 2994 | 本批 |
| R3 | WP16-② | **開機參數核心除錯面**（DebugConfigService）：登錄 SystemStartOptions 純解析——核心除錯（DEBUG／DEBUGPORT）、測試簽章（TESTSIGNING）；沒有關鍵字＝「未啟用」是 Present 的沒有、整個讀不到才是 ReadError；原始字串全文附上可稽核。三組全部接線 LoadSoftwareFacts | 2994 | 本批 |

**偶發測試記錄**：WindowsPowerStateLatencyEngineTests.Windows電源API取樣 全套跑時失敗一次（計時敏感），隔離重跑與全套重跑皆綠——依「紅燈必查」原則記錄於案，後續若復發再修。

## ITER47 ・ WP47 公開規格（2026-10-03，核准計畫第三段）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP47 | **docs/spec/ 五件套**——① `snapshot.schema.v1.json`（JSON Schema 2020-12：快照頂層與 fact 定義、三態與可信度列舉、**numericValue/unit 條件忽略成文**）；② `query-language.v1.md`（七欄位三運算子＋availability 別名表＋「查不到≠沒有」語意＋ParseException 拒靜默）；③ `METHODOLOGY.md`（誠實契約五條、量測路徑五層、交叉對帳、突變測試、效能預算）；④ `LIMITATIONS.md`（七項設計裁決＋六項能力邊界＋兩項環境相依，全部指向帳本內既有裁決）；⑤ `CERTIFICATION-PLAN.md`（C1–C4 骨架，**明示未執行不得宣稱認證**）。**機器對帳**：PublicSpecTests——Schema 屬性集 vs 實際序列化逐鍵比對（含枚舉清單）、查詢規格必須涵蓋 QueryParser.ValidFields 全部鍵、方法學/限制/認證必須講誠實契約——**文件漂移即紅燈** | 2997 | 本批 |

## ITER48 ・ WP48 骨架＋WP42 資產生命週期（2026-10-03，核准計畫第三段）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP48 | **corpus 貢獻包 v1 骨架**（CorpusUploadService＋docs/spec/corpus-upload.v1.md）：只收遮蔽版快照（敏感保留版拒收回 null）、身份鍵逐鍵排除（serial/uuid/mac/asset.tag/system.product/user.——比 Sensitive 旗標更嚴的雙保險）、SHA-256 完整性信封；**上傳通路／伺服器／同意流程刻意不實作**（骨架＝格式與淨化規則，重開前四項必備條件成文） | 3000 | 本批 |
| R2 | WP42 | **資產生命週期事件**（AssetChangeDetector）：快照差分自動分類——資產級 key 前綴（mem./spd.／cpu.／gpu.／disk./nvme./smart.／board.）→ 對應資產類的 Added/Removed/Changed 事件；**其餘變更（韌體安全、暫存器、感測器）照列但誠實標「狀態」不冒充硬體變更**；跨機器差分如實拒做。接線時間膠囊比較流程：差分後摘要「其中資產事件 N 件」併入通知文字（分類失敗不影響主流程） | 3002 | 本批 |

**模型坑**：HardwareSnapshotIntegrity 的 Algorithm 常數是 `SHA-256`（非小寫）；anonymousMachineId 有格式驗證（`sha256:`+64 hex）——假件要照規格造，繞不過也不能繞。

## ITER49 ・ WP25 時間同步互校＋WP19 CXL（2026-10-03，核准計畫第三段）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP25 | **時間同步互校**（TimeSyncFactsService）：① HPET 存在＝ACPI 表列的 HPET 表指紋；② ACPI PM timer＝FADT PM_TMR_BLK（offset 76 u32 LE，hex＋十進位並列可稽核）；③ **QPC vs 系統時鐘 200 毫秒窗漂移 ppm**——量的是行為不是硬體映射（QPC 用哪個計時器是 OS 決策不猜），NTP 校正造成的跳動屬正常並成文。接線 LoadPlatformFacts | 3005 | 本批 |
| R2 | WP19 | **CXL CEDT**（CedtDecoder 進 Decoders 庫＋SpecRef＋CxlFactsService）：CFMWS（Type 0）逐欄位——BaseHPA@16、WindowSize@24、InterleaveWays@32、記錄長度走 RecordLength、異常即停；**本機無 CEDT＝NotApplicable（無此硬體不是錯誤）**。接線 LoadPlatformFacts | 3008 | 本批 |

**測試坑**：假 ACPI 表要照規格造完整（Length 欄位＋offset 9 校驗和使全表位元組和 mod 256=0，否則 TryParseHeader 如實拒收——假件也要誠實）。

## ITER50 ・ WP10 周邊匯流排（2026-10-03，核准計畫第四段）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP10 | **USB 拓撲摘要**（UsbTopologyService）：WMI Win32_USBControllerDevice 相依對——控制器數、裝置數、**最忙碌控制器**（供電與頻寬衝突排查指紋）；WMI 相依對只有一層，更深 hub 樹不猜。**螢幕連接介面**（MonitorConnectionDecoder 進 Decoders 庫＋SpecRef＋MonitorConnectionService）：VideoOutputTechnology 剝 0x80000000 旗標位後解碼（DP 外接/內嵌、HDMI、DVI、VGA、Miracast…未收錄如實標）、DisplayPort 連接台數統計。兩組接線 LoadSoftwareFacts | 3018 | 本批 |

**範圍裁定**：HID 輪詢率無公開系統 API（各廠商私有驅動介面），usermode 誠實不做；DP 鏈路速率（link rate）不在 WMI 公開範圍——只出連接介面類型。

## ITER51 ・ WP17 主機板解碼深化：SuperIO HWM（2026-10-03，核准計畫第四段）

| 輪 | 工作包 | 內容 | 測試數 | commit |
|---|---|---|---|---|
| R1 | WP17 | **SuperIO HWM 感測器**（SuperIoHwmDecoder 進 Decoders 庫＋SpecRef＋SuperIoHwmFactsService）：ITE 家族佈局——設定模式進入→LDN 4（環境控制器）啟用→基址 0x60/0x61→退出→HWM 以 base＋5（index）／＋6（data）讀取；風扇 RPM＝1,350,000/(divisor×count)（count 0/0xFFFF 無效回 null 不回 0）、溫度 8-bit 二補數、電壓 LSB 16mV **標「未經主機板校準」**；設定模式必以 finally 退出（含失敗路徑，狀態化假 I/O 釘死）。接線 ReloadDriverBackedFacts（HwmFacts 進韌體安全頁＋快照＋AllFacts） | 3022 | 本批 |

**測試坑**：ITE 風扇無效碼是 16-bit 的 0xFFFF 不是 8-bit 的 255；假 I/O 的 index/data 埠（base+5/+6）要分開接——InByte(0x295) 永遠讀不到（data 在 0x296）。

## 待辦（下一批）

- **主線：核准收尾計畫進行中——ITER43–51 完成；下一批 ITER52：WP23 攝影機/UVC 列舉＋WP24 企業儲存偵測（FC/iSCSI/NVMe-oF/MPIO，本機無則如實標）。**
- Wi-Fi 連線態欄位（RSSI／頻道/BSSID）實測：本機無線電軟體關閉，待使用者開啟 Wi-Fi 並連線後重開程式補驗。
- **帳本更正：對帳規則實際為 26 條**（ITER31 記 25 是手寫數錯——機器檢查再次抓到手寫漂移）。
- WP30 知識庫續推：PCH 世代判定（CHIPSEC cfg 逐檔交叉建 LPC DID→世代名對照；勘察記錄見 ITER30）、device 型號對照的出處化資料源。
- Stryker 剩餘逃逸（SpecRef 反射輔助／PlatformTrustDecoder／PciBars 字串分支）後續批次隨手補；differential 擴大。
- CLI 擴充候選：全機快照（需 headless 化 WPF 服務層）、批次清單檔。
- PawnIO 模組整合（HVCI 環境備援；本機 IntelMsr 模組已知回 0 的問題要先解）。
- 發佈：等使用者明說。20 輪＋ITER21–28 內容尚未折疊進 changelog（發佈前必做，FileVersion .5→.6）。
