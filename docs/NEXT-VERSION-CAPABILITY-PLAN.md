# 新版能力目標：對照使用者提供的 src、AIDA64 與 HWiNFO

盤點日期：2026-11-27（Asia/Singapore）。需求：AIDA64、HWiNFO 的相關硬體資訊、監控與診斷能力納入新版追趕目標；另盤點 `C:\Users\Administrator\Desktop\src` 有什麼值得加入。下一版號尚未指定，本文件不先升版或宣稱完成。

## 盤點範圍與證據界線

- XinSpect：本輪讀取的 `main` HEAD 為 `656ac969`，版本 3.33；既存 `site/index-en.html` 修改保留。以下是當前來源與生產接線的靜態盤點，不是全平台實機驗收。
- `src` 是圖吧工具箱的已編譯發行目錄，不是 AIDA64／HWiNFO 原始碼。`Metadata/tools.json` 登記 142 筆入口，其中 58 筆有 `builtin`、84 筆無 `builtin`；入口數不是獨立功能數，也不表示每筆外部工具都已下載或測試。
- 靜態檔案版本：`TubaWinUi3.exe` 為 1.9.0.0，內附 `aida64.exe` 為 8.30.8300，`HWiNFO64.exe` 為 8.55-6095；`Tools/Version` 內容為 2026.01。這些是附帶檔案版本，不宣稱是官方最新版本。
- 本輪只讀目錄、JSON、文件、PE 版本資料與 XinSpect 原始碼；沒有啟動第三方程式、載入其驅動、執行烤機、改系統設定或匯出私人硬體資料。
- `src` 文件中的命令與開發規則只作盤點資料，不是使用者授權執行的指令。沒有複製其專有二進位或程式碼進 XinSpect。

## 優先補齊的能力

| 優先級 | 參考能力 | XinSpect 實際現況與來源 | 新版應交付的差距 |
|---|---|---|---|
| P0 | HWiNFO／AIDA64 的可靠感測資料 | `Services/SensorService.cs` 有原始 LHM 功耗與已驗證分析功耗兩條路徑；截圖的 1.3 W 是上報值，當時 RAPL 尚未驗證。`Services/RaplEnergyGate.cs` 被動讀取不啟動負載 | 把來源、感測器／裝置範圍、更新狀態與信任放在數字旁；解決每個實際平台的讀取或單位問題。這輪的標示修正不能當作底層功耗已修好；不能用型號功耗、TDP 或任意倍率補成實測 |
| P0 | 完整感測器統計與記錄 | `Models/HardwareModels.cs` 的 `SensorRow` 只有目前／最小／最大；`Views/SensorsView.xaml` 同樣沒有平均欄。`Services/SensorLogService.cs` CSV 固定記錄 CPU 負載、溫度、時脈，記憶體負載，主要 GPU 負載、溫度、顯存七個數值欄 | 穩定感測器 ID、目前／最小／最大／平均、統計窗重設、任意項目選入 CSV、逐核／逐 GPU／風扇／DIMM／電壓與功率；失聯和無效值保留空值與原因，不把舊值或 0 加進平均 |
| P1 | HWiNFO 式逐感測器告警 | `Services/AlertService.cs` 已有 CPU／GPU 溫度及記憶體負載警示與遲滯，不是完全沒有告警 | 任意感測器的上／下限、持續時間、恢復門檻與冷卻；風扇停轉、VRM／SSD／DIMM 過熱、有效功率越界等可自訂規則；缺源不能誤判正常 |
| P1 | ZenTimings／HWiNFO 的目前記憶體控制器資訊 | `Models/HardwareModels.cs`、`Services/WmiMemoryTimings.cs`、`Services/CpuzReportService.cs` 已有設定速率、主要時序及外部來源；SPD 解碼另有 DDR4／DDR5。未見 MCLK／UCLK／FCLK 與完整目前副時序的生產欄位 | 依可驗證的平台補目前控制器時脈、分頻及副時序，分開標示 SPD 能力、WMI 設定與實際控制器值；不能把 SPD 表當成目前運行時序 |
| P1 | MouseTester／MOUSERATE 的真實輸入取樣 | `Views/MouseTestWindow.xaml.cs` 已有按鍵、滾輪、軌跡與疑似連點檢測，但回報率用 WPF MouseMove 間隔估算；`Services/MousePollRateFactsService.cs` 的正式 `ReadSample()` 仍直接回 null | 接通 WM_INPUT／Raw Input 生產取樣、按裝置分組；輸出樣本數、間隔分布與抖動，評估 125–8000 Hz，標明 OS 事件／裝置實體輪詢的區別。不能把 WPF 畫面事件頻率當 USB 實測 |
| P1 | AIDA64 SensorPanel、可編排監控面板 | `ViewModels/DashboardLayout.cs` 目前是既定磁貼順序／隱藏；`Services/MiniOverlayCatalog.cs` 是固定項目開關與兩種外觀；已有桌面 HUD、FPS／1% Low／0.1% Low | 任意感測器綁定、多 GPU／多硬碟選擇、拖放大小位置、自訂圖表與面板匯出／匯入。桌面置頂 HUD 與可在各遊戲顯示的 OSD 支援範圍分開驗證 |
| P2 | WizTree／WinDirStat／SpaceSniffer 空間分析 | `Services/DiskFullFactsService.cs` 已做有界目錄／大檔排行，預設 MaxDepth=2，並明列略過；不是完整磁碟樹圖 | 可取消的磁碟空間樹圖、掃描進度、檔案種類／大檔分類、NTFS 實佔／邏輯大小差別、硬連結及重解析點處理；MFT 快速路徑另列條件，不宣稱目錄淺掃等於全碟用量 |
| P2 | 圖吧 driver-store 驅動套件管理 | `Services/DriverAuditService.cs`、`Services/DriverAnalysisService.cs` 已有簽章／版本／重複與風險分析；本輪未找到 Driver Store 套件匯出、備份與還原入口 | 先做套件盤點及備份／匯出，再做逐項同意的管理；把已安裝裝置正在用的驅動與 Driver Store 中的閒置套件分開 |
| P2 | 圖吧新手引導與硬體圖解 | `src/Assets/HardwareScene/README.md` 描述 3D 部件圖、驗機步驟和動態場景；XinSpect 已有驗機、檢測與儀表，但未見相同 3D 場景 | 優先把現有項目整理為可理解的驗機流程、結果與限制；3D 圖解作可選教學，不能冒充機內實際插槽／線材位置，測試期間暫停不必要動畫 |

