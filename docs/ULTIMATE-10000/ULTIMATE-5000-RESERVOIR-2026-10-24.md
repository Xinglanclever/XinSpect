# 曦覽 XinSpect ・ ULTIMATE-5000 候選規格庫・儲集區總表（草案 v0）

**日期 2026-10-24　·　性質：候選清單（規格庫草案），不是開工指令**

> 本文件由代理（GLM）在工作區草案資料夾產出，**未放入 XinSpect 倉庫**。要納入請移至
> `docs/` 並過一輪查重。三條承襲規則與 ULTIMATE-1000 完全一致：圈了 ID 才開工；
> 計數紀律不變（新增能力或收口才算一項，重複／半套／純聲明不計）；
> 五原則與六態誠實不變（讀不到標 `Present／NotApplicable／NotSupported／InsufficientPrivilege／Unknown／ReadError`，不以 0 或典型值填補）。

---

## 一、對帳起點（這份清單不重複計什麼）

| 既有資產 | 規模 | 出處 |
|---|---:|---|
| INDUSTRIAL-CATALOG VOL1 | 300 項 | `docs/INDUSTRIAL-CATALOG-2026-10-10.md` |
| INDUSTRIAL-CATALOG VOL2 | 300 項 | `docs/INDUSTRIAL-CATALOG-VOL2-2026-10-11.md` |
| ULTIMATE-1000（十階段） | 1,050 項（階段一完成、階段二進行中：75 到位＋24 別名＋15 待辦） | `docs/ULTIMATE-1000/` |
| 事實鍵目錄 | 1,044 把（含動態家族） | `Services/FactKeyCatalog.cs` |
| 測試基線 | 6,580 綠（2.87） | `Tests/TestSuiteBaseline.cs` |

「新功能」的計數單位沿用專案既有口徑：**可定位的事實鍵與收口**，不是行銷語意的功能句。
使用者可感知的功能粒度，誠實上限是數百；但事實鍵粒度上，規格空間遠大於 5,000——這正是
ULTIMATE-1000 已經在用的計數法（1,050 項裡多數是鍵級與收口級）。

---

## 二、5,000 這個數字成立嗎——成立，依據如下

以下為**儲集區分解表**：每一列的「依據」都是可查證的規格面事實（規格文件已定義的欄位／位元／
結構數量），「保守列數」是扣掉明顯已做項之後的下限。每一列的計數指**目錄列**，不承諾本機必讀
得到——讀不到照樣列（六態），這是階段二已經在用的口徑。

| # | 儲集區 | 依據（規格面） | 保守列數 | 對應階段 |
|---:|---|---|---:|---|
| 1 | CPUID 位元旗標與欄位全集 | Intel SDM／AMD APM 已定義的 leaf×subleaf×暫存器位元（leaf 1、7.0/7.1/7.2、0x80000001 等合計逾 300 個命名位元，加上各葉數值欄位） | 400 | 階三 |
| 2 | MSR 目錄（Intel Vol.4＋AMD） | Vol.4 以 CPUID 簽章分表記載的暫存器，跨家族去重後仍以千計；當前平臺可讀子集遠小於此，計數以目錄列為準 | 1,000 | 階三 |
| 3 | 快取／拓撲／NUMA 可定位欄位 | leaf 4 逐 subleaf、leaf 0xB/0x1F/0x18(DAT)、SLIT/SRAT、PPTT | 150 | 階三 |
| 4 | SPD（DDR4／DDR5）＋記憶體拓撲 | DDR4 SPD 附錄 byte 級映射；DDR5 SPD hub 上/下兩個 512B 區塊；XMP 3.0／EXPO 設定檔欄位 | 250 | 階三 |
| 5 | SMBIOS 型別×欄位（扣除已做） | 3.4 版約 45 個結構型別、平均十餘欄；已做之 Type 19、部分 Type 17 需逐項扣除 | 300 | 階二／三 |
| 6 | ACPI 表×欄位（淨新增） | 已做表頭級者扣除；PPTT／SLIT／CDIT／NFIT／IORT／MPST 等尚未展開 | 250 | 階二 |
| 7 | UEFI 變數／開機鏈欄位 | 已知變數（全域＋廠商 GUID）逐變數的值欄位與屬性 | 150 | 階二 |
| 8 | TPM 2.0 屬性／TCG 事件類型／PCR 族 | Part 2 之 TPM_PT 屬性組、TCG EV_* 事件類型、PCR bank×索引 | 200 | 階二 |
| 9 | NVMe Identify×Log Page×Feature ID | 2.0 規格：Identify Controller／Namespace／I/O CS 三結構約 250 個已定義欄位；log page 族（含 persistent event log、endurance group、ANA）；feature ID 約 40 個 × get-feature 欄位 | 600 | 階四 |
| 10 | ATA／SATA（IDENTIFY／SMART／SCT／DevStat） | IDENTIFY DEVICE 256 word 中約 150 個有意義 word；錯誤日誌／自測日誌逐條欄位；SCT 溫度歷史；Device Statistics 逐頁 | 250 | 階四 |
| 11 | SAS／SMP（如有控制器） | Report General／PHY 錯誤計數等 | 80 | 際四 |
| 12 | PCIe 標頭＋能力結構目錄 | 經典能力約 25 型＋延伸能力約 50 型，每型 3–10 欄（含 AER 逐計數器、L1 子狀態、等化欄位） | 300 | 階五 |
| 13 | USB／HID／Type-C／UCSI | 標準＋類別描述元逐欄；HID report descriptor item；UCSI 能力與現狀 | 200 | 階五 |
| 14 | 網路（NDIS／Wi-Fi／BT／PHY） | 卸載能力位元、802.11 各代能力位、regdomain、PHY EEE | 200 | 階五 |
| 15 | GPU（NVML／ADL／IGCL）＋EDID/CTA | NVML 查詢族（時脈／throttle 原因／ECC／退役頁／PCIe 計數）；EDID 基本塊＋CTA 延伸塊逐欄 | 300 | 階五 |
| 16 | 電源／電池／熱／EC 可讀面 | BIF/BST 欄位、熱區（已做盤點，欄位未全收口）、電源供應器型別（SMBIOS 38–42） | 100 | 階二／三 |
| 17 | OS／PnP／WMI／事件通道 | Win32_* 硬體相關子集、DEVPKEY 清單、WHEA 事件類型、硬體相關事件通道 | 300 | 階六 |
| 18 | 安全／鑑識／BitLocker／事件日誌 | 事件日誌逐型別統計、BitLocker 模式欄、憑證鏈展開（不列主體名稱） | 150 | 階七 |
| 19 | PMU 事件目錄（ring0 前提） | 架構式事件表＋家族式事件表（Intel SDM／AMD PPR），目錄列不承諾本機可量 | 400 | 階八 |
| 20 | 量測科學方法論 | 重複性／預算／分位數／互校／自校——方法論項自成計數 | 150 | 階八 |
| 21 | 平台化／報告／介面 | 匯出 schema、CLI 契約、無障礙、多語同步、SDK | 200 | 階九 |
| 22 | 企業／機隊／知識包 | 聚合基線、差異報告、驗收表、schema 遷移 | 200 | 階十 |
| 23 | 明文不做清單（附解除條件） | 界線成文本身計數 | 50 | 全域 |
| | **合計** | | **≈ 6,180** | |

