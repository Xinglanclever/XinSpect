# XinSpect 企業巨集規格・可取得性稽核與落地紀錄

- 日期：2026-11-03
- 對象：`xinspect_enterprise_grand_specification.md`（八大模組、宣稱 2,000+ 事實鍵）
- 基線：XinSpect 3.13（`v3.13`，commit `e43f118`），事實鍵目錄 1,174 把、動態家族 68 個、測試基線 7,865
- 方法：逐條比對現有 `Services/` 生產者與 `FactKeyCatalog`；再用本機 Gemini 3.8 Flash（桌面版）對八條關鍵技術宣稱做對抗性判讀；每一條判讀都回頭對程式碼／規格驗證，不把模型輸出當結論

---

## 0. 一句話結論

規格裡真正能在「本機、唯讀、不新增驅動」的範圍內拿到的，大約是 **三分之一**；再花工程可以拿到另外 **三分之一**（需要自寫核心驅動、NVMe pass-through 或廠商介面）；剩下 **三分之一** 是 BMC／光模組／封裝內類比遙測與熔絲，**任何作業系統都拿不到**，不應該做成事實鍵。

這一輪已把「拿得到的那部分」做成三個可獨立驗證的出口：

- `--compliance`：把既有事實對到 NIST SP 800-147／155／193 與 MITRE ATT&CK。
- `--merkle`：把穩定事實做成 SHA-256 Merkle 指紋（白名單＋排除敏感／非 Present／易變鍵）。
- `--speculation`：七項 `infer.*` 推測，全部帶鍵名前綴、值前綴與 `Speculative` 信任等級三重標註；不合規、不進 Merkle。

三者都**沒有新增任何硬體讀取**，因此不會為了「功能看起來很滿」而製造假事實。

---

## 1. 先修正規格裡的三個前提

### 1.1 「2,000+ 編譯期事實鍵」不是事實

- 目前目錄是 **1,174 把靜態鍵**＋**68 個動態家族**。
- 動態家族的成員由機器決定，不可能「編譯期」列完：`tpm.pcr.*`（PCR bank 與索引）、`acpi.table.*`（韌體提供幾張表）、`pci.dev.*`（插了哪些裝置）、`nvme.*`（有幾顆碟）、`usb.*`（有幾顆控制器）。
- 把「動態」寫成「編譯期」會導致兩個錯誤期待：以為可以靜態枚舉、以為數量固定。實際上目錄的守門測試就是在擋這件事（`FactKeyCatalogTests`）。
- 本機實測一輪收集產生 **2,019 筆事實**（目錄 1,174 把靜態鍵＋動態家族展開），其中符合穩定白名單的只有 **65 筆**。這也解釋了規格「2,000+」的來源：那是**執行期事實數**，不是編譯期鍵數；兩者不該混用。

### 1.2 「全域 2,000+ 事實平行算 Merkle」不能當穩定指紋

溫度、風扇、負載、可用記憶體、延遲、佇列長度、IOPS 每次收集都變。對全部事實建樹，同一台機器每次跑都得到不同 root——那只能叫「這一次開機的雜湊」，不能叫硬體指紋。

本輪的做法是**白名單**：只收韌體／平台組態／ACPI 拓撲／CPU 步進／TPM 版本這類穩定鍵；`Sensitive`、非 `Present`、鍵名含易變語彙的一律排除，且排除原因分類計數。輸出只含鍵與葉雜湊，**不含值**。

### 1.3 「八大模組平行採集，MaxDegreeOfParallelism = CPU 數」是危險的

MSR、PCI ECAM、SMBus、SPI 不是可以任意平行的一般記憶體：

- SMBus 是共享實體匯流排，多個 master 同時發起會碰撞、仲裁失敗，嚴重時匯流排鎖死。
- SPI 快閃一次只能有一個 flash cycle；並行讀取會讓 HSFSTS 的 FDONE／FCERR 語意混亂。
- EC／ME 的郵箱有單一擁有者與時序要求，並行會互踢。
- 對同一顆 PCI 裝置的配置空間做讀-改-寫，並行會撕裂。

現有 `EvidenceCollection` 是**串行**組裝的，這是正確的。要縮短時間，應該做「分階段、每階段內安全平行」（例如不同 PCIe root port 的 AER 唯讀），而不是把全部 collector 丟進 `Parallel.ForEachAsync`。

---

## 2. 八條關鍵宣稱的對抗性判讀（本機 Gemini 3.8 Flash）

