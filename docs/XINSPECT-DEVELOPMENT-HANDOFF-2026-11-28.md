# XinSpect 新版開發交接與修正紀錄

整理日期：2026-11-28（Asia/Singapore）。本文件已於同日更新定向測試及隔離畫面證據。

專案：`C:\Users\Administrator\XinSpect`。整理時分支為 `main`，HEAD 為 `656ac969`；工作樹包含本輪尚未提交的修改。原有版本為 3.33，本輪未指定下一版號。

本文合併目前需求、實作進度、驗證證據、已知問題與接續步驟。**目前仍在開發及整合，不能作為「全部修好」或「已發布」的公告。** 後續來源如再修改，須以新的建置、TRX 與畫面驗證更新狀態。

## 1. 使用者最新決定

1. **不要真實本地 AI／模型推論功能。** 本輪新加的推論模組、入口與套件應撤除；這不代表刪除所有既有 AI 相關能力。
2. **簡體中文與英語的繁體中文殘留必須系統性修復。** 不再以零散替換、單一頁面截圖或詞庫數量作完成判斷。
3. **不做第三方插件 SDK。** 不列為本版功能，也不列為後續待辦。
4. 其餘先前授權的硬體監控、統計、診斷與工具能力繼續整合。
5. 這次先提供一份可交接的 Markdown，將完成與未完成的內容分清楚。

## 2. 比較目標與範圍

目標是參考 AIDA64、HWiNFO 及使用者提供的 `src`，補強有實際用途的硬體能力，不以功能名稱或入口數宣稱全面對等。

`C:\Users\Administrator\Desktop\src` 經先前盤點是圖吧工具箱的已編譯發行目錄，**不是 AIDA64 或 HWiNFO 的原始碼**。本輪沒有將其專有程式碼或二進位複製進 XinSpect。

參考工具的已附版本、功能列表與既有盤點保留於 `C:\Users\Administrator\XinSpect\docs\NEXT-VERSION-CAPABILITY-PLAN.md`；不將附帶版本說成官方最新版本。

## 3. 已撤回的本地 AI 新功能

整理時已逐檔確認以下 11 個新增正式檔案不存在：

```text
Models/LocalInferenceModels.cs
Services/LocalInferenceService.cs
Services/LocalInferenceOrt.cs
Services/LocalInferenceOpenVino.cs
Services/LocalInferenceOnnxInspector.cs
Views/LocalInferenceWindow.xaml
Views/LocalInferenceWindow.xaml.cs
Tests/LocalInferenceTests.cs
Tests/LocalInferenceInputTests.cs
Tests/LocalInferenceProfileTests.cs
Tests/LocalInferenceNpuTests.cs
```

`XinSpect.csproj` 中本輪新增的 `Microsoft.ML.OnnxRuntime.DirectML` 引用已撤除；NPU 頁新增的本地推論入口亦已撤回。調查和失敗證據保留於 `work`，不視為產品功能。

保留既有 NPU 識別與明確標為 **CPU 工作負載** 的 SIMD 基準。後者的 FLOP 計數與多執行緒輸出範圍修正獨立保留，不能稱為 GPU／NPU 推論測試。

## 4. 語言殘留：根因與目前修正

| 問題 | 目前來源處理 | 驗證狀態與剩餘限制 |
|---|---|---|
| 語言切換原先只處理主視窗 | 增加共用 WPF 本地化生命週期與載入／卸載監看 | `focused-09` 的 lifecycle/UI smoke 測試通過；隔離畫面 probe 另有 217/217 合成資料檢查 |
| 綁定文字被略過或不會重新求值 | 保留 Binding、converter、StringFormat，於語言及來源更新時重新投影 | lifecycle、來源更新與 binding-load probe 已通過；未逐頁人工檢視的文字仍見 6.3 |
| 舊原文槽覆蓋後續新狀態 | 分開記錄原文及最近輸出 | 多次狀態更新與語言往返由 lifecycle 回歸覆蓋 |
| bool 切換入口未清除英語旗標 | 統一切換入口及互斥語言旗標 | 繁中→簡中→英語→繁中循環由語言回歸覆蓋 |
| 英語詞庫缺少文字或格式模板 | 擴充精確文案與有文字錨點的模板；參數保持原值 | DriverStore 與混合訊息欄位有定向回歸；`focused-09` 通過 |
| 唯讀文字框與自繪圖形未完整本地化 | 加入明確顯示標記及繪圖邊界翻譯／重繪 | 本輪定向測試及已列出的合成畫面驗證通過；其餘人工目視項目仍列於 6.3 |
| 全字串翻譯可能誤改外部資料 | 區分產品文案與原始內容；限制可翻譯的顯示綁定 | 路徑、裝置名稱、規則名稱、錯誤內容與參數有保留原值的回歸 |