**扣重陳述**：上表與既有 1,650 個計畫項（VOL1＋VOL2＋ULTIMATE-1000）及 1,044 把鍵的重疊，
按查重規則逐項扣除；重疊估計不超過 1,000 列，**扣除後仍 > 5,000**。5,000 不是湊出來的數字，
是規格空間的下限；真正的約束不是「有沒有 5,000 項」，而是**每一項能不能補齊出處與接縫**。

---

## 三、第一波展開：203 項（十三域）

格式沿用階段檔：候選鍵為**建議前綴**（實際以 FactKeyCatalog 查重後為準）；「前提」欄如實標註
ring0／規格包／查重／待核。**未圈選不開工。**

### U5-CP ・ CPU 自述（CPUID 與拓撲）・ 25 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-CP-001 | Leaf 1 ECX/EDX 旗標逐位收口（缺位補齊、命名表版本化） | `cpuid.1.*` | Intel SDM；查重：部分已有 |
| U5-CP-002 | Leaf 7 subleaf 0 四暫存器逐位（AMX／KEYLOCKER／CET 等） | `cpuid.7.0.*` | Intel SDM |
| U5-CP-003 | Leaf 7 subleaf 1/2 逐位（AVX-VNNI 等） | `cpuid.7.1.*` | Intel SDM |
| U5-CP-004 | Leaf 4 快取 subleaf 族逐欄（級別／大小／關聯／共用域） | `cpuid.4.{n}.*` | Intel SDM；查重 |
| U5-CP-005 | Leaf 2 TLB／快取描述元逐組解碼（描述元值→語意表） | `cpuid.2.desc.*` | Intel SDM 附表 |
| U5-CP-006 | Leaf 0xB 與 0x1F 拓撲葉對照（x2APIC 層級差異陳述） | `cpuid.topo.cross` | Intel SDM |
| U5-CP-007 | Leaf 0x15/0x16 時脈比率與標稱頻率（不可得時明說，不推算） | `cpuid.clkratio.*` | Intel SDM |
| U5-CP-008 | Leaf 0x17 SOC 廠商識別族（串列 subleaf） | `cpuid.soc.*` | Intel SDM |
| U5-CP-009 | Leaf 0x18 位址轉譯參數（頁大小／TLB 逐級） | `cpuid.18.{n}.*` | Intel SDM |
| U5-CP-010 | Leaf 0x1A hybrid 逐核類型（native model ID＋core type） | `cpuid.1a.core{n}` | Intel SDM；動態家族 |
| U5-CP-011 | Leaf 0x10 架構監視計數器 subleaf 與事件長度對照 | `cpuid.10.*` | Intel SDM |
| U5-CP-012 | Leaf 0x12 SGX 子葉展開（EPC 逐銀行大小） | `cpuid.12.*` | Intel SDM；查重：階二 mc/sgx |
| U5-CP-013 | Leaf 0x14 處理器追蹤能力逐位 | `cpuid.14.*` | Intel SDM |
| U5-CP-014 | Leaf 0x19 KEYLOCKER 參數（存在才列） | `cpuid.19` | Intel SDM |
| U5-CP-015 | Hypervisor 葉族 0x400000xx 逐廠牌（Hyper-V/KVM/VMware/Xen 簽章） | `cpuid.hv.*` | 各廠虛擬化規格 |
| U5-CP-016 | AMD 0x80000001 逐位（SVM／LAHF 等） | `cpuid.amd.80000001.*` | AMD APM |
| U5-CP-017 | AMD 0x80000007/08 逐位（invariant TSC／ASID／TopologyExt） | `cpuid.amd.ext8.*` | AMD APM |
| U5-CP-018 | AMD 0x8000001A/1B/1D/1E 逐位（架構能力／快取拓撲／APIC 拓撲） | `cpuid.amd.extb.*` | AMD APM |
| U5-CP-019 | AMD 0x80000026 拓撲第二版（Zen 4+） | `cpuid.amd.topo2` | AMD PPR；待核 |
| U5-CP-020 | Brand string 三段拼裝與字元集校驗（不可列印字元如實顯示） | `cpu.brand.raw` | — |
| U5-CP-021 | Microcode 修訂雙源對帳（登錄檔 vs MSR 0x8B，衝突陳述） | `mc.rev.crosscheck` | 查重：mc.* 已有，此為收口 |
| U5-CP-022 | APIC ID 全集 vs OS 可見核（SMT／被隱藏核差異） | `cpu.apic.visible` | — |
| U5-CP-023 | NUMA：SLIT 距離矩陣與 Windows 節點對照 | `numa.slit.grid` | ACPI；查重：SRAT 已有 |
| U5-CP-024 | hybrid 類型統計（P/E/LPE 分列，混合型不得加總成單一核數） | `cpu.hybrid.counts` | — |
| U5-CP-025 | CPUID 最大葉與廠商串的規格版本宣告（哪個葉由哪版規格定義） | `cpuid.leaf.max` | 出處鏈 spec.ref |