問句：逐條判斷「正確／錯誤／不確定」，錯的給正確寫法。結果 **8 條全部判為錯誤**；以下是判讀與程式碼／規格的交叉驗證。

| # | 宣稱 | 判讀 | 正確寫法（已對過程式碼） |
|---|------|------|--------------------------|
| 1 | TPM PCR 18 是 OS kernel loader 的度量 | 錯誤 | PCR 18 屬 DRTM 的 MLE 度量；Windows 開機載入器走 PCR 4（本專案 `MeasuredBootFactsService` 的註解與讀值就是 PCR 0／2／4／7）。 |
| 2 | 读 UEFI Secure Boot 變數即可判斷 ML-DSA／PQC 是否已載入 | 錯誤 | `db`／`dbx` 只存簽章清單；要逐條解析 `EFI_SIGNATURE_LIST` 的 `SignatureType` GUID 與雜湊演算法，才能說「清單裡有沒有某一類簽章」。變數本身沒有「PQC 已載入」這個欄位。 |
| 3 | Intel Boot Guard 的 FPF 熔絲狀態可由 Windows usermode 直讀 | 錯誤 | FPF 由 ME／硬體保護，OS 沒有通用讀法。本專案 `BootGuardFactsService` 只從 FIT 目錄看 IBB／ACM 條目，且明文說「目錄有條目」不等於「熔絲已燒、Boot Guard 已強制」。 |
| 4 | UCIe 的 raw BER、bump droop、interposer 溫度可由 OS 取得 | 錯誤 | 這些是封裝內 PHY／類比遥測，沒有 OS 可見的標準介面。UCIe DVSEC 最多給 capability、link width／speed、部分錯誤狀態，不給 raw BER 或電壓 droop。 |
| 5 | PCIe LMR 是逐插槽（per-slot）的量測 | 錯誤 | LMR 是 receiver lane margining，結果**逐 lane**、且是訓練／量測當下的暫態；不是每插槽一個數字。Windows 也沒有通用 API。 |
| 6 | 對所有事實鍵（含即時溫度）算 Merkle root 可作穩定硬體指紋 | 錯誤 | 含易變事實的 root 不穩定。只能對白名單的穩定事實建樹；root 也不等於簽章，要另外有 TPM Quote／TSA 才有遠端可驗性。 |
| 7 | 平行採集 MSR／ECAM／SMBus／SPI 可縮短時間且安全 | 錯誤 | 共享匯流排與單一 flash cycle 會碰撞、鎖死、撕裂。必須串行或分階段安全平行。 |
| 8 | SMI 最大延遲與 DRAM TRR 有效性可由 Windows usermode 量測 | 錯誤 | SMI 會搶走 CPU，OS 處於盲區，量不到單次最大延遲；TRR 的 MAC／有效性也沒有 OS 標準介面。 |

> 判讀只當線索：上表每一條都已回到本專案程式碼或公開規格確認，沒有直接把模型輸出入帳。

---

## 3. 逐模組可取得性地圖

分級定義（比規格書的 A/B/C/D 更貼近本專案）：

- **A｜現有路徑已可得**：XinSpect 目前就能生產（含已簽章驅動的 PCI／MSR／MMIO／SMBus 唯讀路徑）。
- **B｜工程可得**：需要新增核心驅動 IOCTL、NVMe pass-through、或把既有驅動擴充；不改硬體、不新增帶外設備。
- **C｜帶外可得**：需要 BMC（Redfish／IPMI）、光模組 I2C、或廠商管理 API；本機 OS 單獨做不到。
- **D｜不可得**：封裝內類比遥測、熔絲、PUF、UDS、SMM 最大延遲、TRR 有效性等；**不應宣告可量測**。

### 模組 I：矽晶信任根、PQC、DRTM

