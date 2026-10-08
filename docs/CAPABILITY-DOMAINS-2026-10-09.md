# 12 能力域整合判定（2026-10-09）

> 對象：使用者校準後的「12 能力域」版清單。
> 基準：`main` @ `f5e6d32`（v2.37）＋本輪工作區改動；專案測試 **3548**、全套 **3549**、0 failed。
> 這一版把前一份 `TOOL-CAPABILITY-GAP-2026-10-09.md` 的 22 列粗分類，換成你重新校準過的 **12 個能力域**逐域判定；**調度路徑放在最後**（第四節）。

---

## 零、怎麼讀這份判定

四種判定，每一條都要有**本專案的程式碼當證據**：

| 判定 | 意思 |
|---|---|
| **已整合** | 這個能力已經在本專案裡，且做的是同一件事 |
| **本次整合** | 這一輪真的動了程式碼（有新增檔案／有測試） |
| **可整合未做** | 規格公開、唯讀、可機器驗收 → 列在第三節當缺口 |
| **不可整合** | 附理由，且理由必須是「硬體／授權／與已裁決原則衝突／屬另一類產品」之一 |

**全文不替任何品牌背書。** 清單裡每一項工具的存在、功能、授權我都沒有逐一查證（唯一的授權查詢沒有可用結果），所以下面只談**能力**。凡宣稱「已有」的，證據一律是本專案自己的檔案。

---

## 一、逐域判定

### 域 1｜底層硬體存取、總線與協議分析

**已整合：** `Services/XsRegProbe*`（白名單唯讀驅動，12 檔引用）＋ WinRing0 反射載入（47 檔引用）；PCI／PCIe 設定空間、SMBIOS、SPD 皆已在讀。

**這一域的關鍵差異（也是本專案跟你清單最大的分歧點）：** RWEverything／HE 那一類是「全權可讀可寫」的硬體視窗；本專案走的是**白名單唯讀**——只開得出明確列舉過的那幾條讀取路徑。所以這一域**不是「沒整合」，是刻意整合成更窄的形狀**。

**不可整合：** LeCroy Summit／LinkExpert、Kandou Besso（需分析儀／retimer 硬體）、Bus Hound、PCAN-Explorer、BUSMASTER（需硬體或廠商驅動）、PawnIO 的各功能模組（第三方核心驅動，與白名單最小化衝突）、pcileech／StrataDMA／Aetheris 的 DMA 記憶體存取（攻擊性記憶體讀寫，與唯讀衝突）。

**可整合未做：** `setupapi.dev.log` 外設連接時間線（peripheral-forensic 那條能力）——**純文字日誌解析、離線、唯讀、不需任何驅動**，而且本專案已經在用 SetupAPI（5 檔）。這是這一域裡唯一值得動的東西。

### 域 2｜內核、驅動、轉儲與實時調試

**已整合：** `WheaErrorService`／`WheaTimelineStore`／`McaService`／`CrashLog`／`BugCheck`（含 ACPI `BERT`／`CPER`）、驅動簽章稽核＋BYOVD 封鎖清單（**BYOVD 這個詞在 `Services/`＋`XinSpect.Decoders/` 的 80 個檔案裡出現**）、`DpcLatencyService` 的 DPC／ISR 延遲歸因。ETW 這一層見域 6。

**可整合未做（兩個，都在第三節排序內）：**
- **離線 `.dmp` 的結構化事實**——只讀標頭／`BugCheckCode`／模組清單／`KdDebuggerDataBlock` 這類**不需要符號**的部分，如實標示讀不到的區塊。
- **驅動 IOCTL／符號連結的純靜態分析**（DrvEye／DriverSight 那條能力）——不載入驅動、不呼叫 IOCTL，只解析 PE 裡的 `IRP_MJ_DEVICE_CONTROL` 分派與 `\\Device\\`／`\\DosDevices\\` 字串，再與既有 BYOVD 清單交叉引用。**不需驅動、不需權限**，完全落在本專案的能力範圍。

