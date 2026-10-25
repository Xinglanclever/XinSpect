# U10 第二圈候選 30 項逐項收口判定（2026-10-25）

## 一、方法

對 `CIRCLE-2-CANDIDATES-2026-10-25.md` 的 30 項（TS 12／VZ 8／UI 4／PF 4／RP 2），逐項在倉庫原始碼（`Services/` 含 `DeepTest/`、`DeepBench/`，`Views/`、`Controls/`、`Tools/`、`Tests/`、`Nav/`）grep 實證：

1. **服務面**：候選功能對應的服務類別（如 BenchService、CacheBenchService、CeilingService、ReportService、EvidenceLabService、BaselineLearningService）逐讀原始碼確認量的是什麼、單位是什麼。
2. **UI 面**：Controls/ 與 Views/ 的控制項與頁面（CommandPalette、CoreHeatmap、TimelineChart、Sparkline、SensorsView、BeginnerDashboardView 等）。
3. **鍵面**：`Services/FactKeyCatalog.cs`（HEAD 實測 **1,071 把鍵**）逐前綴比對——`viz.`／`ui.`／`pf.`／`rp.`／`rep.`／`exp.`／`cli.`／`ps.` 前綴在目錄中 **0 命中**，新鍵無撞名風險；`ts.` 前綴已有 11 把（ts.aud.／ts.clock.session.drift／ts.disp.／ts.kbd.repeat／ts.pen.pressure／ts.reg.enum.latency／ts.svc.depend.depth／ts.touch.maxpoints，FactKeyCatalog.cs:969-979），TS 域新鍵只需避開這 11 個尾名。
4. **意外發現**：上一輪 DEDUP 判「新列」的兩項已在本輪之前落地（見 §四）。

**判準**（同 DEDUP-VERDICTS 體例）：候選描述的主體已有可指認實作＝**收口**；主體在但候選明言的差異面（純整數隔離、核×時間軸、兩份檔互比、不合成評分等）確實無實作＝**部分收口**；全庫搜不到該功能任何線索＝**新列**。搜不到就是搜不到，不猜。

> 表格 ID 欄為 U10 目錄 ID 去掉 `U10-` 前綴的短形式（TS-0001 即 U10-TS-0001，餘類推）。

### 統計

| 結論 | 筆數 |
|---|---:|
| 收口 | **6** |
| 部分收口 | **16** |
| 新列 | **8** |
| 合計 | **30** |

---

## 二、逐項判定

### TS ・ 檢測與測試（12 項）