| 項目 | 級別 | 現況與說明 |
|------|------|------------|
| TPM PCR banks／PCR 值／TPM 版本／owned | A | 已有 `tpm.pcr.*`、`tpm.pcr.banks`、`tpm.version`、`tpm.owned`。 |
| TCG log 與 PCR 重放 | A | 已有 `tpm.replay.summary`／`tpm.replay.pcr.*`；不一致是事實，不是入侵判決。 |
| Secure Boot db／dbx／KEK／PK 條數與清單 | A | 已有 `uefi.db*`、`uefi.dbx*`、`uefi.kek*`、`uefi.pk`；清單逐條最多列 5 條。 |
| 韌體量測（PCR 0／2／4／7） | A | 已有 `bt.measured_boot`／`bt.measured_boot.evidence`。 |
| TXT／SKINIT 是否支援 | B | 需要讀 MSR／TXT 公開 registers 或 ACPI；部分可做，但「支援」不等於「已執行」。`SENTER_Done` 這類一次性狀態不能靠事後輪詢假裝。 |
| DRTM 的 PCR 17／18 內容 | A（若 TPM 可讀） | PCR 可讀，但**語意要寫對**：17／18 是 DRTM 範圍，不是「Windows kernel loader」。 |
| PQC（ML-DSA／LMS）部署狀態 | B | 只能從 `db`／`dbx` 的 `EFI_SIGNATURE_LIST` 逐條解析簽章型別與演算法；沒有單一「PQC 已載入」開關。LMS 剩餘次數**不可得**（D）。 |
| Boot Guard FPF／AMD PSB Vendor ID／JTAG 熔絲 | D | 由 ME／PSP／硬體保護；OS 沒有通用讀法。FIT 目錄只能證明「有 IBB 條目」，不能證明熔絲已燒。 |
| SEV-SNP RMP base／TDX key count | D | 硬體內部；OS 可見的只有「是否啟用」與部分 attestation report，不是位址或金鑰池。 |
| PUF 熵品質 | D | 封裝內類比／啟動期一次性資料，OS 不可見。 |
| DICE CDI／UDS | C／D | UDS 永不出晶片（D）；CDI 可能經 SPDM 取得（C），但需要元件的 SPDM stack，不是本機 ACPI／WMI。 |

### 模組 II：UCIe／CXL／Chiplet

| 項目 | 級別 | 現況與說明 |
|------|------|------------|
| CXL 裝置與 CEDT 拓撲 | A／B | `ac.cedt.*` 與 `cxl.*` 已有一部分；CXL 記憶體裝置的 media error 需要額外 IOCTL／driver。 |
| CXL protocol io／cache／mem enabled | B | 可從 PCIe DVSEC／CXL capability 讀，需把現有 PCI 設定空間路徑擴充到 DVSEC。 |
| CXL 池化容量、switch hops、QoS | C | 需要 CXL switch／fabric manager 或 BMC；本機 OS 只看到被分配到的片段。 |
| UCIe link width／speed／protocol stack | B | 可從 UCIe DVSEC 讀（若平台暴露）；不是所有平台都有。 |
| UCIe raw BER／bump droop／interposer 溫度 | D | 封裝內 PHY／類比遥測，OS 無介面。 |
| UPI link count／speed | B | 部分可從 MSR／PCI 設定空間；CRC 錯誤計數多數平台不暴露（D）。 |
| Infinity Fabric FCLK／UCLK 分頻 | B | 可從 SMN／MSR 讀，需 AMD 專用路徑；不是通用 ACPI。 |
| 封裝技術（CoWoS／EMIB／Foveros） | D | 沒有 OS 可見欄位；只能靠型號外部查表，不能自稱量測。 |

### 模組 III：PCIe 電氣層／AER／VRM

| 項目 | 級別 | 現況與說明 |
|------|------|------------|
| AER correctable／uncorrectable 狀態 | A／B | 現有 `pcieaer.*` 已能讀部分；**AER status 是 RW1CS 旗標，不是累計計數器**。讀取不應清除；若清除，必須在輸出明說「本次已清」。 |
| AER 根端口第一錯誤指標 | **第一來源已完成** | `pcieaer.aer.*` 逐裝置讀 Error Source Identification（Root Port 專用）；多 segment 仍未掃。0 不代表沒有錯誤。 |
| PCIe LMR timing／voltage margin | B／C | 需要 vendor driver 或 BMC；Windows 無通用 API。結果是**逐 lane**、量測當下有效，不能存成永久「插槽裕量」。 |
| TxPreset／RxPresetHint／EQ phase | B | 可從 Link Control／Lane Equalization 狀態讀，需小心不要觸發重新訓練。 |
| WHEA 錯誤記錄 | A | 已有 WHEA 相關事實；但 WHEA record 與 AER bit 不是一對一，不能互相代言。 |
| VRM 相數／切換頻率／MOSFET 溫度 | C／D | 需 BMC／PMBus／VRM 控制器；一般桌面 OS 沒有。 |
| PL1／PL2／Tau（MSR 0x610） | B | 既有的 MSR 路徑可讀；但 MSR 0x610 的欄位與單位要按平台世代驗證，不能跨世代硬套。 |
| SVID alert | D | 處理器與 VRM 之間的私有匯流排，OS 不可見。 |

