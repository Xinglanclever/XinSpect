# XinSpect 全碼審查報告（2026-09-29）

範圍：非測試程式全數（Services 第一層 216 檔＋Gpu/Overclock/ExternalSensors/BlueSquadron 子目錄、Views/Controls、ViewModels/Nav/Models/Dialogs/Bridge、根目錄檔案），約 7.6 萬行 C#＋主要 XAML。七個並行審查代理逐檔精讀，只報讀到證據的問題；唯讀硬體設計不列為缺陷。另含此前已確認的 LanguageService 在地化管線問題（見文末）。行號以審查當下工作副本為準。

## P0（當掉／給使用者錯數據／危險硬體狀態／假成功）

1. **儲存頁字串解碼全是亂碼並回填序號** — `Services/StorageSmartService.cs:625-635`（NVMe `AsciiString` 每 2 位元組取 1，但 NVMe 是一字元一位元組）、`:662-676`（ATA 只取偶數位元組且 word 內 byte-swap 方向反了——首字元在高位元組，參照 Linux `ata_id_string` 取 `>>8` 先行）、`:647-651`（NN 在 offset 516 非 513；「NCAP」讀 0x38 落在型號字串內；CC 是 BAR0 暫存器不在 Identify 裡）。亂碼序號經 `:213` `SerialFromRows` 回填取代 WMI 正確值（`:465-474`）。**`Tests/SmartDecoderTests.cs:230-247,267-269` 用同一套錯 offset 造合成資料自證自對，把 bug 鎖死**——修復時測試 fixture 要一併重造。
2. **GPU「還原安全預設」把溫度上限拉到最大** — `Services/Gpu/GpuOcService.cs:502-505`：還原寫入 `THRESHOLD_ACOUSTIC_MAX`（可設定上限 93°C），不是原廠預設（在 `ACOUSTIC_CURR` 開機值，通常 84°C）。還原路徑留下更熱的狀態。
3. **UEFI 特權啟用假成功** — `Services/FirmwareService.cs:42-48`：`TOKEN_PRIVILEGES` 用 `long Luid`，Sequential layout 下 Luid 在 offset 8、結構 24 bytes（正確 16 bytes/offset 4；正確寫法參照 `MemoryService.cs:191-193`）。AdjustTokenPrivileges 恆敗但函式回 true → Secure Boot 頁永遠顯示「變數不存在：可能是 Legacy 開機」。同型：`Services/LargePageService.cs:48-49`（SeLockMemoryPrivilege 恆敗＋誤報無權限）。
4. **超頻寫入假成功** — `Bridge/XtuCore.cs:845`＋`Services/Overclock/XtuOcEngine.cs:225-226`：`Apply()` 不看 SDK `GeneralCode`，host 端對 RequiresReboot 項在 `ok==true` 直接報「已排入設定」。SDK 拒絕時使用者看到成功。
5. **簡體在地化啟動不生效**（此前已確認）— `MainWindow.xaml.cs:34` 只讀旗標無人轉換；`ChineseConverter` 未註冊零引用（`MainWindow.xaml:131` 導覽裸綁定）；`T()` 零呼叫、`Changed` 零訂閱；`ConvertPage` 死碼；`_cache` 快取頁脫離視覺樹不跟隨切換；就地改寫＋有損往返（暫存→缓存→快取）造成文字漂移。詳見 2026-09-29 對話記錄。

## P1（邊界條件錯誤）

