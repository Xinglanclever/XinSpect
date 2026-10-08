# 整合優先、調度最後：對照清單的重寫（2026-10-09）

> 對象＝使用者提供的 Windows 底層工具清單（約 150 條）。原則＝**能整合進本體的一律整合（最高優先級）；把工作派給外部／中間層（調度）只在整合真的不可行時才用（最低優先級）。**
> 基準：`main` @ `f5e6d32`，v2.37，專案測試 3527 綠。
> 本文與前兩份的分工：`UNIFIED-PIPELINE-2026-10-09.md` 談**內部四套**（事實→規則→判決→行動）；`TOOL-CAPABILITY-GAP-2026-10-09.md` 談**本專案能力覆蓋**；本文談**每一項能力該用整合還是調度取得**。

---

## 一、判準：四級分類（先定級，再談優先順序）

| 級 | 名稱 | 定義 | 優先級 |
|---|---|---|---|
| **A** | 整合（原生） | 由本程式自己讀：WMI／MSR／PCI 設定空間／ioctl／PDH／SetupAPI／**原生 ETW 消費者**／純解碼器 | **最高** |
| **B** | 整合（OS 內建命令，取結構化輸出） | 呼叫 Windows 自帶命令，且輸出有穩定結構（XML／JSON） | 次之（準整合） |
| **C** | 調度（第三方工具） | 呼叫第三方 CLI／服務／引擎 | **最低**——須通過三條件檢驗 |
| **D** | 不整合 | 已有／與唯讀原則或既有裁決衝突／不在範疇 | 不做 |

**「調度」的准入三條件（須同時成立）：**
1. 該事實**沒有公開規格或穩定 API** 可在本機取得；
2. 且它**無法離線轉成本專案的事實模型**；
3. 且它的取得**不得引入網路、第三方執行檔或簽章驅動**（否則與「零上傳、單一執行檔」的賣點衝突）。

**任何一項不成立 → 走 A 或 B。** 這條判準要寫進 Help，否則「不可行」會變成誰都能自己定義的藉口。

---

## 二、三個改變判定的實測事實

這三件我實際在機器／程式碼上驗證過，它們讓清單中好幾項的判定從「要調度」翻成「可整合」：

1. **`pktmon` 是 Windows 內建**：`C:\Windows\System32\PktMon.exe`（`where pktmon` 實測）。但它的資料源是 **ETW**，命令列只是前端——所以「丟包歸因」可以走 A 級整合，不必呼叫這支 exe。
2. **本專案已經有原生 ETW 消費者**：`BlueSquadron/EtwWatcher.cs` 以 ETW Session 掛 `Microsoft-Windows-Kernel-Process`。**注意它位於獨立行程的守護進程專案 `BlueSquadron/`**（隨 `BlueSquadronBridge.exe` 出貨），不是 `Services/BlueSquadron/`（那是主程式的整合層）。這意味著：**凡是 ETW 可得的事實，本專案已有現成的取得路徑與先例**。
3. **`powercfg` 的診斷報告是本地化 HTML，沒有結構化輸出**：`powercfg /?` 實測確認 `/ENERGY`、`/BATTERYREPORT`、`/SLEEPSTUDY`、`/SYSTEMSLEEPDIAGNOSTICS`；help 中唯一的 XML 是 `/PROVISIONINGXML`（電源配置匯出，與診斷報告無關）。所以 `SleepDiagnosticsService`「**原樣呈現、不解析**」的決定是對的——那不是偷懶，是唯一不會在別的語言上靜默失效的做法。此類屬 **B 級**（OS 內建命令、輸出非結構化）→ 維持原樣呈現，**不升級為第三方調度**。

---

## 三、逐類對照（清單類別 → 原生通路 → 判定）

