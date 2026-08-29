# 曦覽 XinSpect ・ 後續開發方向彙整（給接手代理 GLM 5.3）

> **這份文件的身分：** 使用者（Xinglanclever）在 2026-08-29 與前一位代理（Claude Opus 4.8）
> 進行了三輪方向討論——「工程方向」、「參數方面的深入」、「更深入」。本文是那三輪的完整彙整。
>
> **這是構想清單，不是工作指令，也不是承諾。** 每一項都還沒有經過 brainstorming／design 階段，
> 沒有任何一項獲得使用者批准開工。
>
> **動手前的硬性前提：**
> 1. 先把 `HANDOFF-1.3.2.md` 裡的三個剩餘步驟做完（pdb 決定 → `git push origin main` → Release v1.3.2）。
>    1.3.2 是使用者明示「一次成型」的單一版本，**在它發佈之前不要往本文任何方向寫程式**。
> 2. 本文任何一項要開工，都必須先向使用者提出並取得同意。使用者已經駁回過兩次選單式提問，
>    **不要重複詢問已經決定的事**，但也不要未經同意就自行開工。
> 3. 本文未納入版控，可視需要刪除。
>
> **不可動搖的專案主軸（讀完本文其餘部分之前先記住）：**
> **誠實。** 讀不到就顯示「—」，絕不以典型值／估計值代替。任何讀值都必須是真的量到的。
> 說明文字不得誇大工具能力。本文第四部把這條原則在各方向上的具體界線列了出來，**那一部分請務必讀。**

---

## 建議執行順序（結論先行）

| 順位 | 項目 | 特權需求 | 風險 | 為什麼排在這裡 |
|---|---|---|---|---|
| 1 | View 繫結煙霧測試 | 無 | 無 | 斷掉「進頁面就閃退」這個反覆發生的老毛病 |
| 2 | 核心到核心延遲矩陣 | 無 | 無 | 純使用者態，一兩天可見成果，熱圖說服力最強 |
| 3 | CPUID 全葉展開 | 無 | 無 | 零相依，資訊密度最高，直接問矽晶片 |
| 4 | 記憶體延遲曲線＋自動推導快取邊界 | 無 | 無 | 實測 vs CPUID 宣稱的對照，最合誠實主軸 |
| 5 | SMBIOS 原始表全解 | 無 | 無 | WMI 只轉譯了其中一小部分 |
| 6 | PCIe 設定空間 ＋ AER 錯誤計數 | 無 | 低 | 「你的插槽／延長線有問題」的鐵證 |
| 7 | S.M.A.R.T. 原始屬性 | 無 | 低 | 把一個百分比換成真實計數 |
| 8 | **ring0 路線決策** | — | — | 第 9 項之後全部依賴它，見第三部第五節 |
| 9 | 有效時脈＋C-state＋降頻原因＋RAPL | ring0 | 中 | 徹底回答「它為什麼不跑滿」 |
| 10 | SPD 直讀 | ring0 | 中 | 可拔掉記憶體頁對外部 CPU-Z 的依賴 |

前七項全部是純使用者態、零硬體風險，**建議在 ring0 決策之前先把它們做完**。

---

# 第一部 ・ 工程方向（第一輪討論）

## 1. 把「頁面載入就閃退」從根上斷掉 ★最推薦的起手式

**問題陳述。** 這個專案至今最常見的失敗模式，就是 XAML 繫結寫錯導致整頁掛掉。已經發生過至少三次：

- `ProgressBar.Value` 綁到唯讀 getter（`RangeBase.ValueProperty` 繼承 `BindsTwoWayByDefault`，
  繫結時就丟例外）→ 超頻頁一進去就閃退。
- code-behind 指派已繫結的 `Topmost` → 摧毀該繫結。
- 根元素上的 `Data="{Binding}"` / `DataContext="{Binding}"` 在 `Host.Content` 延遲載入時不解析。

現有 434 項測試全是純函式測試，**一個 `UserControl` 都沒有被實際建構過**，所以這類錯誤測試抓不到，
只能靠人開起來點一遍——而三支 1.3.2 的新檢測連這一步都還沒做過。

**做法。**

1. 新增一支測試，逐一 `new` 出全部導覽頁的 `UserControl`、`UtilitiesView` 底下每一支工具的
   `UserControl`、以及五個全螢幕檢測視窗（`ScreenTestWindow`／`MouseTestWindow`／
   `KeyboardTestWindow`／`SpeakerTestWindow`／`MotionTestWindow`）。
2. 掛一個 `TraceListener` 到 `PresentationTraceSources.DataBindingSource`，並把
   `PresentationTraceSources.Refresh()` 先叫起來、`Switch.Level = SourceLevels.Warning`。
   只要出現 `BindingExpression path error`、`Cannot convert`、`Cannot find governing FrameworkElement`
   就讓測試失敗，並把訊息原文放進 assert 訊息。
3. 每個 View 建構後叫一次 `Measure`／`Arrange`（給一個假尺寸）逼繫結真的求值，
   否則很多繫結在沒有版面配置時不會被觸發。

**難點。** WPF 需要 STA 執行緒與一個 `Application` 實例才能解析 `StaticResource`：

- xunit 需要 `[STAThread]` 或自訂 `TestFramework`／`SynchronizationContext`。
- 必須先建立 `Application` 並把 `Themes/Theme.xaml` 併進 `Application.Current.Resources`，
  否則所有 `{StaticResource Card}` 之類都會找不到。
- 現行慣例是**測試絕不建構 `MainViewModel`**（會拉起整組服務、真的去讀硬體）。
  這一項要打破那條慣例，所以要嘛做一個假的 DataContext（提供相同屬性形狀），
  要嘛把這支測試獨立成「UI 煙霧測試」專案並明確標註它會拉起服務。
  **建議走假 DataContext，不要在測試裡開 LibreHardwareMonitor。**

**價值。** 做完之後，未來每一次改 XAML 都自動有回歸保護。這是唯一一項「防止過去的錯誤重演」
而不是「增加新功能」的工作，所以排第一。

## 2. S.M.A.R.T. 從「剩餘壽命」走到真實原始屬性

**問題陳述。** 儲存頁現在只給一個「剩餘壽命百分比」。這是全程式最容易被質疑
「你這個數字怎麼算出來的」的地方——而如果它是推算的，就已經違反誠實原則。

**做法。**

- **NVMe**：`DeviceIoControl` + `IOCTL_STORAGE_QUERY_PROPERTY`，
  `PropertyId = StorageDeviceProtocolSpecificProperty`，
  `ProtocolType = ProtocolTypeNvme`，`DataType = NVMeDataTypeLogPage`，`ProtocolDataRequestValue = 0x02`
  → 取回 512 bytes 的 SMART / Health Information log。可解出：
  Composite Temperature、Available Spare、**Percentage Used**、Data Units Read/Written
  （單位是 1000×512 bytes）、Host Read/Write Commands、Power Cycles、Power On Hours、
  **Unsafe Shutdowns**、Media and Data Integrity Errors、Error Information Log Entries、
  兩個溫度感測器與 Thermal Management 累計時間。
  另外 `DataType = NVMeDataTypeIdentify`、`RequestValue = 1` 取 Identify Controller
  → 型號、序號、韌體、Max Data Transfer Size、命名空間數、支援的功能位元。
- **SATA**：`ATA_PASS_THROUGH_EX` 下 `SMART READ DATA`（0xB0/0xD0）取 30 筆屬性表，
  每筆給 ID、旗標、**目前值／最差值／門檻／6 bytes raw**。誠實重點是**把 raw 原樣列出**，
  不要只給廠商公式換算後的值（不同廠商對同一 ID 的 raw 定義不同）。
  再下 `IDENTIFY DEVICE`（0xEC）逐 word 解：轉速（word 217，1 = SSD）、TRIM、DevSleep、
  安全狀態、實體尺寸（word 168）。