**超頻/硬體狀態**
- `Services/Overclock/XtuOcEngine.cs:156,386-391`：IPC 一次逾時即 `MarkDead()` 永不重連；看門狗還原時 `_engine.Apply` 全失敗——**已套用超頻無軟體回復手段**。
- `Services/Overclock/OverclockService.cs:1077-1078`：`IsRisky` 漏 `VoltageOffset`——多數平台（Vcore 走 offset）所有電壓寫入不武裝看門狗。
- `Bridge/XtuCore.cs:877-895`：`RestoreDefaults()` 中途例外直接 return，已 Tune 的還原 staged 在 SDK，汙染之後每次 Apply 的寫入範圍。
- `Services/Overclock/XtuOcEngine.cs:212,228-235`：讀回失敗（`activeKnown=false`）以舊值比對容差，把「無法驗證」誤報成「寫入失敗」。
- `Services/Overclock/SiliconProbeService.cs:257`：MSR 讀失敗 `?? 0` 造成 `0-e0` 無號下溢→天文瓦數污染矽品質計算。
- `Services/BlueSquadron/SecurityPostureService.cs:116`：屬性 3 是 HVCI 可用，屬性 4 才是 DMA 保護——誤報「Kernel DMA Protection 已啟用」。

**解碼/數據正確性**
- `Services/CpuTopologyService.cs:90`：CACHE_RELATIONSHIP 的 GroupMask 應在 +16 卻讀 +32（照抄 PROCESSOR_RELATIONSHIP 的 +24/+32，參照正確的 `CpuAffinity.cs:213`）——快取「每 N 執行緒共用」全錯＋末筆越界讀。
- `Services/CpuIdService.cs:244-248`：擴充葉傳 `-1`（EAX=0xFFFFFFFF），CPU 回最高擴充葉資料；守門 `Math.Abs(leaf)>maxExt` 恆 false——LZCNT/NX/SYSCALL 等七位元永不顯示。
- `Services/CoreTempMapService.cs:49,69-87`：混合架構（P-core SMT＋E-core 無）`threadsPerCore` 整齊映射錯位，E-core 溫度歸錯格。
- `Services/SpdConsistencyAuditService.cs:467`：regex 把 PC4-25600 的 25600 當 MT/s——驗機 R-MEM 誤報速度矛盾。
- `Services/MemBandwidthMath.cs:198`：字串寫死 `"&lt; 5%"`，使用者看到 HTML 實體字面。
- `Services/StorageSmartService.cs:679`：Word0 bit15=1 是 ATAPI，語意寫反，真 ATA 碟標成 ATAPI。

**流程/race/凍結**
- `Services/PcieAnalysisService.cs:180-186,191-194`：呼叫 `Refresh()`（fire-and-forget）後立即讀 `Rows`——鏈路富集永不生效；配對只比 VEN 不比 DEV，多卡會套錯數據誤報插槽拆分。
- `Services/GpuStressService.cs:204-217`：winget 安裝不讀 stdout 直接 `WaitForExit()`——pipe 緩衝滿即死鎖，安裝永久鎖死。
- `Services/DiskBenchService.cs:100-104`：`Path.Combine("C:", file)` 產生 drive-relative 路徑，512MB 暫存檔建在錯的地方。
- `Services/CleanupService.cs:119-132`：ACL 目錄讓 `DirSize` 整體 return 0，清理量計算跟著失真。
- `Services/ChipsetAnalysisService.cs:41-111`、`PcieAnalysisService.cs:49-129`、`NpuDetectionService.cs:56-140`、`CpuPinoutService.cs:39-59`（建構子!）：UI 執行緒同步 WMI 全表列舉，進頁凍結數秒。
- `Services/ReportService.cs:45`：`File.WriteAllText` 裸奔無 try/catch，匯出遇鎖檔直接閃退。
- `MachineAgeService.cs:64-73`、`NetAdapterService.cs:62-69`、`NvmePowerService.cs:139-141`、`SleepDiagnosticsService.cs:64-71`、`BootBreakdownService.cs:81-92`、`DramTrafficService.cs:78-89`（同型：InvisibleStall/License/DisplayLink/LargePage）：`Task.Run(...).ContinueWith(t => t.Result ...)` 不查 `t.IsFaulted`——例外在 UI 執行緒重拋或 `IsBusy` 永久卡 true、按鈕永久停用。
- `Services/TerminalService.cs:56-57`：PowerShell 分支強制 UTF8 解碼但 console codepage 是 950/GBK——中文輸出亂碼（cmd 分支有 chcp 65001 補救）。

