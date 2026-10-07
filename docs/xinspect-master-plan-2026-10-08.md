# 曦覽 XinSpect 擴展總藍圖（2026-10-08）

> 這份把 2026-10-08 幾輪討論的計劃**全部收斂到一份**：藍圖落差、地基強化、現有功能加／修／加倍、
> 分眾（Server／WS／DT）、工業（OT）、企業級橫向、深水區研究、分期路線圖、以及明文不做的清單。
>
> 相關文件：`docs/gap-blueprint-2026-10-08.md`（逐項落差分析，含落地檔案證據）
> 基準：`main` @ 2adc165，csproj 版號 2.25；已發佈 Release：v2.25 Olympus

## 狀態圖例

| 標記 | 意義 |
|---|---|
| ✅ | 已實作且有證據 |
| 🟡 | 有基礎，缺深度或子項 |
| ⬜ | 未實作 |
| ⛔ | 明文裁決不做（與五原則衝突或風險） |
| 🧪 | 需實機才可誠實驗證（不可先寫死值） |

---

## 一、地基：三個「超級加倍」（最高優先）

這三件不是新功能，而是**讓之後每加一個功能都自動變強**的結構性投資。

### 1.1 六態守恆 lattice（型別化三態）
- 現況：三態散在各服務各自實作（`Present/NotSupported/InsufficientPrivilege/ReadError/NotApplicable`）。
- 目標：定義 `{Present, NotSupported, NoPermission, ReadError, NotApplicable, Unknown}` 的**偏序格**，
  任何衍生事實都必須走這個格傳播，**永不靜默塌成一個值**。
- 效益：對帳不會把「讀不到」當值；突變測試可鎖；報告不會誤導；新服務自動繼承語意。
- 驗收：新增事實型別時，編譯期即要求處理全部狀態。

### 1.2 事實 → 規則 → 判決 → 行動 統一管線
- 現況：事實層、對帳（`FactRelationEngine`）、政策、機隊是四套。
- 目標：任何服務吐事實即**自動**參與交叉對帳 + 策略合規 + 機隊差異。
- 效益：後續每個新功能（工業抖動、BMC、PTP）自動獲得四倍價值。

### 1.3 規則覆蓋測試
- 現況：26 條對帳規則為舊版所寫；後續新增 AMD／DDR5／工業等大量事實，**沒有機制確保被對帳**。
- 目標：每個 fact key 必須被至少一條規則考慮，或被明確豁免（清單可稽核）。
- 效益：新事實不會靜默漏對帳。

### 1.4 其他必要修正
| # | 項目 | 說明 |
|---|---|---|
| 1 | 版本號一致性 | 受守門測試保護的四處（csproj／README 徽章／AboutView／ChangelogCatalog）已一致（2.25）；但**不受保護的** README 下載連結與版本沿革曾落後（本次已修）→ 應擴大守門範圍 |
| 2 | 文件對帳範圍 | 「README 版號一致」的測試只釘徽章，管不到下載連結與版本沿革 → 新增檢查 |
| 3 | 驗證狀態旗標 | AMD SMU／PSP／DDR5 等「未在本機驗證」必須在事實層帶旗標（已驗證／合成金標／未驗證），UI 與報告都要顯示 |
| 4 | 事件記錄完整化 | 現有 System 摘要＋Security 1102＋Application（`ReliabilityHistoryService`）；**Setup log 仍缺** |
| 5 | 報告輸出 | PDF 靠瀏覽器列印、XML 只進不出 → 企業場景需原生 PDF 與可簽章輸出 |
| 6 | PMU S5 對照 | `docs/PMU-SANDBOX-PLAN.md` 承諾與 wpr 對照未執行 → 要做或降級措辭 |
| 7 | 知識表收錄率 | `PciKnowledge`/`SuperIoKnowledge`/`OuiKnowledge` 為手工子集 → 標示收錄率 |

---

## 二、現有功能：加什麼 × 怎麼加倍