### 4.1 主要檔案

以下相對路徑均位於專案根目錄 `C:\Users\Administrator\XinSpect`：

```text
Services/LanguageService.cs
Services/WpfLocalizationBridge.cs
Services/UiTextTranslation.cs
Services/EnglishStrings.cs
Tests/LanguageLifecycleTests.cs
Tests/EnglishCatalogCoverageTests.cs
```

Sensors、SensorAlerts、Memory 三個畫面已新增 16 處明確的 `Mode=OneWay + Lang` 顯示繫結；保留既有可編輯綁定、數值格式、brush／visibility converter、裝置名稱與 ID。這批來源已包含在 `focused-07` 主程式建置及定向測試中。

DriverStore、DiskSpace、MouseTest、SensorPanel 與多個自繪 Controls 已補語言變更後的重新顯示或重繪路徑；`focused-09` 廣泛定向測試通過相關測試群。DriverStore 完整格式模板翻譯另補參數與原始 INF 路徑回歸。未逐項人工檢視的可見文字仍列於 6.3，不以中途鍵數作為完成判準。

### 4.2 本輪收口與剩餘驗收限制

共用 WPF bridge 已修正舊 focused run 中的四個生命週期失敗。回歸涵蓋 Loaded 前移除子項時清理延後 handler、視窗關閉後清理 Popup／綁定 `Run` observer，以及巢狀 `Run`、選單、工具提示、欄標頭、Popup 子內容替換與語言往返。DriverStore 詳情測試呼叫正式 selection handler，逐語言確認原始 INF 路徑仍顯示。

另發現本地化初始化的真實崩潰：內建詞庫模板排序使用 25 ms Regex timeout，逾時例外可能從 WPF `Loaded` 冒出而使程序退出。來源已調整為：

- 內建靜態模板的 placeholder 掃描不使用短逾時。
- 動態模板比對保留 25 ms 邊界；捕捉逾時後保留原文。

Regex timeout catch 已由確定性測試覆蓋：以明確拋出 `RegexMatchTimeoutException` 的測試委派驗證逾時後保留原文，沒有靠不穩定的長輸入或時間競賽。該測試包含於窄範圍 `focused-08`（150/150）；`focused-09` 擴大 UI smoke 與功能範圍。

混合訊息已在產生端分段投影：`SensorLog` 狀態、外部感測器狀態／來源說明、警示事件與通知，以及記憶體控制器欄位和原因。`ProductTextProjection` 僅處理精確詞庫項目或具文字錨點的模板；未知來源文字、規則名稱、數值、路徑及例外資料保持原值。相關回歸包含英語／簡體輸出與 payload 保留。

收尾時發現 `{StaticResource Lang}` 被多個新增頁面使用，但共用 `Themes/Theme.xaml` 未註冊該資源，造成 UI smoke 建構 `MemoryView`／`SensorsView` 時失敗。已在 Theme 字典註冊 `ChineseConverter` 為 `Lang`；產品 App 與 `WpfEnv` 共用同一份 Theme，因此 smoke 現在驗證相同資源來源。修正後窄範圍 150/150、廣泛定向 455/455 通過，細節見 6.1。

## 5. 其餘硬體能力的本輪進度

下表「已實作」表示來源已有實作，**不等於全套回歸、所有入口或實機硬體矩陣已驗收**。

