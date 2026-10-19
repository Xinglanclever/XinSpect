# 極致計畫・階段二｜韌體、開機鏈與信任根

> **規格庫，不是開工指令**：圈了 ID 才開工；接縫不到 API 級不許開工。
> **計數紀律**：只有「新增能力」或「收口」才算一項。
> **本階段 105 項（P2-001–P2-105）**，域碼 8 個。前置：階段一（骨架、六態、出處鏈、守門）。

---

## 一、階段定位

韌體是整台機器**最底層、也最不可替換**的一層：作業系統可以重裝、驅動可以回退、硬體可以更換，但開機鏈一旦被改寫，使用者通常看不到、也修不回。這一階段因此同時是「能力最深」與「責任最重」的一階段。

階段的目標不是替使用者判斷韌體是好是壞，而是把它**攤開到可對照**：用了哪些 ACPI 表、韌體映像裡有哪些區段、開機鏈量測記錄了什麼、Secure Boot 的雜湊落在哪一代、SPI 快閃的哪一段被改過。這些都是可讀的事實，且大多不需新權限——多數只需要讀取既有介面或解析使用者提供的映像。

排在階段二的理由是它**決定階段三之後一切事實的可信度**：若不清楚開機鏈是否被量測過、Secure Boot 是否在正確狀態，那麼後面所有「系統層事實」都少了可對照的基準。

---

## 二、完美判準（Gate）

1. **映像分解可導覽**：任一 UEFI 映像可攤開為 FV→FFS→區段樹，輸出 GUID、型別、位移、大小；解不開的位元組如實標示（P2-034）。
2. **以真實傾印為 fixture**：映像與 SPI 類解碼器的 golden 值來自真實機器，不用合成資料自證（P2-036）。
3. **開機鏈量測可重放**：TPM 事件日誌逐筆重放且與 PCR 對帳，重放不一致時具名指出是哪一筆（P2-064）。
4. **表頭校驗和全查**：每一張 ACPI／SMBIOS 表都檢查表頭校驗和；不符時標 `ReadError` 並附期望與實測值（P2-001）。
5. **出處到位移級**：每個表欄位的出處記到規範的章節與表號，AML 相關項另標「未評估位元碼」的界線（P2-004）。
6. **Secure Boot 四態**：變數存在性、已啟用、已設定、雜湊世代四者分開陳述，不以單一「已啟用」概括（P2-078）。
7. **SPI／快閃可比對**：可輸出 BIOS 區雜湊並與原廠映像比對，差異按區段列出（P2-048）。
8. **不做 EC 埠寫入**：EC 相關項全為唯讀評估，並在輸出中明示「不讀寫 EC 埠」的界線（P2-092）。

---

## 三、域碼與項數

| 域碼 | 域 | 項數 | 主要輸出前綴 | 敏感度上限 |
|---|---|---:|---|---|
| AC | ACPI 與 AML 深化 | 18 | `ac.` | L1 |
| SB | SMBIOS 與 PCIe 表深化 | 15 | `sb.`／`smbios.` | L1 |
| FW | UEFI 映像與開機 | 18 | `uefi.`／`bios.`／`fw.`／`boot.`／`spi.`／`mc.` | L1 |
| BT | Boot Guard 與信任根 | 12 | `bt.`／`fit.`／`tpm.` | L0 |
| TPM | TPM 與量測開機 | 14 | `tpm.` | L1 |
| EV | EFI 變數、SPI 與快閃 | 14 | `ev.`／`spi.`／`fw.` | L1 |
| EC | EC 與周邊韌體風險 | 7 | `ec.`／`pdev.` | L1 |
| FR | 韌體風險評分與報告 | 7 | `fwrisk.`／`fw.` | L1 |
| **合計** |  | **105** |  |  |

---

## 四、完整規格（九欄）

### ★ P2-034｜UEFI 映像 FV／FFS／區段樹分解

**目標**：把一個 UEFI 韌體映像攤成可導覽的結構樹，回答「這個映像裡有哪些模組、各佔哪裡」，且不猜解不出的位元組。

**接縫**：純解碼器（無硬體、無權限需求）。輸入為使用者提供的映像檔，或本專案已讀得到的 BIOS 區；輸出為節點清單（GUID／型別／位移／大小／所屬 FV）。