| 清單類別（代表） | 同一份事實的原生通路（A 級整合路徑） | 現況 | 判定 |
|---|---|---|---|
| 底層硬體讀寫（RWEverything／HE／PawnIO） | 白名單唯讀驅動＋PCI／MSR 讀取：`XsRegProbe`、WinRing0 | 已有 | **A 已完成**；寫入面不做 |
| 核心轉儲分析（WinDbg `!analyze -v`） | minidump／`DUMP_HEADER64` 為**公開格式** → 純解碼器可讀標頭、bugcheck 碼與四參數、載入模組清單 | `WheaErrorService`／`BugCheck`／`CrashLog`／`McaService` 已做線上讀取 | **A 部分可補**；**符號歸因不可整合**（需符號伺服器＝網路）→ 明確聲明做不到，不得假裝 |
| 記憶體診斷（MemTest86、WPMD） | 開機前測試無法在行程內做；Windows 記憶體診斷的結果**若存在**會在事件記錄通道裡 | 無 | **A 部分可補**；**但本機實測 `wevtutil gl MemoryDiagnostics-Results` ⇒ 找不到該通道**——所以它不是「讀就有」，必須以三態處理（沒有通道＝NotApplicable），不得假設存在 |
| PCIe 協議級（LeCroy、Kandou） | 無——需分析儀與 retimer 硬體 | `PcieAerDecoder`／`PcieLink`／`PcieNegotiationGap`（唯讀落差判讀） | **需硬體**；判讀已整合 |
| ETW／效能（WPT、PerfView、GPUView、xperf、ALPA、wpa-mcp、etw-mcp） | **原生 ETW 消費者**（已有先例）；`.etl` 離線解析可寫成純解碼器 | `BlueSquadron/EtwWatcher`、`DpcLatencyService`、`CoreLatencyService`、`InterruptAffinity`、`LatencyCurveService` | **A 已完成大半**，這條路上不該出現調度 |
| 儲存（smartctl、HD Tune、openSeaChest、SeaTools、Victoria、Clear Disk Info） | `SMART_RCV_DRIVE_DATA` ioctl、NVMe log pages、**`MSFT_StorageReliabilityCounter`**（可靠性計數器，本專案尚未讀） | SMART 屬性＋門檻、NVMe 健康／錯誤／WCTEMP、HPA、表面掃描、假容量驗證 | **A 已有**；補可靠性計數器 |
| GPU（GPU-Z、Nsight、GPUd、Brokkr、lsdsk） | NVML／NVAPI／D3DKMT；GPUd 的 Xid 可自驅動事件記錄取得 | NVML／NVAPI／EDID、GPU 深測（readback 逐位元組比對） | **A 已有**；可補 Xid 錯誤紀錄 |
| 網路（PsPing、Pktmon、netsh trace、NETworkManager、NetSonar） | 介面統計（`MSFT_NetAdapterStatistics`）、**ETW 丟包歸因**（見第二節事實 1） | 錯誤與丟棄計數、Wi-Fi、延遲量測 | **A 可補**（丟包歸因走 ETW，不呼叫 exe） |
| 電源（ThrottleStop、SleepStudy、powir、SMUDebugTool） | MSR 牆（溫度／功耗／電流）、`powercfg`（B 級、原樣呈現） | `CeilingService`、`SleepDiagnosticsService`、powercfg /energy | **已有**；SMU 需硬體 |
| ACPI（ACPICA／iasl、ACPI Debugger、windows-pc-stability-evaluation） | 表解析已有；AML 反編譯有公開規格可寫成純解碼器 | `AcpiTable`／`AcpiSlit`／`CxlCedt`、表頭校驗和 | **A 已有**；AML 可補但價值低 |
| 虛擬化偵測（PafishX、Coreinfo） | CPUID／hypervisor 簽章／VBS 查詢 | `PlatformTrustDecoder`、`VirtualizationJudge` | **A 已有** |
| 固件／UEFI（UEFITool、CERT UEFI Parser、CHIPSEC、efiXplorer、pybinwalk、reap-cli、Velociraptor） | **FV／FFS 為公開規格** → 純解碼器；ESP 唯讀掃描；**YARA 引擎本身不整合**（第三方引擎）→ 只整合「掃描結果的事實」 | 只有 `EfiSigListDecoder`（Secure Boot 簽章清單） | **A 可補**（FV／FFS 分解、ESP 掃描）；YARA 不整合 |
| 驅動驗證（Driver Verifier、DevCon、ApiValidator、DriverExplorer、DriverRookie、DrvEye、DriverSight） | **`WinVerifyTrust`＋WHQL 目錄驗證**（本機 API，可把「已簽章」由 Reported 升為 Measured）；**IOCTL／符號連結靜態掃描是 PE 解析，不需驅動** | 驅動簽章稽核、BYOVD 封鎖清單比對 | **A 可補**（簽章升級、IOCTL 靜態分析） |
| 內核／ARK（KSword、YDArk、NtWarden、Surveyor、WKE、kn-live-dbg） | 需自寫核心驅動（SSDT／回調／EPT 檢查） | 無 | **D**：與「白名單最小化驅動」原則衝突 → 需你重新裁決 |
| 記憶體取證（Volatility、KAPE、LovelyMem、Aetheris、BitProbe、tlvb、螢火） | 需記憶體傾印；本身是另一類產品 | 無 | **D**（不在範疇）；其中 **Glow 的能力（TPM／Secure Boot／VBS／HVCI／IOMMU）已整合** |
| 系統資訊（SPECS、NWinfo、PC-AI、laptop-check、OxidPulse、wfdiag、SysInfo、LiteMonitor、rtop／pstop、Zyphor） | 本產品核心 | 已有 | **A 已完成**；可借鏡其結構化輸出（JSON／YAML） |
| 匯流排協議（Bus Hound、USBPcap、Free USB Analyzer、PCAN-Explorer、BUSMASTER） | USB 協議捕獲需驅動／Npcap；CAN 需硬體 | USB 鏈路速度、`USBSTOR` 痕跡 | **D**：引入 Npcap／驅動違反條件 3 |
| Thunderbolt／USB4 | PCI 掃描與 ACPI 已可列出主機路由器與隧道設定 | `PciBus0Inventory`、ACPI | **A 可補**（中等價值） |
| EC 訪問（PawnIO LpcACPIEC、EC-Access-Tool） | — | 無 | **D**：與既有裁決（EC 埠不做）衝突 |
| 無線廠商工具（TI WiLink、Realtek LOG Tool） | 需廠商驅動與硬體 | Wi-Fi BSS／RSSI／頻道 | **D**：需廠商專有介面 |
| 音訊（PyAudioFixerWin11） | 音訊端點與格式列舉 | `AudioLatencyFactsService`、`AudioFormatDecoder` | **A 已完成** |
| 中斷／設備樹（LatencyMon、Deansbury） | DPC 已有；**設備樹用 SetupAPI**（清單明載）→ 可整合 | `DpcLatencyService`；裝置樹未做 | **A 可補**（SetupAPI 裝置樹，唯讀） |
| 安全事件／取證（Sysmon、SentinelX、ATool、Kdrill、Kellect） | ETW＋事件記錄＋驅動清單 | 已在做（含 Security 1102、Defender 排除、X509、BYOVD） | **A 已有** |
| 性能計數器（PerfMon、Get-PhysicalDisk） | PDH／`MSFT_StorageReliabilityCounter` | 部分 | **A 可補** |