### 模組 IV：微架構／MCA／SMM

| 項目 | 級別 | 現況與說明 |
|------|------|------------|
| 微碼版本（MSR 0x8B） | A | 已有 `mc.version*`；Windows 熱修補與 BIOS 初始載入要分開陳述。 |
| MCA bank count／status／address／misc | B | 可讀 MSR，但**讀 MCA status 可能影響錯誤記錄**；要明示唯讀且不清除。Bank 數量由 CPUID 決定。 |
| 投機執行緩解（IBRS／STIBP／SSBD／BHI／GDS／Inception） | A／B | 部分可由 CPUID／MSR／登錄檔得知；「已啟用」與「硬體支援」必须分開。 |
| SMI 次數 | B | 部分 Intel 平台有 SMI count MSR；不是通用。 |
| SMI 最大延遲 | D | OS 在 SMI 期間被搶走 CPU，量不到；宣稱可量測就是造假。 |
| Thread Director 啟用／class count | B | 可從 CPUID／MSR 讀部分；錯配率需要排程器內部資料（D）。 |
| DRAM TRR mode／MAC limit／Rowhammer 有效性 | D | 記憶體控制器私有；OS 沒有標準介面。可讀 SPD 時序，但不能推論 TRR 有效。 |

### 模組 V：企業儲存／NVMe-MI／ZNS／FDP

| 項目 | 級別 | 現況與說明 |
|------|------|------------|
| NVMe Identify／SMART／溫度／磨損 | A | 已有 NVMe 事實家族；Windows 可透過 IOCTL 取得。 |
| ZNS 支援／zone size／max active zones | B | 可用 Identify Namespace（CNS 0x05／0x1A 等）與 IOCTL pass-through；Windows 檔案系統支援有限，但「硬體支援」可讀。 |
| FDP 支援／reclaim group count | B | 同上，Identify／Feature；不是所有驅動都放行。 |
| NVMe-MI protocol version／SMBus 通道 | C | 通常走 SMBus／PCIe VDM，需 BMC 或 vendor 工具；Windows inbox 驅動不暴露 MI。 |
| NAND raw BER／program fail／erase fail | C／D | 部分在 vendor log／SMART 廠商特定欄位；raw BER 多數不暴露。 |
| PLP 電容健康／放電自檢 | C | 需 vendor log 或 BMC；不是標準 SMART。 |
| WAF／E2E CRC | B／C | 部分在 controller log；Windows 只給標準 SMART／log page。 |

### 模組 VI：RDMA／PTP／SyncE／光模組

| 項目 | 級別 | 現況與說明 |
|------|------|------------|
| RDMA 協定／PFC／ECN／retransmit | B／C | NDIS／per-NIC 計數器可讀一部分；PFC 死鎖、硬體重傳多數要 vendor counter。 |
| PTP 硬體時間戳支援 | B | 可查 NIC 能力；offset／path delay 需要實際 PTP stack 與 master，Windows 沒有通用 `dev/ptp`。 |
| SyncE 時鐘 extraction／ESMC | C | 電信級 NIC／BMC 功能；一般 Windows 不暴露。 |
| 光模組 part number／Tx／Rx power／bias | C | SFF-8472／SFF-8636 走 I2C，通常由 BMC 或 switch 讀；本機 NIC 驅動不給。 |
| LOS 警報 | C | 同上。 |

### 模組 VII：資料中心／BMC／OOB

| 項目 | 級別 | 現況與說明 |
|------|------|------------|
| BMC 韌體版本／KCS 狀態／Redfish schema | C | 需要 BMC 網路或 KCS；SMBIOS Type 38 只描述 IPMI 介面，不給 BMC 韌體版本。 |
| 機箱入侵／背板溫度／PSU 效率／輸入輸出瓦特 | C | BMC／PMBus；本機 OS 一般沒有。 |
| 浸沒式液冷介電液／含水量／槽壓／泵氣蝕 | C／D | 需液冷控制器／BMC；沒有標準 OS 介面。 |
| 出風口溫度 | C | 需機箱感測器／BMC。 |

### 模組 VIII：Merkle／合規／SIEM