**方法**：依 UEFI PI 規範解析 FV 表頭（校驗和、長度、檔案數）→ FFS 表頭（GUID、型別、大小、狀態位）→ 區段（壓縮、GUID 定義、使用者介面、PE32）。遇到不支援的壓縮或未知型別時**停止該子樹並標示**，不嘗試啟發式猜測。

**輸出鍵**：`uefi.fv.count`／`uefi.fv.list`、`uefi.ffs.count`／`uefi.ffs.list`、`uefi.section.tree`（縮排樹）、`uefi.parse.gaps`（未能解析的位元組範圍清單）。

**三態行為**：映像不可讀 → `ReadError` 附原因；格式非 UEFI 映像（無有效 FV 表頭）→ `NotApplicable`；映像可讀但含未知壓縮型別 → `Present` 且 `uefi.parse.gaps` 非空（**部分解析不是失敗，但必須具名**）。

**誠實界線**：本項**不判定**任何模組是否惡意、是否簽章有效、是否與原廠一致；這些需要與原廠映像或資料庫對照，屬另一項（P2-048、P2-050）。本項也不反編譯 PE32 內容。

**守門**：金標／往返（模式：解碼器）——以真實映像切出的固定片段作 golden 值，逐節點比對；另以突變測試（截斷、長度欄位改寫、循環位移）驗證解碼器不崩潰且把異常標為缺口。**正對照**：故意把一個 FV 長度改壞，測試須紅燈。

**依賴**：P1-023（出處註記）、P1-056（金標守門）、P1-009（離線輸入接縫）。

**驗收**：真實映像 golden 值全綠；截斷輸入不崩潰；`uefi.parse.gaps` 與人工檢視結果一致；未施測的映像型別列入聲明。

### ★ P2-064｜TPM 開機鏈事件日誌重放

**目標**：把 TCG 事件日誌逐筆重放並與 TPM 的 PCR 對帳，回答「這台機器這次開機量測了什麼、每一步的雜湊是否對得上」。

**接縫**：TPM 2.0 事件日誌（本機可讀者）＋ PCR 讀取介面；重放為純函式，雜湊演算法依日誌中演算法 ID 選擇（本機以 SHA-256 為主）。

**方法**：解析日誌表頭 → 逐筆事件（PCR 索引、事件型別、摘要、事件資料）→ 依 TCG 規範的擴充規則重算 PCR → 與 TPM 實際 PCR 值比對。第一筆不一致處**停止重放並具名**該事件（索引、型別、期望值、實測值）。支援的演算法與事件型別以清單宣告，未支援者標示而非略過。

**輸出鍵**：`tpm.replay.summary`、`tpm.replay.pcr.{n}`（逐 PCR）、`tpm.replay.first_mismatch`、`tpm.replay.unsupported.count`。

**三態行為**：無 TPM → `NotSupported`；TPM 存在但無日誌 → `NotApplicable`；日誌存在但演算法不支援 → `Present` 且未支援清單非空；PCR 不可讀 → `InsufficientPrivilege` 並仍輸出重放結果（**重放本身不需要權限，這是本項設計的價值**）。

**誠實界線**：重放一致**不代表**開機鏈安全，只代表日誌與 PCR 自洽；本項不判斷哪個開機元件「該不該存在」。也未支援的演算法與事件型別一律列入清單，不以 0 補。

**守門**：金標（多台機器日誌切片）＋性質測試（擴充為結合律，順序不可交換）；正對照：人工改一個摘要位元，須紅燈並指出該筆。

**依賴**：P2-063（PCR 讀取）、P1-056、P1-079（動態家族理由，因輸出逐 PCR）。

**驗收**：本機重放與 PCR 全數相符；改壞一筆即紅燈；未支援清單與實際日誌相符。

---

## 五、矩陣（105 項）