- **誠實加分項（我認為這是本項真正的價值）：** 把「已寫入總量」隨時間存進歷史庫，
  就能算出**實測日均寫入量**，再配合廠商標示的 TBW 推出「以目前這台機器的實際寫入速度，
  還能用多久」。這個數字**完全是量到的**——寫入量是裝置回報的計數器，時間是真的過去的時間。
  這跟「剩餘壽命 87%」有本質差別，也是市面工具很少做的。

**注意。** 需要對實體磁碟開 handle（`\\.\PhysicalDriveN`），需要管理員權限（程式已 `requireAdministrator`）。
某些 USB 橋接器不轉送 ATA/NVMe 命令，此時**要顯示「此介面不支援」而不是留白或填 0**。

## 3. 硬體變更稽核與長期趨勢

**問題陳述。** 現在有 CSV 感測記錄（`SensorLogService`）與事件時間軸，但沒有「這台機器的身分歷史」。

**做法。**

- 開機時把關鍵靜態規格做成快照（記憶體條的 SPD 序號／部件號、GPU 裝置 ID、
  硬碟序號、BIOS 版本與日期、CPU 微碼修訂版、主機板 UUID），與上次快照比對，
  差異寫成一條時間軸事件：「2026-09-14 偵測到記憶體由 4×8GB 變更為 8×8GB」。
- 把 CSV 換成 SQLite（`Microsoft.Data.Sqlite`）。CSV 沒辦法回答「同負載下的溫度是否隨時間變高」，
  因為要做的是條件式聚合查詢。有了資料庫才能問：
  **「這顆 CPU 在 80–90% 負載區間的平均封裝溫度，三個月來上升了幾度？」**
  這是散熱劣化／積灰的真實訊號，而且**只有長期記錄才給得出來**——第三方工具大多是即時取樣，
  這是本專案能做出差異化的地方。

**誠實界線。** 溫度上升的原因可能是室溫、可能是灰塵、可能是矽脂老化。
**只呈現趨勢與量測條件，不要替使用者下「你該清灰塵了」這種診斷結論**，
最多提供「可能原因」清單並標明這是推測。

## 4. AI 從「寫評語」升級為「能查資料的代理」

**問題陳述。** 現在的 `AiService` 是把一份快照文字丟給模型，模型只能泛泛而談。
使用者問「為什麼我玩遊戲會卡」，它拿不到降頻紀錄與溫度曲線，只能猜。

**做法。** 用 OpenAI-compatible 的 tool use（function calling）給模型工具：
`查感測(名稱)`、`查歷史(項目, 天數)`、`列出硬碟屬性`、`列出降頻事件(天數)`、`跑某項跑分`。
模型自己決定要查什麼，回答就會建立在真實資料上。

**紅線（必須守住）：** **只開放讀取工具。任何寫入——超頻、風扇轉速、記憶體整理、
垃圾清理、登錄／Hosts 修改、DNS 切換——一律不得暴露給模型。**
要改就回到人工介面按按鈕。這不是能力限制的問題，是責任歸屬的問題：
模型判斷錯誤而燒掉使用者的硬體，沒有任何說詞可以解釋。

**風險評估。** 這一項最花時間，也最容易做成花瓶（模型亂查、答案更長但沒更準）。
**建議等前面幾項落地再說。** 另外注意使用者的既有約束：
**「Key 不用管」「Key 你不需要關係」——不要主動追問或改動既有 API 金鑰設定。**

## 5. 發佈與信任

- **自我更新檢查**：讀 GitHub Releases API（`/repos/Xinglanclever/XinSpect/releases/latest`）
  比對版號，**只提示不自動下載**（自動下載執行檔在便攜工具上是反模式）。
- **winget 清單**：送一份 manifest 到 `microsoft/winget-pkgs`，之後 `winget install XinSpect` 就能裝。
  程式本身已經在用 winget 做「一鍵裝機」，自己上架算是閉環。
- **SmartScreen 問題的真正解法是程式碼簽章憑證，那要錢。** winget 上架只能減輕、不能消除。
  **不要向使用者宣稱上架 winget 就不會被攔**——那是誇大。

## 6. 無管理員降級模式

**問題陳述。** `app.manifest` 寫死 `requireAdministrator`，在公司鎖權限的機器上**根本開不起來**。

**做法。** 改成偵測提權狀態：偵測不到就進唯讀模式，把需要特權的功能整批停用並**明說原因**
（超頻、風扇控制、Hosts 編輯、右鍵選單、記憶體整理、DNS 切換、S.M.A.R.T. 直讀、LHM 的部分感測），
其餘照常運作。

**注意。** manifest 一旦從 `requireAdministrator` 改成 `asInvoker`，
就變成**預設不提權**，現有使用者會突然發現超頻頁不能用了。
比較安全的設計是保留 `requireAdministrator`，但在提權失敗（使用者按取消）時
仍然啟動並進入唯讀模式——需要確認 Windows 在這情況下是否會直接終止程序（可能會，需實測）。

---

# 第二部 ・ 參數方面的深入（第二輪討論）

現在的靜態規格主要來自 WMI，記憶體時序來自背景跑 CPU-Z 再解析報告。
這一部按「拿得到的難度」分四層。

## 第一層 ・ 純 .NET／Win32 API，零額外相依，可立刻做

### 1-A. CPUID 全葉展開 ★

`System.Runtime.Intrinsics.X86.X86Base.CpuId(int leaf, int subleaf)` 是 **.NET 5+ 內建**的，
回傳 `(int Eax, int Ebx, int Ecx, int Edx)`，**直接從矽晶片讀，不需要 native code、不需要驅動、
不需要管理員權限**。能拿到現在完全沒顯示的東西：

| Leaf | 內容 |
|---|---|
| `0x04`（子葉迭代） | **每一層快取的真實幾何**：路數（ways）、分割數、集合數（sets）、行大小、被幾個邏輯核共用、是否 inclusive。現在只顯示容量。 |
| `0x18` | TLB 結構（各層 entry 數與頁面大小支援） |
| `0x15` | TSC 與核心晶振的比值 → **真正的外頻**，不是反推的 |
| `0x16` | 處理器的 base／max／bus MHz（廠商標稱值） |
| `0x07`（全子葉） | AVX-512 的各個子集（F/VL/DQ/BW/CD/ER/PF/VNNI/IFMA/BITALG/VPOPCNTDQ…）、AMX、SGX、TSX、SHA、控制流強制執行（CET）、各種緩解措施位元 |
| `0x1A` | 混合架構的核心類型（P-core／E-core 標記）。X299 的 7980XE 沒有，但 12 代以後的機器有。 |
| `0x0B` / `0x1F` | 擴充拓樸列舉：SMT／Core／Die／Package 的層級與位移 |
| `0x80000002-4` | 品牌字串（可與 WMI 的名稱互相驗證） |
| `0x80000008` | 實體／虛擬位址位元數 |

**為什麼我把這一項排在參數方向的第一位：** 它完全誠實（讀不到某個 leaf 就是這顆 CPU 沒有，
直接顯示「—」）、零風險、資訊密度極高，而且它會讓 CPU 頁從
「WMI 抄來的規格」變成「我真的問過這顆晶片」。這正是本專案的主軸。

**實作提醒。** 先用 `X86Base.IsSupported` 檢查，再用 leaf 0 的 Eax 取得最大支援 leaf、
`0x80000000` 取得最大擴充 leaf，**超出範圍的 leaf 不要讀**（會回傳無意義值或最後一個有效 leaf 的內容）。
子葉迭代（如 0x04）要照規格判斷終止條件，不要寫死次數。

### 1-B. SMBIOS 原始表全解 ★

`GetSystemFirmwareTable('RSMB', 0, ...)` 拿到整份 SMBIOS 自己解析結構。
WMI 只轉譯了其中一小部分，直接解表能多拿到：

- **Type 17（Memory Device）**：真實 Part Number、Manufacturer、Serial、
  **Configured Memory Speed vs (Max) Speed**（能看出有沒有跑在 XMP）、Rank 數、
  **Bank Locator／Device Locator（到底插在哪一槽）**、製造週資訊、技術類型（DRAM/NVDIMM）。
