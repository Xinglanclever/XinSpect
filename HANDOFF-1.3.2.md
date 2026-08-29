# 曦覽 XinSpect 1.3.2 交接說明（給接手的 AI 代理）

> **✅ 本文件的三個剩餘步驟已於 2026-08-29 全部完成**：pdb 內嵌 → push（`a67cfa0..d9454a0`，七筆）→
> Release v1.3.2 已發佈（https://github.com/Xinglanclever/XinSpect/releases/tag/v1.3.2 ，附 XinSpect.exe，
> 9,603,804 bytes、state=uploaded，發佈說明含四大方向＋安全強化摘要）。
> **1.3.2 到此收尾；後續方向見 `ROADMAP-給GLM.md`（開工前須先取得使用者同意）。**

> 這份文件是工作交接用，不是專案文件；發佈完成後可直接刪除（目前未進版控）。

## 一、專案基本事實

- 路徑：`C:\Users\Administrator\XinSpect`，git repo，遠端 `origin` = `https://github.com/Xinglanclever/XinSpect.git`，工作分支 `main`。
- 技術：WPF / .NET 10（`net10.0-windows`、WinExe、`win-x64`、`SelfContained=false`、單一執行檔便攜、`requireAdministrator`）。作者 Xinglanclever。
- **語言：介面與程式註解一律繁體中文（台灣用語）**，不得出現簡體或英文介面字串。
- **核心原則：誠實。** 讀不到就顯示「—」，絕不以「典型值／估計值」代替。任何讀值都必須是真的量到的；說明文字不得誇大工具能力（例：喇叭檢測只說「送出了哪個聲道」，不宣稱使用者聽得見；動態檢測說明量的是「本程式被畫出來的節奏」，不是面板規格；記憶體檢測明言不是 MemTest86 的替代品）。

## 二、目前狀態（截至 2026-08-29）

- 四個升級方向（AI 能力強化／精確度與效能打磨／報告與視覺升級／新增硬體檢測）**全部完成**。
- 版本已 bump：`XinSpect.csproj` → `<Version>1.3.2</Version>`、`<FileVersion>1.3.2.0</FileVersion>`；`Views/AboutView.xaml` → 「版本 1.3.2」。
- 建置：Debug／Release 皆 `0 個警告 0 個錯誤`。
- 測試：`Tests/XinSpect.Tests.csproj`（xunit）**434 項全通過**（1.3.1 時為 427，本次新增 7 項 `Tests/MemoryTestRowTests.cs`）。
- 已重新 publish：`publish/XinSpect.exe`（版本字串 `1.3.2+60814b1…`）；publish/ 只輸出執行檔（WebView2 *.xml 已不再輸出；pdb 內嵌）。
  ※ 目錄內的 `XinSpect.old-*.exe` 是「執行中映像不可刪、改名讓路」的前版發佈檔：對應程序結束後即可刪除。
- 已本機提交：
  - `4787024`「1.3.2：AI 工具箱、精確度打磨、報告與視覺升級、三項新檢測」（113 檔、+15,615／−1,221）
  - `a82effe`「1.3.2 安全與穩定性強化：橋接程式 SHA256 驗證＋ACL、狀態檔原子寫入、看門狗回復移出 UI 執行緒」
    （新增 `Services/AtomicWrite.cs`、`Services/FileHash.cs`；移除 Bridge/ 的 7 個 SDK 探勘傾印檔）
  - `5f3d198`「1.3.2 加強：開機自啟改工作排程器（最高權限登入觸發）、單一執行個體防護、歷史回寫原子化、publish 輸出瘦身」
  - `60814b1`「網速測試新增節點：SGIX 新加坡（OpenSpeedTest 原生量測）、CTM 澳門電訊與測速網中國（官方頁跳轉）」
    （`NodeProtocol` 新增 `OpenSpeedTest`；SGIX 端點 `/downloading`、`/upload` 已實測：ping 89ms・下載 777Mbps・上傳量測可用）
  - `0a9647f`「網速測試節點選擇器改 WrapPanel 自動換行：低 DPI／窄窗不再水平截斷」
  - `8c1d5af`「網速測試：移除澳門與 Cloudflare 節點、HKBN 標籤改官方測速・香港；瀏覽器測速新增『回到測速頁面』返回鈕」
    （`NodeProtocol.Cloudflare` 已整組移除；`MainWindow.NavigateToBrowser(url, returnUtilityKey)` 帶返回鍵時，
    `BrowserView` 工具列顯示「⟨ 回到測速頁面」，點擊經 `NavigateToUtility("netspeed")` 回實用工具）
  - `d9454a0`「瀏覽器工具列改 WrapPanel 自動換行：窄窗／低 DPI 時網址列與按鈕折行，不再水平截斷」
    （網址列改固定寬度 420／MinWidth 200 參與折行；WrapPanel 不支援 * 填滿，屬取捨）
  **七筆皆尚未 push。**