### U5-MS ・ MSR／電源／時脈（多數 ring0 前提）・ 18 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-MS-001 | IA32_APERF/MPERF 逐核有效時脈（窗口化取樣） | `clk.eff.core{n}` | ring0；查重 |
| U5-MS-002 | C-state 駐留逐態（C1/C3/C6/C7/C8/C9/C10） | `pwr.cres.{n}` | ring0 |
| U5-MS-003 | IA32_THERM_STATUS 逐核（狀態位元／log 位） | `thermal.core{n}.status` | ring0 |
| U5-MS-004 | 套件溫度目標與調節偏移（TEMP_TARGET） | `thermal.pkg.target` | ring0 |
| U5-MS-005 | PL1/PL2/tau 現值讀回（MSR_PKG_POWER_LIMIT 0x610） | `pwr.limit.pl{1,2}` | ring0 |
| U5-MS-006 | RAPL 能量計數器逐域（pkg/cores/dram/gpu/psys）；換算係數無出處就不出功率，只出原始計數 | `pwr.rapl.{dom}` | ring0＋規格包 |
| U5-MS-007 | Turbo 比率限制逐組讀回（0x1AD） | `clk.turbo.grp{n}` | ring0 |
| U5-MS-008 | 電流上限與 VR 遙測（無規格出處者僅列原始值） | `pwr.current.limit` | ring0；待核 |
| U5-MS-009 | Ring／快取頻率上限（IA32_PLATFORM_INFO 0xCE 位域） | `clk.ring.limit` | ring0 |
| U5-MS-010 | 電壓偏移現值讀回（0x150 平面選擇；非架構 MSR，僅讀不寫） | `volt.offset.plane{n}` | ring0；非架構，待核 |
| U5-MS-011 | IA32_MISC_ENABLE 逐位現值（快速字串／Turbo 位） | `msr.misc_enable` | ring0 |
| U5-MS-012 | IA32_FEATURE_CONTROL（lock／VMX／SGX 啟用位） | `msr.feature_control` | ring0 |
| U5-MS-013 | MTRR/PAT 容量與類型清單 | `msr.mtrr.cap` | ring0 |
| U5-MS-014 | AMD P-state 表逐態（COF/VID；CPPC 或 MSR 路徑） | `amd.pstate.{n}` | ring0；AMD PPR |
| U5-MS-015 | AMD SMU 遙測路徑立場宣告（未公開界面→明文不做，見 U5-NX-006） | `amd.smu.stance` | 界線成文 |
| U5-MS-016 | Throttle 原因彙總（thermal/power/current/residency 四源分列，不合成單一分數） | `throttle.why` | 彙總陳述 |
| U5-MS-017 | TSC／核心時脈／匯流排時脈三源對照與一致性陳述 | `clk.source.cross` | — |
| U5-MS-018 | C0 residency 全核聚合 | `pwr.c0.total` | ring0 |