- **Type 9（System Slot）**：**每一槽用了沒有**（Current Usage）、實體寬度 vs 資料寬度、
  匯流排位址、支援的特性。這是「你的第三根 PCIe 是空的」這種資訊的來源。
- **Type 4（Processor）**：真實插槽名稱、**Core Count vs Core Enabled vs Thread Count**
  （能看出有沒有在 BIOS 裡關核）、電壓、外部時脈、L1/L2/L3 handle。
- **Type 16/19**：實體記憶體陣列與位址對應。
- **Type 43（TPM Device）**、**Type 41（板載裝置）**、**Type 39（電源）**（少有板子填，
  但**有填就顯示、沒填就說沒有**，符合誠實原則）。
- **Type 0/1/2/3**：BIOS 版本與日期、系統與主機板與機箱資訊。

### 1-C. ACPI 表與 UEFI 變數

`GetSystemFirmwareTable('ACPI', signature, ...)`（先用 `EnumSystemFirmwareTables` 列舉有哪些表）：

- **`SRAT` / `SLIT`**：NUMA 節點配置與**節點間距離矩陣**。單插槽 X299 用不到，
  但開了 sub-NUMA clustering 或雙插槽平台就有意義。
- **`DMAR`**：判斷 VT-d／IOMMU 是否存在。
- **`MCFG`**：PCIe 設定空間的 MMIO 基底位址（第三部第四節的 IMC 寄存器會用到）。
- **`HPET`**：高精度計時器（第三部的 TSC 校準會用到）。
- **`FACP`（FADT）**：固定功能旗標、支援的 S 狀態。
- **`MSDM` / `SLIC`**：OEM 授權金鑰資訊——**這個要小心，`MSDM` 內含 OEM Windows 產品金鑰。
  可以顯示「存在／不存在」，但絕對不要把金鑰值顯示出來或寫入報告檔。**

`GetFirmwareEnvironmentVariable`（需 `SE_SYSTEM_ENVIRONMENT_NAME` 權限）：
SecureBoot 狀態、SetupMode、PK/KEK 存在性、開機順序與各開機項。

### 1-D. PCIe 設定空間完整走訪 ★

現在只顯示「PCIe 連結速率」。走訪整個設定空間能深得多：

- **`LnkCap` vs `LnkSta`**：**能力 vs 實際**。一眼看出顯卡是不是掉到 x8、或協商成 Gen2。
  這是最常見的實際問題（插錯槽、延長線品質、BIOS 設定），而且**必須兩者並列才誠實**——
  只顯示目前速率會讓使用者誤以為那就是上限。
- **ASPM 狀態**（L0s/L1／L1.1／L1.2）、**MPS／MRRS**（Max Payload／Read Request Size）。
- **Resizable BAR** 能力與目前生效狀態、**SR-IOV** 能力。
- **AER（Advanced Error Reporting）錯誤計數器** ← **本項的核心價值。**
  可修正錯誤（Correctable：Bad TLP、Bad DLLP、Replay Timer Timeout、Receiver Error）與
  不可修正錯誤的實際發生次數。**「你的顯卡插槽累積了 137 次可修正錯誤」是硬體或延長線
  有問題的鐵證**，而市面上的免費工具幾乎都不顯示這個。

**存取方式。** 純使用者態可以走 `SetupAPI` + `IOCTL_PCI_...`（受限），
或用 `\\.\PciConfig` 之類的介面（不穩定）。**完整走訪實務上需要 MMIO 存取
（透過 `MCFG` 基底），也就是需要 ring0。** 但基本的 LnkCap/LnkSta 可以從
`SetupDiGetDeviceRegistryProperty` 與 WMI 的部分屬性拼出來，先做這部分。
另一條路是解析 Windows 自己記錄的 WHEA 事件（事件檢視器 `Microsoft-Windows-WHEA-Logger`），
那裡有作業系統已經收到的 PCIe 錯誤——**這條路零特權且完全可靠，建議優先做。**

## 第二層 ・ 需要 ring0（LHM 已帶簽章驅動，但要先確認其 API 是否對外公開）

> **未驗證事項：** LibreHardwareMonitorLib 0.9.6 的 `Ring0` 類別在新版中可能是 `internal`。
> 動手前請先用反射或看原始碼確認。若不可用，見第三部第五節的路線決策。

### 2-A. MSR 讀取 → 降頻原因與功耗真相

| MSR | 內容 |
|---|---|
| `0x19C`（IA32_THERM_STATUS） | 熱度狀態與**溫度餘裕（Digital Readout）** |
| `0x1B1`（IA32_PACKAGE_THERM_STATUS） | 封裝層級熱度狀態 |
| `0x64F`（CORE_PERF_LIMIT_REASONS） | **它為什麼降頻**：PROCHOT、熱度、殘餘熱、PL1/PL2 功耗上限、電流上限、VR 過熱、Turbo 衰減、最大核心數限制——**逐項分開，還分「目前」與「曾經發生」兩組位元** |
| `0x610`（PKG_POWER_LIMIT） | **PL1／PL2 的實際設定值與 tau 時間窗**、是否被鎖定 |
| `0x611` / `0x639` / `0x641` / `0x619` | **RAPL 累積能量計數器**（package／PP0 cores／DRAM／uncore），配 `0x606`（POWER_UNIT）取單位 |
| `0x0CE`（PLATFORM_INFO） | 基礎倍頻、最低效率倍頻、**是否可超頻**、TDP 可配置性 |
| `0x1AD`（TURBO_RATIO_LIMIT） | **1 核到 18 核各自的 Turbo 倍頻表**（Skylake-X 另有 `0x1AE` 的核心數門檻） |
| `0x1A2`（TEMPERATURE_TARGET） | **真實 TjMax**。現在多半是猜或用預設 100°C——這正是不該存在的「典型值」 |
| `0x8B`（IA32_BIOS_SIGN_ID） | **目前生效的微碼修訂版** |
| `0x620`（UNCORE_RATIO_LIMIT） | mesh／ring 時脈的上下限（X299 有意義） |
| `0xE7` / `0xE8`（APERF/MPERF） | **有效時脈**的來源，見第三部第二節 |
| `0x3FC`–`0x3FE`、`0x3F8`–`0x3FA` | core／package **C-state 停留計數器** |

**`0x64F` 這一項是整份文件裡我認為對使用者最有價值的單一功能。**
現在程式只能說「它降頻了」，之後能說「它因為撞到 PL1 而降頻，且從開機以來發生過 43 次」。

### 2-B. SPD 直讀（SMBus 位址 0x50–0x57）

現在記憶體時序是靠背景跑 CPU-Z 產生報告再解析——**會慢、依賴外部程式、而且解不到全部欄位**。
直讀 SPD 就能自己拿到完整 JEDEC 時序表與 XMP 區塊（DDR4 的 XMP 2.0 在 0x180 起）。

DDR5 還多一個 SPD5118 hub：**每根記憶體條上的溫度感測器**（DDR4 沒有）與 PMIC 電壓。
注意**使用者這台是 X299 / DDR4**，所以 DDR5 那部分無法在他機器上驗證。

這一項做成了，就可以把記憶體頁對 `CpuzReportService` 的依賴拔掉。
（**但 `Services/CpuzReportService.cs` 不要刪除**——它還負責 CPU 與 GPU 的深度規格。
另外**該檔 391-392 行的簡體字是用來解析第三方 CPU-Z 報告的，合法，不要「修正」。**）

## 第三層 ・ 廠商 SDK 已在手上，只是還沒挖深

NVML／NVAPI 已經接了（`GpuOcService`：NVML 的功耗／風扇／溫度上限，NVAPI 的核心與記憶體時脈偏移）。
同一組 API 還能給：

- **`nvmlDeviceGetCurrentClocksThrottleReasons`** ← **GPU 版的降頻原因**
  （GpuIdle／ApplicationsClocksSetting／SwPowerCap／HwSlowdown／SyncBoost／SwThermalSlowdown／
  HwThermalSlowdown／HwPowerBrakeSlowdown／DisplayClockSetting）。
  跟第二層 `0x64F` 的 CPU 降頻原因湊成完整一對，**這是我建議一起做的組合**。