| ID | 名稱 | 目標（一句） | 輸出鍵 | 敏感度 | 成本 | 依賴 | 實作鍵 |
|---|---|---|---|---|---|---|---|
| P2-001 | ACPI 表頭校驗和全查 | 逐表驗證表頭 checksum 並標不符 | `ac.checksum.bad` | L0 | S | P2-002 |ok:ac.checksum.bad 
| P2-002 | ACPI 表盤點與彙總 | 列出全部表、簽章、長度、序號 | `ac.tables.summary` | L0 | S | — |ok:ac.tables.summary 
| P2-003 | DSDT／SSDT 字串掃描 | 掃出 AML 內可見字串與計數 | `ac.aml.str.count` | L1 | S | — |ok:ac.aml.str.count 
| P2-004 | 第三方 SSDT 對照 | 標出非原廠簽章的 SSDT | `ac.ssdt.third.list` | L0 | M | P2-003 |ok:ac.ssdt.third.list 
| P2-005 | 位元碼評估界線聲明 | 明示不評估 AML 位元碼 | `ac.aml.boundary` | L0 | S | — |ok:ac.aml.boundary 
| P2-006 | MADT 全欄位 | 中斷控制器與處理器清單 | `ac.madt.*` | L0 | M | — |ok:ac.madt.* 
| P2-007 | SRAT／SLIT 拓撲 | NUMA 親和與距離矩陣 | `ac.srat.*`／`ac.slit.*` | L0 | M | — |ok:ac.srat.*／alias:numa.slit.nodes@Services/TopologyDeepenFactsService.cs:75 
| P2-008 | HMAT 記憶體效能表 | 頻寬／延遲階層 | `ac.hmat.*` | L0 | M | P2-007 |todo: 
| P2-009 | PPTT 處理器拓撲 | 核心／快取階層拓撲 | `ac.pptt.*` | L0 | M | — |ok:ac.pptt.* 
| P2-010 | DMAR／IVRS IOMMU 表 | 以 ACPI 表陳述 IOMMU 範圍 | `ac.dmar.*` | L0 | M | — |ok:ac.dmar.* 
| P2-011 | CEDT／CXL 表 | CXL 元件與記憶體範圍 | `ac.cedt.*` | L0 | M | — |alias:cxl.cfmws.count@Services/CxlFactsService.cs:34 
| P2-012 | NFIT 永久記憶體表 | NVDIMM 區域與裝置 | `ac.nfit.*` | L0 | M | — |ok:ac.nfit.* 
| P2-013 | BGRT 開機圖形表 | 開機標誌位置與狀態 | `ac.bgrt` | L0 | S | — |ok:ac.bgrt 
| P2-014 | SPCR／DBG2 除錯埠 | 序列與除錯埠宣告 | `ac.spcr`／`ac.dbg2.*` | L0 | S | — |ok:ac.spcr／ok:ac.dbg2.* 
| P2-015 | HPET 與計時器表 | 高精度計時器全欄位 | `ac.hpet.*` | L0 | S | — |ok:ac.hpet.* 
| P2-016 | MCFG 與 PCIe 段 | ECAM 基底與段號 | `ac.mcfg.*` | L0 | S | — |ok:ac.mcfg.* 
| P2-017 | TPM2 與 WPBT 表 | TPM 位置與 WPBT 存在性陳述 | `ac.tpm2`／`ac.table.wpbt` | L0 | S | — |ok:ac.tpm2／alias:se.wpbt@Services/ClosingFactsService.cs:27 
| P2-018 | PDTT／LPIT 等其餘表 | 逐簽章解碼並標未支援 | `ac.table.{sig}` | L0 | M | P2-002 |alias:acpi.table.{sig}@Services/AcpiTableService.cs:42 
| P2-019 | SMBIOS Type 0–3 | BIOS／系統／底板／機箱全欄位 | `sb.type0.*`–`sb.type3.*` | L1 | M | — |alias:sb.type0@Services/SmbiosIdentityFactsService.cs:32 
| P2-020 | SMBIOS Type 4／7 | 處理器與快取 | `sb.type4.*` | L0 | M | — |ok:sb.type4.* 
| P2-021 | SMBIOS Type 9 插槽 | 系統插槽與已用狀態 | `sb.slot.*` | L0 | M | — |ok:sb.slot.* 
| P2-022 | SMBIOS Type 11 OEM 字串 | 廠商字串列 | `sb.oem.strings` | L1 | S | — |alias:sb.oem.list@Services/SmbiosDeepFactsService.cs:92 
| P2-023 | SMBIOS Type 16／17 記憶體 | 陣列與裝置全欄位（序號遮蔽） | `sb.mem.*` | L1 | M | — |alias:sb.dim.*@Services/SmbiosDeepFactsService.cs:65 
| P2-024 | SMBIOS Type 19 記憶體映射 | 實體位址映射 | `sb.memmap.*` | L0 | M | — |ok:sb.memmap.count／ok:sb.memmap.total 
| P2-025 | SMBIOS Type 32 開機資訊 | 開機狀態與 POST 結果 | `sb.boot.*` | L0 | S | — |ok:sb.boot.* 
| P2-026 | SMBIOS Type 39 PSU | 電源供應器與容量 | `sb.psu.*` | L0 | S | P1-043 |alias:sb.psu@Services/SmbiosDeepFactsService.cs:99 
| P2-027 | SMBIOS Type 41 內建裝置 | 內建裝置與狀態 | `sb.onboard.*` | L0 | S | — |ok:sb.onboard.* 
| P2-028 | SMBIOS Type 42 管理控制器 | BMC／管理引擎存在性 | `sb.mc.*` | L1 | S | — |alias:sb.mctl@Services/SmbiosDeepFactsService.cs:79 
| P2-029 | SMBIOS Type 43／44 TPM 與處理器附加 | TPM 裝置與處理器特性 | `sb.tpmdev`／`sb.proc_add` | L1 | S | — |ok:sb.tpmdev／todo: 
| P2-030 | SMBIOS 結構長度政策 | 多於規範長度的尾巴欄位 | `sb.trailing.count` | L0 | S | — |ok:sb.trailing.count 
| P2-031 | SMBIOS 校驗與範圍檢查 | 表長度與結構邊界一致 | `sb.bounds.ok` | L0 | S | — |ok:sb.bounds.ok 
| P2-032 | M.2／PCIe 槽位對照 | 槽位對 PCIe 埠的對應 | `sb.m2.map` | L0 | M | P2-021 |alias:sb.m2slot.*@Services/SmbiosDeepFactsService.cs:109 
| P2-033 | SMBIOS 與 WMI 對照 | SMBIOS 陳述對作業系統視圖 | `sb.cross.*` | L0 | M | P2-019 |ok:sb.cross.* 
| ★P2-034 | UEFI 映像 FV／FFS／區段樹 | 攤開映像結構為可導覽樹 | `uefi.section.tree` | L1 | L | P1-009 |ok:uefi.section.tree 
| P2-035 | 映像 PE32 模組清單 | 列出映像內驅動與應用模組 | `uefi.pe.count` | L1 | M | P2-034 |ok:uefi.pe.count 
| ★P2-036 | 映像 golden 值以真實傾印為準 | 解碼正確性以真機樣本證明 | （測試） | L0 | M | P2-034 |todo: 
| P2-037 | 映像截斷與突變耐受 | 異常輸入不崩潰且標缺口 | `uefi.parse.gaps` | L0 | M | P2-034 |ok:uefi.parse.gaps 
| P2-038 | 映像壓縮型別支援清單 | 已支援／未支援壓縮宣告 | `uefi.comp.supported` | L0 | S | P2-034 |ok:uefi.comp.supported 
| P2-039 | BIOS 區唯讀取得 | 以既有介面取得 BIOS 區 | `bios.region.size` | L1 | M | P2-047 |alias:spi.flash_map@Services/SpiFlashHashService.cs:21 
| P2-040 | 開機模式與韌體介面 | UEFI／Legacy 與韌體供應商 | `fw.mode`／`fw.vendor` | L0 | S | — |ok:fw.mode／ok:fw.vendor 
| P2-041 | 韌體版本與日期 | 系統與選項 ROM 版本 | `fw.version`／`fw.date` | L0 | S | — |ok:fw.version／ok:fw.date 
| P2-042 | 開機順序與選項 | 開機裝置順序（唯讀） | `fw.bootorder` | L1 | S | — |ok:fw.bootorder 
| P2-043 | 開機時間分解 | 韌體／開機載入器／核心階段 | `boot.duration_ms` | L0 | M | — |ok:boot.duration_ms 
| P2-044 | 開機自我測試結果 | POST 結果與最後開機時間 | `boot.last_time` | L0 | S | P2-025 |ok:boot.last_time 
| P2-045 | 快速開機與休眠狀態 | 影響量測可信度的狀態 | `boot.fastboot` | L0 | S | — |ok:boot.fastboot 
| P2-046 | 韌體設定可讀項盤點 | 可讀設定與不可讀設定分列 | `fw.settings.readable` | L1 | M | — |ok:fw.settings.readable 
| P2-047 | SPI 快閃地圖 | SPI 區域劃分與用途 | `spi.map.*` | L0 | M | — |alias:spi.regions@Services/SpiFlashService.cs:184 
| P2-048 | BIOS 區雜湊與原廠比對 | 逐區段比對原廠映像 | `bios.hash`／`bios.diff` | L0 | L | P2-039 |alias:spi.bios_hash@Services/SpiFlashHashService.cs:22／alias:spi.bios_compare@Services/SpiFlashCompareService.cs:13 
| P2-049 | 微碼版本雙來源對帳 | MSR 與登錄檔版本互校 | `mc.version` | L0 | M | — |ok:mc.version 
| P2-050 | 映像與韌體版本一致性 | 映像內版本對韌體宣告版本 | `uefi.version.cross` | L0 | M | P2-041 |ok:uefi.version.cross 
| P2-051 | 選項 ROM 盤點 | PCI 裝置選項 ROM 存在與簽章 | `fw.rom.list` | L1 | M | — |ok:fw.rom.list 
| P2-052 | FIT 表存在性與解析 | 韌體介面表的可讀欄位 | `fit.present` | L0 | M | — |alias:fw.fit.summary@Services/SpiFitFactsService.cs:17 
| P2-053 | Boot Guard 狀態陳述 | 依 FIT 與 MSR 陳述 | `bt.state` | L0 | M | P2-052 |todo: 
| P2-054 | 初始開機塊判定 | IBB 範圍與雜湊宣告 | `bt.ibb` | L0 | L | P2-053 |ok:bt.ibb 
| P2-055 | 金鑰模組判定 | KM 存在性與類型 | `bt.km` | L0 | M | P2-053 |ok:bt.km 
| P2-056 | 量測開機支援旗標 | 平台是否支援量測開機 | `bt.measured_boot` | L0 | S | P2-053 |ok:bt.measured_boot 
| P2-057 | 開機鏈元件清單 | 逐層開機元件與其量測狀態 | `bt.chain.list` | L1 | M | P2-064 |alias:fw.bootchain.summary@Services/FirmwareAuditFactsService.cs:25 
| P2-058 | 信任根來源陳述 | 硬體信任根類型 | `bt.root` | L0 | S | — |ok:bt.root 
| P2-059 | 韌體寫入保護狀態 | BIOS 寫入保護旗標 | `bt.wp` | L0 | S | — |alias:fw.spiperm.summary@Services/FirmwareAuditFactsService.cs:18 
| P2-060 | 開機鏈落差報告 | 宣告與實測的量測落差 | `bt.chain.gap` | L1 | M | P2-057 |todo: 
| P2-061 | 韌體復原路徑陳述 | 復原區與膠囊支援 | `bt.recovery` | L0 | S | — |ok:bt.recovery 
| P2-062 | 信任根與 VBS 對照 | 硬體信任根對虛擬化安全 | `bt.cross.vbs` | L0 | S | P2-061；參照：P7-001 |ok:bt.cross.vbs 
| P2-063 | PCR 讀取與銀行清單 | 各 PCR 的演算法銀行 | `tpm.pcr.banks` | L0 | M | — |ok:tpm.pcr.banks 
| ★P2-064 | TPM 開機鏈重放 | 逐筆重放並與 PCR 對帳 | `tpm.replay.summary` | L1 | L | P2-063 |ok:tpm.replay.summary 
| P2-065 | PCR 逐索引陳述 | 逐 PCR 的重放結果 | `tpm.replay.pcr.{n}` | L1 | S | P2-064 |ok:tpm.replay.pcr.{n} 
| P2-066 | 重放首個不一致點 | 具名指出第一筆不符事件 | `tpm.replay.first_mismatch` | L1 | S | P2-064 |todo: 
| P2-067 | 未支援演算法與事件清單 | 不以 0 補，逐項列出 | `tpm.replay.unsupported.count` | L0 | S | P2-064 |todo: 
| P2-068 | TPM 存在性與規格版本 | 2.0／1.2 與實作版本 | `tpm.version` | L0 | S | — |ok:tpm.version 
| P2-069 | 擁有者與啟用狀態 | 是否已取得擁有權 | `tpm.owned` | L1 | S | — |ok:tpm.owned 
| P2-070 | 簽署金鑰存在性 | EK／AK 存在性（不含金鑰值） | `tpm.key.present` | L1 | S | P1-048（L3 禁項掃描守門） |todo: 
| P2-071 | 量測開機日誌摘要 | 日誌筆數與事件型別分布 | `tpm.log.summary` | L1 | S | P2-064 |ok:tpm.log.summary 
| P2-072 | 開機事件型別直方圖 | 各型別事件計數 | `tpm.log.histogram` | L1 | S | P2-064 |ok:tpm.log.histogram 
| P2-073 | TPM 與 Secure Boot 對照 | 兩者的狀態一致性 | `tpm.cross.sb` | L0 | S | P2-078 |ok:tpm.cross.sb 
| P2-074 | TPM 韌體版本與弱點知識包 | 對照已知弱點 | `tpm.cve` | L0 | M | P1-041 |todo: 
| P2-075 | 遠端證明能力陳述 | 是否具備證明能力（不上網） | `tpm.attest.capable` | L1 | S | — |ok:tpm.attest.capable 
| P2-076 | 清除與重設風險說明 | 明示本專案不做清除 | `tpm.clear.boundary` | L0 | S | — |ok:tpm.clear.boundary 
| P2-077 | 量測鏈與硬體事件關聯 | 開機事件對硬體變更時間線 | `tpm.cross.hwchange` | L1 | M | P2-064 |todo: 
| P2-078 | Secure Boot 四態陳述 | 存在／啟用／已設定／雜湊世代 | `ev.secureboot.*` | L0 | M | — |alias:uefi.secure_boot@Services/UefiBootFactsService.cs:30 
| P2-079 | 平台金鑰與 KEK 計數 | PK／KEK 存在性與計數 | `ev.pk.count` | L1 | S | P2-078 |alias:uefi.pk@Services/UefiSignatureFactsService.cs:37 
| P2-080 | db／dbx 條目計數 | 允許與撤銷清單規模 | `ev.db.count`／`ev.dbx.count` | L0 | S | P2-078 |alias:uefi.db.count@Services/UefiSignatureFactsService.cs:61／alias:uefi.dbx.count@Services/UefiSignatureFactsService.cs:61 
| P2-081 | dbx 版本與知識包對照 | 撤銷清單是否落後 | `ev.dbx.stale` | L0 | M | P1-042 |todo: 
| P2-082 | EFI 變數盤點 | 變數名稱、大小、屬性 | `ev.var.count` | L1 | M | — |ok:ev.var.count 
| P2-083 | 變數屬性與寫入保護 | 唯讀／執行期可寫 | `ev.var.attrs` | L1 | S | P2-082 |ok:ev.var.attrs 
| P2-084 | 開機選項變數 | 開機序號變數內容 | `ev.bootorder.var` | L1 | S | P2-082 |alias:uefi.boot_order_count@Services/UefiBootFactsService.cs:38 
| P2-085 | 不安全開機旗標 | testsigning 等狀態陳述 | `ev.testsigning` | L0 | S | — |alias:platform.testsigning@Services/BackendEnvironmentService.cs:80 
| P2-086 | SPI 區域逐段雜湊 | 逐區域雜湊與大小 | `spi.hash.*` | L0 | M | P2-047 |todo: 
| P2-087 | SPI 區域變更時間線 | 與前次快照比對的變更段 | `spi.changed` | L0 | M | P2-086 |todo: 
| P2-088 | 快閃描述符解析 | 唯讀解析描述符欄位 | `spi.desc.*` | L0 | M | P2-047 |todo: 
| P2-089 | 韌體膠囊與更新痕跡 | 已套用的膠囊與版本跳躍 | `fw.capsule.history` | L0 | M | P2-082 |ok:fw.capsule.history 
| P2-090 | 變數儲存用量 | NVRAM 使用量與上限 | `ev.nvram.usage` | L0 | S | P2-082 |ok:ev.nvram.usage 
| P2-091 | 韌體熵分布圖 | 逐區塊熵值（抗混淆指標） | `spi.entropy.*` | L0 | M | P2-086 |alias:spi.entropy_map@Services/SpiEntropyService.cs:47 
| P2-092 | EC 唯讀風險評估 | 陳述 EC 埠不讀寫的界線 | `ec.boundary` | L0 | S | — |ok:ec.boundary 
| P2-093 | EC 存在性與型號 | 由既有表間接陳述 | `ec.present` | L0 | S | P2-028 |ok:ec.present 
| P2-094 | 熱管理與風扇曲線可讀項 | 由 EC 或 WMI 可讀者 | `ec.thermal.readable` | L0 | M | — |ok:ec.thermal.readable 
| P2-095 | 電池與 EC 充放狀態 | 唯讀充放事實 | `ec.battery.state` | L0 | S | — |ok:ec.battery.state 
| P2-096 | 周邊韌體版本盤點 | 硬碟／SSD／網卡韌體版本 | `pdev.fw.version` | L0 | M | — |ok:pdev.fw.version 
| P2-097 | 周邊韌體與原廠對照 | 版本落後與建議（陳述非判決） | `pdev.fw.delta` | L0 | M | P1-043 |todo: 
| P2-098 | 韌體更新能力宣告 | 各元件是否可更新（唯讀宣告） | `pdev.fw.updatable` | L0 | S | — |ok:pdev.fw.updatable 
| P2-099 | 韌體風險分數構成 | 各項風險事實與其來源 | `fw.risk.facts.parts` | L0 | M | — |ok:fw.risk.facts.parts 
| P2-100 | 韌體風險等級陳述 | 分級但不下「不安全」判決 | `fw.risk.facts.level` | L0 | S | P2-099 |ok:fw.risk.facts.level 
| P2-101 | 韌體風險缺口申報 | 未取得項對風險分的影響 | `fw.risk.facts.gap` | L0 | S | P2-099 |ok:fw.risk.facts.gap 
| P2-102 | 韌體基線可匯出 | 匯出可離線比對的基線 | `fw.risk.facts.baseline` | L1 | S | P2-099 |ok:fw.risk.facts.baseline 
| P2-103 | 韌體變更報告 | 兩次掃描間的韌體差異 | `fw.risk.facts.delta` | L1 | M | P2-102 |ok:fw.risk.facts.delta 
| P2-104 | 韌體稽核報告輸出 | 交第三方複核的報告 | `fw.risk.facts.report` | L1 | M | P1-072 |ok:fw.risk.facts.report 
| P2-105 | 韌體章節的文件對帳 | 本階段規格與實作逐項對帳 | `fw.doc.reconcile` | L0 | S | P1-105 |ok:fw.doc.reconcile 

