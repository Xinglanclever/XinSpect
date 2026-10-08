# 還能加什麼——校準版目錄的增量判定（2026-10-09）

> 對象：`docs/TOOL-CATALOG-2026-10-09.md`（你這輪的重寫去重校準版全文，已照原樣收錄進 repo）。
> 基準：`main` @ `f5e6d32`（v2.37）＋工作樹未提交改動；專案測試基線 3548。
> 原則照你這輪的定調：**重寫整合為最高優先級；調度／排序只作最後最低優先級（實在不可行時的最小選型路徑）**。
> 前置判定：`docs/CAPABILITY-DOMAINS-2026-10-09.md`（12 域逐域、四缺口）。本文只寫**校準造成的差異**與**新增判定**，不重複逐域全文。

---

## 一、五條校準原則對既有判定逐條過

1. **問題域重組** → 判定框架本來就按域走，不改變任何結論。
2. **同一工具單一主條目** → 不可整合清單去重後更乾淨（Surveyor／YDArk／NtWarden 等合併），**沒有一條判定翻轉**。
3. **本體 vs 生態**（smartctl∈smartmontools、UEFIExtract∈UEFITool、WPR/WPA/xperf∈WPT）→ 修正昨天文件的一處口徑：域5 寫「smartctl／GSmartControl 已有」，正確說法是**本專案整合的是 SMART 讀取能力，不是 smartmontools 生態的任何組件**——生態合併後這句不會再被誤讀成「搬 smartctl 進來」。判定不變。
4. **原生內建類的實質影響**：powercfg `/energy`、`/batteryreport`、`/systempowerreport` 合併為同一內建工具的參數——**印證昨天域9 的修正**（`/energy` 沒整合、`/batteryreport` 已整合；且這三個都是**寫出報告檔**的子命令，與 `INativeToolSource` 那五條唯讀查詢**不同類**，不能直接進唯讀區段）。
5. **調度降為最後最低** → 第四節照此重排（你目錄末的十條選型路徑逐條校準）。

---

## 二、校準版新出現、昨天沒判到的條目（證據一律是本專案程式碼）

| 條目（校準版位置） | 判定 | 證據／理由 |
|---|---|---|
| **USB4／Thunderbolt 鏈路排查**（域8） | **已整合** | `Services/TbUsb4Service.cs`——WMI 列舉 `Win32_PnPEntity` 中含 Thunderbolt／USB4 的控制器（零特權、埠級速率如實標需提權）；`SecurityScoreEngine`／`HardeningAdvisor` 引用、`Tests/WifiTpmTbHdrTests.cs` 釘住 |
| **Kellect**（域2，ETW 內核事件收集） | **已整合（能力層）** | 能力＝ETW 事件收集：`BlueSquadron/EtwWatcher.cs`（即時訂閱）＋`EtwTraceService`（標準 `.etl` 產出）。第三方收集器本身不需要——要的是能力 |
| **DTrace on Windows**（域2） | **不可整合；調度最低** | 需要 Windows Server 2025＋內建 dtrace 驅動＋提權；動態追蹤在本專案的對應物是 ETW（已在）。真要用＝調用外部 CLI → 按你的定調放最低優先級選型路徑 |
| **whesvc**（域2/9） | **不可整合** | windiag.dll 內嵌 Lua 的描述無公開規格可驗；要的是它的事件輸出——若真有 ETW provider，會併入域6 的 ETW 能力（缺口 #4 的讀回），不整合服務本身 |
| **Velociraptor UEFI Artifacts**（域3/10） | **可整合未做——新增真缺口 #5** | 能力＝**ESP（EFI 系統分割區）檔案層掃描**。實測 `grep -rln "SystemPartition\|PARTITION_"`（Services／Decoders）**全 0 命中**——本專案讀韌體變數與 dbx（`UefiSignatureFactsService`）都是**韌體層**，ESP **檔案層**完全沒做。唯讀、離線、只需提權讀檔，且能與既有 dbx 封鎖清單交叉引用 |
| **DiskSpd**（域5） | **不可整合** | 負載產生器＝寫入壓力；本專案壓測已有 `StressTestService`／`DeepBench`＋同意閘門 |
| **PC-AI**（域11，本地 LLM 診斷） | **待決（乙-2）** | 能力是 LLM 編排不是診斷事實；對應 NEXT-STEPS 乙-2「本機推理代理（Ollama）」——等你決定 |
| **etw-mcp／wpa-mcp**（域6） | **併入既有缺口 #4** | 能力＝.etl 離線分析＝「寫得出、認得出、讀不回來」那條，不需要 MCP 層 |

---

## 三、還能加什麼（最終缺口清單；整合為最高優先級）

**五個真缺口——全部是純解碼器或唯讀讀取，不需要硬體、不需要新權限、不需要第三方引擎：**

