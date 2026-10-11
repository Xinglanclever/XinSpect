# XinSpect 600 項推進計畫（2026-10-11）

## 0. 推進規則（使用者定調）

- **50 項＝一版**：版本號、changelog 條目、README 徽章／連結／沿革、測試基線都在**累計 50 項完成時**才動。
- **每 10 項跑一次檢測**：每組 10 項做完就跑一次套件（前景），紅的當場修，不把紅的帶進下一組。
- **每版（累計 50 項）發一次 GitHub Release**：push＋tag＋三資產＋`verify-release.ps1`＋README 位元組數 docs commit（順序不可顛倒）。
- **先簡後難**：優先挑「純讀取、有現成接縫、可合成 fixture 單元測試、不碰硬體風險」的項（表解碼、既有服務補事實鍵），把需要硬體／AML 解譯／UI 算繪／大工程的項往後排。
- 誠實紅線不變：**讀不到一律三態**（Present／NotApplicable／NotSupported／InsufficientPrivilege／Unknown／ReadError），**沒對準出處就不出值**，**不造假值**。

## 1. 起點（2026-10-11 稽核，見記憶）

| 目錄 | 總項 | 已完成 | 部分覆蓋 | 未開工 |
|---|---|---|---|---|
| VOL1（第一組，22 域） | 300 | 14 | 47 | 239 |
| VOL2（第二組，27 域） | 300 | 26 | 76 | 198 |
| **合計** | **600** | **40** | **123** | **437** |

「部分覆蓋」＝倉庫內有相鄰能力但規格所述輸出鍵／範圍未落地——這一類多半是**把既有能力補上事實鍵**，屬本計畫的優先獵物。

## 2. v2.67（目標 50 項）

### G1 ✓ ACPI 表層深化 10 項（第一組檢測已過）

| ID | 名稱 | 產出 | 狀態 |
|---|---|---|---|
| AC-006 | DSDT/SSDT 字串掃描 | `ac.aml.str.count`／`ac.aml.str.sample` | ✓ |
| AC-008 | 第三方 SSDT 對照 | `ac.ssdt.third.count`／`ac.ssdt.third.list` | ✓ |
| AC-009 | NFIT 解碼 | `ac.nfit.summary`／`ac.nfit.spa.count`／`ac.nfit.devices` | ✓ |
| AC-010 | MPAM 存在性 | `ac.mpam` | ✓ |
| AC-011 | BGRT 開機圖形表 | `ac.bgrt` | ✓ |
| AC-012 | SPCR 序列主控台 | `ac.spcr` | ✓ |
| AC-013 | DBG2 除錯埠 | `ac.dbg2`／`ac.dbg2.devices` | ✓ |
| AC-014 | HPET 全欄位 | `ac.hpet.full.id`／`.base`／`.min_tick`／`.sequence` | ✓ |
| AC-015 | MCFG 全欄位 | `ac.mcfg.full.count`／`ac.mcfg.full.list` | ✓ |
| AC-018 | ACPI 表匯總 | `ac.tables.summary` | ✓ |

解碼器：`XinSpect.Decoders/AcpiDeep.cs`（版面出處：Linux ACPICA `actbl1.h`／`actbl2.h`，2026-10-11 抓取核對）。界線：**不評估 AML 位元碼**（_PSS／_CPC／_DSM 不在此組）。

### G2 SMBIOS 表層 10 項（待做）

SB-001（Type 0–3 全欄位）、SB-002（Type 9 插槽）、SB-003（Type 17 全欄位／序號遮蔽 L1）、SB-004（Type 42 管理控制器）、SB-005（Type 11 OEM strings）、SB-006（Type 39 PSU）、SB-010（L1 子狀態）、SB-011（DPC capability）、SB-013（AER 完整欄位）、SB-015（M.2 槽位對照）

### G3 ACPI／PCIe 續 10 項（待做）

AC-017（PPTT 拓撲↔CPUID 交叉）、AC-016（SLIT 矩陣熱圖 UI）、SB-007（PCI capability 全解）、SB-008（SR-IOV VF）、SB-009（ACS）、SB-012（VC/Arbitration）、SB-014（裝置 revision 知識表），＋候補：NG-001／NG-006／AG-003

### G4 伺服器角色 10 項（待做；本機 Server 2025 可實測）