| 功能域 | 加（Add） | 加倍（Supercharge） |
|---|---|---|
| CPU（`CpuMsrFacts`／`CeilingService`／`PmuCapability`） | 逐核 EPP、TSC 跨核同步、指令延遲微基準、AMD 逐核 P-state | 效能天花板 → **跨機基準**（同型號比對「你這顆弱幾 %」） |
| 記憶體（`SpdReader`／`MemoryService`／`NumaTopology`） | XMP/EXPO 生效判定、DDR5 on-DIMM 溫度、sub-channel、PMIC | SPD → **記憶體護照**（型號+序號+時序+一致性，抓假模組批次） |
| 主機板／韌體（`SpiFlash*`／`UefiSignature`／`Cmos`） | SMBIOS Type 8/9、BootNext、TPM NV、Boot Guard/PSB | SPI 稽核 → **韌體鑑識**（熵圖、NVRAM 定位、EFI 模組辨識、離線辨真偽） |
| 儲存（`NvmeHealth`／`DiskSurfaceScan`／`FakeCapacity`） | TRIM 實際下發驗證、寫入放大／耐久、NVMe self-test log 0x06 | SMART/HPA/假容量/NVMe 趨勢 → **硬碟判決卡**（還能撐多久、是否翻新） |
| 顯示卡（`NvmlInterop`／`GpuCodec`／`HdrCapability`） | 多 GPU/NVLink、ISV 驅動分支（Studio vs GRD）、AV1、光追/DLSS 探測 | GPU 深測 → **工作流就緒報告**（這台能不能扛這個 render/AI 工作流） |
| 網路（`NicHealth`／`WifiSignal`／`NetOffload`） | EEE／Flow Control／鏈路協商、Wi-Fi 世代/MLO/320MHz、藍牙版本/編解碼 | 網路事實 → **即時性評估**（抖動長尾、PTP 偏差、是否適合運動控制） |
| 安全／對帳（`FactRelationEngine`／`Rules`／`BlueSquadron`） | Boot Guard/PSB、WDAC/AppLocker、驅動 Verifier/WHQL、TPM NV | **對帳引擎形式化**（證明規則一致、抓死規則、自動推導新規則） |
| 系統軟體層（`ServiceInventory`／`Startup`／`OptionalFeature`） | 服務依存圖、Setup log、WDAC 列舉 | 持久化面**差分監控**（新持久化點＝惡意指紋） |
| 深測中心（`DeepBench` 38 項） | 壓縮／轉碼／光追／Tensor Core／TRIM | 每項加**可信度引擎＋自動回歸**（歷史基準、跨機比較、flaky 重跑） |
| 監控量測（`HistoryStore`／`DpcLatency`／`ThreadMigration`） | 趨勢／異常偵測／預測、電池放電曲線、聲學振動 | 歷史回放 → **事實時序 DB**（查詢語言＋統計異常＋預測） |
| 證據實驗室（`EvidenceLab`／`RawRegister`／`HtmlReport`） | SHA-3／BLAKE3、簽章報告、原生 PDF | 時間膠囊 → **機隊＋Merkle 差異見證＋硬體護照** |
| CLI／本機 API（`CliService`／`LocalApiHandler`） | 策略引擎指令、機隊輸出、退出碼語意 | CLI → **可排程的策略執行器**（企業／OT 無人值守） |
| 品質保證（突變 82%／SpecRef／FsCheck） | 主程式注入式突變、規則覆蓋測試 | 突變 → **行為等價測試**擴到對帳與策略層 |

---

## 三、藍圖之外的加值（高槓桿六條）

全都複用既有基礎（事實模型／對帳／雜湊鏈／時間膠囊），不需新硬體。

| # | 功能 | 機制 | 前置 |
|---|---|---|---|
| 1 | **開機鏈離線存證** | SPI 雜湊＋TPM PCR＋UEFI db/dbx/KEK/PK 綁進同一份可驗章存證 → 「開機鏈自上次快照未變」 | 已有三項服務，僅需整合層 |
| 2 | **反事實／因果引擎** | 症狀輸入 → 反向求解規則鏈，每步附原始數字 | 26 條規則已外部化 |
| 3 | **持久化面差分監控** | 啟動項／服務／排程／驅動／BITS／WMI 訂閱／Shell 擴充納入快照並與基線差分 | 需新增 BITS/WMI/Shell 蒐集器 |
| 4 | **功率守恆交叉對帳** | PSU PMBus 市電 vs RAPL vs NVML → 消失的瓦數＝轉換損失 → PSU/VRM 健康 | 三個服務都已存在 |
| 5 | **靜默損毀哨兵** | 週期性雜湊韌體／登錄檔／關鍵檔，寫入既有雜湊鏈審計日誌 | 已有 `AuditLogService` |
| 6 | **跨機隊離線統計** | 多機時間膠囊 → 每 fact 正常分布 → 離群機／刷板卡批次／假模組 | 需機隊層 |

### 其他加值（選配）
- **硬體護照**：可攜、離線可驗章的機器身分（保固／二手／失竊／盤點）。
- **修復收據鏈**：維修前後各出一份署名存證（接既有自簽 CA）。
- **影子庫存**：從 setupapi.dev.log 與登錄檔反推歷史組態（例：以前 32GB、現在 16GB）。
- **MCA → DIMM 地理定位**：WHEA/MCA 對回物理位址與 DRAM 映射 → 指出哪條 DIMM/rank 壞。
- **本機推理代理（選配、預設關）**：事實模型包成本機 Ollama 可呼叫工具（尊重藍圖移除 AI 的決定）。