**UI 生命週期（快取頁重入）**
- `Controls/CoreColumns.cs:56-66`：Unloaded 解訂後重入不重 Hook——Motion off 時逐核液柱**永久凍結**在重入瞬間，看似活儀表實為停格。
- `Views/FrameTimeView.xaml.cs:17-21`：計時器建構即 Start、無 OnDeactivated——切頁後 ETW DXGI 訂閱永不停。
- `Views/PortUsageView.xaml.cs:26-76`：`_loaded` 一次性守衛＋Unloaded 停計時器——重入後 UI 顯示「自動更新=開」實際已死。
- `Views/AiView.xaml.cs:116-177`：多檔並行 `_ = AttachFileByPathAsync` 取 `PendingAttachments[^1]` 競態——縮圖指錯資料、✕ 刪到別檔附件；`Loaded` 每次重入加一個 CollectionChanged handler 永不退訂。
- `Views/BrowserView.xaml.cs:33-44`＋`App.xaml.cs:30-36`：`_initStarted` 失敗永不重試；多開實例共用 WebView2 使用者資料夾——第二實例瀏覽器必死且只顯示誤導訊息。

**資料/目錄**
- `Nav/HelpCatalog.cs:62-96 vs 1805-1840`：六個 beginner 鍵各定義兩次，索引子語法靜默覆蓋且兩份內容已分叉——改前份永遠不生效。
- `ViewModels/UpgradeFactsCollector.cs:139`：秒級樣本 <60 筆得 0 分鐘歷史（`BottleneckFactsCollector.cs:237` 已修同型並註明，此處漏改）。
- `Bridge/XtuCore.cs:756`：`v != 0` 才回——0 RPM/0 W 被當「不存在」，0 與無值混淆，違反專案明文立場。

## P2（摘要，共約 40 條）

- **文化敏感格式化漏網**：`DriverAuditDecoder.cs:36`、`EventsService.cs:39-41`、`ReliabilityHistoryService.cs:166`、`SensorLogService.cs:80`（ToString 未用 InvariantCulture，`:` 是文化分隔符）、`WinsatService.cs:209-221`（TryParse 無 InvariantCulture）、`ComputeChartService.cs:140-148`（解析顯示文字而非數值，de-DE 靜默給錯）。
- **ContinueWith/逾時/Dispose 家族**：`DpcLatencyService.cs:268`（`TimeSpan.FromSeconds(2000)`＝33 分鐘，疑似 2 秒筆誤）、`EnvCheckService.cs:157-174`/`GpuStressService.cs:184-196`（WaitForExit 逾時不 Kill）、`RdtService.cs:318-322`（Dispose 不 Join worker）、`BlueSquadronModule.cs:167-342`（CTS Cancel 後立即 Dispose）、`MemoryTestService.cs:212-214` 同型 CTS 競態、`SettingsService.cs:439`（Save 空 catch 吞整份設定丟失）、`Diag.cs:98-106`（diag.log 檔案寫入在鎖外互撞）。
- **頁面導覽**：`MainWindow.xaml.cs:148-163`（Factory 失敗後側欄亮新頁內容留舊頁）、`Views/UtilitiesView.xaml.cs:37-40`（子工具 Factory 無 try/catch）、`Views/TerminalView.xaml.cs:88-100`（歷史瀏覽 Clamp 令「回到空白行」不可達）、`Views/BatteryView.xaml.cs:86-92`/`HostsEditorView.xaml.cs:106-113`（UI 執行緒同步 WaitForExit 最長 8 秒）。
- **雜項**：`HwInfoSharedMem.cs:83-180`（AbandonedMutexException 已取得所有權卻不 Release）、`GpuOcService.cs:121-123`（NVML Init 後 handle 失敗不 shutdown）、`OverclockService.cs:990`（ConfirmStable 不查 IsApplying）、`OcModels.cs:246`（Offset 格式無條件「−」前綴）、`BlueSquadronEngine.cs:105-108`（5 秒逾時＝終身處決）、`SecurityPostureService.cs:598-602`（查無 C: 回 false＝BitLocker 未啟用扣 30 分）、`SiliconQuality.cs:79`（截距 SE 漏槓桿項，區間偏窄）、`ReliabilityHistoryService.cs:105`（MM-dd 字串排序跨年錯序）、`HelpCatalog.cs:1859`（五大/六大防線鍵名不符）、`HelpCatalog.cs:1873`（sata 孤兒鍵）、`HistoryStore.cs:294-306`（NTP 回撥破壞嚴格遞增不變量）、`DriverAnalysisService.cs:84-85`（輪詢無退場）、`CeremonyService.cs:86-98`（無條件等 5 分鐘）、`AudioSpectrumService.cs:45-58`（Start/Stop 迴圈洩漏 WASAPI）、`HardwareEvidenceViewModel.cs:122-124`（Insert(0) 整組反轉）、`MetricsPump.cs:38-42`（重啟即漏訂閱）、`DnsService.cs:206-222`（stderr 未並行讀＋編碼不符）。