- **溫度門檻**：`nvmlDeviceGetTemperatureThreshold` 的 SLOWDOWN／SHUTDOWN／GPU_MAX_OPERATING，
  以及 `nvmlDeviceGetThermalSettings`。**這樣「距離降頻還有幾度」就是算出來的，不是猜的。**
- **PCIe 目前世代／寬度 vs 最大世代／寬度**（`nvmlDeviceGetCurrPcieLinkGeneration` 等）、
  **PCIe 吞吐計數器**（`nvmlDeviceGetPcieThroughput`，TX/RX KB/s）。
- **VBIOS 版本**、**顯示記憶體顆粒廠**（三星／海力士／美光）、**ECC 狀態與 retired pages**
  （消費卡通常不支援 ECC，**不支援就顯示「不支援」**）。
- **編碼／解碼器使用率**（`nvmlDeviceGetEncoderUtilization`／`DecoderUtilization`）、
  **逐行程顯示記憶體占用**（`nvmlDeviceGetComputeRunningProcesses`／`GraphicsRunningProcesses`）。
- **NVAPI 的 DisplayPort 資訊**：實際連結速率、lane 數、bpc、色彩格式（RGB／YCbCr444／422）。
  這是「我的螢幕到底跑在幾 bit 幾 Hz」的真實答案。

## 第四層 ・ 儲存與顯示的參數層

- **NVMe**：Identify Controller 全欄位、Log Page `0x03`（韌體插槽與各槽版本）、
  `0x05`（支援的命令與功能）、廠商 OCP `0xC0` 擴充 SMART（許多 SSD 有更細的計數）、
  熱節流狀態與累計時間。
- **SATA**：`IDENTIFY DEVICE` 逐 word 解析（見第一部第 2 項）。
- **EDID / CTA-861 擴充塊全解**（現在有 EDID 色域，但沒解完擴充塊）：
  **HDR 靜態中介資料**（最大／平均／最小亮度、支援的 EOTF）、**VRR 範圍**（最低／最高刷新率）、
  **DSC 能力**、**HDMI 2.1 FRL 速率**、逐一詳細時序（DTD）、音訊資料塊、
  Speaker Allocation、以及 DisplayID 2.0 區塊。

---

# 第三部 ・ 再深入：三個深水區（第三輪討論）

## 第一節 ・ 微架構量測層——不是讀規格，而是實測出規格（純使用者態，零風險）★

這一層價值最高，因為它產出的是**只有實測才存在的數字**，而且完全不碰特權介面。

### 3-1-A. 核心到核心延遲矩陣 ★★ 最推薦

**做法。** 綁定親和性後，讓兩個執行緒對同一條 cache line 做原子 ping-pong
（一方 `Interlocked.Exchange` 寫入 flag、另一方輪詢到值變了再寫回），量往返延遲，
跑滿 N×N 組合，畫成熱圖。

- 使用者這顆是 **18 核 36 執行緒的 Skylake-X（i9-7980XE），mesh 架構**，
  這張圖會直接畫出 mesh 拓樸與跨 tile 的懲罰差異，以及 SMT 兄弟核（延遲極低）的對角紋理。
- 實作用 `Thread.BeginThreadAffinity()` + `SetThreadAffinityMask`，或 .NET 的
  `ProcessThread.ProcessorAffinity`。**超過 64 邏輯處理器要處理 Processor Group**
  （36 執行緒沒問題，但寫的時候別假設單一 group）。
- 要用 `Thread.Sleep(0)` 之外的忙等、把執行緒優先權拉高、丟掉前幾輪暖機結果、取中位數而非平均。
- **零特權、零風險、視覺衝擊最大。** AIDA64 有這功能，圖吧工具箱沒有。

### 3-1-B. 記憶體延遲曲線 ＋ 自動推導快取邊界 ★

**做法。** 把現有的 `CacheBenchService` 升級成連續曲線：footprint 從 1 KB 掃到 1 GB
（每倍頻或每 1/4 倍頻一點），用**指標追逐（pointer-chasing，隨機化的環狀鏈結）防硬體預取**，
量每次存取的平均 ns。

然後**從曲線的階梯自動推導出 L1／L2／L3／DRAM 的邊界與各層延遲**，
再跟 CPUID leaf 0x04 宣稱的容量並列對照：

> 「L3 宣稱 24.75 MB，實測階梯落在 24 MB 附近」

**這種「實測 vs 宣稱」的並列，是本專案誠實主軸最漂亮的體現。**
順帶一提，這也是唯一能誠實回答「我的快取真的有那麼大嗎」的方法。

### 3-1-C. 記憶體頻寬 vs 執行緒數曲線

同樣純使用者態：1 到 N 執行緒各自跑串流讀／寫／複製，畫出頻寬隨執行緒數的飽和曲線。
能看出**記憶體控制器在幾個執行緒時就飽和**（X299 是四通道，通常 6-8 執行緒就到頂）。

### 3-1-D. TSC 校準 → 真實 BCLK

用 HPET 或 `QueryPerformanceCounter` 在一個時間窗內反推 TSC 頻率，
除以 CPUID `0x16` 給的名義倍頻，得到**真實外頻到 0.01 MHz**。
超頻使用者在意 BCLK 100.3 和 100.0 的差別——那個差別是真的存在的，而且會影響所有時脈讀值。

## 第二節 ・ 有效時脈（Effective Clock）——現有的時脈顯示其實在騙人

**這是一個現存的誠實問題，不只是新功能。**

現在顯示的是**瞬時時脈**：核心進了 C-state 之後，回報值可能還停在 4.2 GHz，
但那顆核實際上有 90% 的時間在睡。使用者看到「4.2 GHz」會以為它在全速運轉——
**這在事實上是誤導，即使每個數字都是感測器回報的。**

**正確做法。** 讀 **MSR `0xE7`（MPERF）與 `0xE8`（APERF）**，
取兩次取樣的差值比（ΔAPERF / ΔMPERF）× BCLK，
得到「這段取樣區間內平均真正運轉的時脈」。HWiNFO 就是這樣做的，
這也是它跟便宜工具的分水嶺。

**配套。**

- **C-state 停留計數器**（core `0x3FC`–`0x3FE`、package `0x3F8`–`0x3FA`）
  → 真實的 C1／C3／C6／C7 停留比例。有了它，「時脈很低」就能解釋成
  「因為它 87% 的時間在 C6，沒事做」。
- **`0x620`（UNCORE_RATIO_LIMIT）** → mesh 時脈的上下限設定。

**建議 UI 呈現：兩者並列，不要取代。**「瞬時 4.2 GHz ／ 有效 0.4 GHz」並列，
並在說明裡解釋差別。取代掉瞬時值會讓習慣其他工具的使用者以為讀錯了。

## 第三節 ・ PMU／效能監視計數器——把「為什麼慢」變成可量化

用 `IA32_PERFEVTSEL0-3` 設定事件、讀 `IA32_PMC0-3`（或走 ETW 的硬體 PMU 取樣，
Windows 有 `EventTracingPmc` 支援），能拿到 **Intel PCM 級別**的指標：

- **IPC**（每週期指令數）、前端／後端停滯占比
- **各層快取未命中率**、**分支預測失誤率**
- **透過 IMC uncore 計數器算每通道的實際記憶體頻寬**
  ——注意這不是跑分，是「你現在真的在用多少 GB/s」

**價值。** 這會讓「健康」頁從溫度／容量警示，升級成能說出
**「你的瓶頸在記憶體頻寬而不是 CPU」**。

**代價。** 這是全篇最重的一項：事件編碼逐微架構不同（Skylake-X 的事件表跟 12 代完全不同），
需要一份事件對照表；uncore 計數器要走 PCI config 到 IMC 裝置；
還要處理計數器溢位與多工。**建議放在最後，或只做最基礎的 IPC 與快取未命中率。**