---

## 四、深水區（研究級，越深越依賴硬體與正確性論證）

### 4.1 推理層
| # | 功能 | 說明 |
|---|---|---|
| 1 | 不確定性代數 | 每個事實帶 (值, 來源鏈, 可信度, 時戳, 量測法)；衍生事實可信度依定義傳播；衝突時算候選值後驗分布 |
| 2 | 反事實引擎 | 「關掉 Turbo／拔一條 DIMM 會怎樣」離線模擬 |
| 3 | 故障樹自動合成 | 從事實＋規則生成故障樹，算最小割集排序 |
| 4 | 時序因果（離線） | 從感測史推斷誰領先誰（溫度領先節流…），建有向圖 |
| 5 | 對帳規則形式化證明 | 證明規則集一致、抓死規則、自動推導新規則 |

### 4.2 量測／反解層 🧪
| # | 功能 | 說明 |
|---|---|---|
| 6 | DRAM 位址映射反解 | 列衝突延遲側信道（DRAMA 式）推出 bit→bank/channel/rank；MCA 定位到 DIMM/rank |
| 7 | 韌體 blob 離線鑑識 | SPI dump 的熵圖、NVRAM 定位、EFI 模組 GUID、對照廠商映像 → 「這顆 BIOS 是原廠的嗎」 |
| 8 | 端到端損毀定位 | 沿 CPU→DRAM→PCIe→控制器→NAND 受控校驗和，定位位元翻轉出在哪一段 |
| 9 | 電源側信道指紋 | 高頻取樣 RAPL/PMBus 反推活動單元；偵測 hypervisor 騙 MSR |
| 10 | 時鐘側信道偵測虛擬化 | TSC 跨核去同步、HPET 漂移、PM timer 異常 |

### 4.3 安全／信任層 🧪
| # | 功能 | 說明 |
|---|---|---|
| 11 | 硬體信任鏈遞移證明 | 微碼→韌體→bootloader→Secure Boot→hypervisor→驅動，逐環附證據，斷在哪指到哪 |
| 12 | DMA 攻擊面盤點 | TB/USB4 PCIe 隧道、IOMMU/VT-d、核心 DMA 保護、逐裝置重映射 |
| 13 | 關鍵暫存器寫入絆線 | 攔截對關鍵 MSR／PCI 設定空間的寫入 → 活的完整性監控（需同意閘門） |
| 14 | 緩解有效性實測 | 快取時序驗證常數時間、TRR/pTRR/ECC 是否真的擋得住 Rowhammer |

### 4.4 跨機／供應鏈／形式化
| # | 功能 | 說明 |
|---|---|---|
| 15 | 硬體基因組 | 依元件指紋分群 → 翻新碟、刷板卡、重標晶片 |
| 16 | 離線聯邦比對 | 跨機隊匿名比較，聚合值做差分隱私 |
| 17 | 可重現性證明 | 每個量測帶 (方法, 環境, 種子, 原始樣本)，第三方可重跑 |
| 18 | Merkle 化事實樹證差 | 只證「只有 X 變了」不洩漏其餘事實 |
| 19 | 形式化規格知識庫 | JEDEC/PCIe/USB 規格編成機器可讀約束，解碼自動引用條文（SpecRef 升級） |

### 4.5 大膽
| # | 功能 | 說明 |
|---|---|---|
| 20 | 自動生成並驗證的修復計畫 | 從約束模型輸出逐步計畫＋預期 before/after 事實 |
| 21 | UEFI Shell 版本 | 開機前事實收集、與 Windows 版對照、救援模式（大工程） |
| 22 | 事實時序資料庫 | 查詢語言＋趨勢＋異常偵測＋預測＋回放 |
| 23 | 知識圖譜 UI | 關聯圖、因果鏈、矛盾地圖、來源樹、影響分析 |

---

## 五、分眾設計：Server／Workstation／Desktop

架構原則：**不做四套程式**，而是 `核心（已存在）→ Profile（分眾頁面/檢查集）→ Policy（宣告式預期狀態）→ Fleet（離線機隊）`。

