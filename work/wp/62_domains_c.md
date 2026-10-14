## 第三十五章　電源、散熱與風扇控制

### 35.1 事實來源

| 來源 | 取得方式 | 可信層級 | 內容 |
|---|---|---|---|
| MSR | Ring-0 讀取 | `Measured` | 電源狀態、頻率狀態、溫度上限、能耗相關計數 |
| RAPL（執行期平均功率限制） | 硬體計數器 | `Measured` | 封裝與各域的能耗計數 |
| Super I/O | I/O 埠存取 | `Measured` | 風扇轉速、溫度、電壓、輸入電壓 |
| 電池 API | 作業系統提供 | `Reported` | 電池的設計容量、滿充容量、充放電狀態、循環次數 |
| PMBus（電源供應單元） | 裝置存取 | `Measured` | 輸入與輸出的電壓、電流、功率 |
| UPS | 裝置存取 | `Reported` | 電池狀態、負載、運轉時間 |
| 電源策略 API | 作業系統提供 | `Reported` | 電源計畫、睡眠設定、處理器效能狀態 |

本領域的特點是存在**功率守恆的交叉條件**：輸入功率（PMBus 或 RAPL）與輸出功率（元件能耗之和）理論上應滿足守恆關係。此條件在藍圖中列為未實作的對帳方向（見 35.4）。

### 35.2 電源策略的判讀

`PowerPolicyService` 與 `PowerPolicyDecoder` 處理電源計畫的解碼，包括處理器效能狀態（P-state）的上下限、核心停用策略、以及睡眠與休眠設定。

此層的判讀要點是：**電源策略是「設定的值」，而非「量測的值」**。使用者設定的效能下限可能因散熱或電源限制而無法達到；反之，設定的上限可能被平台忽略。因此電源策略事實必須與實際量測的頻率與功耗並列，兩者的落差才是有意義的資訊。

`PpttFactsService` 處理 ACPI 的處理器屬性拓撲表（PPTT），其內容包括每個層級的快取與電源域的關係。此表在混合架構處理器上的診斷價值較高，因為效能核與效率核的電源域配置不同。

`CppcCapabilityFactsService` 處理協同處理器效能控制（CPPC）的能力事實。此項的判讀要點是：啟用 CPPC 需要韌體、作業系統與處理器三者的支援，缺少任一者即回退到傳統的 ACPI P-state 控制，兩種控制方式的量測結果不可直接比較。

### 35.3 散熱與感測器

`SensorService` 與其風扇相關的部分（`SensorService.Fans`、`FanControlRow`、`FanCurveService`）提供風扇與溫度的量測與控制。`CoreTempMapService` 提供逐核心的溫度分佈。`ThermalStickyService` 處理溫度相關的黏滯現象（例如溫度讀值不變的情形）。

`SensorSanity` 的職責已在 12.1 說明：判斷感測器讀值是否可信。本領域是此模組最主要的適用場景，因為主機板的 Super I/O 感測器在未連接時回報固定值的情形相當常見。

`SuperIoProbeService` 與 `SuperIoHwmFactsService` 處理 Super I/O 的探測與硬體監控解碼。Super I/O 的事實以動態家族 `sio.0x` 與 `sio.hwm.0x` 登記，其登記理由為：「按實際探到的指標埠命名（0x2E／0x4E，可能還有別的）——埠號是平台事實，不是編譯期常量。」

**風扇控制的寫入性質。** 風扇曲線的套用屬於寫入操作，因此經由寫入閘門並需同意（見 2.6）。其風險標示必須包含影響範圍（哪些風扇、溫度範圍）與可能的後果（散熱不足）。

### 35.4 功率的來源與守恆

**RAPL。** `RdtService` 與相關的能耗計數提供封裝與各域的能耗量測。其判讀要點是：能耗計數為累計值且會溢位，因此量測需以兩次讀值的差與時間間隔計算功率，並處理溢位與計數器重設的情形。

**PMBus 與電源供應單元。** `PsuPmbusFactsService` 提供電源供應單元的輸入與輸出量測。此類事實的獨特性在於它是**系統外部的量測**：元件能耗由內部計數器取得，而電源供應單元的量測反映的是整機的實際輸入。

兩者的比較即為功率守恆的交叉條件。此對帳尚未實作，其理由已登記於覆蓋申報的註記（「psu.：PMBus 市電 vs RAPL vs NVML 是可以對的（藍圖的『功率守恆交叉對帳』）」）。此條註記的價值在於它是一個**明確的待辦**：可對帳而尚未對帳，與不可對帳而豁免，兩者在申報中被區分。

**電池。** `BatteryService` 提供電池的事實，包括設計容量與滿充容量（規則 `R-BAT-01` 的輸入）。其豁免理由為：「電池／UPS 事實來自 Windows 電池 API，單一來源。」電池的健康度判定已在規則中完成，不需另立跨來源規則。

**UPS。** `UpsFactsService` 提供不斷電系統的事實。其判定價值在於確認保護機制存在，而非評估其容量。

### 35.5 已知限制