| ID | 項目 | 判定 | 證據 | 建議 |
|---|---|---|---|---|
| TS-0001 | 整數單執行緒吞吐 | **部分收口** | 單核運算吞吐已有：BenchService.cs:8,128-130（單執行緒相位 MOPS）＋ChessBenchService.cs:36（單執行緒相位 3 秒，BenchView.xaml:13「原生節點吞吐」）；但 BenchService 核心是「混合整數／浮點」（BenchService.cs:194-206），棋類是特定負載——**純整數 MOPS 隔離核全庫 0 命中** | 在 BenchService 加一個純整數相位即可收掉；或併入 TS-0002 一個服務兩相位 |
| TS-0002 | 整數多執行緒吞吐 | **部分收口** | 多核運算吞吐已有：BenchService.cs:132-134（多執行緒相位）＋ChessBenchService.cs:106-146（1–4096 線程、平行效率）；同上，無純整數隔離核 | 同 TS-0001，一個服務一次收 |
| TS-0003 | FP32 單/多 | **部分收口** | 多核 FP32 SIMD 吞吐已有：NpuComputeBenchService.cs:7-47（Vector256 FMA、GFLOPS、Parallel.For 全核）；**單核 FP32 專屬相位未有**（BenchService 單核相位是混合核） | NpuComputeBenchService 加單線程模式成本極低 |
| TS-0005 | AVX2 吞吐（單/多） | **部分收口** | SIMD FP 吞吐會搭上 AVX2（NpuComputeBenchService.cs:24「通常 8（AVX2）」），但**AVX2 專屬隔離（vs 純量/AVX-512 對照）與單核相位未實作**；CeilingService 的 AVX2 窗量的是功耗降頻不是吞吐 | 與 TS-0003 合成一個「向量寬度階梯吞吐」（純量/128/256/512）最有價值 |
| TS-0006 | AVX-512 吞吐＋降頻效應 | **部分收口** | **降頻效應已收口**：CeilingService.cs:16-18,139-146,1105-1106（AVX-512F 全核 512 位元乘法窗、向量授權降頻）＋CeilingDecoder.cs:527；**吞吐側未有**（Ceiling 系量頻率/功耗/溫度，MOPS/GOPS 於該服務 0 命中） | 降頻面別重做；只補吞吐量測，直接引用 Ceiling 的頻率窗對照 |
| TS-0015 | L1 帶寬 | **新列** | 快取階梯已有的是**延遲**：CacheBenchService.cs:20-35,208-210（指標追逐 L1/L2/L3 ns）＋LatencyCurveService；帶寬量測只有主記憶體 STREAM（MemBandwidthService.cs:183「工作集要遠大於 L3」）與核間帶寬（TopologyCoreBandwidthService.cs:37-39）——**L1/L2/L3 帶寬（GB/s）階梯全庫 0 命中** | 與 0016/0017 合併成一個「快取帶寬階梯」服務，CacheBenchService 的工作集掃描可直接複用 |
| TS-0016 | L2 帶寬 | **新列** | 同上（0 命中） | 同上 |
| TS-0017 | L3 帶寬 | **新列** | 同上（0 命中） | 同上 |
| TS-0018 | memcpy 帶寬（SIMD vs REP） | **新列** | `memcpy`／`memset`／`REP MOVSB` 基準於 Services/Tests 全庫 0 命中；唯一相關是 FSRM **位元偵測**（CpuIdService.cs:643）——有鑰匙沒門 | 新服務；StreamBandwidthAdapter（DeepBench）是現成接入點 |
| TS-0019 | memset 帶寬 | **新列** | 同上（0 命中） | 與 0018 同一服務兩核 |
| TS-0024 | TSC 跨核同步檢查 | **新列** | TSC 相關現有：單核實測 TSC 頻率（CeilingService.cs:406,473——釘**單一**核心對 QPC）、Invariant TSC 位元三態（PlatformTrustService.cs:149）、會話期牆鐘漂移（DeepTest/ClockSessionDriftService.cs）；**跨核對讀 TSC 比偏移（skew）全庫 0 命中** | 新服務；CeilingService 的釘選＋MSR 橋接是現成基建，成本中低 |
| TS-0026 | 逐核一致性測試（弱核偵測） | **部分收口** | 頻率面已有：PlatformReconciliationFactsService.cs:118（逐核對稱性檢查，APERF/MPERF 跨幅%）＋PowerPolicyService.cs:44（逐核頻率一致性、限頻顆數）；微碼逐核一致性（CpuFirmwareFactsService.cs:58-71）；**「同一負載逐核輪跑比吞吐找弱核」0 命中**（弱核/同負載輪跑均無） | 候選的核心差異（負載面非頻率面）值得做：把 NpuComputeBench 核逐核釘選輪跑即可 |

### VZ ・ 可視化（8 項）