| 項目 | 級別 | 現況與說明 |
|------|------|------------|
| 穩定事實 Merkle root | **已完成（本輪）** | `--merkle`；白名單、排除敏感／非 Present／易變鍵；輸出只含鍵與雜湊。 |
| NIST 800-147／155／193 對照 | **已完成（本輪）** | `--compliance`；11 條控制項，缺資料一律 Unverifiable。 |
| MITRE ATT&CK 曝險 | **已完成（本輪）** | 5 條技術：T1542.001、T1495、T1014、T1562.001、T1553.006。 |
| TPM Quote 印證／RFC 3161 時間戳 | C／B | Quote 需真的向 TPM 發命令並驗簽；TSA 需網路。本輪刻意不做，控制項維持 Unverifiable。 |
| SIEM CEF／OTel 輸出 | **已完成（本輪）** | `--compliance --format cef` 或 `--format otel`；純格式化、只寫 stdout／檔案，不主動連 SIEM。OCSF 未做。 |
| CIS／DISA STIG 條目 | B | 需要維護對照表與版本；不是硬體讀取問題。 |
| 與 baseline 的差異／竄改位元遮罩 | **差異已完成** | `--compliance-diff` 比較兩份合規 JSON 的判定；只讀檔、不重新收集。竄改位元遮罩未做。 |

---

## 4. 這一輪實際加了什麼

### 4.1 `Services/ComplianceMappingService.cs`

- 純函式：輸入 `IReadOnlyList<HardwareFact>`，輸出 `ComplianceReport`。
- 11 條控制項：
  - NIST 800-147：BIOS 寫入保護、SMM 隔離、SPI 受保護範圍、更新真實性（Unverifiable）。
  - NIST 800-155：韌體量測、TCG log 重放一致、Secure Boot、遠端 Quote（Unverifiable）。
  - NIST 800-193：保護、偵測、復原（Unverifiable）。
- 5 條 ATT&CK：T1542.001、T1495、T1014、T1562.001、T1553.006。
- 判定規則只使用既有事實的**已知語意**（例如 `chipset.bios_cntl` 的「最強保護／有鎖保護／未保護」、`spi.write_surface` 的「暴露面」、`tpm.replay.summary` 的一致／不一致計數）。
- **缺鍵、非 Present、文字形狀不認得**：一律 Unverifiable，不猜。

### 4.2 `Services/FactMerkleService.cs`

- canonical payload：`key ＋ 0x1F ＋ value ＋ 0x1F ＋ trust ＋ 0x1F ＋ source`。
- 不含 `MeasuredAtUtc`；同機同組態跨次收集 root 相同。
- SHA-256 葉節點，逐鍵排序，兩兩合併；落單的最後一片原樣提升。
- 排除分類：`Sensitive`、非 `Present`、易變鍵、非白名單。
- 輸出只含 `key` 與 `leafSha256`，不含值。

### 4.3 CLI

```text
XinSpect --compliance [--format json|sarif|junit|cef|otel] [--out <檔案>]
XinSpect --compliance-diff <舊 JSON> <新 JSON> [--out <檔案>]
XinSpect --fleet [--snapshots <目錄>] [--out <檔案>]
XinSpect --merkle [--out <檔案>]
XinSpect --speculation [--out <檔案>]
```

- `--compliance` 退出碼：0＝全部 Satisfied／NotApplicable；2＝有任何 NotSatisfied／Unverifiable 或 Exposed／Unknown；1＝致命。
- `--compliance-diff` 退出碼：0＝兩份判定全同；2＝有任何新增／消失／改善／退步／變動；1＝讀檔或格式錯誤。
  Satisfied／Mitigated→其他（含 Unverifiable）＝regressed；其他→Satisfied／Mitigated＝improved；其餘判定變動＝changed。
  這只是判定差異，不是風險評分；0 不代表機器安全。
- `--format`：`json`（預設）、`sarif`（2.1.0）、`junit`（XML）、`cef`（逐行 Common Event Format）、`otel`（OTLP/JSON logs）；不認得的值或缺少值回 1，不會默默退回 JSON。
  SARIF 只把非通過項變成 result（NotSatisfied／Exposed＝error，Unverifiable／Unknown＝warning）；
  JUnit 把 Unverifiable／Unknown 記成 failure 而不是 skipped，避免 CI 顯示綠色；
  CEF 每項一行（severity：NotSatisfied／Exposed＝8、Unverifiable／Unknown＝4），標頭與延伸值都轉義；
  OTel 每項一筆 log record（severity：ERROR／WARN／INFO），resource 帶 service.name／version。
  四者都只轉譯既有判定，不重新量測、不連網。