**不可整合：** WinDbg `!analyze -v` 的**符號歸因**（需符號伺服器＝需網路，與零上傳衝突；這是「不做那一步」，不是「調度 WinDbg」）、LiveKd／HyperDbg／SoftICE／VEH Debugger（需另起調試器或改目標進程）、ARK 家族（YDArk／NtWarden／KSword／WKE，需自寫核心驅動 → **與白名單最小化原則衝突，這是你的裁決題不是技術題**）、NotMyFault／BurnInTest 的故障注入（與唯讀衝突）、PoolMon（需核心池標籤列舉＝需擴充驅動）。

### 域 3｜固件、UEFI、ACPI、EC 與虛擬化底層

**已整合：** `ChipsetSecurity`、`PlatformSecurity`、`XinSpect.Decoders/SpiFlash.cs`／`SpiFlashMap.cs`、`EntropyMap.cs`、微碼雙來源對帳、EFI 變數四態、EFI 簽章清單（`XinSpect.Decoders/EfiSigList.cs`）、ACPI 表（`AcpiTable`／`AcpiSlit`／`CxlCedt`，含表頭校驗和）、VBS／HVCI（55 檔引用）。

**可整合未做（本清單契合度最高的一項）：** **UEFI 映像 FV／FFS／區段樹分解**——規格公開（UEFI PI Spec），可寫成純解碼器；不需硬體、不需新權限、不需寫入；輸入是使用者給的映像檔或本專案已讀得到的 BIOS 區。

**不可整合：** EC 讀寫（PawnIO LpcACPIEC／EC-Access-Tool）——**本專案已明文裁決不做 EC 埠**；H2ODDT Pro（廠商 BIOS 調試硬體，且它與 Insyde 那條是同一工具）；efiXplorer（IDA 插件）；ACPICA／iasl（ASL 反編譯屬工具鏈，且**反編譯結果是原始碼不是事實**，價值與風險不成比例）；pybinwalk／reap-cli（第三方引擎；要的是「簽章掃描的事實」，不是引擎本身）。

### 域 4｜記憶體診斷與記憶體取證

**已整合：** `RowhammerProbeService`（5 檔）、`PmuProgrammingService`（編程驗證含還原）、記憶體深測、SPD 解碼、記憶體頻寬數學。

**⚠ 一條實測推翻的假設：** 「讀 Windows 內建記憶體診斷的結果通道」這條路**本來就不通**——本機 `wevtutil gl MemoryDiagnostics-Results` **找不到該通道**。也就是說這不是「我們還沒做」，是那條通道不存在。留下這條記錄，免得下一輪又有人去試。

**不可整合：** MemTest86（開機前獨立環境，WPF 進程內做不了）、Volatility 3／LovelyMem／KAPE／DumpIt／WinPmem（另一類產品：需記憶體傾印且多需核心驅動）、Zada-Xor／KVC（需 Ring-0）、BitProbe／tlvb／Elcomsoft（DFIR 編排框架）。

### 域 5｜存儲、NVMe 與文件系統診斷

**已整合：** SMART 屬性＋門檻（failing-now）、NVMe 健康／錯誤紀錄／WCTEMP、HPA、表面掃描、假容量寫入驗證、儲存電源狀態（sleep／exit latency 曲線）。

**本次整合（真的動了程式碼）：** `Services/StorageReliabilityFactsService.cs` —— 對應你清單裡的 `Get-PhysicalDisk` → `Get-StorageReliabilityCounter`，但**不經 PowerShell**，直接走 WMI 關聯取 `MSFT_StorageReliabilityCounter` 的 **19 個欄位**，逐碟逐欄位變成事實：

- `storage.reliability.{id}.<欄位>`：值為 **0 就是 0**（磨損 0%、延遲 0 ms 都是合法值，這條路上最容易踩的坑就是把 0 當成「缺」）。
- `storage.reliability.{id}.coverage`：**數出並具名列出提供者沒給的欄位**，並附「空值不代表 0」——這是三態誠實的具體落實。
- 來源不可用 → 一筆 `ReadError` 並帶原因；來源可用但 0 筆 → `NotApplicable`（**「不是錯誤」**，環境狀態不是缺陷）。