## 第四節 ・ 平台寄存器層——晶片組與韌體的真相

### 4-A. 記憶體控制器寄存器（MMIO，經 ACPI `MCFG` 取基底）

X299 的 IMC 寄存器能給 SPD／XMP 拿不到的東西：
**實際生效的 tREFI、tRFC、command rate（1T/2T）、讀寫轉向延遲、refresh 模式、gear mode**。

**關鍵區別：這是「目前生效值」而非「SPD 宣稱值」，兩者常常不同**
（BIOS 會自己調整、XMP 套用未必全套）。這正是誠實原則要求的方向。

**缺點。** 寄存器位移逐平台世代改變，要對照 Intel datasheet vol.2 逐代寫死，可攜性差。
在不認識的平台上**必須顯示「不支援此平台」而不是讀出垃圾值**。

### 4-B. VRM 遙測（PCH SMBus → VRM 控制器，如 IR35201／uP9511）

能拿到**真實每一相電流、VRM 溫度、loadline calibration 實際生效檔位的回讀**。
這是發燒友最想要、而幾乎沒有免費工具提供的資料。

**三個必須誠實面對的問題：**

1. **SMBus 位址與寄存器對照是逐板逐控制器的。** 沒有對照表就只能拿到 raw 數值，
   **那就只能顯示 raw，不能宣稱它是安培或攝氏。**
2. **與 BIOS／其他監控程式爭用 SMBus 有真實風險**（總線鎖死、讀到錯值，
   極端情況下影響風扇控制器）。
3. **只讀相對安全，寫入絕對不要碰。** VRM 寄存器寫錯可以燒硬體。

### 4-C. Super I/O 深入

LHM 已經讀 0x2E/0x4E 的 Super I/O。再深就是風扇曲線寄存器與溫度來源選擇寄存器。

**但電壓有個誠實的天花板：板廠的分壓比（voltage divider ratio）不公開。**
沒有分壓比，就**不能宣稱那個讀值是 +12V**，只能說是 raw 讀值或標明「未校準」。
這條界線必須在 UI 上講清楚——LHM 本身在某些板子上就是用猜的比例，
如果照抄它的換算，等於把猜測當成量測，**違反本專案主軸**。

### 4-D. PCH 與韌體狀態

- **SPI flash descriptor**：BIOS 區域配置、**寫入保護鎖狀態**、ME 區域是否存在。
- **Boot Guard／BIOS Guard 狀態**、**微碼修訂版**（MSR `0x8B`）配 BIOS 日期。
- **CSME 韌體版本**：走 `\\.\HECI`（MEI）介面查詢。
- **TPM 2.0**：PCR 值與能力（`Tbsi_*` API），**測量開機記錄**（`Tbsi_Get_TCG_Log`）
  → 真實的開機測量鏈。
- **VBS／HVCI／Credential Guard／Kernel DMA Protection** 實際啟用狀態
  （`Win32_DeviceGuard` 的 `SecurityServicesRunning` 而非 `Configured`——
  **設定了但沒跑起來是常見狀況，要顯示實際執行中的那個**）。
- **SecureBoot dbx 撤銷清單版本**。

> **⚠ 誠實紅線（重要）：** 顯示 ME／CSME 韌體版本可以，
> **但不要因此宣稱「你有 SA-00xxx 漏洞」**。那需要一份會過期的對照資料庫，
> 而且判斷還牽涉修補狀態。**列出版本、把判斷留給使用者**，
> 最多提供 Intel 公告頁的連結。同理，微碼版本也不要拿來宣稱「你沒修 Spectre」。

## 第五節 ・ 自己掌握 ring0——所有深入的前提，也是最大的決策點

第二節、第三節、第四節**幾乎全部依賴 MSR／MMIO／PCI／port I/O 存取**。
目前是靠 LibreHardwareMonitor 夾帶的驅動（WinRing0 血統）。這條路有三個現實問題：

1. **很多防毒軟體把它標為威脅**（WinRing0 被大量惡意程式濫用過）。
2. **它有已知的權限提升問題**（任意 MSR 讀寫暴露給非特權程序的設計缺陷）。
3. **它的介面在新版 LHM 裡不一定對外公開**——`Ring0` 類別可能是 `internal`。
   **這一點我沒有查證，動手前請先確認 0.9.6 的實際情況。**

**選項比較：**

| 路線 | 優點 | 缺點 |
|---|---|---|
| 沿用 LHM 驅動 | 零額外工作、已簽章 | 上述三個問題；介面可能不可用 |
| 自寫 KMDF 驅動 | 介面可控、只暴露必要的讀取、可加白名單限制 MSR 範圍 | 需核心模式簽章；寫錯就是別人機器上的藍屏；反作弊軟體會視為威脅 |
| 全部放棄 ring0 | 零風險 | 第二、三、四節全部做不到 |

**自寫驅動的簽章問題有現成經驗可循：** 使用者曾為 Intel I219-V 自簽 INF 驅動並成功部署
（`oem47.inf`，**而且不需要測試簽章模式**，備份在 `C:\I219V-mod`——
詳見記憶檔 `i219v-patched-driver.md`）。那次的簽章與信任鏈流程可以直接復用。
**這是最現實的長期路徑。**

**但要對使用者說清楚代價：** 核心模式驅動要在別人的機器上跑，需要 EV 憑證做核心模式簽章
（要錢），或走測試簽章模式（那就不能發給一般使用者）。
**不要在沒有取得使用者明確同意的情況下開始寫核心驅動。**

**建議：** 前面所有零風險項目（第一部第 1-2 項、第二部第一層、第三部第一節）
**全部做完之後再做這個決策**。到那時程式已經多了大量真實資料，
決策的資訊也更充分。

## 第六節 ・ GPU 的更深一層

- **V/F 曲線讀出**：NVAPI 的 client volt-rails／VF entry 介面
  （`NvAPI_GPU_ClientVoltRailsGetStatus`、`NvAPI_GPU_ClientVfEntry*` 這一組，
  MSI Afterburner 的曲線編輯器用的就是它），能把**每個曲線點的電壓與頻率讀出來**。
  **非官方文件、隨驅動版本可能改變**——要寫成「讀不到就顯示不支援」，
  絕對不要因為介面變了就整頁掛掉。
- **`D3DKMTQueryStatistics`** ★：逐行程 VRAM、分頁流量、
  **每個引擎（3D／複製／視訊編碼／視訊解碼）各自的使用率**。
  **最大優點是它不綁 NVIDIA——AMD 和 Intel 顯卡一樣有效**，
  能補上 NVML 覆蓋不到的機器。建議優先做這一項。
- **VBIOS dump 與解析**：功耗表、記憶體 strap、預設時脈表。
- **GPU 版降頻原因**（第二部第三層已列），跟 CPU 的 `0x64F` 湊成完整一對。

---

# 第四部 ・ 誠實紅線彙總（跨全篇，必讀）

這一部是本文件最重要的部分。上面每一個方向都有它專屬的誠實陷阱，集中列在這裡：

1. **讀不到就顯示「—」。** 不支援的 CPUID leaf、沒填的 SMBIOS 欄位、不轉送 ATA 命令的 USB 橋接器、
   沒有 ECC 的消費卡——全部顯示「不支援」或「—」，**絕不填 0、絕不用典型值、絕不推估**。
2. **「能力」與「實際」必須並列。** PCIe LnkCap vs LnkSta、SPD 宣稱 vs IMC 生效值、
   CPUID 宣稱快取容量 vs 實測階梯、瞬時時脈 vs 有效時脈。
   **只顯示其中一個都會誤導**，即使那個數字本身是真的。
3. **沒有校準係數就不要換算。** Super I/O 電壓缺分壓比、VRM 缺寄存器對照表——
   只能顯示 raw 並標明「未校準」，不能宣稱是伏特或安培。
4. **不要用版本號推論安全結論。** ME 韌體版本、微碼版本可以顯示；
   「你有 SA-00xxx 漏洞」「你沒修 Spectre」不可以說。