## 已驗證乾淨的高危區（逐欄核對過，可放心）

SMBIOS Type 4 全部位移（真實 fixture 對照）、SPD XMP magic 位元組序、SMART 屬性 12-byte 版面、SMBus i801 暫存器/協定碼、USB IOCTL 常數、McaService 銀行 offset、DramTraffic PERFEVTSEL 位域、FrequencyTruth 0xCE 位域、EdidService 10-bit 色度、BsodService DUMP_HEADER64、PciResourceAuditService 描述元 offset、PowerPolicyService SYSTEM_POWER_CAPABILITIES 位元、NvmeLogDecoder/NvmePowerDecoder 佈局、PcieLinkDecoder、PortUsageService MIB 表、MemoryTruthService 結構、ChessEngine perft、XiangqiEngine、MicroarchProfile.DecodeSignature、EraCalendar 遷移與哆啦A夢紀年算術、Bridge IPC 幀協定（UTF-8 無 BOM、持鎖一問一答、逾時 Kill 無孤兒）、ObservableObject、MetricsPump 重入防護。

## 修復優先序建議

1. **第一批（數據可信度）**：StorageSmartService 字串/offset 三連＋SmartDecoderTests fixture 重造；FirmwareService/LargePageService struct layout；SecurityPosture DMA 屬性；CpuTopologyService offset；CpuIdService 擴充葉。
2. **第二批（危險狀態）**：GpuOcService 還原值；XtuOcEngine MarkDead 重連＋IsRisky 補 VoltageOffset＋假成功 GeneralCode＋RestoreDefaults staged 汙染；SiliconProbe 下溢。
3. **第三批（不死鎖不凍結）**：GpuStress/Winget WaitForExit；ContinueWith 家族統一改 async 包 try；四處同步 WMI 改 Task.Run；ReportService 補 catch。
4. **第四批（UI 生命週期）**：CoreColumns 重 Hook、FrameTime/PortUsage 生命週期、AiView 附件競態與訂閱洩漏、BrowserView 重試＋多開資料夾。
5. **第五批**：HelpCatalog 重複鍵、文化敏感格式化掃蕩、其餘 P2。
6. **在地化**（獨立一次成型）：原文保存進 Tag→單一轉換出口→啟動套用→converter 註冊掛綁定→T() 補動態文字→LCMapStringEx 尾 NUL 補測試。

## 審查方法備註

七代理分區精讀（46+61+41+61+25+115+46 檔次，含交叉取證），每位只報讀到證據的發現並附行號；P0 第 3 條經 net10 結構 layout 實測驗證。已知風格問題、慣用 GB 標示、唯讀設計風險均不列。個別存疑但證據不足未報者：BiosMeService MKHI Result 欄位慣例（作者註明實機測通）、AiService 45 秒逾時（ResponseHeadersRead 下正確）。