| # | 缺口 | 來源 | 為什麼契合 | 驗收標準 |
|---|---|---|---|---|
| 1 | **UEFI 映像 FV／FFS／區段樹分解** | 域3（UEFITool 生態） | 規格公開（UEFI PI Spec）→ 純解碼器；突變測試抓過真缺陷的那條路 | 可導覽結構（GUID／型別／位移／大小）；解不開的位元組如實標示；`SpecRef` 覆蓋通過；**真實傾印**當 fixture |
| 2 | **驅動 IOCTL／符號連結純靜態分析** | 域2（DrvEye／DriverSight） | 不載入驅動、不呼叫 IOCTL → 與白名單最小化相容；與既有 BYOVD 清單交叉引用 | 對一顆 `.sys` 列出 IOCTL 分派與裝置名；標 `METHOD_*`／`FILE_*` 旗標；PE 解析失敗如實回報 |
| 3 | **`setupapi.dev.log` 外設連接時間線** | 域1（peripheral-forensic） | 純文字日誌、離線、唯讀；本專案已在用 SetupAPI（4 檔：`BiosMeService`／`DeviceDiagnosticService`／`PciResourceAuditService`／`UsbLinkService`） | 逐筆事件（裝置實例、首次／末次時間、驅動）；**不解析在地化欄位**，只取結構化段落 |
| 4 | **`.etl` 內容解析（讀回）** | 域6（etw-mcp／wpa-mcp／GPUView） | 寫檔／檔頭驗證／清單管理都已存在，只差 `ETWTraceEventSource` 讀回；TraceEvent 已在相依裡 | 讀 provider／事件數／CPU 取樣摘要；不可解的事件如實歸類（**不以 0 補**） |
| 5 | **ESP 檔案層掃描**（本輪新增） | 域3/10（Velociraptor UEFI Artifacts） | 列舉 EFI 系統分割區上的 `.efi` 檔，**與既有 dbx 封鎖清單交叉引用**（ESP 上的檔是否在封鎖清單）；唯讀、離線、只需提權讀檔 | 逐檔（名稱／大小／SHA-256／dbx 命中）；ESP 掛載點找不到時如實三態；**不得對檔案內容做 YARA 式判決**——要的是事實，不是引擎 |

建議順序：**1 → 2 → 3 → 4 → 5**，每個小而完整一版（一梯一版節奏）。#1 與 #5 同源（都是 UEFI 規格），可同輪。

---

## 四、最低優先級：你目錄末十條選型路徑的逐條校準

| 你的選型路徑 | 校準判定 |
|---|---|
| 藍屏／WHEA／驅動崩潰 | **大半已被自讀整合覆蓋**（WHEA／MCA／BERT／CPER／bugcheck）；缺離線 `.dmp`＝缺口 #2 的延伸；**唯一真調度＝WinDbg 符號歸因（需網路，與零上傳衝突）** |
| PCIe／高速鏈路／Retimer | **真調度**（需分析儀／retimer 硬體） |
| 存儲／NVMe／硬碟健康 | **已被自讀整合覆蓋**（SMART＋門檻＋儲存可靠性 19 欄——工作樹裡那輪）；DiskSpd 屬壓測不是診斷 |
| 性能卡頓／微卡頓／輸入延遲 | **ETW 是 A 級自讀**——本專案有原生 ETW 消費者先例；缺 `.etl` 讀回＝缺口 #4；**零調度** |
| 電源／睡眠／續航 | 唯讀查詢已接縫化（`INativeToolSource` 第一個實作者）；報告檔類子命令待你決定（已記錄）；**零調度** |
| 固件／UEFI／ACPI 安全 | FV／FFS＋ESP 掃描＝缺口 #1／#5 自讀後即覆蓋；ACPICA 反編譯刻意不做（反編譯結果是原始碼不是事實，價值與風險不成比例） |
| 安全取證／應急響應 | **真調度**（另一類產品：需安裝服務／傾印／落檔證據鏈） |
| GPU／圖形／計算 | 已整合（MSR 直讀比 GPU-Z 的顯示值更底層）；逐幀捕獲需注入目標進程＝**真調度** |
| 網絡／丟包／協議復現 | Pktmon 堆疊層丟包歸因＝**待你裁決**（資料源是 ETW → A 級自讀可做，不是「調度 pktmon」）；其餘介面層已整合 |
| 驅動開發／驗證 | **不在範疇**（開發者工具鏈；Driver Verifier 開啟驗證即寫入核心設定，與唯讀衝突） |

**校準後的總結**：十條選型路徑裡，**6 條被自讀整合覆蓋、或即將被缺口清單覆蓋**；真調度剩 **4 條**，全部是「前提不存在」——儀器硬體、核心捕獲驅動、記憶體傾印、進程注入＋符號網路——不是「我們不整合，是整合的前提不在這台機器上」。

---

## 五、界線

- **沒有逐一查證**清單中任何工具的存在、功能或授權；「已整合」全部以本專案程式碼為證，「可整合未做」以規格公開、唯讀可行為判準，「不可整合」的理由限四類：硬體／授權／與已裁決原則衝突／屬另一類產品。
- 新增缺口 #5（ESP 掃描）**還沒實作**，只是候選；工作樹裡上一輪的未提交改動（`INativeToolSource`＋`StorageReliabilityFactsService`）不受影響、未 commit。
- 對照是**能力層**的判斷，不是對任何品牌的評價。
