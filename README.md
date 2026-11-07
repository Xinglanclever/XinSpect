[简体中文](README.zh-CN.md) · 繁體中文 · [English](README.en.md)

[![官方網站](https://img.shields.io/badge/%F0%9F%8C%90_%E5%AE%98%E6%96%B9%E7%B6%B2%E7%AB%99-xinspect.de5.net-4C8DFF?style=for-the-badge&logo=cloudflare&logoColor=white)](https://xinspect.de5.net)

> ## 🌐 [官方網站：xinspect.de5.net ↗](https://xinspect.de5.net)
>
> **首頁 · xsc 終端機 · 下載 · 版本沿革** —— 瑞士現代主義風格的一頁站，自訂網域 [xinspect.de5.net](https://xinspect.de5.net)。
> 網站只呈現說明，**不會掃描你的電腦**，也不需要任何安裝。

**可翻閱版（自足 HTML，含目錄與錨點，離線可讀）：[百科.html](百科.html) ・ [手冊.html](手冊.html)**
**百科 v3.17 底稿全文（87 萬字敘事主體）：[docs/百科-v3.17.md](docs/百科-v3.17.md)**

# 曦覽 XinSpect 百科・修正版（v3.17・2026-11-07 審計後）

![版本](https://img.shields.io/badge/version-3.18-4C8DFF)
![平台](https://img.shields.io/badge/platform-Windows%20x64-0A7EA4)
![框架](https://img.shields.io/badge/.NET-10.0--windows%20(WPF)-512BD4)
![測試](https://img.shields.io/badge/tests-7884%20passed-3FB950)
![突變分數](https://img.shields.io/badge/Stryker-82%25-8B5CF6)
![授權](https://img.shields.io/badge/license-AGPL--3.0--or--later-blue)
![商業授權](https://img.shields.io/badge/license-%E5%95%86%E6%A5%AD%E6%8E%88%E6%AC%8A%E5%8F%AF%E8%AB%87-8B5CF6)

**一款免費開源、在 Windows 上執行的電腦工具箱，帶有 ring0 切核等底層功能：可直接讀寫硬體暫存器（MSR、PCI 設定空間、SMBus 等）。單一執行檔、免安裝，讀取一律唯讀、寫入全部經同意閘門，不收集不上傳任何使用者資料。**

> **這份文件與 v3.17 百科的關係：** 不是重寫，是**修正**。v3.17 百科（[docs/百科-v3.17.md](docs/百科-v3.17.md)，87 萬字）仍然是這棵樹的敘事主體；本版做三件事——
> ①把 v3.17 發佈後的全庫審查（逐行約五萬七千行、116 條發現；[docs/審查報告-v3.17.md](docs/審查報告-v3.17.md)）如實申報並標注每條的修復狀態，v3.18 增量審查（111 條編目）見 [docs/審查報告-v3.18.md](docs/審查報告-v3.18.md)；
> ②把審查揭出的**守門紅燈**（規模表兩格數字過期）當場修掉——紅燈在 3.17.1 修復輪已清（規模表現值 1,587／123，守門測試釘住），本版規模表是綠燈狀態下的現值表；
> ③把 CLI、內核特權層、修復分叉史納入百科敘事——它們一直在樹上，卻一直不在書裡。
> 逐章擴寫至繁中 104 萬的計畫見 [百科-修正版-總藍圖](docs/百科-修正版-總藍圖.md)。

---

## 這份文件的一句話說明

曦覽的立場：**一台電腦的每一項性質，都應該分成三份陳述來看——硬體自己說的、韌體說的、作業系統說的。** 能讀的給數值與出處，讀不到的給原因（六態），互相矛盾的用紅字具名列出雙方，判讀留給人。本版新增第四份陳述：**審計者說的**——116 條外部審查發現、含審計者自己的誤報，全部在案。

---

## 這棵樹的事實表（可複算）

下表每一個數字都能在乾淨 checkout 上當場數出來，數法寫在第三欄。**數法是內容的一部分**：換一種數法得到別的數字是正常的，但數法必須寫出來，否則讀者無從反駁，也無從複算。

| 類別 | 數量 | 數法（在倉庫根執行） |
|---|---:|---|
| 編譯期事實鍵 | **1,178** | `Services/FactKeyCatalog.cs` 的 `Keys` 成員數 |
| 事實域（鍵的第一段前綴） | **157** | 1,178 把鍵各自取第一段後去重 |
| 動態鍵家族 | **69** | `Services/FactKeyDynamicCatalog.cs` 的 `Families` 成員數 |
| 一鍵域／≤3 鍵域／≥20 鍵域 | 34／67／12 | 以域分組後計數 |
| 可到達畫面（`PageRegistry`） | **65** | `Nav/PageRegistry.cs` 的 `Key =` 條目數 |
| XAML 檔（含對話框與控制項） | **82** | `ls Views/*.xaml` |
| 全站說明條目（圓圈問號） | **288** | `Nav/HelpCatalog.cs` 的 `Entries` 成員數 |
| 服務檔 | **673** | `find Services -name '*.cs' \| wc -l` |
| 全域 C# 檔 | **1,587** | 走訪所有 `*.cs`，排除 `obj/`、`bin/`、`work/`、`publish-*/`、`.git/` |
| 版本紀錄筆數 | **124** | `Nav/ChangelogCatalog.cs` 的 `Entries` 成員數 |
| 專案測試基線 | **7,884** | `Tests/TestSuiteBaseline.cs` 的 `ProjectTests`（`dotnet test` 的總數） |
| CLI 指令（`Execute` case 標籤） | **40** | `grep -c 'case "' XinSpectCli/Program.cs`（指令書詳解 39 條，見 `XinSpectCli/CommandBook.cs`） |
| 內核組件 | **4** | WinRing0（借）／PawnIO（未接入）／BlueSquadron（防護・毛坯）／XsRegProbe（白名單唯讀探測） |
| 外部審查發現 | **116** | `docs/審查報告-v3.17.md` 的編目條目（v3.18 增量審查另計 111 條編目，見同目錄） |

**這張表不能證明什麼。** 1,178 把鍵不是 1,178 個正確答案；116 條發現也不是 116 個皆真的斷言——外部審查約有一到兩成誤報率，每條都帶 `檔案:行號` 供按圖索驥，偽者請撕。

---

## 修正申報總表（本版與 v3.17 百科底稿的全部差異來源）

> 完整 116 條見[審查報告 v3.17](docs/審查報告-v3.17.md)；此處列 P0／P1 全量與 P2 要點。狀態三態：**已修復（本樹）**／**待修復**／**需真機驗證**。沒有第四種狀態——「已修復但沒測過」必須落在「需真機驗證」，不許冒充前者。狀態以修復 commit 為準（截至 2026-11-07 的 main）。

### P0 違反自家承諾（13 條）

| # | 位置 | 問題 | 狀態 |
|---|---|---|---|
| P0-1 | `XinSpectCli/Program.cs` | CLI 在「唯讀模式」橫幅下靜默 `sc.exe delete` 刪除系統服務 | **已修復（本樹）**——改唯讀診斷 `DiagnoseRing0Service`，修復動作改顯式 `--repair-ring0` |
| P0-2 | `README.md` 規模表 | 規模表兩格過期、守門應紅燈 | **已修復（本版規模表即證據）** |
| P0-3 | `Services/AiService.cs` | 代理模式工具結果繞過遮蔽，序號/MAC 出國 | **已修復（本樹）**——工具結果過 `AiRedactor`（`AiAgentRedactToolResults` 預設開） |
| P0-4 | `Services/AiService.cs` | 送出預覽對代理模式為假 | **已修復（本樹）**——`AiAgentPreviewToolResults` 逐輪暫停預覽（預設關） |
| P0-5 | `README.md` §7.5 | 「唯一例外是語料上傳」——實際 opt-in 網路路徑 5 條 | **已修復（本樹）**——三份 README 改五條，語料上傳通路本就未實作、不再寫成例外 |
| P0-6 | `README.md` 深測門檻 | p99≥100 樣本、削波連續性：成文門檻代碼中不存在 | 待修復 |
| P0-7 | `XinSpectCli/Tui.cs` | 進度條失敗也印 100% | **已修復（本樹）**——`SpinnerStop(ok)` 誠實中斷列 |
| P0-8 | `README.md:703` | 「17 個寫入呼叫點」不可複算 | **已修復（本版）**——修正版不再宣稱不可複算的數字；可複算數法列為總藍圖卷二驗證閘 |
| P0-9 | `cloudflare/ai-proxy/worker.js` | 「只收三個欄位」與存 country 矛盾 | **已修復（本樹）**——註解改四個欄位（含 cf-ipcountry） |
| P0-10 | `BlueSquadron/Driver/BlueSquadronDriver.c` | 部分失敗仍回 STATUS_SUCCESS | 部分修復——GET_CAPS 能力旗標已加，usermode 接線待做 |
| P0-11 | `Services/CeilingService.cs` | 0x613 讀取失敗顯示為健康 | 待修復 |
| P0-12 | `BlueSquadron/PolicyEngine.cs` | 「阻擋」模式無執行路徑（三重斷鏈） | 待修復 |
| P0-13 | `BlueSquadron/EtwWatcher.cs` | BYOVD 清單 5 條 vs 宣稱完整防護 | 待修復 |
| P0-14 | `BlueSquadron/DriverManager.cs` | 「DeviceIoControl 通訊」宣稱無 P/Invoke——五個 IOCTL 出貨端全不可達 | 待修復 |
| P0-15 | `BlueSquadron/Driver/Callbacks.c` | g_BlockList 唯寫、登錄檔回呼零事件——防護宣稱無閉環 | 待修復 |
| P0-16 | `Services/AiService.cs` | 主動診斷一次性 opt-in 後自動觸發，違反「逐次」宣稱 | 部分修復——README 已明示「半自動」；行為改逐次確認待做 |
| P0-17 | 發佈矩陣 | SHA256SUMS.txt 從發佈無聲消失 | **已修復（本樹）**——release.ps1 恢復生成＋上傳，v3.17 Release 已補上 |

### P1 崩潰／掛死（9 條）

| # | 位置 | 問題 | 狀態 |
|---|---|---|---|
| P1-1 | `Services/DeepBench/Interop/D3D11Native.cs` | vtable slot 50/49 自相矛盾 → 0xC0000005 | 部分修復——ClearRenderTargetView 50→49 已修；CopyResource/UpdateSubresource 索引三方矛盾待整表重核 |
| P1-2 | `Services/StorageSmartService.cs` | OVERLAPPED hEvent 偏移錯 → NVMe 健康資料系統性丟失 | **已修復（本樹・需真機驗證）**——offset 8→24 |
| P1-3 | `Services/StorageSmartService.cs` | CancelIoEx 後 pinning 解除 → 堆積毀損風險 | **已修復（本樹）**——緩衝改 `AllocHGlobal` 非託管，毀損面消失 |
| P1-4 | `XinSpectCli/Program.cs` | `top` 無 Ctrl+C 處理，承諾不實；管線掛死 | **已修復（本樹）**——`TreatControlCAsInput`＋管線拒絕＋退出碼三態 |
| P1-5 | `XinSpectCli/Program.cs` 五處 | Spinner 無 try/finally，游標永久隱藏 | 部分修復——facts 點已包；USB／PCI／驅動／bsod／pci-scan 點待補 |
| P1-6 | `Views/AiView.xaml.cs` | 剪貼簿 API 未包 try，async void 擊落行程 | **已修復（本樹）**——三處包 try |
| P1-7 | `BlueSquadron/Driver/MsrSnapshot.c` | 卸載期 DPC 在飛，缺 KeFlushQueuedDpcs | **已修復（本樹）**——整檔重寫（per-CPU 基線＋釘核 DPC＋KeFlushQueuedDpcs） |
| P1-8 | `XinSpect.Decoders/EfiSigList.cs` | uint 直轉 int 負索引 | **已修復（本樹）**——邊界檢查後才轉型 |
| P1-9 | `XinSpectCli/Program.cs` | OutputEncoding 在 try 外，啟動即崩點 | **已修復（本樹）**——編碼設定包 try |

### P2 錯誤判決／安全（20 條，要點）

已修復：MSR 快照 per-CPU 基線（整檔重寫）、LSASS DUPLICATE 裸奔（已補）、apply 靜默零寫入硬體（`U32`/`Dbl` 轉可空）、SMART 用 raw 值評門檻（改 normalized 現值）、bcdedit 無絕對路徑＋DEP fail-open（改 Unknown）、DNS 介面名稱命令注入（SafeName 全面封口）、**即時功耗無能量計驗證（1.3W 病灶——RaplEnergyGate 上線，CLI/GUI 兩端接閘）**。待修復：SDK 路徑 DLL 注入面（XtuCore）、IOMMU 事實兩份漂移（WmiSecurityReader）、SpecOverride／CFG fail-open、baseline.json 無完整性保護。全表見[審查報告 v3.17](docs/審查報告-v3.17.md)。

---

## 這棵樹上有什麼（四個入口）

### 入口一：GUI（65 個可到達畫面）
驗機、監控、超頻、韌體安全稽核、深測中心 43 項、垃圾清理、藍屏分析、DNS/Hosts/右鍵選單……逐頁解剖見百科第 3 部（[docs/百科-v3.17.md](docs/百科-v3.17.md)）。**修正版新增申報**：65 頁的 code-behind（86 檔 8,237 行）已逐行審查，View 層三態紀律零違規；已知問題（三個死頁、PortUsage 凍結、OverclockView 即發即忘）見修正申報。主程式以 asInvoker（一般權限）啟動，深層功能經提權會話按需開啟——非提權時深層頁面顯示「—」，這是六態降級設計，不是壞掉。

### 入口二：xsc 終端機（CLI，40 指令）
`facts` 採集全機事實 → `page`/`domain`/`fact` 查詢 → `msr`/`pci` 直讀 → `ipc` 守護進程 → `compliance`/`merkle` 證據導出 → `book` 離線百科。**修正版新增申報**：CLI 四檔 2,562 行逐行審查；P0-1 與 P1-4/5/9 在此層——CLI 曾是本版最需要修的層，P0-1/P1-4/P1-9 已修。

### 入口三：特權層（四枚內核組件＋橋接）
- **XsRegProbe**：白名單唯讀 MSR/MMIO 探測驅動——全案審查的唯一「優秀」級，229 行在可窮舉證明範圍內；SDDL 已開 Users 唯讀（按需提權會話的地基）；
- **WinRing0**（借自 LHM 0.9.4）：萬能鑰匙，裝置無呼叫者校驗，暴露窗口與退役順序（§5.7）在案；
- **BlueSquadron**：防護驅動，毛坯狀態如實申報（映像載入只記不算、阻擋清單收了不執行）；
- **PawnIO**：8-30 因「成功卻回零」退回；修正版的後端鏈設計（`KernelBackends` 補丁包）讓它**用自證證據決定何時歸隊**。

### 入口四：審計層（新增）
修正版首次把審查本身納入文檔：116 條的完整清單（[docs/審查報告-v3.17.md](docs/審查報告-v3.17.md)）、審計者誤報的申報、修復分叉史（D3D11 slot／IOMMU／netsh 三案：為什麼修一份漏一份）、以及**守門紅燈事件**（規模表兩格過期被外部審計先於 CI 發現——守門測試的存在理由與失敗方式，同時入案）。v3.18 增量審查（111 條編目＋夜間自報去重）見 [docs/審查報告-v3.18.md](docs/審查報告-v3.18.md)。

---

## 未來構想（三條基礎設施路徑）

1. **硬件審計標準層**：`xsc facts` 的結構化輸出若被腳本與代理依賴，鍵目錄（編譯期釘死＋版本化契約）即為 de facto 標準——Nmap/Lynis 的路。
2. **AI 時代的硬件事實層**：AI 代理管機需要「帶出處與可信度的硬件真相」，六態＋四級可信度＋鍵目錄正是代理消費所需的元數據格式——這個生態位現在無人佔。
3. **二手硬件的 CARFAX**：三源對帳＋Merkle 證據鏈＝硬件版車輛歷史報告生成器；需要交易平台的 BD 接入，不是代碼。

**共同前置條件**：無聊。基礎設施需要十年不變的鍵名與讓依賴者睡得著的穩定——本專案 2.5 個月 123 版的迭代速度是工具的美德、基礎設施的毒藥。分岔口會來，屆時二選一。

## 目前困難（六條，如實）

1. **簽章**：EV 憑證＋WHQL 是錢與流程，代碼解不了；解決前「從 GitHub 直連下載」是使用者的必要紀律；
2. **bus factor**：單人＋AI 代理的品質依賴守門人本人；HANDOFF 制度是對沖，不是解藥；
3. **文檔稅**：五重表示（代碼/鍵目錄/HelpCatalog/百科/README）靠守門測試釘住——而守門測試剛剛被證明也會紅燈失守；長期解法是單一來源生成；
4. **硬體多樣性**：1,178 鍵在長尾硬體上的表現只能靠使用者數量堆，沒有工程捷徑；
5. **修復分叉**：同一修正只修一份拷貝的模式已三度出現；解法是把修復下沉到單一數據源（引擎拆分＋類型化誠實）；
6. **AI 棧的遮蔽缺口**：代理模式的資料出境半徑比宣稱大（P0-3/4/5 已修復遮蔽與預覽；剩主動診斷的逐次確認，見 P0-16）。

---

## AI 與瀏覽器的界線（事實摘要）

這一版的 AI／瀏覽器加固寫進了程式，界線的**硬事實**如下（守門釘住三份 README 都要寫到）：

- **API 金鑰以 Windows DPAPI 加密後才寫進 settings.json**，解不開就是「沒有金鑰＋原因」，設定頁顯示磁碟上實際的存放方式；
- **內建瀏覽器只開 http／https**：`file://`、`javascript:`、`data:`、`about:` 等一律擋下，左下角說明被擋的是什麼；
- **瀏覽器下載落在 `%USERPROFILE%\XinSpect\Downloads`**，不落系統下載資料夾；
- **暫時性失敗重試**（429／502／503／504／408，依端點的 `Retry-After`、有限次），且只在還沒吐出任何字之前重試；
- **開發者工具預設關閉**，只在明確設定 `XINSPECT_BROWSER_DEVTOOLS=1` 後才開啟。

## 授權

雙授權，兩條路二選一、功能相同：**AGPL-3.0-or-later** 免費、可商業使用，但散布修改版或以網路服務提供時須以相同授權開源；**商業授權**（見 `COMMERCIAL-LICENSE.md`）付費，供閉源整合、隨硬體出貨或組織政策禁用 copyleft 的情況，不觸發原始碼公開義務。第三人元件（Intel XTU SDK／LibreHardwareMonitor／TraceEvent／NAudio／WebView2 等）維持各自授權，`THIRD-PARTY-NOTICES.md` 逐欄記載其授權與來源。

## 下載與檔案大小

| 檔案 | 大小 | 說明 |
|---|---|---|
| [XinSpect.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/XinSpect.exe) | 37,876,523 bytes | 主程式。藍色中隊守護進程已內建；「強控台」按鈕橋接 xsc 終端機 |
| [XinSpectConsole.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/XinSpectConsole.exe) | 100,344,183 bytes | 曦覽終端機（CLI）。可獨立執行，也被主程式強控台喚起；自含 .NET 10 執行階段故體積較大（見下方說明） |
| [BlueSquadronBridge.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/BlueSquadronBridge.exe) | 6,502,948 bytes | 獨立守護進程。要脫離主程式單獨跑防護時使用 |
| [XinSpect-full.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/XinSpect-full.exe) | 210,642,770 bytes | 主程式・帶環境版：.NET 10 Desktop Runtime 一併打包，免裝執行階段（功能與 XinSpect.exe 相同） |
| [XinSpectConsole-nort.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/XinSpectConsole-nort.exe) | 65,908,376 bytes | 曦覽終端機・Windows 11／Server 2025・不帶環境：需已裝 .NET 10 Desktop Runtime |
| [XinSpectConsole-legacy-nort.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/XinSpectConsole-legacy-nort.exe) | 65,908,418 bytes | 曦覽終端機・Windows 10／2016/2019/2022・不帶環境：純文字界面，需已裝 .NET 10 Desktop Runtime |
| [XinSpectDeploy.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/XinSpectDeploy.exe) | 25,600 bytes | 一鍵安裝器。第一層選裝什麼：A 裝主程式（GUI）、B 裝曦覽終端機；第二層 1/2/3 選下載來源（GitHub／Drive／加速節點）；下載時顯示進度條、速度與剩餘時間 |
| [XsRegProbe.sys](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/XsRegProbe.sys) | 24,064 bytes | XsRegProbe 內核探測驅動（白名單唯讀 MSR／MMIO，全案審查唯一「優秀」級）。未微軟簽章：主程式以提權會話安裝到 `ProgramData\XinSpect\Driver` 並建服務；手動載入需測試簽章環境（testsigning） |
| [XinSpectConsole-legacy.exe](https://github.com/Xinglanclever/XinSpect/releases/download/v3.18/XinSpectConsole-legacy.exe) | 100,340,129 bytes | 曦覽終端機（CLI）**純文字相容版**：給 Windows 10／Windows Server 2016、2019、2022 的傳統主控台——無色彩、無框線定位，其餘功能（facts／book／help）相同 |

逐資產 SHA-256 見 Release 頁的 `SHA256SUMS.txt`（防傳輸損毀；能替換整個 Release 的攻擊者防不了——不可否認性需簽名，列排期）。

> **上表的位元組數為本版（v3.18）實際發佈的檔案大小**；請以 Release 頁面列出的檔案為準。

安裝器（`XinSpectDeploy.exe`）第一層先選裝什麼：**A** 是 XinSpect.exe 主程式（全自動部署主程式＋守護進程；守護進程自 3.13 起預設不啟用，要自己到安全頁開）、**B** 是曦覽終端機（單檔下載一步到位）；版本都分兩維：**帶環境**（self-contained，免裝 .NET）與**不帶環境**（framework-dependent，機器要已裝 .NET 10 Desktop Runtime）。下載完成後一律檢查 PE 標頭與大小門檻，抓到 HTML（分享頁、限流頁）會被擋下、刪檔並說出內容像什麼。注意：走第三方加速節點的位元組只能驗 PE 標頭，驗不了發佈者簽章。

需求：Windows 10 1903 以上／Windows 11、x64、.NET 10 Desktop Runtime（單檔版不含執行階段，需另裝）。本程式預設以一般權限啟動（asInvoker，最小特權——WebView2、AI 等不該吃管理員令牌）；想取得感測器、MSR 直讀與超頻頁面的完整功能，請「以系統管理員身分執行」——不提權不是壞掉，只是那些頁面會整片顯示「—」（原因見第 0 部的六態），這正是六態降級設計存在的理由。

## 版本沿革（近期）

- **v3.18**（2026-11-07）：審計落實版：116 條修復＋功耗誠實閘門＋按需提權（本版 11 項）
- **v3.17**（2026-11-05）：終端機定稿：Claude Code 式界面、永不崩 REPL、百科嵌入（本版 10 項）
- **v3.16**（2026-11-04）：強控台指令控制台＋rf.* 帶外事實域＋主題記憶修正（本版 7 項）
- **v3.15**（2026-11-03）：主題記憶修正＋離線 Redfish 目錄＋迷你 HUD 重新喚醒（本版 8 項）
- **v3.14**（2026-11-03）：企業證據出口：合規、Merkle、推測、CI／SIEM 與族群基線（本版 10 項）

完整 124 筆沿革見 [docs/百科-v3.17.md](docs/百科-v3.17.md) 第 11 部。

---

## 誠實申報（這份文件的邊界）

- 本版是修正版的**第一塊磚**（約 1.4 萬字元），佔繁中 104 萬字計畫目標量 1.3%。逐章擴寫按[總藍圖](docs/百科-修正版-總藍圖.md)流水線推進，每章核實後入庫；
- 狀態表反映的是 2026-11-07 main 的修復 commit；「已修復」不代表「已驗證」——標「需真機驗證」的條目以實機覆核為準；
- 外部審查由 AI 代理執行，誤報率約一至兩成；本文件沿用同一個立場：**讀者可按圖索驥，偽者請撕**；
- 本文件未回答：性能對比、推薦配置、購買建議——判讀留給人。

---

**版本：v3.17 修正版（2026-11-07 審計後）· 這棵樹的第一次被完整審計後的第一次自我陳述。**