- `--merkle` 退出碼：0＝至少一片葉子；2＝沒有穩定事實；1＝致命。
- `--speculation` 退出碼：0＝七項都產生；2＝推測項數不是七（規則集合漂移）；1＝致命。
- `--fleet` 退出碼：0＝有可比的同型號族群；2＝沒有快照／沒有型號／沒有第二台；1＝致命。只讀本機快照，不上傳。
  **0 不代表機器安全或合規**——推測不是量測。
- headless 模式目錄由 10 增為 15（compliance、compliance-diff、fleet、merkle、speculation）；`Tests/AutomationAndGuidanceFactsTests.cs` 已同步。

### 4.4 推測層（`--speculation`）

有些問題的答案不是「有／沒有」，而是「以現有證據只能猜到這裡」。把這些猜測塞進
`bt.*`／`tpm.*` 會讓它們看起來像量測，所以另立 `infer.*` 層，並用三重標註把界線寫死在資料裡：

| 標記 | 位置 | 作用 |
|---|---|---|
| `infer.` | 鍵名前綴 | 只讀鍵的消費者也知道這不是一般事實 |
| `【推測】` | 值的第一個字元 | 只讀值的消費者也知道 |
| `Speculative` | `HardwareFact.Trust` | 只讀信任等級的消費者也知道 |

七項規則與它們**刻意保留的不確定**：

| 鍵 | 依據 | 推測結論 | 信心 | 主要限制 |
|---|---|---|---|---|
| `infer.bt.boot_guard` | `bt.ibb`＋`bt.manifests`；`fw.fit.summary` 的「沒有 FIT 簽名」 | FIT 目錄同時有啟動模組與 ACM → 映像含 Boot Guard 元件（**不代表熔絲已燒**）；確定沒有 FIT → 可能沒有 | 低 | 靜態映像不是硬體狀態：profile 未讀、ACM 未驗簽、開機策略未重放，也未讀 MSR／ME 狀態 |
| `infer.cxl.role` | `ac.cedt`＋`cxl.cfmws.count` | 有 CFMWS → 韌體已保留 CXL 記憶體窗口（**不代表端點在線**）；窗口 0 → 可能僅 Type 1／2 | 低 | CFMWS 只描述主機端窗口；未掃 PCIe DVSEC／連結訓練狀態，不能說已接 Type 3 |
| `infer.fw.recovery` | `bt.recovery`＋膠囊事實 | `OsIndicationsSupported` 非 0 → 可能有 OS 觸發更新／復原；0 → 沒有標準膠囊路徑 | 中／低 | 位元語意未逐位命名、復原區未辨識、OEM 私有路徑仍可能存在 |
| `infer.fw.update_auth` | `uefi.secure_boot`＋`platform.secure_boot`＋膠囊事實 | Secure Boot 開 → 可能走 UEFI／OEM 驗簽；關 → 標準驗證可能未啟用 | 低／無法評估 | 沒有驗任何膠囊簽章、沒有讀 ESRT／更新策略；兩個來源衝突時不挑一個相信 |
| `infer.tpm.quote` | `tpm.present`／`tpm.version`／`tpm.attest.capable` | 背書階層處於啟用狀態（**不代表 EK／AK 已就緒**），實際 Quote 仍可能失敗 | 低 | 沒送 challenge、沒建立或確認 EK／AK、沒驗 EK 憑證鏈、沒驗 PCR 簽章 |
| `infer.uefi.pqc` | `fw.date` 年份 | 2025 後 → 可能開始包含 PQC 元件；2023–2024 → 過渡期；更舊 → 不太可能 | 極低 | 年份不是能力證明；沒有讀 PQC OID／憑證 |
| `infer.disclaimer` | 無（範圍聲明） | 本層不是量測、不是簽章驗證、不是合規證據 | 不適用 | 無 |

**這一層不參與合規判定，也不進 Merkle 指紋。** `ComplianceMappingService` 只讀它點名的事實鍵；
`FactMerkleService` 用白名單前綴，`infer.` 不在名單內。兩者都有測試釘住，避免哪一天有人
「順手」把推測當證據（`Tests/SpeculationFactsTests.cs`）。

**對抗性覆核（本機 Gemini 4 Argon，2026-11-03）。** 初版把三個「靜態宣告」寫成了過強的結論，
經 Argon 指出後已收斂，並補上對應測試：

1. FIT 目錄有 IBB＋ACM 只是韌體映像含元件，不是硬體熔絲已燒、驗證已生效。
   已改為「映像包含 Boot Guard 元件；不代表硬體熔絲（FPF）已燒錄」，仍列 MSR／ME 狀態未讀。