| 能力 | 本輪來源進度 | 重要限制與後續 |
|---|---|---|
| 共用感測資料骨架 | 已加入不可變 reading／frame、裝置與感測器身分、來源、單位、信任、六態與擷取時間 | 來源更新時間與本程式讀取時間須分開；失敗不能沿用舊數字 |
| 感測器統計 | 已實作目前／最小／最大／平均、有效筆數及重設 | 非有限、過期、歧義或未確認值不得混入統計 |
| 可選項 CSV 記錄 | 已實作穩定感測器 ID 選取、來源資訊、長格式 CSV、公式防護及失敗處理 | 缺值保留空值與原因；不能用 0 或舊值補齊 |
| 自訂感測器警示 | 已實作上／下限、持續時間、遲滯、冷卻、未知狀態與有界歷史 | 取樣失敗不能重新評估快取數值或誤判已恢復 |
| 自訂 SensorPanel | 已實作任意感測器綁定、數字／儀表／歷史、拖移縮放與匯入匯出 | 本輪 `focused-09` 已包含 SensorPanel 定向測試；尚未做真實硬體矩陣驗收，損壞設定須保留原檔 |
| 記憶體控制器資料 | 已加入現有來源可提供的 MCLK／UCLK／FCLK 等投影與狀態說明 | 不把 SPD 能力或 WMI 設定速率當成目前控制器實測；來源快取新鮮度須明列 |
| Raw Input 滑鼠取樣 | 已接入手動 WM_INPUT／QPC、逐裝置間隔與統計 | OS 事件到達率不是實體 USB 輪詢率；不搶占其他註冊，不自動開始擷取 |
| 磁碟空間分析 | 已實作可取消有界掃描、樹圖、類型／大檔、邏輯與實佔大小及 JSON | 處理硬連結、重解析點、路徑邊界與記憶體預算；不是 MFT 快速掃描 |
| Driver Store 盤點／匯出 | 已實作結構化盤點、手動匯出與 SHA-256；補強相同 handle 的檔案驗證 | 僅盤點與備份，不做安裝、刪除、還原；未執行真實驅動匯出 |
| CPU package power 被動診斷 | 已實作手動、既有讀取通道限定的 MSR 診斷 | 平台範圍嚴格限定；不啟動負載、不初始化驅動、不提升全域信任 |
| 新手引導與 3D 圖解 | 仍屬後續可選規劃 | 本輪沒有完整實作，不列為已交付 |

### 5.1 感測器與功率的關鍵修正

- LHM CPU Power 讀值不因其他來源的全域 RAPL 驗證通過，就自動獲得相同信任。不同 provider／socket／sensor 必須各自保留來源證據。
- 取樣失敗時，CSV 與自訂警示接收失敗 frame，不重新拿舊 scalar 當成新一輪讀值。
- 重複感測器 ID 保留各列自己的裝置及讀值，不能以第一筆代替全部。
- 新鮮度採保守的 poll 開始單調時間；UTC 另記擷取完成時刻。
- 先前截圖的約 1.3 W 上報值根因仍未確定；顯示「未驗證」不等於底層讀取已修好。

被動功率診斷目前只涵蓋已限定的 Intel family 6／model 0x55、Windows、裸機、單 package 等條件，使用既有通道讀取 `0x606`／`0x611`。本輪沒有實際執行該硬體診斷，不能宣稱使用者機器的 CPU 功率問題已解決。

## 6. 已有測試證據

### 6.1 最近一次主專案定向測試

2026-11-28 最新廣泛主專案定向測試為 `focused-09`，直接解析 TRX counters：

| 項目 | 數量 |
|---|---:|
| 總計／執行 | 455 |
| 通過 | 455 |
| 失敗 | 0 |
| 未執行 | 0 |

原始 TRX 位於 `obj/audit/feature-language-20261128/focused-09/focused-09.trx`；獨立輸出組件位於 `Tests/obj/_featureverify09/bin/Release/net10.0-windows/win-x64/`。此輪含 UI smoke、Sensors、Alerts、Panel、Memory、Mouse、DiskSpace、DriverStore、CPU 診斷、NPU bench、Language、English catalog、ProductTextProjection、Sensor cross-check 與 RAPL load safety filter。`Lang` 資源註冊後 smoke 通過。這是廣泛定向測試，不是全套測試或實機硬體驗收。

更窄的收尾回歸 `focused-08` 為 150/150 通過，含 UI smoke、語言生命週期／詞庫／Regex timeout、混合訊息、SensorLog／警示／記憶體控制器與 DriverStore／DiskSpace 語言測試。其後執行的 `focused-09` 擴大範圍並使用獨立輸出，未覆寫既有證據。

先前廣泛主專案定向測試 `focused-07` 直接解析 TRX counters：