**感測器的可信度。** 感測器讀值需經 `SensorSanity` 判斷，但該模組的判定基於啟發式規則，無法涵蓋所有異常情形。

**溫度與功耗的時序相依。** 見 25.4。

**平台相依的 Super I/O。** 不同平台的 Super I/O 型號與暫存器配置不同，探測失敗的情形以 `NotSupported` 呈現。

**功率守恆對帳尚未實作。** 見 35.4。

## 第三十六章　周邊與匯流排

### 36.1 事實來源與範圍

本領域涵蓋匯流排層的裝置列舉與連結協商，其事實以 `Measured` 為主，因為 PCI／PCIe 的設定空間與 USB 的描述元皆由裝置直接提供。

| 子領域 | 模組 | 內容 |
|---|---|---|
| PCI 匯流排盤點 | `Bus0InventoryService`、`PciResourceAuditService` | 匯流排上的裝置、其資源（BAR）配置與衝突 |
| PCI 能力 | `PciCapabilityFactsService`、`BusCapabilityFactsService` | 裝置支援的能力與其組態 |
| PCIe 鏈路 | `PcieLinkService`、`PcieLinkDecoder`、`PcieAnalysisService` | 逐裝置的鏈路寬度與速度、與裝置能力的比較 |
| PCIe 錯誤 | `EcamAerService` | 進階錯誤報告（AER）的可存取性與狀態 |
| USB 拓撲 | `UsbTopologyService`、`UsbLinkService`、`UsbLinkDecoder` | 控制器與中樞的樹狀結構、逐埠的協商速率 |
| Thunderbolt／USB4 | `TbUsb4Service` | 連結的協商與安全性層級 |
| 輸入裝置 | `MousePollRateFactsService` | 滑鼠的輪詢率 |
| 其他周邊 | `CameraFactsService`、`ServerRoleFilePrintFactsService` | 攝影機與列印裝置 |

### 36.2 PCI／PCIe 的盤點

PCI 裝置的列舉以動態家族 `pci.dev.` 與 `pci.res.` 登記，理由為成員由匯流排枚舉決定。`PciResourceAuditService` 的職責是審計資源的配置，包括 BAR 的位址、大小與是否有重疊。

**資源衝突**是此領域的核心診斷項目。其表現為裝置無法正常運作或資源被重新配置。工具陳述衝突的具體內容（哪些裝置、哪些位址範圍），使使用者可據以調整 BIOS 設定。

`BusDeepDiveFactsService` 與 `BusCapabilityFactsService` 提供能力層的深入事實，包括裝置支援的鏈路寬度與速度上限、以及各種能力結構的內容。

### 36.3 USB 拓撲與連結

`UsbTopologyService` 提供控制器與中樞的樹狀結構，並以動態家族的形式登記（其豁免理由為「USB 拓撲列舉來自 USB 控制器與中樞的裝置樹，單一來源，沒有第二條路徑可以交叉」）。

`UsbLinkService` 與 `UsbLinkDecoder` 處理逐埠的連結協商，其判讀要點是**協商速率與裝置能力的比較**：一個 USB 3.2 裝置連接到只支援 USB 2.0 的埠時，其協商速率為 USB 2.0 的速率，此落差是有效的診斷資訊（常見於機殼前置面板的連接線）。

`TbUsb4Service` 處理 Thunderbolt／USB4 的連結，包括其安全性層級。此項的安全性層級（決定哪些裝置可被連接）與 14.1 所述的安全稽核相關。

`EcamAerService` 處理 PCIe 的進階錯誤報告。其判讀要點是：AER 的可用性依賴平台是否提供 ECAM 存取與是否啟用 AER，因此「無法讀取 AER 狀態」與「沒有 AER 錯誤」必須區分。

### 36.4 已知限制

**匯流排列舉的時效。** 熱插拔裝置使列舉結果具有時效性。

**橋接裝置的鏈路。** 經過 PCIe 橋接或切換器時，鏈路事實需區分裝置與橋接層各段。

**USB 的速度協商。** 協商速率受線材、埠與裝置三者影響，因此落差不必然指向單一原因。

**周邊裝置的單一來源。** 攝影機、列印裝置等列舉類事實多為單一來源（見 34.4），不具備跨來源對帳條件。

## 第三十七章　作業系統、軟體與更新

### 37.1 事實範圍

本領域涵蓋作業系統與其上軟體的組態事實。其可信層級為 `Reported`，且本領域是**最容易被改寫**的一層（見 7.3）。