## 三、剩下要做的事（照順序）

1. ~~（可選，需先問使用者）pdb 處理。~~ **已完成**：`XinSpect.csproj` 已加 `<DebugType>embedded</DebugType>` 並重新 publish，`publish/` 不再有 `XinSpect.pdb`，CrashLog 行號保留。
2. **push**：`git push origin main`。
3. **建立 GitHub Release `v1.3.2`**，附上 `publish/XinSpect.exe`。
   - 陷阱：`gh release create` 同時帶資產時，上傳失敗會把整個 release 回捲。**先建 release，再 `gh release upload` 並自行重試。**
   - **憑證問題：`gh auth status` 回報 keyring token 已失效，舊 token 檔已刪除。必須向使用者索取新 token。**
   - 安全紅線（使用者明示）：**不得使用 GitHub 帳號密碼**；輸出任何內容前以
     `sed -E 's/(gh[pous]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)/***/g'` 遮蔽，**永不回顯 token 值**。
4. 發佈說明可取自提交 `4787024` 的訊息（已按四個方向分段）。

**使用者對版本節奏的明確要求（原話）：「可以，不過一次成型1.3.2而不是經常的版本迭代」——只發一次 1.3.2，不要每做一階段就 bump 版本。**

## 四、建置／測試／發佈指令

使用者的 XinSpect.exe 常在執行中，會鎖住 `bin\Release\...\XinSpect.exe`，**務必改輸出路徑**：

```bash
# 建置驗證
dotnet build XinSpect.csproj -c Debug -v q --nologo -p:BaseOutputPath=obj/_verify/

# 測試（會連帶編譯主專案，等於驗證 XAML；不受 apphost 鎖影響）
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo

# 發佈單一執行檔
dotnet publish XinSpect.csproj -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:BaseOutputPath=obj/_pub/ -o publish -v q --nologo
```

要上傳的只有 `publish/XinSpect.exe`；自 `5f3d198` 起 publish/ 已不會輸出 WebView2 的 IntelliSense xml，目錄僅含執行檔本身。

## 五、必須知道的地雷（重複踩過的）

- `Severity` 列舉只有 `{ Neutral, Good, Warning, Serious, Critical }`，**沒有 `None`**。
- **不要改共用資源筆刷的 `.Color` / `.Opacity`**（會污染全域主題）；新筆刷請 `Freeze()`。
- **code-behind 指派已繫結的 DP 會摧毀該繫結**（曾把 `Topmost` 的繫結弄掉）。
- `System.Text.Json` 寫入 `double.NaN` 會丟例外，**會靜默毀掉整份設定持久化**。
- XAML partial 類別裡若有成員叫 `Grid`，會遮蔽 `Controls.Grid`。
- `Anim.RevealDelay` 預設 `double.NaN` 代表「不做動畫」，是安全的失敗方向。
- **根元素上的 `Data="{Binding}"` / `DataContext="{Binding}"` 在 `Host.Content` 延遲載入時不會解析**，必須在 code-behind 指派。頁面的 `Vm` 存取子慣例：
  `DataContext as MainViewModel ?? Application.Current?.MainWindow?.DataContext as MainViewModel`。