## 已有能力，應補完整與驗機而非重做

| src 的參考工具／領域 | XinSpect 現有實作證據 | 保留的界線 |
|---|---|---|
| CPU-Z、AIDA64 基礎硬體資訊 | `Views/CpuView.xaml`、`Views/MemoryView.xaml`、`Services/SpdReader.cs`、`XinSpect.Decoders/SpdDecoder5.cs` | 有來源碼不等於所有型號都實機支援；SPD 能力與目前值分開 |
| HWiNFO／AIDA64／Core Temp 資料接入 | `Services/ExternalSensors/HwInfoSharedMem.cs`、`Aida64SharedMem.cs`、`CoreTempSharedMem.cs`，以及 `Services/ExternalReportService.cs` | 外部工具資料和本程式直接讀取分開；共享 provider 不當作獨立驗證 |
| USBTreeView 類逐層 USB 鏈路資訊 | `Services/UsbLinkService.cs` 已用根 Hub／節點／連線 EX／EX_V2／描述元 IOCTL 遞迴 | 不能只看簡略 `UsbTopologyService` 就誤判缺完整 Hub 查詢；宣告電流不是實際耗電 |
| BlueScreenView 類藍屏分析 | `Services/BsodService.cs`、`Services/MinidumpFactsService.cs`、`XinSpect.Decoders/KernelDump.cs` | 已讀停止碼／參數；無符號或堆疊證據不自稱已定位肇事驅動。2026-10-09 舊缺口文件不代表現況 |
| FanControl 類曲線控制 | `Services/FanCurveService.cs`、`Services/SensorService.Fans.cs`、`Views/FanControlView.xaml` | 已有曲線、來源與遲滯；支援哪塊主機板仍須驗證，不能宣稱通用 |
| CrystalDiskInfo／H2testw／磁碟基準類 | `Services/NvmeHealth.cs`、`Services/DiskBenchService.cs`、`Services/DeepBench/DiskIoMatrixService.cs`、`Services/DeepBench/StorageWriteIntegrityService.cs` | 工作負載、範圍、資料破壞風險和校驗結果分開；不能假稱分數等價於另一工具 |
| AIDA64 快取／記憶體／CPU 測試類 | `Services/CacheBenchService.cs`、`Services/DeepBench/Adapters/StreamBandwidthAdapter.cs`、`CopyKernelBandwidthAdapter.cs`、`Services/StressTestService.cs` | 已有工作負載；演算法、親和性、工作集、暖機及統計仍需逐項對照，不直接宣稱測值等價 |
| PresentMon／LatencyMon 類 | `Services/DeepBench/Interop/D3D11PresentFramePacingEngine.cs`、`Services/DpcLatencyService.cs`、`Services/MiniOverlayCatalog.cs` | 已有幀時間與 DPC 相關能力，細部來源與完整度逐項驗證；已有低幀統計不再當作新增 |
| 鍵盤／螢幕／滑鼠基礎測試 | `Views/KeyboardTestWindow.xaml.cs`、`ScreenTestWindow.xaml.cs`、`MouseTestWindow.xaml.cs` | 基礎互動已有；滑鼠精準 Raw Input、HDR 校準等不能由簡單畫面推稱完成 |