實測（本機真實 WMI，臨時探針跑完即刪）：46 筆事實，`storage.reliability.1.wear=33`、`...0.power_on_hours=1424`、`...0.read_errors_total=1`、`temperature_max` 真的回 0 且被如實呈現。另有一個 WMI 坑已記錄：這類別**直接查會回空，必須靠關聯取**；自組 `ASSOCIATORS OF` WQL 會因為 `ObjectId` 含反斜線引號而失敗（`0x80041031`），要走 `ManagementObject.GetRelated`。

**不可整合：** openSeaChest（第三方 CLI，且 USB 橋接要 SATA passthrough）、Victoria／HD Tune（表面測試含寫入）、廠商工具箱（無公開規格，且固件更新／安全擦除是寫入）、WizTree（MFT 需 raw 檔案系統解析，且屬「磁碟清理定位」不是驗機）、`lsdsk`（其分組能力本專案已用控制器／PCIe 路徑分組涵蓋）。

### 域 6｜性能、ETW、延遲與系統監控

**已整合：** 本專案有**兩處**原生 ETW 消費者——主程式的 `Services/DpcLatencyService.cs`、`Services/FrameTimeService.cs`、`Services/ThreadMigrationService.cs`（即時訂閱，**不是要調度 WPR**），以及獨立守護進程 `BlueSquadron/EtwWatcher.cs`（命名空間 `BlueSquadron`，隨 `BlueSquadronBridge.exe` 出貨，**與 `Services/BlueSquadron/` 那個整合層是不同目錄**）。另有 `Services/EtwTraceService.cs`：用 TraceEvent **檔案模式**工作階段把事件寫成**標準 `.etl`**（平行落地的產物，可用 WPA 事後重播），並負責 `.etl` 清單／刪除／修剪與**檔頭驗證**（`IsValidEtl`，實測本機 200 個系統 `.etl`）。再加上 `CoreLatencyService`、`InterruptAffinity`、`LatencyCurveService`、`DeepBench`，以及系統監控面（程序、句柄、網路連線、磁碟／GPU 活動）。

**這一域的關鍵事實：** WPT／WPR／xperf／PerfView／GPUView 的核心資料源是 **ETW**，而 `.etl` 是**可離線解析的檔案格式** → 依判準屬 **A 級（自己讀）**，不屬 C 級（調度）。所以這一域**沒有一項需要調度第三方**。

**不可整合：** LatencyMon／ALPA／PresentMon／Superluminal（第三方，且其價值在本專案已用更底層的方式在做——本專案直接讀 MSR 與 ETW，不靠它們的 UI）。

**可整合未做（比我原本寫的窄）：** `.etl` 的**內容**解析。本專案**已經產出** `.etl`（`EtwTraceService` 檔案模式）也**已經驗證** `.etl` 檔頭，但全專案**沒有任何一處**用 `ETWTraceEventSource` 讀回內容（實測 `grep -rln ETWTraceEventSource` 為 0 命中）——也就是「**寫得出、認得出、讀不回來**」。缺的是讀回內容那一步，不是整個 ETW 離線能力；這一項可並列第三節第四名。

### 域 7｜GPU、圖形與計算調試

**已整合：** NVML（13 檔）／NVAPI（6 檔）／D3DKMT／EDID、GPU 深測以 readback **逐位元組**比對 CPU 參考值、GPU 編解碼（`GpuCodecService`）、幀時間。

**不可整合：** RenderDoc／Nsight／GPU Inspector／GFXReconstruct／PVRCarbon／GAPID／CodeXL——這一整排的共同點是**要注入或 hook 目標進程的圖形 API**，與本專案「唯讀、不注入別的進程」的立場直接衝突；Brokkr Diagnostics／GPUd 是 NVIDIA 資料中心＋第三方常駐 agent，與「免安裝、零常駐」衝突。

**結論：這一域沒有值得動的缺口。** 你要的「GPU 降頻／功耗牆歸因」本專案已經用 MSR 直讀在做（比讀 GPU-Z 的顯示值更底層）。

### 域 8｜網絡、無線、USB、Thunderbolt、音訊與總線

**已整合：** `MSFT_NetAdapterStatistics`（錯誤與丟棄）、wlanapi（41 檔引用，RSSI／頻道換算）、延遲量測、網速、DNS、USB 鏈路速度與 `Enum\USBSTOR` 痕跡（4 檔）。

