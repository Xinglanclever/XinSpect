# 2026-11-01 修復紀錄：網路頁三處誤判、核心熱力圖少一半

`v3.11`（Swiss 測試版）資產就地更新，tag `d8305e4`、`XinSpect.exe` 37,348,651 bytes、
sha256 `20631a38…`。這一輪修掉四個「畫面上看得到、但講錯話」的缺陷，每一個都有守門測試擋回去。

---

## 一、總覽

| # | 症狀 | 根因 | 誰會撞到 |
|---|---|---|---|
| 1 | 網路頁「網卡落差」整組空白，`diag.log` 每 40 秒刷一條 | WQL 選了 `MSFT_NetAdapter` 不存在的欄位，整條查詢無效 | 每一台機器 |
| 2 | 無線網卡被標成「乙太網路 802.3」 | `Win32_NetworkAdapter.AdapterType` 對 Wi-Fi 也回 Ethernet | 有 Wi-Fi 的機器 |
| 3 | 「額定速度」印的其實是目前協商速率（10G 卡顯示 100 Mbps） | 值是 `Speed`（當前值），標籤寫成「額定」 | 每一台機器 |
| 4 | 核心熱力圖 18 核只亮 9 格，且亮的那 9 格溫度是兩顆核心取最大 | 逐核清單已是一核一列，卻又乘 2 折疊 | 多核機器 |

---

## 二、逐條

### 1. 網卡落差整組讀不到

**現象**：`%APPDATA%\XinSpect\diag.log` 從 10/31 起持續出現
`網卡基本資料查詢　ManagementException：查詢無效　後果：網卡落差整組讀不到`，每 40 秒一條。

**根因**：`Services/NicLinkFactsService.cs` 的查詢選了三個 `MSFT_NetAdapter` 沒有的欄位：

- `LinkSpeed` —— 速度其實叫 `Speed`（bit/s）
- `MediaType` —— 這個類別沒有這欄
- `MediaConnectionState` —— 正確是 `MediaConnectState`，而且是 UInt32（0 未知／1 已連線／2 未連線），
  舊程式還拿它跟字串 `"Connected"` 比對

WQL 只要有一個欄位不存在，就是**整條查詢失敗**，不是少一欄 —— 整個網卡段落歸零，畫面上是一片空白。

**修法**：`BaseQuery`（`Services/NicLinkFactsService.cs:40`）只查四個真的存在的欄位
（`Name`、`InterfaceDescription`、`Speed`、`MediaConnectState`），連線狀態用枚舉值比對。
媒體類型如實留空，由判讀層印「未知」，不從介面名稱猜。

**證據**：
- 直接用同一條 WQL 驗證：原句 `查詢無效`，去掉錯誤欄位後正常回資料。
- 修好後實跑，`diag.log` 不再出現這條（前一版每 40 秒一條）。

### 2. 無線網卡被標成「乙太網路 802.3」

**現象**：Intel Wireless-AC 9260 那一張卡，類型欄寫「乙太網路 802.3」。

**根因**：值來自 `Win32_NetworkAdapter.AdapterType`，它對無線網卡也回 `Ethernet 802.3`（本機實測）。

**修法**：改用 `System.Net.NetworkInformation` 依實體位址對回真正的介面類型
（`Services/SystemInfoService.cs` 的 `GetNetworkAdapters`），對不到才退回 WMI 字串；
型別對照表複用 `NetworkService.TypeZh`（`Services/NetworkService.cs:214`）。

**證據**：修好後實跑，該欄變成「無線網路」；有線兩張仍是「乙太網路」。

### 3. 「額定速度」印的是目前速率

**現象**：Marvell 10G 那一格顯示 `100 Mbps`。

**根因**：`Win32_NetworkAdapter.Speed` 是**目前**協商到的頻寬，不是額定能力；
而 `MSFT_NetAdapter.MaxSpeed`（真正的額定值）在本機是空的，讀不到。
標籤與數值講的是兩件事，讀者只會得到「這張 10G 卡只有 100M」的錯誤結論。

**修法**：標籤改成「目前速度」，UI（`Views/NetworkView.xaml:66`）與匯出報告表頭
（`Services/ReportService.cs:571`）同步。**不編一個額定值**。

> 順帶留下一個真實事實：那張卡現在真的只跑 100 Mbps。要不要查（線材／對端交換器／驅動鎖速）
> 是使用者的事，但工具不該把「目前」說成「額定」。

### 4. 核心熱力圖 18 核只亮 9 格

**現象**：i9-7980XE（18 核／36 執行緒），熱力圖 18 格只有前 9 格有溫度，後 9 格空白。

**根因**（兩層，缺一不會壞）：

1. `SensorService.CoreIndex` 的正則是 `#(\d+)`，只命中名稱裡**第一個**編號 ——
   `"CPU Core #5 Thread #2"` 會被歸回**核心 5**。所以 `CpuCores` 本來就是「一核一列」（18 筆）。
2. `CoreTempMapService.Tick()` 仍假設那是「逐邏輯執行緒一列」，一律乘 `_threadsPerCore`（=2）折疊。

於是：

- 第 0～8 格吃掉第 2p、2p+1 列 → **每格是兩顆不同核心取最高溫**，那格溫度不是它標的那顆核心的
- 第 9～17 格起點是 18，已超出筆數 → **一格都沒有值**

**修法**：`CoreTempMapService.Apply`（`Services/CoreTempMapService.cs:105`）改成
**標籤對位優先**（兩邊標籤同源，都是「核心 #N」），一核一列不折疊；標籤對不上才退回折疊，
而且比率由**實際筆數**推導（筆數／格數），不再寫死 2。
混合架構（P-core 有 SMT、E-core 沒有）那個長年 TODO 也一併消失。