5. **不要替使用者下診斷結論。** 溫度趨勢上升可以呈現，「你該清灰塵了」不可以斷言，
   最多列出可能原因並標明是推測。
6. **不要對模型開放寫入能力。** AI 代理化只給讀取工具。
7. **不要宣稱減輕了無法驗證的問題。** 例如「上架 winget 就不會被 SmartScreen 攔」是假的。
8. **敏感值不外顯。** ACPI `MSDM` 內含 OEM 產品金鑰，只顯示「存在」，不顯示值，也不寫進報告檔。
9. **量測方法要寫在 UI 上。** 現有的三支新檢測已經建立了這個慣例：
   喇叭檢測只說「送出了哪個聲道」不宣稱使用者聽得見；動態檢測明言量的是
   「本程式被畫出來的節奏」而非面板規格；記憶體檢測明言不是 MemTest86 的替代品。
   **新功能一律沿用這個寫法。**

---

# 第五部 ・ 給 GLM 的執行守則

## 動手順序

1. **先完成 1.3.2 的發佈**（見 `HANDOFF-1.3.2.md`）。本文任何一項都排在那之後。
2. 要開工任何一項，**先向使用者提出並取得同意**。使用者對版本節奏的原話是
   **「可以，不過一次成型1.3.2而不是經常的版本迭代」**——
   不要每做一項就 bump 版號，累積成一個版本再發。
3. 一次只推進一項。使用者的節奏偏好是「一個一個來」（他在實用工具批次時明說過）。

## 沿用既有慣例，不要另創

- **跑分／檢測服務**一律照 `Services/CacheBenchService.cs` 的形狀：
  `ObservableObject`、`IsRunning`/`CanStart`、`Phase`、`ProgressFraction`/`ProgressPercent`、
  `StatusLine`、`ObservableCollection<Row> Rows`、`Start()` → `_ = RunAsync()`、
  `Cancel()` → `_cts?.Cancel()`；`Progress<>` 在 UI 執行緒建立後 `await Task.Run(...)`；
  `finally` 收 `IsRunning`／`_cts`。
- **卡片版面**照 `Views/BenchView.xaml:352-416`；下拉用 `DarkCombo`（`Themes/Theme.xaml:336`）；
  進度條用 `LoadBar`；讀值磁貼用 `Surface2Brush`。
- **能做成既有頁面上的卡片就別開新頁**——導覽有 `_views[]` ↔ Nav 平行陣列，
  增頁必須兩邊同步。（1.3.2 的記憶體圖樣檢測就是為此掛在 `MemoryView` 上。）
- **測試只測純函式**，方法名用中文，見 `Tests/DashboardLayoutTests.cs`。
  **第一部第 1 項是唯一要突破這條的例外**，突破時請用假 DataContext。
- **介面與程式註解一律繁體中文（台灣用語）**，不得出現簡體或英文介面字串。

## 已知地雷（重複踩過的，別再踩）

- `Severity` 列舉只有 `{ Neutral, Good, Warning, Serious, Critical }`，**沒有 `None`**。
- **`ProgressBar.Value` 繼承 `BindsTwoWayByDefault`**，綁唯讀屬性必寫 `Mode=OneWay`，
  否則整頁閃退。
- **不要改共用資源筆刷的 `.Color` / `.Opacity`**（污染全域主題）；新筆刷請 `Freeze()`。
- **code-behind 指派已繫結的 DP 會摧毀該繫結。**
- `System.Text.Json` 寫入 `double.NaN` 會丟例外，**會靜默毀掉整份設定持久化**。
- XAML partial 類別裡若有成員叫 `Grid`，會遮蔽 `Controls.Grid`。
- **根元素上的 `Data="{Binding}"` / `DataContext="{Binding}"` 在 `Host.Content` 延遲載入時不解析**，
  必須在 code-behind 指派。頁面的 `Vm` 存取子慣例：
  `DataContext as MainViewModel ?? Application.Current?.MainWindow?.DataContext as MainViewModel`。
- 私有方法命名別撞 `Window` 成員（曾有 `Show()` 遮蔽 `Window.Show()` 造成 `CS0108`）。
- 全螢幕檢測視窗**必須自己 `Focus()`**，否則 `KeyDown` 不會觸發。
- **`Services/CpuzReportService.cs:391-392` 的簡體字是解析第三方 CPU-Z 報告用的，
  合法，不要「修正」。**
- 用 ripgrep，不要 `grep -P`：多位元組字元的 bracket class 會被逐 byte 比對。
- **PowerShell 5.1 會把沒有 BOM 的 UTF-8 `.ps1` 當 ANSI 解析**，中文字串字面值會被弄壞。
  寫驗證腳本時用純 ASCII，或加 UTF-8 BOM，或改用 `pwsh`。
- 建置／發佈時使用者的 XinSpect.exe 常在執行中會鎖住輸出，
  **用 `-p:BaseOutputPath=obj/_verify/`（建置）或 `obj/_pub/`（發佈）繞過，
  不要 taskkill 掉他正在跑的程式。**

## 這台機器的硬體事實（決定哪些項目能在本機驗證）

- **CPU：Intel Core i9-7980XE（18 核 36 執行緒，Skylake-X，mesh 架構，X299 平台）**
- **記憶體：32 GB DDR4**（所以 **DDR5 SPD5118／每條溫度感測器／PMIC 電壓無法在本機驗證**）
- **OS：Windows Server 2025 Datacenter 24H2，組建 26100.32690，已啟用，非評估版、非 Insider**
- 網路卡是自簽 INF 修補過的 Intel I219-V（`oem47.inf`）
- 時區 UTC+8

**因此：** 核心到核心延遲矩陣、記憶體延遲曲線、mesh 時脈、四通道頻寬曲線
都能在本機做出漂亮的驗證；DDR5 相關、混合架構（P/E core）相關的項目**只能寫、不能驗**，
寫的時候要特別小心 fallback 路徑。

## 使用者互動注意

- **回覆用繁體中文，簡潔，不要客套開場。**
- 使用者已駁回過兩次選單式提問（`AskUserQuestion`），**不要重複詢問已決定的事**。
- **「Key 不用管」「Key 你不需要關係」**：不要主動追問或改動既有 API 金鑰設定。
- GitHub 認證紅線：**不得使用帳號密碼**；輸出任何內容前以
  `sed -E 's/(gh[pous]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)/***/g'` 遮蔽，
  **永不回顯 token 值**。
- 使用者會直接指出你改錯了。**那不是刁難，照著查真正的病根再重寫比小修小補快。**

---

## 附：這份文件與 `HANDOFF-1.3.2.md` 的分工

| 文件 | 內容 | 時效 |
|---|---|---|
| `HANDOFF-1.3.2.md` | 1.3.2 的**現狀與剩餘三步**（pdb 決定、push、Release） | 發佈完可刪 |
| `ROADMAP-給GLM.md`（本文） | 1.3.2 **之後**的方向清單，含技術細節與誠實紅線 | 長期參考 |

兩份都未納入版控。**先讀前者、做完發佈，再回來讀這份。**

— 由 Claude Opus 4.8 整理，2026-08-29


---

# 第六部 ・ 第四輪（強大功能）與第五輪（參數深潛）——2026-08-29 補錄

> 兩輪內容原本不在本文件，2026-08-29 由代理補錄。第四輪三項（幀時間/DPC/SLC）**已於 2026-08-29 完成並 push**（`00c4fc2`，幀時間監測＋DPC 延遲工具子頁、SLC 卡片掛儲存頁）。
> 實作時的實測發現（重要，別再踩）：
> - 本機 Present 事件**只從 Dwm-Core #40 來，DXGI #42 不發**——幀時間服務已兩源並訂。
> - 經典 ETW 的 DPC/ISR 事件**只有常式指標＋時間戳，無單次時長**；LatencyMon 級時長排行需要核心驅動。已誠實改為「頻次排行（依核心模組，EnumDeviceDrivers 定位）」，實測輸出真實排行（ntoskrnl/nvlddmkm/dxgkrnl…）。
> - `\.\PHYSICALDRIVE` 的 ATA_PASS-THROUGH 會無視逾時卡死 IRP（程序殺不掉）——**永久禁用**；SMART 直讀已加匯流排預檢＋15 秒斷路器。