**可整合未做：** **Pktmon 的堆疊層丟包點歸因**。注意這裡的判定與你的清單不同：`pktmon` 在本機**內建**（`C:\Windows\System32\PktMon.exe`），但它的**資料源是 ETW**——而本專案已有原生 ETW 消費者先例 → 依判準屬 **A 級（自己讀）**，不是「調度 pktmon 這個工具」。

**不可整合：** USBPcap／Npcap／NetReplay（需安裝核心級捕獲驅動＋Scapy 相依）、PCAN-Explorer／BUSMASTER／Free USB Analyzer（需硬體或廠商驅動）、TI WiLink 套件／Realtek LOG Tool（廠商 RF 測試硬體）、PyAudioFixerWin11（**「修復」即寫入**，與唯讀衝突；診斷那半本專案已有）。

### 域 9｜電源管理與能效診斷

**已整合：** `CeilingService`（溫度／功耗／電流牆，直接讀 **MSR**，比 ThrottleStop 那種「監控＋調整」少了調整那一半——**這是刻意的**）、`SleepDiagnosticsService`（powercfg 唯讀查詢，**本次接縫化**）、電源計劃讀取（`ProfileService`）、`PowerPolicyDecoder`、`powercfg /batteryreport`（`Views/BatteryView.xaml.cs`）。

**⚠ 用你的校準修正我前一份文件：** 前一份把「`powercfg /energy`」寫成已有——**那是錯的**。本專案實際用的是 `/batteryreport`（且是**產生 HTML 報告檔**的子命令，走 `/output`），**`/energy` 並沒有整合**。

**本機實測（順便驗證你的第 7 條校準）：** `powercfg /?` 裡 `/SYSTEMSLEEPDIAGNOSTICS` **仍存在**，但它的說明文字已經寫「**已改為 `powercfg /systempowerreport`**」→**你的判斷正確**。補充一件你清單沒提的事：`/sleepstudy`、`/systempowerreport`、`/batteryreport`、`/energy` **都是會寫出報告檔的子命令**，跟本專案那五條唯讀查詢**不同類**——所以它們不該直接進 `INativeToolSource` 的唯讀區段，要嘛另立「報告產生」的分類，要嘛維持現狀（只留 batteryreport 那條已同意的路徑）。

**不可整合：** ThrottleStop／SMUDebugTool（**要寫入 MSR 與電源表**）、Powir（第三方）。

### 域 10｜安全、Rootkit、取證與應急響應

**已整合：** TPM 2.0（`XinSpect.Decoders/Tpm2.cs`；`Tpm2` 這個詞在 `Services/`＋`XinSpect.Decoders/` 的 74 個檔案裡出現）、EFI 變數四態、`SecurityScoreEngine` 的 `dma.iommu` 檢查、HVCI／VBS、驅動簽章＋BYOVD、ETW 行為面。

**不可整合：** Sysmon（需**安裝服務＋驅動**）、KAPE／Elcomsoft／Volatility／Aetheris／BitProbe／tlvb／windows-ir-toolkit／螢火／Live Forensicator（**另一類產品**：DFIR 採集與編排，且多數要落檔成證據鏈）、ARK 家族（需驅動，見域 2）、YARA（**第三方引擎；要的是「掃描結果的事實」，不是引擎本身**）。

**註：** Glow 那一類硬體安全基線審計（TPM 2.0／Secure Boot／VBS／HVCI／IOMMU）本專案**已經在做**，是這一域裡覆蓋最完整的一塊。

### 域 11｜綜合硬件診斷、系統信息與便攜工具

**已整合：** 這就是本產品本身——單一執行檔、免安裝、離線、零上傳；WMI／SMBIOS 硬體資訊、評分、HTML／JSON 報告匯出。

**判定：這一域幾乎整排都是「同類第三方程式」**（SPECS／NWinfo／PC-Check／PC-AI／laptop-check／OxidPulse／wfdiag），不是要整合進來的對象——它們的價值在本專案裡已經以對應能力存在（`wfdiag` 的 49 項任務與本專案的頁面集合高度重疊）。唯一值得注意的差異化點是 `laptop-check` 的「二手機真實規格驗證」，而本專案已有假容量寫入驗證＋SPD 解碼涵蓋了那個動機。

### 域 12｜驅動開發與驗證