### U5-MM ・ 記憶體・ 15 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-MM-001 | DDR5 SPD hub 上/下兩個 512B 區塊直讀（非 256B 舊佈局） | `spd.ddr5.block{0,1}` | JEDEC；ring0（SMBus） |
| U5-MM-002 | SPD 原始 hex 傾印（含保留位元組，不加工） | `spd.raw.{dimm}` | — |
| U5-MM-003 | 模組製造商 OUI 對照（知識包；三態：命中／未收錄／無法解析） | `spd.mfr.oui` | 知識包 |
| U5-MM-004 | DRAM 顆粒製造商與模組品牌分開陳述（不同鍵） | `spd.dram.mfr` | JEDEC |
| U5-MM-005 | XMP 3.0 三設定檔逐欄 vs EXPO 並存陳述（互不代言） | `spd.xmp.{p}`／`spd.expo` | JEDEC／AMD |
| U5-MM-006 | DDR5 PMIC／溫度感測繼資料欄（位移待核，未核不出值） | `spd.pmic.*` | JEDEC；待核 |
| U5-MM-007 | 記憶體速度「設定 vs 實跑」對照（SPD 速率 vs 控制器現值） | `mem.speed.setvsrun` | — |
| U5-MM-008 | 通道/插槽實體對映（SMBIOS Type 17 locator 交叉） | `mem.slot.map` | 查重 |
| U5-MM-009 | 延遲曲線（stride 掃描）自動推導快取邊界（roadmap 老項） | `mem.latency.curve` | 查重 |
| U5-MM-010 | 核到核延遲矩陣（同 CCD／跨 CCD／跨 socket 分塊，不加總單值） | `mem.latency.cc` | 查重 |
| U5-MM-011 | 帶寬四向實測與理論峰值對照（讀/寫/複製/延遲） | `mem.bw.*` | — |
| U5-MM-012 | 快取階層逐級容量與共享域（L3 共享範圍明說） | `cache.l{1d,1i,2,3}.size` | leaf 4 |
| U5-MM-013 | TLB 頁大小支援清單（leaf 2 與 leaf 0x18 交叉） | `mem.tlb.pages` | Intel SDM |
| U5-MM-014 | 二階轉譯（EPT/NPT）能力與啟用分開陳述 | `mem.virt.ept` | — |
| U5-MM-015 | TME／RAS 能力位與啟用狀態（能力≠啟用） | `mem.tme.state` | Intel SDM |

### U5-BC ・ SMBIOS／PCIe／ACPI 深化・ 18 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-BC-001 | Type 0 擴充欄（ROM 大小延伸、特性位元組全列） | `sb.type0.*` | SMBIOS 3.4 |
| U5-BC-002 | 序號/UUID 一致性與「同機不變」假設的明文界線 | `sb.ident.stability` | 界線成文 |
| U5-BC-003 | Type 4 電壓/外頻/最大速度/現速欄收口 | `sb.type4.speed` | SMBIOS 3.4 |
| U5-BC-004 | Type 17 詳細技術（電壓/製造日期碼；序號位置 L2 遮蔽） | `sb.type17.detail` | SMBIOS 3.4 |
| U5-BC-005 | Type 38–42 電源供應器（瓦數/電流/效率評等/冗餘）與 IPMI | `sb.ps.*` | SMBIOS 3.4 |
| U5-BC-006 | Type 41 onboard 裝置 vs PCI 列舉對照 | `sb.type41.cross` | SMBIOS 3.4 |
| U5-BC-007 | OEM 結構（0x80+）：原始長度與「不解碼」宣告 | `sb.oem.raw` | 廠商未公開 |
| U5-BC-008 | SMBIOS 規格有、本機韌體沒有的型別差集清單 | `sb.types.missing` | — |
| U5-BC-009 | PCIe LnkCap vs LnkSta（上限 vs 協商現值；ASPM 支援/啟用差異） | `pcie.link.capvssta` | PCIe Base Spec |
| U5-BC-010 | PCIe AER 逐計數器（可校正/致命各型別暫存器；cap 存在才列） | `pcie.aer.{ce,fe}` | PCIe Base Spec |
| U5-BC-011 | L1 子狀態（L1.1/L1.2）能力與啟用 | `pcie.l1sub` | PCIe Base Spec |
| U5-BC-012 | 16 GT/s+ 等化欄位與 LaneErr 計數 | `pcie.eq.laneerr` | PCIe Base Spec |
| U5-BC-013 | ATS/PRI/PASID/ACS 能力存在性清單 | `pcie.cap.atsetc` | PCIe Base Spec |
| U5-BC-014 | DPC（下行埠防護）能力與最後狀態 | `pcie.dpc` | PCIe Base Spec |
| U5-BC-015 | ACPI PPTT 拓撲與快取階層表逐節點 | `ac.pptt.node{n}` | ACPI spec |
| U5-BC-016 | ACPI SLIT/CDIT 距離表 | `ac.slit.grid` | ACPI spec |
| U5-BC-017 | ACPI NFIT/PMEM（如有 NVDIMM） | `ac.nfit.*` | ACPI spec |
| U5-BC-018 | ACPI IORT（中斷重映射/IOMMU 拓撲，如有） | `ac.iort.*` | ACPI spec |

