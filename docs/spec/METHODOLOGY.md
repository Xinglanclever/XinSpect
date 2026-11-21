# XinSpect 量測方法學（公開版）

- **狀態**：已實作的契約成文。本文件描述的是程式**實際遵守**的規則——有機器檢查對帳（`Tests/` 與 `Tests/PublicSpecTests.cs`），文件與程式漂移會紅燈。

## 1. 誠實契約（最高約束）

1. **六態**（2.36 起由五態擴充）：每筆事實帶 `availability`——`Present`（讀到且確認）／
   `Unknown`（**有值但未確認**：快取殘留、來源可疑、多來源尚未仲裁）／
   `NotApplicable`（本機無此硬體，環境事實）／`NotSupported`（平台或本版不提供這條路徑）／
   `InsufficientPrivilege`（權限不足，提權後可得）／`ReadError`（試過但失敗且原因未確認）。
   **讀不到就說讀不到，不以 0、0xFF、典型值或舊值填補；有值但不敢背書的一律標 `Unknown`，不冒充 `Present`。**

   **格上的強弱序**（由強到弱）：`Present` ＞ `Unknown` ＞ `NotApplicable` ＞ `NotSupported` ＞
   `InsufficientPrivilege` ＞ `ReadError`。**衍生事實的可用性取來源中最弱者**（合取傳播）——
   三個來源都 `Present` 才是 `Present`，只要有一個讀不到，結論就不能宣稱可用。
   實作：`Services/FactStateLattice.cs`（`Combine`／`IsTrustworthy`），測試：`Tests/FactStateLatticeTests.cs`。
   這條規則存在的理由很單純：**把「讀不到」當 0 帶進算式，是所有誠實工具最常見的塌陷方式，
   而它不會產生任何錯誤訊息。**
2. **非自造驗證來源**：每筆事實標明來源（暫存器位址＋規格出處、WMI 類別、API 名稱）。
   沒有出處的值不出。
3. **SpecRef**：暫存器解碼器的方法必須附規格引用（文件、章節、暫存器、位元位置）；
   由反射機器檢查（`SpecRefRegistry`）——新解碼方法而未附引用會直接紅燈。
4. **可信度是標示不是保證**：`Unknown`／`Reported`（系統自己記的）／`Derived`（由其他事實推導）／
   `Measured`（本工具量到）／`Speculative`（推測，非量測；只出現在 `infer.*`，值帶「【推測】」，
   不進合規判定與 Merkle）。
5. **不解碼垃圾**：格式異常、長度不足、佈局歧義一律如實拒解（回 null／三態），不猜。

## 2. 量測路徑分層

| 層 | 通路 | 可信度標示慣例 |
|---|---|---|
| 特權層 | WinRing0（MSR/PCI/實體記憶體）／XsRegProbe（白名單備援驅動） | Measured |
| 感測層 | SMBus（SPD/TSOD）、SuperIO、CMOS | Measured |
| 系統層 | WMI／登錄檔／EventLog／LSA／wlanapi／psapi | Reported |
| 韌體仲介 | Windows TBS（TPM）、UEFI 變數 API | Reported（Windows 代轉） |
| 推導 | 交叉對帳規則引擎（26 條，外部化於 `Rules/builtin.json`） | Derived |

## 3. 交叉對帳

- 同一事實的多個獨立來源互相印證（例：微碼修訂版——CPU 自己說的 MSR 0x8B vs Windows 記的登錄檔）。
- 規則結論三種：**一致**（綠）／**矛盾**（紅，具名列輸入）／**無法驗證**（灰，任一輸入缺席）。
- **雙向矛盾拆方向性規則**：當兩側都可能缺席，單條規則無法兩向涵蓋——拆兩條，各宣告保證 Present 的一側。
- 判決不下「你安全／不安全」的結論，只指出事實之間的關係。

## 4. 突變測試

純解碼器抽 `XinSpect.Decoders` 類別庫後以 dotnet-stryker 對帳（門檻 low=70）；
資料型知識表（SuperIoKnowledge／PciKnowledge）刻意排除——逐條字串斷言＝快照重複。

## 5. 效能預算

冷啟動目標 ≤3 秒；舊紀錄的 2.7 秒是當時環境的歷史結果，不代表目前版本或其他硬體。現況須依限定版本、硬體與採集範圍重新量測。其餘門檻見 `Models/PerfBudget.cs`；自我遙測預設關閉、匿名只記數字與時間。


## 記憶體快照的量與來源（R380）

`memory.current` 保留時序各欄實際來源，`memory.rate` 保留速率實際來源；兩者可來自不同通路。
安全且已支援的控制器目前值、Windows WMI 設定值、CPU-Z 目前值依既有優先序補值，
SPD JEDEC 是模組能力基準，不能填補成已確認的控制器目前值。
Windows `ConfiguredClockSpeed` 的速率標為設定值及 `Reported`，不是即時量測。
僅有 SPD JEDEC 時，兩把相容鍵仍保留基準／標稱文字，但 `availability=Unknown`、
`NumericValue=null`，名稱與理由明列尚未取得目前值；不供 Present 條件採信。
目前時序的 CL/tRCD/tRP/tRAS 任一缺值，摘要亦為 Unknown。
來源六態逐量傳播；新中繼資料未填寫的舊物件預設 Unknown，不自動升為 Present。
此修正只改快照投影；不新增硬體讀取或控制器能力，不宣稱來源更新時間已知。

## CLI 記憶體設定速率發布（R386）

CLI 與桌面 usermode 證據採集共用既有 `Win32_PhysicalMemory` 混插摘要查詢的一次列舉，
新增讀取 `ConfiguredClockSpeed`、`SMBIOSMemoryType` 與 `Capacity` 欄位；沒有新增查詢、
SPD／控制器讀取或 CPU-Z 啟動。舊混插摘要仍按 `Speed` 的標稱值算跨度，不用它填設定速率。
只有完整列舉且每條模組設定速率同為正值時，`memory.rate` 才以 WMI 設定值、Reported／Present 發布。
缺值、混速及空集合為 Unknown；列舉中途失敗不採信已保留部分列，權限／讀取錯誤如實保留。
新批開始即清掉上一批成功值；逾時、取消或被新批取代後不得晚到覆寫。
此路徑共用 R380 純投影，不提供 CL／tRCD／tRP／tRAS，故不產生 `memory.current`。
CLI evidence JSON、Agent Bridge snapshot 及 evidence pack 可消費這把設定速率鍵；
這不等於控制器目前時序、即時量測、完整桌面快照或獨立硬體驗證。
來源更新時間仍未知，`MeasuredAtUtc` 是本程式投影時刻。