| 項目 | 數量 |
|---|---:|
| 總計／執行 | 439 |
| 通過 | 439 |
| 失敗 | 0 |
| 未執行 | 0 |

原始輸出位於 `obj/audit/feature-language-20261128/focused-07.log` 與 `focused-07.trx`。該輪建置當時的主程式與測試組件，並執行交接列出的 Sensors、Alerts、Panel、Memory、Mouse、DiskSpace、DriverStore、CPU 診斷、NPU bench、Language、English catalog、Sensor cross-check 與 RAPL load safety filter。上一輪 `focused-03` 中 9 個失敗所屬的記憶體參數、Panel 右移、英文詞庫與語言生命週期測試群本輪皆通過。

後續 DriverStore／DiskSpace 欄位版面修正以隔離輸出重建，`DriverStoreLanguage` 與 `DiskSpaceLanguage` 定向測試 9/9 通過；log／TRX 為 `focused-visualfix-02`。這是 XAML 呈現範圍回歸，未重跑全套或 439 項廣泛定向測試。

CPU 診斷按鈕呈現修正後，以隔離輸出重建並執行 `CpuPackagePowerDiagnostic` 測試，28/28 通過；log／TRX 為 `focused-cpuvisual-01`。

本輪最終來源的全套測試尚未執行。既有文件中的歷史 9,420 項全套成績屬舊版本證據，不能挪用成本輪結果。

### 6.2 隔離與畫面證據

以下是不同範圍、不同來源時間點的結果，不相加成主專案總通過數：

- DiskSpace 隔離領域／邊界測試 42 項；加入語言測試後另一輪實際 48／48 通過。
- DriverStore 邊界隔離測試 39／39 通過。
- 舊快照 WPF 語言 harness 曾有 187 項語意檢查通過，並產生 56 份 PNG／JSON；快照 DLL SHA-256 為 `FC228A66109C7E7E3F1186857D6DA298ADF3BB5D3977E27EDD7B3910A31BA0AC`。這早於最後一批來源修正，不能作最終驗收。
- DiskSpace 三語 WPF 畫面已用合成掃描結果產生；產品文字會更新，刻意含中文的原始檔名、路徑與系統尾段保留原值。這不是一次真實磁碟掃描。
- 2026-11-28 最新隔離 harness 證據位於 `obj/audit/language-visual-20261128/cpu-controls-20261128-01/`。輸出快照 43 個檔案，來源建置前後及快照間 manifest drift 均為 0；主 DLL 與快照 SHA-256 均為 `19EE046F0DA855B6AE7D01BFABDABB7C618E5CDA9E5EFB828B51FF4847458782`。產生 86 張 PNG 與 217 項語意檢查，217 通過、0 失敗、0 harness errors。SensorPanel late-status probe 呼叫產品 `SetStatus(string)`；DriverStore probe 選取合成列以呈現完整明細。所有畫面資料均為合成值，沒有啟動主程式、硬體掃描、磁碟掃描或 Driver Store 盤點。
- 已人工檢視此最新 run 的 `DriverStoreWindow-2-English.png`、`DiskSpaceWindow-2-English.png` 與 `CpuPackagePowerDiagnosticWindow-2-English.png`。DriverStore 表頭完整可見，長儲存格以省略號呈現並附全文提示；選取列後明細可見 `Original INF: qa-device.inf`。DiskSpace 空間基準選項完整顯示。CPU 診斷助記鍵不再印出底線，取消／匯出按鈕使用深色工具按鈕，唯讀證據框可讀。這些都是隔離合成資料畫面。
- 先前 `handoff-fix-20261128-02` 的 binding-load probe 以 240 個合成 TextBlock、720 個 observer 執行繁中／簡中／英文／繁中切換，每階段來源更新 9 次，關閉後 observer 為 0。建立／顯示／綁定 118.5 ms；四次語言切換分別 61.8、126.3、61.5、134.1 ms；36 次來源更新各次觀察範圍：繁中 44.0–71.7 ms、簡中 48.1–68.9 ms、英文 16.7–30.5 ms；關閉與 dispatcher 清理 7.7 ms。這些是本機合成 UI 的 wall-clock 觀察，不是效能門檻或硬體負載結果。
- 最新三頁 XAML、Panel／Controls 的檢查包括 XML、C# 語法與局部 diff；靜態檢查不能替代執行期結果。