2. CEDT 的 CFMWS 只代表韌體保留主機端位址窗口，不代表插槽上有已訓練成功的 CXL 端點。
   已改為「已配置 CXL 記憶體窗口」，並註明需掃 PCIe DVSEC 才能確認實體裝置。
3. TPM 背書階層可用只代表未被鎖定，EK／AK 控制代碼未必存在，直接 Quote 仍可能失敗。
   已改為「背書階層處於啟用狀態」，並把「需確認或建立 EK／AK」寫進限制。

### 4.5 沒有做的事（刻意的）

- 沒有新增硬體讀取、P/Invoke、驅動或網路；七把 `infer.*` 全部由既有事實推導。
- 沒有把推測混進 `--json evidence` 的一般事實流：它們只在 `--speculation` 輸出，
  要合併使用的人必須自己跨兩份輸出，這個摩擦是刻意的。
- 沒有把「工具沒驗證」寫成「機器通過」：更新簽章、TPM Quote、韌體復原三條永遠 Unverifiable。

---

## 5. 建議路線圖

### 第一階段（已完成）

- 既有事實 → 標準語言（NIST／ATT&CK）。
- 穩定事實 Merkle 指紋。
- CLI＋測試＋headless 目錄。
- CI 匯出：`--compliance --format sarif|junit`（只轉譯判定，Unverifiable 不顯示為綠）。

### 第二階段：in-band、低成本、可誠實交付

1. **ESRT（EFI System Resource Table）**：讀 `fw.esrt.*`，補韌體元件版本與 capsule 對帳。
2. **SPD 寫保護狀態**：從 SMBus SPD 頁的 SWP／PSWP 位元，回答「記憶體 SPD 是否可被寫」。
3. **HWP／EPP／CPPC2**：MSR 0x770／0x774 與 ACPI CPPC，唯讀，補電源管理面。
4. **SMBus 全匯流排掃描**：既有 SMBus 路徑擴充，需處理時序與白名單，避免碰到未知裝置。
5. **PCIe AER 唯讀盤點強化**：明確標示 RW1CS、不清除；把「讀過」與「清除過」分開。

### 第三階段：需要驅動或 pass-through

1. **NVMe ZNS／FDP identify**：IOCTL_STORAGE_PROTOCOL_COMMAND；只讀 Identify，不送會改狀態的 command。
2. **PCIe LMR／EQ 狀態**：需 vendor driver 或特定 root port 介面；結果逐 lane、當下有效，不能存成永久插槽值。
3. **CXL DVSEC／media error**：擴充 PCI 設定空間路徑到 DVSEC；media error 需 CXL driver。
4. **PL1／PL2／Tau**：MSR 0x610，按世代驗證欄位；只讀。
5. **MCA bank 唯讀快照**：讀 status／address／misc，明示可能影響錯誤記錄，預設不清除。

### 第四階段：帶外（BMC／Redfish／SPDM）

- BMC 韌體版本、機箱入侵、PSU／風扇／進風溫度、光模組 DDMI。
- SPDM／DICE 遠端證明、TPM Quote 的遠端挑戰。
- 這些需要網路與憑證，與「零網路、零常駐」的預設衝突，應做成明示的獨立模式，預設關閉。

### 不建議做（D 級）

- FPF／PSB Vendor ID／JTAG 熔絲、PUF 熵、UDS、SEV-SNP RMP base、TDX key count。
- UCIe raw BER／bump droop／interposer 溫度、UPI CRC 計數、VRM 相數／MOSFET 溫度。
- SMI 最大延遲、TRR 有效性、NAND raw BER、SyncE ESMC、光模組 Tx／Rx power（本機）。
- 理由：不是「還沒做」，是**沒有可信來源**。做成事實鍵只會讓使用者以為量到了。

---

## 6. 15 把「企業看得懂、且本機真的拿得到」的起手鍵