| ID | 項目 | 判定 | 證據 | 建議 |
|---|---|---|---|---|
| VZ-0001 | 多序列折線圖 | **收口** | Controls/TimelineChart.xaml.cs:8-14——「可縮放平移的多指標疊圖」，多序列、事件標記、十字準線讀值、滾輪縮放/拖曳平移；即時版 HistoryGraph（每秒重畫省成本）分立 | 已超額覆蓋（互動都有了），無需新列 |
| VZ-0004 | 熱圖（核×時間） | **部分收口** | 核熱圖已有但無時間軸：CoreHeatmap.xaml.cs:9-11（一核一格＝**當下**溫度/使用率，用於 CpuView/OverclockView/CoreDetailWindow）；另有核×核延遲熱圖（CoreLatencyService.cs:5-7，N×N 矩陣）；逐核液柱只有「最近數秒高點」細線（BottleneckView.xaml:83）——**核×時間軸熱圖 0 命中** | 資料面（HistoryStore 每秒逐指標）已有，缺的是把逐核序列畫成格子；建議接 HistoryStore |
| VZ-0009 | 直方圖（延遲分布） | **收口** | DiskLatencyStats.cs:32-45,107（延遲分佈：1-2-5 階梯區間直方圖＋百分位）＋BenchView.xaml:776-798（直方圖列渲染＋說明）；驅動年齡直方圖另有一處（DriverAnalysisService.cs:143-148） | 磁碟面已收口；若要網路延遲直方圖屬增量（ux.network-stack-latency 已有逐筆延遲樣本可接） |
| VZ-0022 | 子彈圖（量測 vs 規格區間） | **新列** | `bullet`／子彈圖於 Views/Controls/Services 全庫 0 命中（唯一命中是 XML 註解 `<list type="bullet">`，與圖無關）；最接近的是 RadialGauge/AnalogVoltMeter 單值儀表 | 新控制項；「電壓 vs ATX ±5%」的規格區間資料（PowerDelivery 頁）已有可接 |
| VZ-0057 | 感測器儀表板頁 | **收口** | Views/SensorsView.xaml:5-19——「所有感測器」頁，Live.AllSensors 即時分組表（溫度/風扇/電壓全在）；MiniOverlayWindow 另有常駐浮窗版 | 主體已有；「儀表板化」（加圖塊）屬美觀增量 |
| VZ-0098 | 日曆熱圖（異常按日統計） | **部分收口** | 按日異常**資料**已有：ReliabilityHistoryService.cs:23-25（近 30 天 Kernel-Power 41/6008、BugCheck 1001、App 1000/1002、開機耗時）＋HealthView.xaml:187-210；但呈現是**事件清單**——`calendar`/日曆熱圖視覺化 0 命中 | 只差一張格子圖：資料管道現成，新控制項畫 30 格即可 |
| VZ-0103 | ECDF 圖 | **新列** | `ecdf`／經驗分布全庫 0 命中；百分位**數字**已有（DiskLatencyStats 百分位、gpu.dispatch-jitter p50/p95/p99，DeepBenchCatalog.cs:48）但無分布曲線圖 | DiskLatencyStats 的逐筆延遲陣列就是 ECDF 的輸入，一條排序曲線的事 |
| VZ-0105 | 控制圖（SPC） | **部分收口** | SPC 的**統計引擎**已有：BaselineLearningService.cs:69-118（逐指標自基線：中位數/P95/平均/標準差＋最新點 z 分數）；AlertService.cs:77-86（門檻警報）；**控制圖本身（中心線/管制上下限的圖形呈現）0 命中** | z 分數與基線都能取了，畫成 TimelineChart 上的一條帶狀 overlay 即可 |

### UI ・ 介面（4 項）

| ID | 項目 | 判定 | 證據 | 建議 |
|---|---|---|---|---|
| UI-0001 | 全域鍵盤快捷鍵表 | **部分收口** | 全域快捷鍵僅一組：Ctrl+K／Ctrl+P 開命令面板（MainWindow.xaml.cs:303-316；MainWindow.xaml:78-85 有 Ctrl+K 提示）；其餘鍵盤處理皆單視窗內部（Esc 關窗等，MotionTestWindow/ScreenTestWindow）；**快捷鍵「表」（更多快捷鍵＋說明頁/清單）全庫 0 命中** | 先定義快捷鍵集（F5 重掃、Ctrl+E 匯出…），再借 HelpCatalog 出說明頁 |
| UI-0002 | 命令面板（Ctrl+K） | **收口** | Controls/CommandPalette.xaml.cs:21「命令面板（Ctrl+K）：對所有頁面、實用工具與全域動作做模糊搜尋並直接執行」；MainWindow.xaml.cs:310-315 綁定；PaletteCatalog＋PageRegistry 單一權威清單（PageRegistry.cs:6-9） | 完整收口，含上下鍵/Enter/Esc 鍵盤導航 |
| UI-0003 | 表格就地過濾/排序 | **部分收口** | 就地**過濾**已有：FactAtlasView.xaml:75-83（文字過濾＋域下拉，TwoWay 即打即篩）、DriverAuditView/PortUsageView/ToolboxView 亦有（PortUsageView.xaml.cs:35 ICollectionView.Filter）；**就地排序未有**——各表只有程式內固定排序（如 ThreadMigrationView.xaml:102 依遷移率），點欄位表頭排序 0 命中 | 差一半：給 FactAtlas/EvidenceLab 事實表加欄位排序即可全收 |
| UI-0048 | 首頁健康摘要卡 | **部分收口** | 簡易模式首頁已有完整健康摘要：BeginnerDashboardView.xaml:11-23（健康度圓環＋健康評分＋一句話總結＋健康明細），PageRegistry.cs:21-32（beginner 頁＝「簡易模式首頁」，BeginnerOnly）；**進階模式首頁（OverviewView，關鍵字即「首頁」）無健康摘要卡**——只有數字面板/走勢/硬體識別（OverviewView.xaml:31-101） | 差的是進階首頁那一張卡：把 Health 的評分磁貼（ReportService 的 hero tile 同款）放上 OverviewView |