| 子領域 | 模組 | 內容 |
|---|---|---|
| 服務與啟動 | `ServiceInventoryService`、`ServiceBootFactsService`、`StartupService`、`ScheduledTaskStartup` | 服務的列舉與組態、系統啟動項目、排程工作的啟動項 |
| 驅動程式 | `DriverAnalysisService`、`DriverAuditService`、`DriverAuditDecoder`、`DriverInspectionFactsService` | 驅動程式的列舉、簽章狀態、靜態檢視 |
| 裝置安裝歷史 | `SetupApiTimelineService` | 由裝置安裝記錄解析的裝置安裝時間線 |
| 事件與可靠性 | `EventLogSummaryService`、`EventsService`、`ReliabilityHistoryService`、`ErrorAggregationService` | 事件記錄的摘要與彙總、可靠性歷史 |
| 當機分析 | `BsodService`、`WheaErrorService`、`WheaTimelineStore`、`MinidumpFactsService` | 當機傾印的解析、硬體錯誤事件、錯誤時間線 |
| 更新 | `WindowsUpdateHistoryService`、`UpdateCheckService`、`UpdateRetryFactsService` | 更新的歷史與狀態 |
| 套件管理 | `WingetService` | 套件管理工具的狀態 |
| 軟體清單 | `SbomService`、`AssetFactsService` | 軟體物料清單、資產事實 |
| 授權 | `LicenseService`、`LicenseDecoder` | 授權狀態的解碼 |

### 37.2 驅動程式與裝置安裝歷史

**驅動程式的觀測**分為三層：列舉（有哪些驅動程式與其版本）、簽章狀態（是否經簽章與簽章者）、以及靜態檢視（`DriverInspectionFactsService` 逐檔的欄位檢視，以動態家族 `drvinsp.` 登記）。

靜態檢視的診斷價值在於識別**可疑的驅動程式**：未簽章、簽章者異常、或位於非預期路徑的驅動程式。其與 `ByovdCompareService`（14.3）的差異是：後者比對已知的易受攻擊驅動封鎖清單，前者檢視驅動程式本身的屬性。

**裝置安裝時間線**由 `SetupApiTimelineService` 提供，其來源是系統的裝置安裝記錄檔。解析的方式是只取語言中立的欄位（標記符號、裝置實例識別碼的語彙、時間戳格式），因此在地化的標籤混入時仍可解析。此項的判讀價值在於時間軸的重建：一次異常的裝置安裝或驅動程式更換會在此留下時間戳，可用於與其他事件的時間軸對照。

### 37.3 事件、可靠性與當機分析

**事件記錄的彙總。** `EventLogSummaryService`、`EventsService` 與 `ErrorAggregationService` 提供事件的摘要與錯誤的彙總。其設計要點是**彙總而非傾印**：事件記錄的完整傾印對使用者無價值，因此工具提供分級與聚合的視圖。其豁免理由為：「事件記錄摘要的來源就是事件記錄本身，自我對帳無意義。」

此條揭示一項重要的界線：**自我對帳無意義**。一份摘要與其來源的比較不構成對帳，因為兩者不是獨立的來源。此原則貫穿本專案的豁免清單。

**可靠性歷史。** `ReliabilityHistoryService` 提供系統的可靠性度量歷史。

**當機與硬體錯誤。** `BsodService` 處理當機傾印的解析，`WheaErrorService` 處理 Windows 硬體錯誤架構（WHEA）的事件，`WheaTimelineStore` 保存錯誤的時間線，`MinidumpFactsService` 處理小型傾印的解析。

此組功能的判讀要點是**時間線的對照**：硬體錯誤事件（WHEA）、當機事件與裝置安裝歷史三者的時間軸對照，可定位「問題從何時開始」，這是純粹的靜態事實清單無法提供的資訊。

### 37.4 軟體清單與資產

`SbomService` 產生軟體物料清單（SBOM）。`AssetFactsService` 提供資產事實，其識別欄位為豁免事實（見 10.3）。`LicenseService` 與 `LicenseDecoder` 提供授權狀態的解碼。

SBOM 的設計要點是**來源的可追溯性**：清單中的每一項應能追溯其來源（登錄檔、套件管理工具、或檔案系統）。此要求與 3.2 的來源標識原則一致。

### 37.5 更新與其驗證

`WindowsUpdateHistoryService` 提供更新歷史的解析，`UpdateCheckService` 提供更新狀態的檢查，`UpdateRetryFactsService` 處理更新重試的相關事實。

更新歷史的判讀價值在於**組建號與修補層級的確認**：一台機器的安全性狀態與其修補層級直接相關，而修補層級需由更新歷史確認，而非由版本號推定。

### 37.6 自我完整性

`SelfIntegrityFactsService` 與 `SelfTelemetry` 提供工具自身的完整性事實。此項的意義是：一個用於驗證他物的工具，其自身狀態也應可被檢視（例如行程的簽章狀態、載入的模組）。這是本專案「工具自身的可信度條件也屬於事實」立場的另一實例（與規則 `R-CPU-04` 同屬一類）。

### 37.7 已知限制

**作業系統層的可改寫性。** 見 7.3。本領域的事實應與硬體層或韌體層交叉後才具備較高的可信度。

**事件記錄的清除。** 事件記錄可被清除，且其清除本身可能留有稽核痕跡（也可能不留）。因此事件記錄的「沒有錯誤」不等於「未曾發生錯誤」。

**裝置安裝記錄的格式。** 解析依賴記錄檔的格式，格式變更會導致解析失敗（以 `ReadError` 呈現）。

**軟體清單的完整性。** SBOM 的完整性依賴來源的完整性；未經登錄檔註冊的軟體不會出現於清單中。

---