- 導覽有 `_views[]` ↔ Nav **平行陣列（21 頁）**，增頁必須兩邊同步；能做成既有頁面上的卡片就別開新頁（記憶體圖樣檢測就是為此掛在 `MemoryView`）。
- **`Services/CpuzReportService.cs:391-392` 的簡體字是解析第三方 CPU-Z 報告用的，合法，不要「修正」。**
- **單一執行個體防護（`App.xaml.cs`）**：第二次啟動會彈「已在執行中」並結束自己；開發時若使用者的 XinSpect 正在執行，`dotnet run` 會被擋，屬設計行為（信號名 `Local\XinSpect.SingleInstance`）。
- **開機自啟**：`SettingsService` 以工作排程器註冊 `XinSpect` 工作（登入觸發、最高權限、無執行時限），排程失敗才退回 HKCU Run；走排程時會清掉 Run 舊值。驗證用 `schtasks /Query /TN XinSpect`。
- 橋接目錄 `%LOCALAPPDATA%\XinSpect\bridge\` 已 ACL 鎖定（僅 SYSTEM＋Administrators）且內容以 SHA256 對內嵌正本驗證。若見 `XtuBridge.exe.<8碼>.exe`，那是正式檔名被執行中舊實例鎖住時的替代副本（已驗證、可正常使用）；下次正式檔名空出來會自動換回並清理。
- 用 ripgrep（Grep 工具），不要 `grep -P`：多位元組字元的 bracket class 會被逐 byte 比對。
- 私有方法命名別撞 `Window` 成員（曾有 `Show()` 遮蔽 `Window.Show()`，`CS0108`，而 `ToolboxView.Open()` 正是呼叫 `w.Show()`）。

## 六、慣例（沿用，勿另創）

- 跑分／檢測服務一律照 `Services/CacheBenchService.cs` 的形狀：`ObservableObject`、`IsRunning`/`CanStart`、`Phase`、`ProgressFraction`/`ProgressPercent`、`StatusLine`、`ObservableCollection<Row> Rows`、`Start()` → `_ = RunAsync()`、`Cancel()` → `_cts?.Cancel()`；`Progress<>` 在 UI 執行緒建立後 `await Task.Run(...)`；`finally` 收 `IsRunning`／`_cts`。
- 卡片版面照 `Views/BenchView.xaml:352-416`；下拉選單用 `DarkCombo`（`Themes/Theme.xaml:336`）；進度條用 `LoadBar`；讀值磁貼用 `Surface2Brush`。
- 全螢幕檢測視窗（`ScreenTestWindow`／`MouseTestWindow`／`KeyboardTestWindow`／`SpeakerTestWindow`／`MotionTestWindow`）刻意用硬寫深色（`#0E0F13`／`#9AA0AA`／`#E6E9EF`／`#C0161821`）而非主題筆刷，且**必須自己 `Focus()`**，否則 `KeyDown` 不會觸發。
- 測試只測純函式，**絕不建構 `MainViewModel`**（會拉起整組服務）；測試方法名用中文，見 `Tests/DashboardLayoutTests.cs`。

## 七、尚未於執行期驗證的部分（接手者請留意）

三個新檢測**只通過編譯與單元測試，沒有人在畫面上實際看過或跑過**：

- `Views/SpeakerTestWindow.xaml(.cs)`：1～5 選測試音、S 停止、Esc 離開；`SoundPlayer` + 記憶體內 PCM。
- `Views/MotionTestWindow.xaml(.cs)`：← → 速度、↑ ↓ 背景、空白鍵暫停、R 重設；統計取自 `CompositionTarget.Rendering` 的 `RenderingTime`。
- `Services/MemoryTestService.cs` + `Views/MemoryView.xaml` 的「圖樣檢測」卡：會實際配置數 GB 記憶體（保留 1 GB 給系統），跑滿五種圖樣，測試期間整機會變慢。

若使用者願意，建議發佈前先手動開這三個視窗各跑一次。

## 八、使用者互動注意

- 使用者已駁回過兩次 `AskUserQuestion` 選單，**不要重複詢問已決定的事**。
- 「Key 不用管」「Key 你不需要關係」：不要主動追問或改動既有 API 金鑰設定。
- 回覆用繁體中文，簡潔，不要客套開場。