---

## 四、最高優先級：整合候選（依「可驗收性 × 契合度」排序）

全部符合：唯讀、不需新硬體、可在本機驗收、能進入既有的 SpecRef 與突變測試文化。

| 順位 | 整合項目 | 為什麼是它 | 驗收 |
|---|---|---|---|
| 1 | **UEFI FV／FFS 結構分解** | 規格公開 → 純解碼器；輸入可用既有 BIOS 區或使用者提供的映像 | 輸出 FV→FFS→區段樹；解不開如實標示；真實傾印當 fixture |
| 2 | **驅動 IOCTL／符號連結靜態掃描** | 純 PE 解析，**完全不需要驅動**；與既有 BYOVD 清單天然交叉引用 | 掃出 IOCTL 與符號連結，對得上既有 BYOVD 命中項 |
| 3 | **`WinVerifyTrust` ＋ WHQL 目錄驗證** | 把「已簽章」從自述（Reported）升為實測（Measured） | 與 `Win32_PnPSignedDriver` 的 `IsSigned` 交叉對帳，不一致要能說出為什麼 |
| 4 | **ETW 丟包歸因** | 沿用 `EtwWatcher` 既有模式；**不必呼叫 PktMon.exe** | 給出丟包發生的堆疊層；無資料時如實說無資料 |
| 5 | **`MSFT_StorageReliabilityCounter`** | 清單中唯一明確「還沒讀」的結構化儲存來源；**本機已實測可用**（`Get-PhysicalDisk \| Get-StorageReliabilityCounter` 回來 Wear=0、Temperature=1，而 ReadErrorsTotal／PowerOnHours 為空） | 逐碟可靠性計數器進事實層；空的欄位要如實標「未提供」而不是填 0 |
| 6 | **離線 `.dmp` 標頭與 bugcheck 解析** | 格式公開 → 純解碼器 | 只列可讀欄位；**沒有符號時不得歸因** |
| 7 | **ESP 唯讀掃描** | EFI 系統分割區的內容是既讀得到的事實 | 列出 ESP 內的 EFI 執行檔與其簽章狀態 |

---

## 五、最低優先級：本清單裡**沒有一項需要第三方調度**

按第一節三條件逐一檢驗清單中最可能被判「不可行」的幾項：