### PF ・ 平台化（4 項）

| ID | 項目 | 判定 | 證據 | 建議 |
|---|---|---|---|---|
| PF-0002 | 報告 diff 模式 | **部分收口** | 鍵級差分核心已有：EvidenceLabService.cs:2130-2157（CompareAsync 載入時間膠囊→逐鍵比較，變更/新增/消失/未變計數、**同機守門**、跨機拒比）＋EvidenceLabView.xaml:38「快照差異」節（Before/After/Delta 欄）；事實鍵 ev.diff（EvidenceExportFactsService.cs:26）、rs.rdiff（LifecycleAndReportsFactsService.cs:26-27）亦在目錄；**差異：對照一側恆為「現況」——兩份歷史膠囊檔互比未支援** | 補「開兩份檔互比」入口即可全收；CompareAsync 的 Diff 核心可直接重用 |
| PF-0004 | JSON 匯出 schema 版本化 | **部分收口** | 快照/時間軸 JSON 已版本化並把關：HardwareSnapshotService.cs:138,337-338（SchemaVersion 寫入＋載入端不支援即 NotSupportedException）、EvidenceTimelineService.cs:18,327（CurrentSchemaVersion=1 向後相容檢查）、ev.migrate 事實鍵（ExportUxFactsService.cs，schema 遷移器）；**CLI `--json` 證據匯出 payload 無版本欄**（CliService.cs:98-105：generatedAtUtc/scope/count/… 無 schemaVersion） | 把 schemaVersion 補進 CLI payload 一行的事；相容性承諾政策（未來升版規則）可成文 |
| PF-0006 | Markdown 匯出 | **收口** | ReportService.cs:27-37（另存對話框三選一：HTML/Markdown/純文字）＋72-80（CopyMarkdown 直接進剪貼簿「貼上論壇」）；三格式共用同一份 Section/Block 中介表示（ReportService.cs:88-127），不會走樣 | 已收口（DEDUP 輪 PF-0001 已判，此處重申） |
| PF-0007 | 選擇性匯出（指定域/鍵） | **部分收口** | CLI 已有前綴選擇：CliService.cs:20,93-94（`--query` 前綴過濾事實＋`--out` 寫檔，headless 模式自述亦載明）；**GUI 報告匯出是整包**（ReportService.Export 無域/鍵選擇）；逐鍵任意清單（非前綴）挑選未有 | GUI 匯出對話框加域勾選；或曝露 CLI 既有能力到 GUI |

### RP ・ 報告節（2 項）