---

## 六、誠實界線與明文不做

> **命名註記（2026-10-16）**：本域的風險輸出鍵統一為 `fw.risk.facts.*`。
> 「風險」一詞只出現在「風險**事實**」這個用法上——列的是可複核的事實，不是判決。
> 同一原則見 P2-100 刻意使用 `level` 而非 `verdict`。

> **修正註記（2026-10-17）**：P2-068 的屬性編號與 revision 解碼**本來是錯的**——
> `TPM_PT_REVISION` 是「規格版號×100」（本機 `Win32_Tpm.SpecVersion`＝「2.0, 0, 1.16」，真值 116），
> 舊碼卻把它放在 `0x101`（那是 `LEVEL`）並用位元欄位拆版號，於是真機上顯示「revision 未回報」；
> 廠商字串在 `0x106..0x109`，舊碼讀 `0x104..0x107`（那是 `YEAR` 與 `MANUFACTURER`）。
> 同一次補上 P2-069（`tpm.owned`，TPMA_PERMANENT／STARTUP_CLEAR 逐位）與
> P2-075（`tpm.attest.capable`，陳述、不上網）。**為什麼測試一直綠**（假資料照著錯的假設造）
> 與修正的依據見 `docs/ULTIMATE-1000/GATE-LOG-257.md` 第七節。


**不做**：

| 不做 | 理由 | 解除條件 |
|---|---|---|
| EC 埠讀寫 | 與已裁決的唯讀立場衝突；EC 寫入無公開規格且有變磚風險 | 無（原則性裁決） |
| 反編譯 AML 為原始碼 | 反編譯結果是原始碼而非事實，價值與風險不成比例 | 若出現「必須看 ASL 才能回答」的具體問題，改為陳述該問題的原始位元組 |
| 以符號或網路服務歸因 | 與零上傳衝突 | 由使用者在能上網的機器自行執行 |
| 韌體更新、刷新、降版 | 寫入且不可回復 | 無 |
| 開機前記憶體測試 | 需獨立開機環境 | 無 |

**本階段沒有證明的事**：本階段證明「韌體與開機鏈的事實可讀、可對照、可回溯」，**不證明「機器安全」**。所有與信任相關的輸出都是陳述（狀態、存在性、雜湊），不是判決——這一點在 P2-100 的輸出鍵命名上刻意反映（`level` 而非 `verdict`）。