| 本來像是要調度的 | 檢驗結果 |
|---|---|
| smartctl／GSmartControl | `SMART_RCV_DRIVE_DATA` ioctl 已能用（本專案在做）→ 條件 1 不成立 → **A** |
| Pktmon／netsh trace | 資料源是 ETW，本專案已有原生消費者 → 條件 1 不成立 → **A** |
| powercfg 系列 | OS 內建、輸出非結構化 → 屬 **B**：維持原樣呈現，不解析；**但這也不是 C**（不是第三方） |
| PerfView／WPT／xperf | `.etl` 可離線解析 → **A**（純解碼器） |
| WizTree（MFT） | 讀 MFT 需磁碟 raw 存取與檔案系統解析，且屬「磁碟清理」而非驗機 → 條件 3 與產品定位皆不成立 → **D** |
| WinDbg `!analyze` | 歸因需符號伺服器（網路）→ 條件 3 不成立 → **不做那一步**，而非「調度 WinDbg」 |

**結論：依這條判準，本清單中應該零調度。** 唯一的灰色地帶是 **B 級（OS 內建命令）**，而它已經用「原樣呈現、不解析」處理得當。

---

## 六、明文不整合（附理由）

| 項目 | 理由 |
|---|---|
| 任何**寫入**型能力（RWEverything 的寫、EC 寫、故障注入 NotMyFault／BurnInTest） | 與唯讀原則衝突；本專案少數寫入功能一律掛同意閘門，且只做「驗證資料完整性」而非「改硬體狀態」 |
| ARK／SSDT／內核回調完整性 | 需自寫核心驅動，與白名單最小化驅動的原則衝突 → **需你重新裁決**，不是技術不可行 |
| 記憶體取證框架 | 另一類產品（DFIR），且需傾印檔 |
| 廠商專用無線工具 | 需廠商驅動與硬體 |
| 協定分析儀／retimer | 需硬體 |
| YARA 引擎 | 第三方引擎；要的是「掃描結果的事實」，不是引擎本身 |

---

## 七、我沒查證的

- 清單中**任何一項工具**的存在、功能、授權，我都沒有逐一查證（清單本身也把 Surveyor／YDArk／NtWarden／CERT UEFI Parser／NetReplay／gpufl／SPECS／wfdiag 等整段重複列了 3–4 次）。因此本文一律以**能力**為單位，不替品牌背書。
- 第二節的三項實測限於**本機這一台 Windows**（`where pktmon`、`powercfg /?`、`BlueSquadron/EtwWatcher.cs` 的 provider 字串）。
- 一份 `.dmp` 的完整格式、`MSFT_StorageReliabilityCounter` 的欄位、FV／FFS 的完整結構，我引用的都是「有公開規格可依」這個層級的判斷，**尚未逐欄核對**。

---

## 八、原生自帶的：留下接口（不重寫、也不盲派）

OS 已經提供的能力（本文件的 **B 級**），既不該重寫一份，也不該盲目派给它——**留下接縫**，讓它在管線裡有一個誠實的位置。

### 8.1 接縫要照專案自己的慣例，不要自創

本專案已經有 63 個 interface，其中 `Services/AcpiTableService.cs` 的 `IAcpiTableSource` 正是這個用法：

```csharp
/// <summary>ACPI 表來源的可注入接縫：真實以 EnumSystemFirmwareTables + GetSystemFirmwareTable 取；測試注入合成表，不碰韌體。</summary>
public interface IAcpiTableSource
{
    bool Available { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<byte[]> ReadAll();
}
```

它可貴的不是介面本身，而是兩件事：

1. **依參數注入**（`AcpiService.Collect(IAcpiTableSource source, DateTimeOffset at)`）——不需要 DI 容器，測試直接注入合成來源；
2. **呼叫端第一件事就是處理不可用**：`Collect` 開頭就檢查 `Available`，不可用就吐一筆三態事實——所以「讀不到」永遠有位置放，不會靜默塌成「沒有」。

### 8.2 原生工具接縫的具體形狀

照同一個模板，原生自帶的工具來源應該是：

```csharp
/// <summary>OS 內建命令／工具的可注入接縫。真實以命令列取；測試注入固定輸出，不執行任何行程。</summary>
public interface INativeToolSource
{
    bool Available { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<NativeToolSection> Run();
}

/// <summary>一段原生輸出：命令、意義說明、以及<b>原樣</b>輸出。</summary>
public sealed record NativeToolSection(string Title, string Command, string What, string Output);
```