**已整合：** 驅動簽章稽核＋BYOVD 封鎖清單比對（80 檔引用）。

**不可整合：** WDK／Driver Verifier／DevCon／ApiValidator／KASAN／DriverStudio／SoftICE——**開發者工具鏈**，且 Driver Verifier **會改系統狀態**（開啟驗證即寫入核心設定），與唯讀原則衝突。

---

## 二、如果你只要看一行

**12 域裡，2 域（7、11）沒有缺口，5 域（1、2、3、5、8）已經整合大半，3 域（4、10、12）的核心屬於另一類產品或開發者工具鏈，剩下真正值得動的缺口一共 4 個——全部是「純解碼器或唯讀讀取」，都不需要硬體、不需要新權限、不需要第三方引擎。**

---

## 三、四個真缺口（依契合度排序；都不需要硬體、不需要新權限）

| # | 缺口 | 為什麼契合 | 驗收標準 |
|---|---|---|---|
| 1 | **UEFI 映像 FV／FFS／區段樹分解** | 規格公開 → 純解碼器；本專案唯一「用突變測試抓過真缺陷」的那條路 | 輸出可導覽結構（GUID／型別／位移／大小）；解不開的位元組**如實標示**；`SpecRef` 覆蓋通過；以**真實傾印**當 fixture（本專案在 NVMe 位移上吃過合成資料的虧） |
| 2 | **驅動 IOCTL／符號連結純靜態分析** | 不載入驅動、不呼叫 IOCTL → 與白名單最小化**相容**；且能與既有 BYOVD 清單交叉引用 | 對一顆 `.sys` 列出 IOCTL 分派與裝置名，標示 `METHOD_*`／`FILE_*` 旗標；無 PE 解析失敗時如實回報 |
| 3 | **`setupapi.dev.log` 外設連接時間線** | 純文字日誌、離線、唯讀；本專案已在用 SetupAPI | 逐筆事件（裝置實例、首次／末次時間、驅動）；**不解析在地化欄位**，只取結構化段落 |
| 4 | **`.etl` 內容解析（讀回）** | 寫檔／檔頭驗證／清單管理都已存在，只差 `ETWTraceEventSource` 讀回；TraceEvent 已在相依裡 | 讀 `.etl` 的 provider／事件數／CPU 取樣摘要；不可解的事件如實歸類為未解（**不以 0 補**） |

---

## 四、最低優先級：最小調度路徑（**只有整合不可行時才用**）

按第一節的判定，你的 12 域裡**絕大多數不需要調度**。真正剩下的只有下面七條——它們的共同點是**「要嘛需要硬體，要嘛需要注入別人的進程，要嘛需要傾印檔」**，也就是「不是我們不整合，是整合的前提不存在」：

| 場景 | 唯一需要外部工具的地方 | 為什麼整合不了 |
|---|---|---|
| 開機前記憶體測試 | **MemTest86**（PassMark） | 需獨立開機環境；WPF 進程內做不了（本專案的 rowhammer／PMU 探針是「執行期能測的那部分」） |
| PCIe 協議級／眼圖 | Teledyne LeCroy、Kandou Besso | 需分析儀／retimer **硬體** |
| USB 協議級捕獲 | USBPcap ＋ Wireshark | 需安裝**核心級捕獲驅動** |
| CAN／總線 | PCAN-Explorer、BUSMASTER | 需**硬體**與廠商驅動 |
| 記憶體取證 | Volatility 3（＋ DumpIt／WinPmem 取得傾印） | **另一類產品**；需傾印檔 |
| GPU 圖形 API 逐幀 | RenderDoc／Nsight | 需**注入目標進程** |
| 藍屏符號歸因 | WinDbg ＋ 符號伺服器 | 需**網路**（與零上傳衝突）→ 由你在能上網的機器自行跑；本專案負責把**不需符號的那半**做成事實（缺口 #1／#2） |

**調度准入三條件**（任一不成立就不准調度）：
1. 這項能力**沒有**穩定 API／公開規格可自己讀；
2. 需要的**外部元件**（驅動／硬體）已經在機器上，不需使用者額外安裝；
3. 它的輸出是**結構化**的，或至少不需要解析在地化文字。