### 5.1 Server（伺服器）—— 核心是 RAS
| 能力 | 現況 | 待補 |
|---|---|---|
| 帶外管理 BMC/IPMI/Redfish（SEL/FRU/SDR/firmware inventory） | ⛔ LIMITATIONS #3（只做訊息解碼） | 🧪 需真機；Redfish 走 **loopback 127.0.0.1**，不違反零網路 API |
| ECC 逐 DIMM（SECDED/SDDC）、錯誤率趨勢 | 🟡 有 ECC 現況 | 逐 DIMM 計數與趨勢 |
| MCA → 實體 DIMM/rank | 🟡 `McaService` | 需 §4.2-6 位址反解 |
| PCIe AER 三類趨勢 | 🟡 `EcamaAerService` | 逐裝置趨勢 |
| 記憶體 RAS 啟用狀態 | ⬜ | mirroring／sparing／patrol scrub |
| 冗餘：雙電源、風扇 N+1、RAID 重建 | 🟡 單顆 PMBus、風扇有 | 逐顆 PSU、冗餘判定、RAID BBU/VD/PD 🧪 |
| 韌體清冊（BIOS/BMC/CPLD/NIC/RAID 最低版本） | ⬜ | Redfish 最自然 |
| 機櫃定位（locate LED、asset tag、U 數） | ⬜ | Redfish |
| 多路 CPU／NUMA 交錯／NVDIMM／CXL | 🟡 NUMA、CXL CEDT 有 | 多 socket 深度 |

### 5.2 Workstation（工作站）—— 核心是「工作流能不能跑完」
- 多 GPU／NVLink 拓撲（現 NVIDIA 單卡為主）。
- ISV 驅動分支驗證（NVIDIA Studio vs GRD、AMD Pro vs Adrenalin）。
- GPU ECC 狀態與工作負載建議。
- 大頁／NUMA／記憶體頻寬評估報告（`LargePageService`／`CoreLatencyService` 已有）。
- 專業音訊延遲（ASIO）、色彩準確度（EDID/色域已有）。
- 長時渲染的持續效能與熱節流（跑兩小時會不會降頻）。
- 組合既有服務成一份 **「這台能不能扛這個工作流」報告**。

### 5.3 Desktop（DT／消費級）
- **XMP/EXPO 是否真的生效**（買 6000 實際跑 4800 的明確判定）—— 最有感的一條。
- 遊戲延遲、刷新率、DPC（已有）。
- 二手驗機／防偽（已有 `FakeCapacity`／`ChassisHpa`／`MachineAge`）。
- 超頻穩定性（已有 `Overclock`／`SuperPi`／`StressBridge`）。

---

## 六、工業（OT／邊緣）設計

曦覽的**氣隙、唯讀、可稽核**三大特性正好對上 OT —— OT 網路本來就隔離，最排斥連雲工具。
工業核心不是「快」，是**確定性**與**不停機**。

### 6.1 即時性與確定性（工業的心臟）
| 能力 | 現況 | 待補 |
|---|---|---|
| DPC／中斷延遲 | ✅ `DpcLatencyService` | **最大抖動、長尾分位、直方圖** |
| 中斷親和性／CPU 隔離 | ✅ `InterruptAffinity` | 隔離核心驗證、ISR 分佈 |
| 計時器抖動 | ✅ `TimerFoundationService`／`TimeSyncFactsService` | 循環測試（cyclictest 式）最大/最小/分位 |
| 隱形停頓（SMI） | ✅ `InvisibleStallService` | 工業視角：把 SMI 當**週期抖動源** |

> 產線掉包常不是硬體壞，而是某個驅動的 ISR **抖了一下**。曦覽已有半個引擎，補「抖動長尾」即完整。

### 6.2 現場總線（唯讀）
- Modbus TCP/RTU 唯讀寄存器掃描、設備列舉。
- OPC UA 連**本機**伺服器：節點樹、時戳、品質碼。
- PROFINET／EtherCAT／CAN：需對應網卡/驅動，能做才做，**讀不到一律三態**。
- 工控 I/O 卡（PCIe 數位/類比、隔離卡）盤點。

### 6.3 PTP／TSN（運動控制的前提）
- IEEE 1588／802.1AS gPTP：網卡**硬體時間戳**能力、同步偏移、主從狀態。
- TSN 能力（時間感知整形、搶佔）。
- 多軸同步的精度就是 PTP 精度 —— 工業極重要。

### 6.4 預知保養（殺手級）
把事實時序 + 異常偵測用在旋轉機械/機台：溫度、功耗、風扇/軸承轉速、振動（經 Modbus/OPC 取）
的**趨勢 + 離群 + 漂移**；與開機時數、環境溫度關聯，建立「這台的正常模型」——**統計，不是猜**。