`NativeToolSection` **不是新發明**——它就是既有的 `SleepSection`（`Services/SleepDiagnosticsService.cs`：`Title`／`Command`／`What`／`Output` 四欄）推廣出來的。也就是說，**第一個實作者已經存在**。

### 8.3 三條守則

1. **照抄命令列。** 使用者要能自己再跑一次——這是「不替他下結論」的具體做法，不是方便而已。
2. **不解析在地化輸出**（見第二節事實 3）。判斷的是使用者，不是一段猜出來的字串比對。
3. **不可用要明示。** `Available=false` ＋ `UnavailableReason`，不得靜默缺席——這一條與主藍圖 §1.1 的六態是同一個要求。

### 8.4 適用清單（B 級）與**不適用**清單

| 走這個接縫（OS 內建命令、輸出非結構化） | 不走（A 級：走事實層） |
|---|---|
| `powercfg /energy`、`/batteryreport`、`/sleepstudy`、`/systempowerreport`（**`/systemsleepdiagnostics` 在本機仍在，但說明文字已寫「已改為 `/systempowerreport`」**；且這四條都是**會寫出報告檔**的子命令，與唯讀查詢不同類）| ETW 消費者（`BlueSquadron/EtwWatcher` 模式） |
| `pktmon`（若決定要露出命令列給使用者自行重跑） | WMI ／ PDH ／ `MSFT_StorageReliabilityCounter` |
| `netsh trace` | `SMART_RCV_DRIVE_DATA` ioctl ／ NVMe log pages |
| `mdsched`（記憶體診斷） | MSR ／ PCI 設定空間 ／ SetupAPI |
| `Get-PhysicalDisk`（PowerShell 內建） | 任何純解碼器（FV／FFS、`.dmp` 標頭、PE／IOCTL） |

**判別方式很簡單：** 有穩定 API／規格可自己讀的 → A 級走事實層；只有命令列且輸出在地化 → B 級走這個接縫。

### 8.5 一條自我約束：**不留沒有實作者的介面**

「留接口」不等於先把一堆空介面擺著。空介面在本專案的文化裡是死抽象——**沒有實作者就沒有測試，沒有測試就沒有守門**。

所以落地順序是「一個接縫 ＋ 它的一個真實來源」成對出現；而第一個要做的，就是把既有的 `SleepDiagnosticsService` 重構成 `INativeToolSource` 的第一個實作——**這是一次行為不變的重構**（它的四個區段本來就是這四欄），不是新功能，風險最低，而且立刻讓這個接縫有東西可測。

### 8.6 已落地（2026-10-09，同一天）

上節的落地順序**已經執行完**，而且形狀與 8.2 有多一處不同（見下）：

| 檔案 | 內容 |
|---|---|
| `Services/NativeToolSource.cs` | 新增 `INativeToolSource`（`Available`／`UnavailableReason`／`Run()`）＋ `NativeToolSection`。**與 8.2 草稿的差別：四欄是 `required init` 屬性而不是位置參數**——這樣 `Describe(...) with { Output = ... }` 才寫得出來，宣告與執行才能共用同一個建構點。 |
| `Services/SleepDiagnosticsService.cs` | 成為接縫第一個實作（`SleepSection` 併入 `NativeToolSection`，全專案已無殘留引用）。新增 `Describe()`（**宣告**：查哪五件事、命令怎麼寫、說明有沒有寫）與 `Run()`（**執行**）；新增 `Available`／`UnavailableReason`／`PowercfgPath`（查 `Environment.SystemDirectory` 下的檔案在不在，**不用猜、不吞例外**）。可用性是「**執行檔在不在**」而不是「查了之後有沒有輸出」——後者會把「指令不存在」與「指令跑了、而確實沒有東西阻止睡眠」混成同一種長相，後者是一個結論。 |
| `Tests/NativeToolSourceTests.cs` | 8 條，**刻意不開任何行程**（開行程就把這條接縫的用途堵死了）：驗五段宣告、四欄必填、命令前綴與五個參數照抄、`Describe()` 不得帶輸出、可用／不可用兩態一致性、假來源可注入。 |
| `Tests/TestSuiteBaseline.cs` ＋ 三份 README | 專案測試 3534 → **3542**；全套 **3543**（含未追蹤探針），0 failed。 |

**驗證方式（不是「跑過就好」）：** 把 `Describe()` 的命令前綴改成 `powercfgX ` 之後重跑，**該組紅 2 條**；還原後綠 8 條。這是「守門測試真的在守門」的證據，不是「測試檔存在」的證據。