按這三條檢查上表七項：**七項全部不成立**——它們之所以留在這張表，是因為「前提不存在」，不是因為「判準放行」。**所以本清單的最小調度路徑，誠實的結論是：零個自動調度、七個「請你自行在對的環境跑」。**

---

## 五、本輪工作區真的動了什麼（可對帳）

| 檔案 | 性質 |
|---|---|
| `Services/NativeToolSource.cs` | **新增**：`INativeToolSource`＋`NativeToolSection` 接縫契約（形狀照 `IAcpiTableSource`） |
| `Services/SleepDiagnosticsService.cs` | **改**：成為接縫**第一個實作者**；宣告（`Describe()`）與執行（`Run()`）分離；新增 `Available`／`UnavailableReason`／`PowercfgPath`（查檔案在不在，不猜） |
| `Tests/NativeToolSourceTests.cs` | **新增**：8 條，**不開任何行程**（驗宣告、命令照抄、四欄必填、兩態一致性、假來源可注入）；已實測「改壞會紅」 |
| `Tests/TestSuiteBaseline.cs`、三份 `README` | **改**：專案測試 3534 → **3548**，全套 **3549**，0 failed |
| `Services/StorageReliabilityFactsService.cs`、`Tests/StorageReliabilityFactsTests.cs`、`Services/FactKeyCatalog.cs`、`Services/EvidenceLabService.cs` | **本輪落地**：儲存可靠性 19 欄＋覆蓋申報；事實鍵 134 → **136** |
| `Services/EvidenceCollection.cs`、`App.xaml.cs`、`ViewModels/StartupSequence.cs` | **改（收尾時發現的兩個真缺陷）**：見 §六 |
| `Services/CliService.cs` | **改**：說明文字的範圍與退出碼語意跟實際行為對齊 |
| `Views/FirmwareSecurityView.xaml` | **改**：虛擬化卡片兩個繫結路徑修正（見 §6.4） |
| `Tests/UiSmokeTests.cs` | **新增 1 條**：證據頁**有資料時**的樣板渲染檢查（既有煙霧測試建構的每一頁都是空的） |

---

## 六、收尾時發現的四個真缺陷（都是「測試全綠但東西沒用」）

這一節記的是「做完」時逐項對帳才挖出來的東西。四個都不是新功能寫錯，而是**既有實作在真實環境裡根本沒有作用**：

### 6.1 整合沒接線：事實鍵接進了 `AllFacts`，卻沒有任何地方呼叫載入

`StorageReliabilityFactsService` 有服務、有 7 條單元測試（注入假來源，全綠）、事實鍵也加進了 `FactKeyCatalog`，`AllFacts` 與報告匯出都把 `StorageReliabilityFacts` 串進去——**但全專案沒有任何一處呼叫 `LoadStorageReliability`**。所以在真實的 App 與 CLI 裡它永遠是空清單：畫面上看不到、匯出裡沒有、CLI 也查不到，而測試一路綠燈。

**修法：** 把接線本身做成一個有名字的入口 `EvidenceCollection.LoadUsermodeFacts`（UI 啟動與 CLI 共用同一個），兩條入口都接上；並加一條**原始碼層的守門測試**檢查這兩處還在不在——單元測試測得到服務，測不到「有沒有人接線」。

**為什麼不塞進 `ReloadInto`：** 那是驅動後端的組合點，在啟動路徑上被 `DriverEvidenceGate` 擋著（已有載入在跑就整批跳過）；把一條**不需要驅動**的來源掛進去，等於讓它的有無取決於一個跟它無關的閘門。

### 6.2 三態被寫錯：一次讀取失敗會被講成「不是錯誤」

`Collect` 原本**先看 `Available` 再 `Read`**。而真實 WMI 來源在查詢之前無從得知會不會失敗（`Available` 預設 `true`），於是查詢失敗時會走進「可用但 0 筆」那一支，輸出成：

> 「讀到了來源，但它沒有回報任何計數器——**不是錯誤**，是本機沒有可用的提供者」

**那是一句假話**——它其實是一次失敗，而且我們手上有原因（`ManagementException`）。舊的假來源測試抓不到，因為它們的 `Available` 是固定的、`Read()` 不會改它。