### U5-ST ・ 儲存（NVMe／ATA／SAS）・ 22 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-ST-001 | Identify Controller 逐欄收口（未落地欄位清單化） | `nvme.idctl.gap` | NVMe 2.0 |
| U5-ST-002 | Identify Namespace 逐欄（NCAP/NUSIZE/NUSE、LBA 格式族） | `nvme.ns.*` | NVMe 2.0；動態家族 |
| U5-ST-003 | LBA 格式支援矩陣（每 namespace 的格式與 metadata 量） | `nvme.ns.lba{fmt}` | NVMe 2.0 |
| U5-ST-004 | 智慧健康紀錄頁門檻欄（臨界/告警逐感測器） | `nvme.smart.thresh` | NVMe 2.0 |
| U5-ST-005 | 韌體槽紀錄頁收口（七槽版本/作用槽；查重 pdev.fw.updatable） | `nvme.fw.slots` | 收口 |
| U5-ST-006 | 變更命名空間清單（動態家族） | `nvme.ns.changed` | NVMe 2.0 |
| U5-ST-007 | 命令效果表（支援命令與效果位） | `nvme.cmd.effects` | NVMe 2.0 |
| U5-ST-008 | 裝置自測結果紀錄頁逐條（唯讀列結果；觸發自測屬寫入不提供） | `nvme.selftest.{n}` | NVMe 2.0 |
| U5-ST-009 | 持久事件日誌逐事件類型（韌體/安全/格式化歷史） | `nvme.pevent.{type}` | NVMe 2.0；頁號待核 |
| U5-ST-010 | Get LBA Status（壞塊/棄用區掃描，唯讀） | `nvme.lbastatus` | NVMe 2.0 |
| U5-ST-011 | Get Feature 逐 ID（APST/HMB/PSP/仲裁；只讀現值） | `nvme.feature.{id}` | NVMe 2.0 |
| U5-ST-012 | 溫度感測器門檻現值（composite＋1–9 逐顆） | `nvme.temp.sensor{n}.th` | NVMe 2.0 |
| U5-ST-013 | Sanitize 狀態唯讀查詢（執行屬寫入，明文不做） | `nvme.sanitize.status` | NVMe 2.0 |
| U5-ST-014 | Endurance group 與 NVM set 拓撲（如有） | `nvme.endur.{grp}` | NVMe 2.0 |
| U5-ST-015 | ANA 群組（多路徑盤；單路徑機器＝NotApplicable） | `nvme.ana.grp{n}` | NVMe 2.0 |
| U5-ST-016 | RPMB 存在性宣告（不做寫入測試） | `nvme.rpmb.present` | NVMe 2.0 |
| U5-ST-017 | FDP（彈性資料放置）能力位（新規格，盤支援稀少） | `nvme.fdp.cap` | NVMe 2.0/3.0；待核 |
| U5-ST-018 | ATA IDENTIFY word 級收口（未落地 word 清單化） | `ata.word.gap` | ACS-4 |
| U5-ST-019 | ATA SMART 錯誤日誌逐條（含暫存器快照） | `ata.smart.errlog{n}` | ACS-4 |
| U5-ST-020 | ATA 自測日誌逐條與壽命時數 | `ata.smart.selflog{n}` | ACS-4 |
| U5-ST-021 | SCT 溫度歷史表（支援 SCT 才列） | `ata.sct.hist` | ACS-4 |
| U5-ST-022 | Device Statistics 逐頁（旋轉/溫度/電源統計） | `ata.devstat.page{n}` | ACS-4 |

### U5-GP ・ GPU／顯示・ 16 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-GP-001 | NVML 逐引擎時脈與使用率（graphics/sm/mem/video 分列） | `gpu.clk.eng{e}` | NVML；查重 |
| U5-GP-002 | NVML throttle 原因位元分列（SW cap/HW slow/thermal） | `gpu.throttle.bits` | NVML |
| U5-GP-003 | NVML ECC 計數器族（volatile/aggregate；單/雙位） | `gpu.ecc.{cnt}` | NVML |
| U5-GP-004 | 退役頁/行重映射清單（ECC 事件遺跡，如實列） | `gpu.ecc.retired` | NVML |
| U5-GP-005 | NVML PCIe 重播與吞吐計數 | `gpu.pcie.replay` | NVML |
| U5-GP-006 | MIG 分割拓撲（如有；無＝NotApplicable） | `gpu.mig.inst{n}` | NVML |
| U5-GP-007 | AMD perf cap 位元與熱點/電壓（ADL 路徑） | `amd.perfcap.bits` | ADL SDK |
| U5-GP-008 | Resizable SAM 狀態（BIOS 能力＋驅動啟用分開） | `amd.sam.state` | — |
| U5-GP-009 | Intel Arc/Xe 遙測（公開界面待核；核不出就明說） | `intel.gpu.tele` | 待核 |
| U5-GP-010 | VRAM 模組製造商（無來源就明說，不猜） | `gpu.vram.mfr` | — |
| U5-GP-011 | EDID 基本塊逐欄（廠商碼/產品/序號/製造週） | `edid.base.*` | VESA E-EDID |
| U5-GP-012 | CTA-861 延伸塊逐塊（視訊/音訊/HDR 靜態資料） | `edid.cta.block{n}` | CTA-861-G |
| U5-GP-013 | DisplayPort DSC 能力與通道 | `edid.dp.dsc` | VESA DP |
| U5-GP-014 | HDMI 2.1 FRL/SCDC 能力 | `edid.hdmi.frl` | HDMI Spec |
| U5-GP-015 | 面板型號與製造週如實解讀（型號字串≠面板廠，分開陳述） | `edid.panel.model` | — |
| U5-GP-016 | GPU 驅動版本三源對照（登錄檔/NVML/檔案版本；衝突陳述） | `gpu.driver.cross` | — |