## 已完成（第四輪）
1. ✅ 幀時間擷取：FrameTimeService（TraceEvent 3.1.16；`TraceEventSession` 在 `Microsoft.Diagnostics.Tracing.Session` 命名空間）；1%/0.1% Low＝最差 1%/0.1% 幀平均 FPS。
2. ✅ DPC/ISR：DpcLatencyService（Keywords.DeferedProcedureCalls|Interrupt；PerfInfoDPC/PerfInfoISR；EnumDeviceDrivers 模組定位）。
3. ✅ SLC 快取耗盡：SlcCacheBenchService（每秒 FlushToDisk；斷崖＝<尖峰 35% 且持續；SlcMath.Analyze 純函式）。

## 第五輪（參數深潛）——未開工，開工前需使用者同意；實作路徑調查結果如下

### ⚠ MSR 讀取的現實（2026-08-29 調查）
LHM 0.9.6 **已移除 Ring0/WinRing0**（套件內無該類別與字串）。MSR 存取改經 **PawnIO**（簽章沙箱驅動）＋RAMSPDToolkit：LHM 內含 `IntelMsr` 型別（字串證據），相依 `ramspdtoolkit-ndd` 套件（已在 NuGet 快取）。**反射檢查時 PowerShell 管線被相依載入弄掛**——IntelMsr 的公開 API 形狀與 PawnIO 驅動的安裝狀態尚未驗證。動手順序：(1) 隔離程序驗證 IntelMsr 能讀 MSR 0x10A；(2) 確認 PawnIO 驅動由誰安裝（LHM Computer.Open 或需另行安裝）；(3) 再寫服務。RDT 的 PQR_ASSOC 寫入屬 MSR 寫入——雖經簽章驅動，仍建議向使用者明示後進行。

1. **RDT（CMT/MBM）逐核心 L3 占用與記憶體頻寬**：Skylake-X 支援（CPUID 0x0F）。**逐行程歸屬需要核心層 RMID 排程，使用者態做不到**——誠實版本＝全系統＋逐核心總量（RMID 全核指派、差分讀值）。RMID 寫入（0xC8F）屬 MSR 寫入，安全閘同上。
2. **MCA 銀行＋WHEA**：零特權路線＝WheaErrorService 擴充分類（事件 17/18/19 修正記憶體、46/47 PCIe）；MSR 路線＝MCG_CAP 0x179＋MCi_STATUS 0x400+i*4（位 63 valid、61 UC、32-52 修正計數）——只讀，風險低，等 PawnIO 路徑驗證。
3. **ARCH_CAPABILITIES 0x10A**（RDCL_NO/IBRS_ALL/RSBA/SSB_NO/MDS_NO/TAA_NO/FB_CLEAR…）＋SPEC_CTRL 0x48＋TSX_CTRL 0x122：同樣等 MSR 路徑；這是「安全狀態可以誠實說」的鑰匙。
4. **HWP/Speed Shift**（0x770/771/774、POWER_CTL 0x1FC、CORE_THREAD_COUNT 0x35、PERF_STATUS 0x198）。
5. **DDR4 SPD 逐位元組＋XMP 2.0**（0x180 magic 0x0C 0x4A）：需 SMBus 直讀（RAMSPDToolkit 有 SPD 驅動路徑）；欄位表見使用者原文。
6. **NVMe 深層**（Power State Descriptors、NPWG/NPWA、Get Features 0x02/0x04/0x05/0x06/0x08/0x0C、Log 0x06 自測、OCP 0xC0）：可走 IOCTL_STORAGE_PROTOCOL_COMMAND。
7. **SATA Device Statistics Log 0x04＋SCT 溫度歷史**。
8. **顯示**：EDID 128B 欄位級＋CTA-861（HDR Static Metadata 解碼公式）＋DisplayID 2.0；實際模式 QueryDisplayConfig＋ADVANCED_COLOR_INFO（零特權已文件化）；EDID 可自登錄 Enum\DISPLAY 讀。
9. **PCIe 擴充能力**（LnkCap2/Lane Margining 0x0027/Resizable BAR 0x0015/AER…）：完整走訪需 ring0；WHEA 路線已完成（第六項）。
10. **ACPI 四表**：FPDT（開機韌體耗時）、PMTT、BERT、LPIT——GetSystemFirmwareTable('ACPI', sig)，零特權，與 SMBIOS 同款做法。
11. **SMBIOS 補 Type**：7/11/24/26/27/28/29/32/45（沒填就明說）。
12. **NVML 深挖**：ViolationStatus（降頻停留時間）、TotalEnergyConsumption（累計 mJ→真實平均功耗）、Samples 環形緩衝、ProcessUtilization、NVAPI DynamicPstatesInfoEx（GPU/FB/VID/BUS 四域）。
13. **TCP 逐連線**：GetPerTcpConnectionEStats（RTT/重送/擁塞窗/接收窗/傳送窗限制歸因）＋NDIS OID 查詢。

**第五輪使用者建議的三優先：RDT → MCA/WHEA → ARCH_CAPABILITIES。前兩者與 MSR 路徑綁定；MCA 的 WHEA 半套可先行。**


---

# 第七部 ・ 第六輪（AMD 補完／微架構診斷／黏滯位元等十四項）——2026-08-29 補錄

> 第六輪全文由使用者提供（含 AMD 參數表、Top-down 桶定義、iMC PMON、黃金核心、黏滯位元、CPUID 補葉、NtQuerySystemInformation、中斷親和性、計時器地基、儲存堆疊、USB、音訊、TPM、電池——細節以使用者原文為準，此處僅錄結構與決策）。
> **使用者建議的三優先：AMD 參數補完 → Top-down 微架構分析 → 黏滯節流位元。**
> 實作前置調查（2026-08-29）：三者皆需 **MSR 存取**。MSR 路徑的最新狀態見第六部第五節的警告：LHM 0.9.6 已改用 PawnIO＋RAMSPDToolkit（`IntelMsr` 型別存在、`ramspdtoolkit-ndd` 套件在快取），**IntelMsr 能否讀 MSR 尚未驗證**——這是本輪全部項目的第一塊骨牌。
>
> 誠實紅線（照使用者原文）：AMD 項目在本機（i9-7980XE）無法驗證，一律顯示「本機未驗證」而不是猜；SMU/SMN 郵箱**只讀＋逐世代白名單**，未知世代顯示「—」；0xC0011020+ 未文件化暫存器**完全不碰**。
>
> 其餘十一項的技術摘要：
> 2. Top-down（Retiring/Frontend/BadSpec/Backend 四桶→Memory→L1/2/3/DRAM）：需 PMU 計數（PERFEVTSEL 逐核寫入，Skylake-X 原始事件碼），也是 MSR 寫入；Windows 自身可能佔用 PMC，需先讀 0x0A 確認計數器位寬與可用性。
> 3. iMC PMON（MCFG→MMIO→CAS_COUNT.RD/WR 逐通道頻寬＋UPI/CHA）：需 ring0 MMIO 映射，等 ring0 決策。
> 4. 黃金核心：逐核讀 0x771 Highest Performance ＋ 0x1AD/0x1AE 倍頻表；與第 2 項（ROADMAP 第三部 3-1-A）實測排序對照。
> 5. 黏滯位元：0x19C/0x1B1/0x6B0/0x6B1 的 log 位元——**唯讀 MSR，風險最低**，開機讀一次＋清除即有逐次開機紀錄。若 IntelMsr 驗證通過，這是 MSR 系列的最佳起手式。
> 6. CPUID 補葉：0x15/0x16/0x0A/0x0D/0x06/0x80000007/0x80000008——**零特權，CpuIdService 直接擴充**；拓樸正確做法＝GetLogicalProcessorInformationEx 與 0x1F 交叉比對，不一致即明說（虛擬化／關核）。
> 7. NtQuerySystemInformation（逐核 Idle/Kernel/User/DpcTime/InterruptTime、SystemMemoryListInformation 待命/修改/可用清單、CodeIntegrity 測試簽署與核心除錯位元、HypervisorDetailInformation、IdleCycleTime）：零特權、未文件化但極穩定。
> 8. 中斷親和性：登錄 Interrupt Management\MessageSignaledInterruptProperties＋Affinity Policy，配第 7 點逐核中斷時間。
> 9. 計時器地基：QPC 來源（TSC/HPET/PM timer；useplatformclock 降級偵測）、NtQueryTimerResolution、TSC_ADJUST 一致性、Invariant TSC。
> 10. 儲存堆疊：StorageAccessAlignment/DeviceTemperature/Adapter Property、FSCTL_GET_NTFS_VOLUME_DATA（MFT 碎片）、寫入快取警示、Storage Spaces。
> 11. USB：NODE_CONNECTION_INFORMATION_EX_V2 協商速度 vs 埠能力；bInterval＝HID 回報率。
> 12. 音訊：IAudioClient3::GetSharedModeEnginePeriod（真實可達最低延遲）＋獨占模式格式＋KSPROPERTY_JACK_DESCRIPTION。
> 13. TPM：Tbsi_Get_TCG_Log（PCR 0-7 測量記錄）＋TPM 2.0 能力＋Win32_DeviceGuard 的 Running（非 Configured）。
> 14. 電池：IOCTL_BATTERY_QUERY_INFORMATION（本機無電池，無法驗證——同 AMD 原則）。