### 6.5 OT 安全（IEC 62443）
- Purdue 分層定位（這台在 Level 0–5 哪一層、有無不該有的對外連通）。
- 單向閘道／資料二極體跡象、air-gap 驗證。
- 藍色中隊六防線工業化：聚焦「這台邊緣機是否成為跳板」。

### 6.6 工業環境與壽命
- 寬溫／無風扇邊緣機：溫度牆、散熱裕度、長期熱節流。
- 24/7 連續運轉：SSD 寫入放大／耐久、SMART 磨損、電源品質。
- **靜默資料損毀**（IEC 61508 最在意）：ECC + 記憶體測試 + 端到端校驗。
- 看門狗狀態、開機時數、MTBF 推估。

---

## 七、企業級橫向（真正的「企業級」本體）

| # | 能力 | 內容 | 前置 |
|---|---|---|---|
| 1 | **策略引擎 Policy** | 宣告式預期狀態（沿用 `Rules` 模式）：Secure Boot、VT-d、TPM 已擁有、BIOS 寫保護、無未加密系統碟、韌體≥最低版、無 BYOVD；工業另加 DPC 抖動 < X µs、PTP 偏差 < Y ns、看門狗啟用、無對外連通。逐機輸出**通過/失敗＋原始證據** | 無（純軟體） |
| 2 | **合規對應** | 事實→控制項對應（**不是掃描**）：企業走等保 2.0／ISO 27001／CIS／NIST 800-53；工業走 IEC 62443／IEC 61508。產出可簽章、可存證報告 | 無（純軟體） |
| 3 | **離線機隊層** | CLI/排程蒐集→檔案共享或 USB（零網路 API 照樣成立）→基線差異、跨機聚合、異常機、**Merkle 差異見證** | 需 §三-6 |
| 4 | 部署與營運 | MSI/MSIX 打包、GPO/SCCM/Intune/DSC 派送、靜默 CLI＋退出碼、唯讀 loopback API（已有 `LocalApiHandler`） | 無 |
| 5 | 資產生命週期 | 保固/EOL、韌體到期、授權、**硬體護照** | 需 §三 護照 |
| 6 | 多操作者／本地 RBAC | 離線可稽核的權限與操作紀錄（沿用既有雜湊鏈審計日誌） | 無 |

---

## 八、分期路線圖

| 階段 | 內容 | 需要硬體？ |
|---|---|---|
| **P0 地基** | 1.1 六態守恆、1.3 規則覆蓋測試、1.2 統一管線、1.4 七項修正 | ❌ 純軟體 |
| **P1 企業軟體層** | 策略引擎、合規對應、分眾 Profile、離線機隊骨架 | ❌ 純軟體 |
| **P2 高槓桿六條** | 開機鏈存證、反事實引擎、持久化差分、功率守恆、靜默損毀哨兵、跨機隊統計 | 部分需多機 |
| **P3 分眾深化** | Server RAS 深化、DT XMP/EXPO、WS 工作流報告、工業抖動長尾 | 部分 🧪 |
| **P4 需真機** | BMC/IPMI/Redfish、RAID BBU/VD/PD、現場總線、PTP/TSN、多路 CPU MSR、DDR5 實機 | 🧪 |
| **P5 研究級** | DRAM 位址反解、韌體鑑識、信任鏈、形式化規格知識庫、UEFI Shell | 🧪 |

---

## 九、明文不做（與五原則衝突或已裁決）

| 項目 | 理由 |
|---|---|
| 模擬器 M9／M10、規則市集、二手平台 API、SDK、Linux | `docs/spec/LIMITATIONS.md` 明文不做 |
| 線上機隊遙測、雲端事實庫、雲端 LLM agent | 打破「零網路 API」——那是最貴的資產 |
| RFC 3161 線上時間戳、線上遠端證明 | 需網路；除非走使用者主動觸發白名單 |
| IPMI KCS/SSIF、MegaRAID BBU/VD/PD、PCH 世代名稱、Registry.pol 解析、EC 埠、MCHBAR 時序 | 無硬體可驗或文獻分歧（LIMITATIONS #3–#7） |

---

## 十、風險與誠實界線

1. **越深越依賴硬體**：§四的深水區與 §5/6 的硬體項，沒有真機就只能停在設計，**不可先寫死值**。
2. **同意閘門**：寫入類（暫存器絆線、ECC 注入、Rowhammer 施測、TRIM 下發）一律同意閘門＋風險標註。
3. **正確性論證成本**：形式化（規則定理、規格知識庫）是數人年級的工程，不是程式碼量問題。
4. **不要篡改已發佈版本**：重指標籤會讓已下載者對不上；優先「發新版本」。
5. **三態守恆是主軸資產**：任何新功能若繞過它，就等於回頭製造技術債。