**修法：** 先 `Read` 再判 `Available`（並在接縫契約上把 `Available` 的語意寫清楚：它是「最近一次讀取的結果」，不是事前承諾）；來源自己擲例外也一律變成一筆 `ReadError` 並附上例外訊息，不得靜默成空集合。新增 3 條迴歸測試，並**實測「把舊順序改回去會紅 4 條」**。

### 6.3 CLI 模式在 `main` 上根本跑不起來（本輪順手修掉）

用真實二進位跑 `XinSpect --json evidence` 時，進入點直接崩潰：

```
System.ArgumentNullException: Value cannot be null. (Parameter 'value')
   at System.Windows.Application.set_StartupUri(Uri value)
   at XinSpect.App.OnStartup(...) in App.xaml.cs:line 35
```

`App.OnStartup` 的 CLI 分支寫 `StartupUri = null;` 想擋掉 App.xaml 的主視窗自動建立，而 **.NET 10 的 WPF 把該 setter 改成 `ThrowIfNull`** → CLI 模式在 `OnStartup` 第一行就死，任何 `--json` 都跑不起來。`CliServiceTests` 是直接呼叫 `CliService.Run` 的單元測試，測得到服務、測不到「進入點本身炸掉」。

**修法：** 拿掉那行賦值——`Shutdown(code)` 會在 Dispatcher 處理到「建立 StartupUri 視窗」之前就結束應用程式，這與「第二份實例」那條路徑（同樣在 `OnStartup` 內 `Shutdown` 後 `return`）是同一個機制。並加一條進入點守門測試（只認「非註解行對 `StartupUri` 賦值」，所以解釋這件事的註解不會把測試弄紅）。

**修掉之後的端到端實測（真實二進位、真實 WMI）：**

```
XinSpect.exe --json evidence --query storage.reliability. --out facts.json
→ exit=2（有非 Present 的事實：覆蓋申報列）
→ 46 筆事實、5 顆碟、allPresent=false、storage.reliability.count=5
```

非 Present 的 5 筆正是逐碟的「計數器收錄情形」（提供者沒給的欄位被數出來並具名列出），不是錯誤——這正是三態誠實該有的長相。

**未 commit、未 push、未發版。**

---

### 6.4 韌體安全頁的虛擬化卡片：兩個繫結路徑永遠抓不到東西（補 UI 渲染檢查時發現的第 4 個）

既有 `UiSmokeTests` 建構每一頁時 `MainViewModel` 是**空的**——`ItemsControl.ItemTemplate` 一次也沒被套用，
所以「證據頁畫不畫得出事實列」從來沒有人驗過。把這一層補上之後**立刻抓到一個真缺陷**：

```
Views/FirmwareSecurityView.xaml:99  Text="{Binding VirtualizationHeadline}"
System.Windows.Data Error: 40 : BindingExpression path error:
  'VirtualizationHeadline' property not found on 'object' ''MainViewModel''
```

那兩個屬性住在 `EvidenceLabService`（`Services/EvidenceLabService.cs:278,282`），**不在 `MainViewModel` 上**；
同一支 XAML 裡其他繫結全都寫 `EvidenceLab.X`，只有這兩行漏了前綴——所以畫面上「虛擬化平台」卡片的
標題列與依據列**永遠是空白**，而且 `EvidenceLabService` 內 `OnPropertyChanged(nameof(VirtualizationHeadline))`
的通知也因為路徑對不上而傳不到。**在真實 App 裡這兩行一直是空的，沒有人發現。**

**修法：** 補上 `EvidenceLab.` 前綴（路徑修正後，既有的屬性變更通知順帶就通了）；並新增
`UiSmokeTests.證據頁有事實時樣板套用沒有繫結錯誤且真的長出列`——它把共用組裝點灌進真的 `MainViewModel`、
建構真的 `FirmwareSecurityView`、逼它排版，然後檢查 ①資料到達頁面 ②`FirmwareRowsSource` 真的生出視圖
且含「儲存裝置」的列 ③沒有繫結錯誤。

**實測：把 XAML 改回有缺陷的版本 → 紅 1 條；修復後該檔 6 條全綠；而舊的大煙霧測試在有缺陷時照樣是綠的**
——這正是新測試補上舊測試缺的那一層。