## 本輪決策
- 文件補錄：本部即為第四、五、六輪的彙整落點（第四五輪見第六部，第六輪見本部）。
- 第六輪三優先的開工條件：IntelMsr/PawnIO 驗證通過 → 先做第 5 點（黏滯位元，唯讀最安全）→ 再評估 AMD 塊（無法本機驗證，逐項標「本機未驗證」）與 Top-down（需 PMU 寫入，風險高於唯讀，建議排在 ring0 決策之後）。


### ✅ MSR 路徑已驗證打通（2026-08-29 深夜，本機實測）
`LibreHardwareMonitor.PawnIo.IntelMsr`（public、無參數建構）＋ `ReadMsr(uint index, out ulong value) → bool`
**成功讀到 MSR 0x10A**（回 0x0——7980XE 的微碼如實回報 ARCH_CAP 為空，這本身就是誠實資料）。
PawnIO 驅動會由 IntelMsr 建構式自行載入（不需 LHM Computer 先開）。探針程式碼留存於 `obj/_msrtest/Program.cs`
（上下文用盡前的驗證版本，含 AssemblyResolve 相依解析的完整寫法）。
**第五輪三項（RDT/MCA 銀行/ARCH_CAPABILITIES）與第六輪 MSR 項目的最後障礙已排除，可以開工。**
注意：RDT 需寫 PQR_ASSOC（MSR 寫入）與逐核親和，動工前照例向使用者明示。


---

# 第八部 ・ 第七輪（韌體／裝置樹／電源／無線等十四項）——2026-08-29 補錄

> 全文細節以使用者原文為準，此處錄結構、優先序與誠實紅線。本輪使用者未指定三優先；依零特權優先原則，**第 10 項（可靠性歷史）已於同日完成**（ReliabilityHistoryService＋健康頁卡片：非預期關機 41/6008、藍屏 BugCheck 1001、應用程式當機 1000/1002、開機耗時 Diagnostics-Performance 100）。
>
> 1. **UEFI 變數與 Secure Boot 四態**（GetFirmwareEnvironmentVariableEx＋SE_SYSTEM_ENVIRONMENT_NAME；PK/KEK/db/dbx 簽章清單解析、dbx 撤銷筆數與 BlackLotus 項、BootOrder/Boot####、OsIndicationsSupported、dbDefault 比對）＋Kernel DMA Protection＝完整開機信任鏈。市面工具幾乎沒人做完整。
> 2. **BIOS 設定 WMI 枚舉**（root\wmi：BiosSetting／HP_BIOSSetting／Lenovo_BiosSetting／DCIM_BIOSEnumeration）：名稱/目前值/可選值；**唯讀紅線**（寫入會讓機器開不了機）；X299-Server 大概率無此類別→優雅退化「本機不支援」。
> 3. **裝置樹 CM_Get_\***：CM_PROBLEM 精確原因碼（22/28/31/43/45/51）、資源描述（IRQ/IO/MEM/DMA 重疊=衝突）、PowerData（D-state 支援與現況）、DriverVersion/Date/Provider/**Signer**（自簽 oem47.inf 會現形）、LocationPaths（實體 slot/USB 埠）。殺手級應用：擋睡眠的裝置＋未簽名驅動＋資源打架一次解決。
> 4. **電源**：PowerReadACValueIndex 全子群組（含核心停放、PCIe ASPM、USB 選擇性暫停、隱藏設定）＋CallNtPowerInformation(ProcessorInformation) 逐核頻率百分比（停放實況）＋SYSTEM_POWER_CAPABILITIES 支援矩陣＋powercfg /requests 等價 API（誰擋睡眠）＋Kernel-Power ETW（S0ix 子狀態、喚醒原因）。
> 5. **ACPI 熱區與風扇**：MSAcpi_ThermalZoneTemperature＋_PSV/_AC0-9/_CRT/_HOT/_TC1/_TC2/_TSP/_FIF/_FPS——平台白紙黑字的溫度政策，與 LHM 風扇控制頁互補（不一致本身是資訊）。
> 6. **Wi-Fi/藍牙**：wlanapi（current_connection PHY/速率/RSSI＋BSS IE 解析 HT/VHT/HE/EHT 通道寬度/空間流/MU-MIMO/OFDMA/BSS Color/TWT＋介面能力）——「網卡×AP×協商」三欄併排；藍牙 RADIO_INFO 的 HCI/LMP 版本。
> 7. **虛擬化**：CPUID 0x40000000-FF 廠商簽章＋0x40000003/06 enlightenments＋SystemHypervisorDetailInformation。**關鍵誠實點：VBS/HVCI 開啟時 Windows 本身在 Hyper-V 上，MSR/TSC/PMU 可信度會變——必須先檢測再決定顯示什麼，不能默默給可疑數字**（前六輪的 MSR 讀值都受此影響）。
> 8. **微碼**：0x8B 讀生效 revision＋對照 Windows mcupdate/FeatureSettings——只說「BIOS 給 A、Windows 載 B」，不宣稱漏洞（免疫性交給 ARCH_CAPABILITIES）。
> 9. **BCLK**：MPERF(0xE7) 於 QPC 區間反算實際 BCLK，與 CPUID 0x15 標稱交叉驗證——差值＝BCLK 偏移（X299 可調，影響所有頻率讀值）。需 MSR＋親和（等 ring0 項目）。
> 10. ✅ **可靠性歷史**（已做，見上）＋後續可加 Kernel-Boot 逐次開機、Diagnostics-Performance 100-110 降級分析（含被點名元件）。
> 11. **記憶體管理真實面貌**：SystemFileCache/MemoryList（待命分優先級）＋壓縮存放區（MemCompression 行程工作集＋Memory 計數器——「已使用 12GB」的一半真相）＋認可尖峰超過實體＝曾動用分頁檔。
> 12. **行程緩解政策**：GetProcessMitigationPolicy 逐行程（DEP/強制 ASLR/高熵/CFG/CET 影子堆疊/子行程限制）＋CPUID 0x07 的 SMEP/SMAP/UMIP/CET。**紅線：列事實不打分、不說「你不安全」**。
> 13. **感測與周邊**：Sensor API（桌機全無就誠實說無）；EC IOCTL 風險高建議不做；**DDC/CI**（亮度/對比/輸入源/使用時數——少數安全的寫入操作）；HID report descriptor（DPI/NKRO/軸）；印表機掃描器跳過。