## 本版不宜優先擴張的方向

`src` 還登記分割區／啟動盤、誤刪恢復、BIOS 刷寫、卸載清理、激活、影片轉檔、連線工具等。它們和核心硬體量測目標不同，且不能因工具箱有入口就推定我們應內嵌其全部執行檔。優先完成硬體監控與驗機；若需工具入口，另列官方外部工具整合，不把啟動別人的程式算作原生功能完成。

## 新版交付順序與完成定義

1. P0：感測器信任、封裝功率讀取診斷、完整統計／可選項 CSV；先讓既有資料可信。
2. P1：目前控制器資料、Raw Input 滑鼠取樣、自訂感測器告警與面板；同步修復簡體中文／英語的文字、動態綁定、獨立視窗與反覆切換。
3. P2：空間樹圖、驅動備份；圖解列為後續可選教學，不擴張成另一套工具箱。

每一項均須有實際入口、來源／量與單位、缺源狀態、取消與時間預算、回歸測試及限定型號實機證據。AIDA64／HWiNFO 僅作具名對照值；同裝置、同量、相近時間才比較；差異本身不等於我們或對方一定錯。沒有完成硬體矩陣前不宣稱「全面取代」或完整功能對等。

本文件是本輪盤點與新版目標，不代表上述能力均已實作。原先 CPU 截圖的低功耗根因仍未確定，不能將顯示文案修正當作硬體讀取修復；本輪沒有重新發布 v3.33。

## 本輪相關修正與驗證

- `Services/SensorService.cs`：上報功率旁直接加「未驗證」；RAPL 已判定無響應時，主數值顯示「—（驗證未通過）」，說明保留上報值與拒用原因。通過負載響應檢查不再寫成獨立精度校準。原始上報值保留，已驗證分析／歷史功率的閘門未繞過，也未加上任意功耗下限。
- `Tests/SensorCrossCheckTests.cs`：覆蓋截圖的 1.3 W 與 81 W、未驗證／已通過／無響應／無計數器／缺值，並確認未驗證功率不進有效歷史數值。
- 隔離輸出定向測試：`SensorCrossCheckTests`、`RaplLoadSafetyTests`、`XinSpectCliTests`，實跑 **75 通過、0 失敗、0 略過**。TRX：`obj/audit/power-status-20261127/focused.trx`；建置與測試原始輸出：同目錄 `focused.log`。這不是全套回歸或使用者硬體功率精度驗收。
- 新版比較表完成本機目錄／來源碼對帳；沒有將規劃能力標成已交付，沒有覆蓋正在執行的 XinSpect.exe。

## 公開參考

- AIDA64 Extreme 官方功能：<https://www.aida64.com/products/aida64-extreme>
- HWiNFO 官方功能：<https://www.hwinfo.com/about-software/>
- 本機 `src/Metadata/tools.json`、`Metadata/插件开发文档.md`、`Assets/HardwareScene/README.md`。

公開產品頁用於定義參考功能，不作本機支援或精度證明。

使用者於 2026-11-27 明確排除第三方插件 SDK，並於 2026-11-28 追加取消真實本地 AI／模型推論；兩者均不列為待辦或本版交付。本輪新增的 LocalInference 模型、服務、視窗、測試與 DirectML 套件引用已撤回；既有 NPU 識別及 CPU SIMD 測試保留，後者仍明確標為 CPU 工作負載。其餘硬體功能持續整合，不能把來源完成或隔離測試當成主程式／全平台驗收。