| # | 事實鍵 | 來源 | 對應 |
|---|--------|------|------|
| 1 | `chipset.bios_cntl` | PCI 0:1F.0+0xDC | NIST 800-147 寫入保護；ATT&CK T1542.001 |
| 2 | `chipset.smramc` | PCI 0:0.0+0x88 | NIST 800-147 SMM 隔離 |
| 3 | `spi.write_surface` | BIOS_CNTL＋SPIBAR HSFSTS／FRAP／PR | NIST 800-147／193 保護 |
| 4 | `spi.prr` | SPIBAR+0x74 | NIST 800-147 受保護範圍 |
| 5 | `bt.measured_boot` | PCR 0／2／4／7 | NIST 800-155 量測 |
| 6 | `tpm.replay.summary` | TCG log 重放 × PCR 實讀 | NIST 800-155／193 偵測 |
| 7 | `uefi.secure_boot` | UEFI 變數 | NIST 800-155 Secure Boot；T1553.006 |
| 8 | `uefi.dbx.count` | UEFI dbx | 撤銷面；T1553.006 |
| 9 | `tpm.present` | TBS／WMI | 量測與證明的先決條件 |
| 10 | `tpm.pcr.banks` | TPM GetCapability | 量測鏈可讀性 |
| 11 | `mc.version` | MSR 0x8B | 微碼身分；CVE 對帳 |
| 12 | `ac.dmar.summary` | ACPI DMAR | IOMMU／DMA 保護 |
| 13 | `sec.vbs.state` | Win32_DeviceGuard | ATT&CK T1562.001 |
| 14 | `byovd.hits` | 載入模組 × 微軟封鎖清單 | ATT&CK T1014 |
| 15 | `cxl.cfmws.count`（或 `ac.cedt.*`） | CXL DVSEC／ACPI CEDT | CXL 拓撲起點 |

> 這 15 把全部已有生產者；沒有為了湊數新增鍵。

---

## 7. 驗證紀錄

- 新增測試：`Tests/ComplianceMappingTests.cs`、`Tests/FactMerkleTests.cs`、`Tests/CliComplianceTests.cs`、`Tests/ComplianceExportTests.cs`、`Tests/ComplianceExportSpecTests.cs`、`Tests/ComplianceDiffTests.cs`、`Tests/FleetCliTests.cs`、`Tests/SpeculationFactsTests.cs`。
- 新增規格：`docs/spec/compliance-export.v1.md`（五種格式的欄位、跳脫與 severity 對照）。
- 更新：`Tests/AutomationAndGuidanceFactsTests.cs`（headless 10 → 15）。
- 守門子集：`--filter "Speculation|FactKeyCatalog|AutomationAndGuidance"` → **47 passed, 0 failed**。
- 完整套件：基線 **7,865**；本機因既有 NVMe 核心不可中斷等待，實跑排除 1 條硬體限時測試
  （`NvmeOverlappedIoTests.真的卡住的碟也要在外層預算內交還控制權`），其餘 **7,864 passed, 0 failed**。
  該測試未被跳過、未被改寫，健康機器上照常執行；排除原因是本機磁碟探針會卡住行程，不是本輪程式碼。
- 目錄：1,174 把靜態鍵（+7 把 `infer.*`）；動態家族 68 個不變。
- 真機 e2e：
  - `--compliance` 產出 11 條控制項、5 條 ATT&CK；本機結果是 1 Satisfied／2 NotSatisfied／8 Unverifiable，符合「Legacy 開機、SPI 讀不到、VBS 未啟用」的實況。
  - `--merkle` 連續兩次得到**同一個 root**（65 片葉子）；輸出不含任何事實值（已用 `最強保護`／`未鎖`／`77 個` 等字串檢查）。
  - `--speculation` 的七項規則以注入事實測試覆蓋（三重標註、信心、依據鍵、限制、合規／Merkle 排除）；真機輸出受同一個 NVMe 卡住問題限制，未列入本輪 e2e 結論。
  - 已知環境問題：本機的 NVMe 探針會讓 CLI 在寫完輸出後仍留下一個卡在核心等待的行程（README 第 10 部已記錄同型現象）。輸出與退出碼已產生，但自動化管線若用「等行程結束」判斷完成，會誤以為它還在跑；這是既有的收集層缺陷，不是本輪新增的三個模式造成的。

---

## 8. 附帶發現（與本規格無關，但應修）

1. `README.md` 仍寫「這是 3.11 測試版……正式版請下載 3.10」，實際版本是 3.13（且 `v3.13` 已發佈）。
2. `README.md` 第 57 行寫「64 頁」，同文件規模表與 `PageRegistry` 都是 65 頁（39 個主頁面＋26 個實用工具）。
3. `.agents/skills/xinspect-release/SKILL.md` 的版號、測試數、腳本路徑全部過期（寫 1.6.1／1.7.5、1,134 條、`.Codex` 路徑），實際是 `Tools/release.ps1`＋`Tools/publish_release.py`＋`Tools/verify-release.ps1`。

這三項不影響本輪功能，但會讓後續的人照著錯的文件做事，建議下一輪一併修正。