### U5-NP ・ 網路／周邊／Type-C・ 16 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-NP-001 | NDIS 卸載能力清單與啟用狀態（RSC/LSO/RSS/校驗，能力≠啟用） | `net.ndis.offload.*` | NDIS |
| U5-NP-002 | RSS 佇列數（設定 vs 實際） | `net.rss.queue` | NDIS |
| U5-NP-003 | Wi-Fi 支援標準逐代位元（11a/b/g/n/ac/ax/be） | `wifi.std.bits` | 802.11 |
| U5-NP-004 | Wi-Fi 現況（PHY/頻寬/通道/天線數/空間流） | `wifi.now.*` | WLM/NDIS |
| U5-NP-005 | 藍牙版本與 LE 能力位 | `bt.ver.le` | BT Core Spec |
| U5-NP-006 | Thunderbolt 世代/安全等級/韌體（如有控制器） | `tbolt.gen.sec` | TB SDK；待核 |
| U5-NP-007 | USB4 路由器/介面卡能力（如有） | `usb4.cap` | USB4 Spec；待核 |
| U5-NP-008 | USB 逐裝置描述元全錄（含字串描述元/bcdUSB） | `usb.dev{n}.desc` | USB 2.0/3.x/4 |
| U5-NP-009 | USB 埠協商上限 vs 實際速率（逐埠） | `usb.port.speed` | — |
| U5-NP-010 | UCSI：Type-C 方向/電力契約/PD 版本（WMI UCSI 類別） | `typec.ucsi.*` | UCSI Spec |
| U5-NP-011 | HID report descriptor 解析（滑鼠解析度單位/鍵盤佈局） | `hid.mouse.units` | HID Spec |
| U5-NP-012 | RSS indirection 表現值 | `net.rss.indir` | NDIS |
| U5-NP-013 | 網卡原始計數器族（含錯誤/discards，不加工成「健康度」） | `net.if.counters` | — |
| U5-NP-014 | Wi-Fi 國碼/regdomain 與頻道可用性 | `wifi.regdomain` | 802.11d |
| U5-NP-015 | 有線 PHY EEE 能力與狀態 | `net.phy.eee` | 802.3az |
| U5-NP-016 | USB hub 韌體版本（有來源才列；pdev.fw 收口） | `usb.hub.fw` | 收口 |

### U5-OS ・ OS／驅動／事件・ 15 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-OS-001 | DEVPKEY 逐裝置主要欄位收口（驅動日期/版本/簽署者） | `os.devpkey.gap` | Windows SDK；收口 |
| U5-OS-002 | PnP 問題碼逐裝置（Code 10/28/43 的「為什麼讀不到」一句話） | `os.pnp.issue` | Windows SDK |
| U5-OS-003 | 驅動商店 vs 使用中驅動對照（孤兒驅動清單） | `os.driverstore.orphan` | — |
| U5-OS-004 | 核心 DMA 保護/IOMMU 狀態鏈 | `os.dma.protect` | — |
| U5-OS-005 | HVCI/VBS 啟用鏈（含未啟用原因碼） | `os.vbs.state` | — |
| U5-OS-006 | WHEA 事件逐類型計數（可校正/致命、來源元件） | `os.whea.type{n}` | WHEA |
| U5-OS-007 | WHEA 最新事件的如實轉述（不做因果推論） | `os.whea.last` | WHEA |
| U5-OS-008 | 電源計劃逐設定現值（含隱藏項） | `os.power.setting{k}` | powercfg/登錄檔 |
| U5-OS-009 | 現代待機 vs S3：韌體說法與 OS 說法對照 | `os.standby.mode` | — |
| U5-OS-010 | 電池設計容量/滿充/循環/化學（BIF 交叉） | `bat.design.full` | ACPI；查重 |
| U5-OS-011 | 電池放電曲線被動記錄（觀察不是預測） | `bat.discharge.curve` | — |
| U5-OS-012 | 開機設定唯讀列舉（bcdedit；含 hypervisor 設定） | `boot.bcd.*` | — |
| U5-OS-013 | 硬體相關事件通道清單與可用性 | `os.evchan.list` | — |
| U5-OS-014 | 熱節流事件統計（Kernel-Power 等，逐事件 ID） | `os.thermal.events` | — |
| U5-OS-015 | Windows 更新歷史中驅動類項目清單 | `os.wu.drivers` | — |