**證據**：
- 新增測試 `十八核的逐核列要一格對一顆_不能折疊掉一半`（`Tests/CoreHeatmapTests.cs:66`）：
  18 格 × 18 列，每格必須拿到自己那顆核心的溫度與負載。
- **突變檢查**：把對位改回舊行為（關掉標籤對位 ＋ 比率改回 `threadsPerCore`）→ 測試**紅**；
  改回來才綠。所以這條不是裝飾。（單獨改任一邊不會紅，第一次的突變位置不夠，補齊才咬得住。）
- **跨來源對帳**：用同一支 API `GetLogicalProcessorInformationEx` 量 Windows 這一側 ——
  `RelationAll` 與 `RelationProcessorCore` **都是 18 顆核心**。所以格子數一直是 18，
  問題在「填了幾格」，不是「畫幾格」。

### 5. 順手修的兩個小地方

- **連線狀態讀不到時不再寫成「未連線」**：`MediaConnectState=0`（未知）改印「連線狀態未回報」
  （`XinSpect.Decoders/NicLinkGap.cs:105`）。把不知道講成未連線，會叫人去查線材 —— 查錯方向比承認不知道更糟。
- **卡片容器的無障礙名稱**：`NicInfo` 覆寫 `ToString()`（`Models/DetailModels.cs:199`），
  否則螢幕閱讀器會唸出型別名 `XinSpect.NicInfo`。

---

## 三、這次怎麼驗的（方法本身也改了）

**不用截圖。** 這台機器是深色主題＋小字，`PrintWindow` 與 `CopyFromScreen` 拍出來的畫面
平均亮度只有 26/255，用像素判讀時我兩次把內容「讀」成完全不相干的東西（還讀出一張實驗室膠體圖）。
那是我的判讀問題，不是截圖壞了 —— 但結論一樣：**像素不能當證據**。

**改用 UI Automation 讀 WPF 視覺樹的文字節點**（`AutomationElement.FromHandle` ＋ `RawViewWalker`）。
拿到的是純字串，沒有判讀空間。這一輪的「乙太網路／無線網路」「目前速度 100 Mbps」
「+3.3V＝3.344 V」「Vcore＝1.304 V」「風扇 1546 RPM」「GPU 1099 RPM」都是這樣讀出來的。

**三方對帳**：UI 上的值 ↔ WMI／CIM 實測 ↔ 程式碼裡的判定規則。任一對不上就判定是我讀錯，重新再讀。

**突變檢查**：新增的守門測試要能被「改回舊行為」弄紅，否則只是裝飾。

---

## 四、這次的發佈

| 項目 | 值 |
|---|---|
| 分支 | `experiment/swiss-helvetica` |
| tag | `v3.11` → `d8305e4`（force，pre-release 資產就地取代） |
| `XinSpect.exe` | 37,348,651 bytes，sha256 `20631a38…` |
| 線上資產 | 下載回來比對，雜湊與本機建置完全相同 |
| `verify-release -Tag v3.11` | 全綠（六處版號 ＋ 四件資產大小對 README） |
| `releases/latest` | 仍是 `v3.10`（安裝器鏡像沒動） |
| 測試 | 守門 101/101 綠；案例數 7,726 → **7,727** |
| 文檔 | README×3／CONTRIBUTING／PR 範本／ci.yml／TestSuiteBaseline／附錄 H／百科 全部同步 |

---

## 五、未決

1. **`處理器` 頁在 UI Automation 樹裡只露出側欄**（約 96 個節點），兩版都一樣；
   `網路`／`感測器` 頁正常（268／1763 節點）。所以熱力圖「18 格都亮」只能靠眼睛確認，
   程式面已證明到測試與 Windows API 那一層。下一輪要先解決「為什麼那一頁抓不到節點」。
2. **殘留行程**：這一輪啟動過的實例被 `Stop-Process` 之後，有幾個會卡在不可中斷狀態
   —— 視窗已消失、行程還在（各 300–550 MB），`taskkill` 回「沒有正在執行的工作例項」但仍列在清單裡，
   還會鎖住 `bin\` 下的 exe。事件記錄沒有 `.NET Runtime`／`Application Error`，所以不是崩潰。
   暫時的繞法是換組態（Release）、換輸出目錄、或直接換 publish 資料夾。
3. **全套 7,727 條沒跑**：`UiSmokeTests`／`Sensor*` 這幾組在這台機器會把 `testhost` 卡死
   （然後吃 GPU 與 10 GB 記憶體、殺不掉），所以只跑守門與受影響的測試組。

---

## 六、給下一個人的地雷清單

- **WQL／WMI 的欄位名要逐字對過 schema。** 錯一個欄位不是「少一欄」，是整條查詢失敗、整段歸零，
  而且看起來像「這台機器沒有這個東西」。這個坑同一個檔案裡已經記過一次註解，還是復發了 ——
  註解擋不住，測試才擋得住。
- **`#(\d+)` 這種正則在多執行緒名稱上只會取到第一個數字。** 任何「每 N 條執行緒折一片」的假設
  都要先確認上游清單到底是不是逐執行緒。
- **標籤要跟量到的東西一致。** 「額定」不是「目前」，讀不到額定值就承認讀不到。
- **未知 ≠ 未連線。** 三態在這一頁也要守住。
- **不要用截圖當驗收證據**（至少在這台機器上）；用 UI Automation 讀文字節點。