SV-002（IIS 應用池）、SV-003（AD DS）、SV-004（DNS 區域）、SV-005（DHCP）、SV-006（檔案伺服器）、SV-007（印表伺服器）、SV-008（RDS）、SV-009（Hyper-V 交換器）、SV-010（Failover Cluster）、SV-011（Server Core 判定）、SV-012（授權／啟用，**金鑰 L3 禁**）、SV-013（Server 特有服務）

### G5 檔案鑑識與自我完整性殘項 10 項（待做）

FS-005（USN 讀回）、FS-007（回收桶審計）、FS-008（BITS 佇列）、FS-010（任務歷史）、FS-011（安裝痕跡）、FS-016（開機扇區對照）、FS-002（Amcache，需 hive 解析器→難，排後）、IN-003（執行環境聲明）、IN-008（設定自我修復）、DM-007（隱藏任務）

## 3. 後續版本（依同法往前推）

- v2.68：SB-007..SB-015 與 AC 殘項、UV（匯流排深化 12）、GX（GPU 深化 14）——GPU 項多需 NVML／ADLX，本機無 NVIDIA 卡時以 N/A 誠實出值。
- v2.69：SV 殘項、DS（資料安全 12）、SE（進階安全 14）、ZR（零信任 8）。
- v2.70：PO（效能觀測 14）、TU（深測第二波 12）、HM（硬體事件史 8）、DL（開發者診斷 10）。
- v2.71：FS 剩項（SRUM／VSS／Amcache，皆需新解析器）、EL（EOL 庫 8，需知識表資料）、UF／DM／PL。
- v2.72＋：MU／VR／XT／RS／SG／IN 殘項、AG（自動化 7，含明文不做的遠端查詢）。

## 4. 計數與守門（每版都要過）

- 事實鍵目錄（`Services/FactKeyCatalog.cs`，目前 264）與 `FactKeyCatalogTests` 掃描一致。
- 新增公開靜態解碼方法一律附 `[SpecRef]`，並在 `SpecRefRegistry.CoveredDecoders`／`DecoderHardeningTests` 的期望陣列登記。
- 測試基線（`Tests/TestSuiteBaseline.cs`）＝三份 README 徽章，且 ≥ 測試方法數。
- 全套前景跑綠（省電計畫下 `PerformanceBudgetTests` 會假紅——**驗證前切高效能**，收尾還原省電）。

---

## 5. 推進帳本（每批完成就更新；這是下一次開工的單一來源）

| 累計 | 版本 | 內容 | 檢測 |
|---|---|---|---|
| 10 | —（未發版） | G1：AC-006/008/009/010/011/012/013/014/015/018 | 3913 綠 |
| 20 | —（未發版） | G2：SB-001/002/003/004/005/006/015＋NG-006＋FS-007＋FS-010 | 3934 綠 |
| 30 | **v2.67（已發佈 2026-10-12）** | G3：AC-017＋SB-007..014；G4：SV-002/004/006/007/009/011/012/013 | 3994 綠 |
| 31 | —（下一版累計 1／25） | IN-009 除錯偵測聲明（in.debug） | 3994 綠＋3 |
| 41 | —（下一版累計 11／25） | 維護批次：IN-008＋NG-003/004＋HM-001/002/008＋FS-008/011＋UF-006/009 | 4019 綠 |
| 50 | —（下一版累計 20／25） | 執行環境批次 9 項：IN-005/006＋NG-005＋DL-003/008/009＋UF-007/010＋HM-004 | 4039 綠 |
| 54 | —（下一版累計 24／25） | 收尾批次：IN-003＋QS-002＋DL-010＋NG-001 | 4050 綠 |
| — | 說明 | FS-013 隱私禁項**早已完成**（Vol2 批次 B 的 ForensicBanGateTests 就是它的守門）；本輪只把禁令清單攤成 fact 鍵 fs.privacy.boundary，**不另計一項** | — |

### 下一批（31→55，先簡後難）
- IN 殘項（S／L0，接縫現成）：IN-005 提權最小化審計、IN-006 自我更新完整性、IN-008 設定損毀自我修復（先備份，往返測試）
- NG 殘項（S）：NG-003 檢查進度記憶、NG-004 診斷報告整合、NG-005 導覽歷史（L1）
- FS 殘項（S／L1）：FS-008 BITS 佇列、FS-011 安裝痕跡時間線（MSI 事件）
- 之後才排：需要新解析器的 FS-002 Amcache／FS-004 SRUM／FS-015 VSS、要 UI 算繪的 AC-016 SLIT 熱圖、要 AML 的 AC-001..005、要角色的 SV-003／005／008／010、要硬體工具的 GX／UV 多數項。