### U5-SE ・ 安全／信任・ 11 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-SE-001 | NVRAM 用量：先證明有合法量測路徑（無公開列舉 API）；證不出即移入明文不做 | `uefi.nvram.usage` | 調度准入三條件 |
| U5-SE-002 | TCG 事件日誌 EV_* 逐型別統計 | `tpm.ev.type{n}` | TCG PC Client |
| U5-SE-003 | PCR 現值逐 bank（SHA-1/256/384；重放驗證僅計算，不寫入） | `tpm.pcr.{bank}` | TCG；查重 |
| U5-SE-004 | BitLocker 加密方法/類型/硬體 vs 軟體 | `bl.mode` | — |
| U5-SE-005 | db 憑證鏈展開（不列主體名稱——沿既有 L3 規則） | `uefi.db.chain` | UEFI Spec |
| U5-SE-006 | dbx 撤銷媒介統計（憑證/雜湊分計） | `uefi.dbx.kinds` | UEFI Spec |
| U5-SE-007 | SBER（安全開機錯誤復原）狀態（如有） | `uefi.sber` | UEFI Spec |
| U5-SE-008 | Capsule 更新型別能力（能力宣告，不觸發） | `uefi.capsule.cap` | UEFI Spec |
| U5-SE-009 | Credential Guard/Device Guard 狀態鏈 | `sec.cguard.state` | — |
| U5-SE-010 | 核心偵錯器附加狀態（本身是「其他讀值可信度」的前提） | `sec.kd.attached` | — |
| U5-SE-011 | 測試簽署/簽章強制狀態矩陣（四象限成文） | `sec.testsign.matrix` | — |

### U5-MT ・ 量測科學・ 16 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-MT-001 | 逐鍵重複性：同一鍵 N 次量測分布與漂移（抽樣族） | `mt.repeat.{key}` | — |
| U5-MT-002 | 量測預算表：每域誤差來源成文 | `mt.budget.{domain}` | — |
| U5-MT-003 | 溫度互校：同一元件多來源差異表 | `mt.thermal.xref` | — |
| U5-MT-004 | 感測器取樣率上限量測（各來源最快可達） | `mt.rate.{src}` | — |
| U5-MT-005 | 磁碟唯讀延遲掃描（4K/1M × 佇列深度矩陣） | `mt.disk.qd{n}` | — |
| U5-MT-006 | 供電軌瞬態觀察（負載下 12V/5V 抖動；無示波器的界線成文） | `mt.rail.jitter` | 界線成文 |
| U5-MT-007 | 風扇 PWM/RPM 曲線被動記錄 | `mt.fan.curve` | — |
| U5-MT-008 | 自我 diff：兩次掃描的不穩定鍵偵測 | `mt.selfdiff.unstable` | — |
| U5-MT-009 | 跨版本自校：兩版執行檔事實鍵 diff 報告 | `mt.verdiff` | — |
| U5-MT-010 | 延遲分位數（P50/P95/P99）取代均值 | `mt.quantiles` | — |
| U5-MT-011 | 快取邊界自動推導的置信度陳述（拐點偵測方法成文） | `mt.cachebound.conf` | — |
| U5-MT-012 | 量測窗口與背景噪音申報 | `mt.noise.bg` | — |
| U5-MT-013 | Golden 樣本建立與更新流程（流程鍵，不是一次性行為） | `mt.golden.proc` | — |
| U5-MT-014 | 六態分布本身的量測（各域 Present 率趨勢） | `mt.avail.trend` | — |
| U5-MT-015 | 本工具自身 CPU/記憶體佔用申報（儀器效應） | `mt.self.footprint` | — |
| U5-MT-016 | 量測中斷續跑契約（續跑不產生半套值） | `mt.resume.contract` | — |