| ID | 項目 | 判定 | 證據 | 建議 |
|---|---|---|---|---|
| RP-0001 | 封面與執行環境 | **收口** | 封面已有：ReportService.cs:646-660（brand＋`<h1>硬體報告</h1>`＋產生時間/主機/CPU 副標＋hero 摘要磁貼＋TOC 目錄，:662-664）；執行環境＝「系統」節（ReportService.cs:202-215：OS/版本/架構/主機/使用者/開機時間/BIOS/主機板/晶片組）；小增量：**程式版號未印在報告上**（FootLine :858-862 與封面均無 XinSpect 版本） | 補一行版號即滿分；版號資料在（ChangelogTests 守門的那份） |
| RP-0002 | 系統摘要頁 | **部分收口** | 摘要節已有：「整機摘要」（ReportService.cs:186-199——健康評分/評語/建議＋逐項狀態四欄表＋誠實註記）＋首節即放；**差異：候選明言「不合成評分」，而現有摘要正是以合成評分（X/100）領頭**——無「純事實、不合成」的一頁摘要版 | 若圈選：做一個「事實速覽」節（CPU/記憶體/GPU/儲存各一行關鍵事實，不打分），放在摘要節之前 |

---

## 三、結語

### 新列 8 項——真正的空白

全部集中在兩簇：

- **快取帶寬階梯（TS-0015/0016/0017）＋ memcpy/memset（TS-0018/0019）**：五項實為**一個服務**（工作集掃描帶寬＋兩個搬運核）。基建最全：CacheBenchService 的工作集掃描、MemBandwidthService 的 GB/s 數學、DeepBench StreamBandwidthAdapter 的接入點全在。這是本清單**性價比最高的一簇**。
- **TSC 跨核同步（TS-0024）＋子彈圖（VZ-0022）＋ECDF（VZ-0103）**：三項各自獨立但都小——TSC 有 CeilingService 的釘選/MSR 橋；ECDF 的輸入（DiskLatencyStats 逐筆延遲）已在手上；子彈圖是新控制項但規格區間資料（電源/溫度規格）已有出處。

### 部分收口 16 項——差的都是「最後一里」

共性明顯：**引擎已在、呈現或隔離未到**。三個最便宜的收口動作：

1. NpuComputeBenchService 加單線程相位＋純量/128/256 寬度階梯 → 一次收 TS-0003/0005 大半與 TS-0001/0002 的隔離面。
2. FactAtlas/EvidenceLab 事實表加欄位排序 → 收 UI-0003。
3. CLI payload 補 schemaVersion 一行 → 收 PF-0004 大半。

### 誠實聲明

- 「部分收口」的判定從寬從嚴一致：既有實作必須可指認（檔案:行號），差異面必須在原始碼中確實 0 命中才寫「未有」。
- TS-0001/0002 的「部分收口」採從嚴口径：棋類跑分確實是整數負載的單/多核吞吐，但目錄項目的字面（整數 MOPS 微基準）與棋類負載之間的差異已如實標注；若圈選時接受棋類即代表，此二項可升格收口。
- VZ-0105（SPC）判「部分收口」的根據是 BaselineLearningService 的 z 分數引擎；若候選嚴格指「控制圖」這個圖形，可視同新列——差異已寫明，圈選時自決。
- 本判定未修改任何既有檔案；grep 均為唯讀。

---

## 四、意外發現（對上一輪 DEDUP 的更正線索）

1. **U10-VZ-0064（事實總表 sparkline 欄）已落地**：DEDUP-VERDICTS 判「新列」時稱「sparkline 於 Views/Services 0 命中」；現況是 Controls/Sparkline.cs 存在，且 Views/FactAtlasView.xaml:119 已繫結 `<local:Sparkline … Values="{Binding SparkSeries}"/>`——事實總表每列就是一條迷你走勢。該列應改標「已實作」。
2. **U10-PF-0009（匯出簽章雜湊鏈）已落地**：Services/ReportExportChainService.cs 檔頭自標「U10-PF-0009，exp.hash.chain」——每次匯出（HTML/MD/TXT/CSV）檔尾附 `previous=… content=… algorithm=sha256`，沿用審計帳本串鏈；ReportService.cs:50-52 已接。DEDUP 判「新列」的結論已過期。
3. **ev.／rs. 前綴鍵已入目錄**：FactKeyCatalog 現含 ev.* 24 把（ev.diff、ev.verify、ev.migrate 等）與 rs.*（rs.diff.json、rs.rdiff 等）——匯出/生命週期域的事實面已相當齊全，第二圈 PF 域判定時以此為準。