### 6.3 尚待畫面驗證的細節

- 其他尚未逐項人工檢視的可見文字仍須以實際畫面核對，避免把未顯示的來源 DP 或被 Inline 取代的 Text 屬性誤判為殘留。

## 7. 接續工作順序

1. 本輪已完成語言生命週期、混合訊息投影、Regex timeout 回歸，並修正共用 Theme 漏註冊 `Lang` 資源；`focused-09` 為 455/455。
2. 最新隔離畫面 probe 為 217/217，使用合成資料；沒有真實硬體、磁碟掃描或 Driver Store 匯出驗收。
3. 尚未逐項人工檢視的可見文字仍列為 UI 驗收限制，見 6.3。最終來源的全套測試、真實硬體矩陣、實際磁碟掃描與驅動匯出亦未執行。
4. 本地化 DLL 快照 manifest、hash 與畫面觀察記錄保留於 6.2；舊 `focused-07`、舊 harness 與失敗輸出均保留為歷史證據。
5. 尚未取得發布指示；目前狀態未打包或發布。

### 建議的下一輪定向測試指令

在專案根目錄執行。`focused-09` 是本輪廣泛定向結果；下次來源有變更用 `focused-10` 或更新流水號，保留既有證據。主專案與內嵌 Bridge 建置應串行。

```powershell
dotnet test Tests/XinSpect.Tests.csproj -c Release --no-restore `
  -p:BaseOutputPath=obj/_featureverify10/bin/ -nr:false `
  --filter 'FullyQualifiedName~UiSmokeTests|FullyQualifiedName~SensorObservation|FullyQualifiedName~SensorLogTests|FullyQualifiedName~SensorAlert|FullyQualifiedName~SensorPanel|FullyQualifiedName~MemoryController|FullyQualifiedName~RawMouse|FullyQualifiedName~MousePollRateFactsTests|FullyQualifiedName~DiskSpace|FullyQualifiedName~DriverStore|FullyQualifiedName~CpuPackagePowerDiagnostic|FullyQualifiedName~NpuComputeBenchTests|FullyQualifiedName~Language|FullyQualifiedName~English|FullyQualifiedName~ProductTextProjection|FullyQualifiedName~SensorCrossCheck|FullyQualifiedName~RaplLoadSafety' `
  --logger 'trx;LogFileName=focused-10.trx' `
  --results-directory obj/audit/feature-language-20261128/focused-10 `
  *> obj/audit/feature-language-20261128/focused-10.log
```

此為後續來源變更時的廣泛定向測試範本；本輪廣泛結果為 `focused-09` 455/455，較窄的 `focused-08` 為 150/150。XAML 欄位版面另以 `focused-visualfix-02` 9/9 通過，不得覆寫既有 log／TRX。

## 8. 開發與安全邊界

- 不結束使用者正在執行的 XinSpect，不覆寫其執行檔；使用隔離輸出。
- 不自動安裝／載入驅動、改 Windows 保護、執行硬體寫入或壓力測試。
- 保留既有 `site/index-en.html`、先前 CPU 功率標示修正及其他不相關修改；不執行 `git clean`。
- 讀不到、未確認、不適用、不支援、權限不足、讀取失敗沿用六態，不用 TDP、典型值、任意倍率、0 或舊值補成成功。
- 中文／英文翻譯只作用於產品文案；原始資料、終端輸出、JSON、路徑、ID、使用者內容與未知診斷保留。
- 新增功能的桌面、CLI 與所有後端不得推定已同步接線；逐入口驗證。
- 全套測試通過也不等於全平台硬體精度或相容性認證。

## 9. 文件與證據位置

以下均位於 `C:\Users\Administrator\XinSpect`：

```text
docs/NEXT-VERSION-CAPABILITY-PLAN.md
docs/LOCALIZATION-FIXES-2026-11-28.md
docs/POWER-READING-INVESTIGATION-20261127.md
obj/audit/feature-language-20261128/
obj/audit/driverstore-boundary-20261128/
obj/audit/diskspace-language-20261128-06fbb2/
obj/audit/language-visual-20261128/
work/language-visual-20261128/
work/language-audit-20261128/
```

**交付狀態：本文件是開發交接紀錄；本輪沒有完成新版打包、commit、push、GitHub Release 或雲端上傳。**