### U5-PF ・ 平台化／報告／介面・ 15 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-PF-001 | 報告樹狀索引與可折疊導航（離線自足不變） | `rep.tree.index` | — |
| U5-PF-002 | 匯出 JSON schema 版本化與相容宣告 | `exp.json.schema` | — |
| U5-PF-003 | Headless 退出碼契約（分域失敗的結構化退出） | `cli.exit.contract` | — |
| U5-PF-004 | Watch 模式（指定鍵持續監看＋突變警示，僅 console） | `cli.watch.keys` | — |
| U5-PF-005 | 兩份報告 diff 模式（同機跨時/跨機，跨機「不適用」規則沿用） | `rep.diff.mode` | — |
| U5-PF-006 | 事實鍵讀取 SDK（無 UI 函式庫；先規劃文件級） | `sdk.reader` | 規劃級 |
| U5-PF-007 | 鍵中文名/英文名/鍵名三重同步守門 | `i18n.key.sync` | 守門 |
| U5-PF-008 | 鍵盤全導航覆蓋（無滑鼠可完成全部頁面） | `a11y.kbd.full` | — |
| U5-PF-009 | 高對比主題（對比度以量測值成文） | `a11y.contrast.theme` | — |
| U5-PF-010 | 報告無障礙（表格標題/landmark） | `a11y.rep.tags` | — |
| U5-PF-011 | 每域列印友善輸出 | `rep.print.{domain}` | — |
| U5-PF-012 | 匯出遮蔽預覽（L2 解除前先看會出什麼） | `exp.mask.preview` | — |
| U5-PF-013 | 命令列進度與取消契約 | `cli.progress.cancel` | — |
| U5-PF-014 | 報告內嵌「重跑指令」區塊（可重現性） | `rep.rerun.cmd` | — |
| U5-PF-015 | 報告尾附規格出處索引（spec.ref 全列） | `rep.specref.index` | 出處鏈 |

### U5-EN ・ 企業／機隊／知識包・ 10 項

| ID | 項目 | 候選鍵 | 出處／前提 |
|---|---|---|---|
| U5-EN-001 | 機隊聚合：同型號族群基線 | `fleet.model.baseline` | — |
| U5-EN-002 | 採購驗收表產生器（逐規格項對應鍵；缺鍵明說） | `fleet.acceptance.sheet` | — |
| U5-EN-003 | 稽核匯出對照映射（框架欄位↔事實鍵，附缺口服務） | `fleet.audit.map` | — |
| U5-EN-004 | 知識包版本演進與撤回記錄（查重：key.evolution 已有，此為收口） | `kp.evolution.log` | 收口 |
| U5-EN-005 | 跨機差異報告（只比同鍵；跨機一律不適用者分列） | `fleet.diff.report` | — |
| U5-EN-006 | 長期資料保留 schema 遷移宣告 | `fleet.schema.migrate` | — |
| U5-EN-007 | 批次收集的速率與時間預算（每機掃描時長申報） | `fleet.batch.budget` | — |
| U5-EN-008 | 離線知識包分發與校驗（雜湊＋來源） | `kp.dist.verify` | — |
| U5-EN-009 | 機隊覆蓋率儀表（逐鍵 Present 率 across fleet） | `fleet.coverage` | — |
| U5-EN-010 | 敏感度機隊政策（L2 白名單繼承規則） | `fleet.l2.policy` | — |

### U5-NX ・ 明文不做（附解除條件）・ 6 項

| ID | 項目 | 解除條件 |
|---|---|---|
| U5-NX-001 | 不做記憶體/快取錯誤注入（寫入破壞測試） | 使用者明示且寫入閘門全程記帳 |
| U5-NX-002 | 不做 EC/韌體改寫與 capsule 觸發 | 同上 |
| U5-NX-003 | 不猜無出處的換算係數（RAPL 功率、電池損耗模型） | 係數取得可引用的規格/廠商文件 |
| U5-NX-004 | 不做 3D 拓撲視覺化 | 沿 2.84 明文不變 |
| U5-NX-005 | 不列憑證主體名稱 | 沿既有 L3 規則不變 |
| U5-NX-006 | 不對未公開界面（AMD SMU、廠商 EC map）做逆向猜測讀值 | 廠商公開文件或使用者明示承擔 |

**第一波合計：25＋18＋15＋18＋22＋16＋16＋15＋11＋16＋15＋10＋6 ＝ 203 項。**

---

## 四、剩餘約 4,800 列的展開方法與誠實界線

距 5,000 尚差約 4,800 列。**這些列不能一次生成**，理由就是專案自己的兩條規則：

1. **「說了必有出處」**——每一列都要補規格章節出處與 API 級接縫；補不到的只能標檢索級/推測級。
   從記憶裡批量生成的規格數字（位移、頁號、位元位置）正是「未對準出處就不出值」要擋的東西，
   而這個專案的稽核文化（30-agent 稽核、突變測試、正對照）會正確地把它們全部打回來。
2. **「純聲明不計」**——一列沒有出處與接縫，在本計畫的計數法裡根本不是一列。

展開的建議步驟（沿用階段二已驗證的流程）：

1. 先寫查重腳本：新列 → FactKeyCatalog → Services/ → 既有三份目錄，三方對帳。
2. 按儲集區逐區展開，每區 100–200 列為一輪，每輪補齊出處與接縫後才計數。
3. 每輪結束跑目錄稽核（沿用第 264 輪已自動化的機器檢查）。
4. ring0 與規格包依賴項如實標註，不因為「能列」就開工。

## 五、與 ULTIMATE-1000 的銜接

- ID 空間：`U5-{域碼}-{編號}`，與既有 P1–P10 不衝突；域碼可於納入時對映到十階段。
- Gate：沿用十條判準不另立。
- 關係：本清單是 ULTIMATE-1000 之後的**第四組**（VOL1、VOL2、ULTIMATE-1000 之後），
  規模目標 5,000，計數紀律不變。納入倉庫與否由使用者決定。
