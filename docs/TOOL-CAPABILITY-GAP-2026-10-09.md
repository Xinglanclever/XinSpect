# 工具清單對照：XinSpect 能加什麼、學什麼、併入什麼

> 2026-10-09。對照對象：使用者提供的一份 Windows 底層診斷／工程工具清單（約 150 條）。
> 基準：`main` @ `f5e6d32`，v2.37，專案測試 3527 綠。

---

## 零、先講方法：為什麼我不逐一背書那份清單

三件事讓我決定**以「能力」為單位**對照，而不是以品牌名：

1. **清單內部大量重複。** Surveyor、YDArk、NtWarden、CERT UEFI Parser、NetReplay、gpufl、SPECS、wfdiag 這幾組各自被重複列了 3–4 次，段落幾乎逐字相同。
2. **有相當比例的名稱我沒有在公開來源找到可靠資料。** 我這次**沒有逐一查證**任何一項的存在、功能或授權（唯一的授權查詢沒有得到可用結果）。
3. 因此：**凡是宣稱「XinSpect 已有」的，我用本專案自己的程式碼當證據**；凡是我沒查證的外部工具，我只引用它代表的**能力類別**。

這樣做的好處是：即使清單裡混入了不存在的工具，本文件也不會把它的說法傳下去。

---

## 一、現況快照（2026-10-09，以程式碼掃描為準）

| 項目 | 數量 | 來源 |
|---|---|---|
| 事實生產者服務 | **56** | `Services/*.cs` 以 `IReadOnlyList<HardwareFact> Collect` 簽章掃描（文件先前寫 58，數法差異我沒深究） |
| 事實鍵 | **134** | `Services/FactKeyCatalog.cs`（v2.37 起為編譯期固定） |
| 對帳規則 | **26** | `FactRelationRules.All`（生產路徑那一份） |
| 純解碼器 | **45＋1** | `XinSpect.Decoders/*.cs`（含 1 個 `SpecRefAttribute` 基礎設施） |
| 專案測試 | **3527** | `Tests/TestSuiteBaseline.cs` |

貫穿全部功能的兩條原則：**唯讀為原則**（少數寫入功能另有同意閘門）、**三態誠實**（讀不到就說讀不到，不以 0／典型值／上次讀值填補）。

---

## 二、對照表

判定用語：**已有**＝本專案已經在做且有程式碼證據；**可補**＝唯讀、不需新硬體、可機器驗收；**需硬體／驅動**＝得先有對應條件；**與裁決衝突**＝已明文裁決不做；**不在範疇**＝屬另一類產品。

| 能力領域 | 清單代表 | XinSpect 現況（證據） | 判定 |
|---|---|---|---|
| 底層硬體唯讀存取 | RWEverything、HE、PawnIO | `XsRegProbe` 白名單唯讀驅動＋WinRing0 反射載入 | **已有**（且刻意限制在允許清單） |
| 核心傾印分析 | WinDbg `!analyze -v` | `WheaErrorService`、`WheaTimelineStore`、`McaService`、`CrashLog`、`BugCheck`；ACPI `BERT`／`CPER` 解析 | **部分 → 可補**（缺離線 `.dmp` 解析） |
| 記憶體診斷 | MemTest86 | `RowhammerProbeService`、`PmuProgrammingService`（編程驗證含還原）、深測記憶體項 | **需獨立環境**（開機前測試無法在 WPF 進程內做） |
| PCIe 協議級 | LeCroy Summit／LinkExpert、Kandou Besso | `PcieAerDecoder`、`PcieLink`、`PcieNegotiationGap`（唯讀落差判讀） | **需硬體**（分析儀／retimer） |
| 效能與 ETW | WPT／PerfView／GPUView、etw-mcp、ALPA | `Services/BlueSquadron`（ETW 即時）、`DpcLatencyService`、`CoreLatencyService`、`InterruptAffinity`、`LatencyCurveService`、`DeepBench` | **已有** |
| 儲存 | smartctl／GSmartControl、HD Tune、openSeaChest | SMART 屬性＋門檻（failing-now）、NVMe 健康／錯誤紀錄／WCTEMP、HPA、表面掃描、假容量寫入驗證 | **已有** |
| GPU | NVML／NVAPI、GPU-Z、Nsight | NVML／NVAPI／D3DKMT／EDID；GPU 深測以 readback 逐位元組比對 CPU 參考值 | **已有** |
| 網路 | PsPing、Pktmon、netsh trace | `MSFT_NetAdapterStatistics`（錯誤與丟棄）、wlanapi（RSSI／頻道換算）、延遲量測、網速 | **部分 → 可補**（缺堆疊層丟包點歸因） |
| 電源 | ThrottleStop、SleepStudy、powercfg | `CeilingService`（溫度／功耗／電流牆，直接讀 MSR）、`SleepDiagnosticsService`（powercfg 唯讀，**刻意不解析輸出**）、powercfg /energy | **已有** |
| 固件安全 | CHIPSEC | `ChipsetSecurity`、`PlatformSecurity`、`SpiFlash`、`SpiFlashMap`、`EntropyMap`、微碼雙來源對帳、EFI 變數四態 | **已有大半** |
| UEFI 映像結構 | UEFITool／UEFIExtract、CERT UEFI Parser | 解碼器清單中僅 `EfiSigListDecoder`（Secure Boot 簽章清單）；**沒有 FV／FFS 映像結構分解** | **缺 → 可補**（純解碼器） |
| NVMe／儲存深度 | openSeaChest、Clear Disk Info | 同「儲存」列，已涵蓋 log page 0x01／0x02 與電源狀態表 | **已有** |
| 故障注入／壓力 | NotMyFault、BurnInTest | `StressTestService`、`GpuStressService`、`DeepBench`；寫入類均掛同意閘門 | **不建議**（故障注入與唯讀原則衝突） |
| 驅動驗證 | Driver Verifier、DevCon、ApiValidator | 驅動簽章稽核（`Win32_PnPSignedDriver`）＋ BYOVD 封鎖清單比對 | **不在範疇**（開發者工具鏈） |
| 內核／ARK | KSword、YDArk、NtWarden、WKE | 無 SSDT／內核回調完整性檢查 | **需硬體／驅動**且與白名單最小化原則衝突 → 需裁決 |
| 記憶體取證 | Volatility、KAPE、LovelyMem | 無 | **不在範疇**（另一類產品，且需記憶體傾印） |
| ACPI 工具鏈 | ACPICA／iasl、windows-pc-stability-evaluation | `AcpiTable`、`AcpiSlit`、`CxlCedt`、表頭校驗和；缺 ASL 反編譯 | **可補但價值低** |
| EC 訪問 | PawnIO LpcACPIEC、EC-Access-Tool | 無 | **與裁決衝突**（「EC 埠」已明文不做） |
| USB 協議 | USBPcap、Bus Hound | 有 USB 鏈路速度與 `Enum\USBSTOR` 痕跡；無協議級捕獲 | **需額外驅動／Npcap** |
| 虛擬化偵測 | PafishX、Coreinfo | `PlatformTrustDecoder`、`VirtualizationJudge`、hypervisor 簽章、VBS／HVCI | **已有** |
| 系統資訊／可攜 | SPECS、NWinfo、NWinfo | 本產品核心；單一執行檔、免安裝、不上傳 | **已有** |
| 安全基線 | Glow（TPM2.0／Secure Boot／VBS／HVCI／IOMMU） | `Tpm2`、EFI 變數四態、`SecurityScoreEngine` 的 `dma.iommu` 檢查 | **已有** |

**結論一句話：這份清單與本專案的既有覆蓋面重疊度很高（上表 22 列有 11 列是「已有」）。真正值得動的缺口只有 3 個。**

---

## 三、我建議的三件事（依契合度排序）

### 1. UEFI 映像結構分解（FV／FFS／區段樹）——最契合本專案

**為什麼是它：** 規格公開（UEFI PI Spec 的 Firmware Volume／FFS），所以能寫成**純解碼器**；不需要硬體、不需要新權限、不需要寫入；輸入可以是使用者提供的映像檔，或本專案已經讀得到的 BIOS 區；而且可以立刻納入既有的 `SpecRef` 覆蓋檢查與 Stryker 突變範圍——這正是本專案唯一「用突變測試抓過真缺陷」的那條路。

**驗收標準：**
- 給定一個映像，輸出 FV → FFS → 區段（含 GUID、型別、位移、大小）的可導覽結構
- 對解不開的位元組**如實標示**，不猜測、不用 0 補齊
- `SpecRef` 覆蓋檢查通過（每個公開解碼方法都要有規格引用）
- 以**真實傾印**當 fixture，不以合成資料驗證位移（本專案在 NVMe 位移上吃過這個虧）

### 2. 離線 BSOD 傾印的結構化事實——延伸既有的 WHEA 工作

**現況：** WHEA／CPER／MCA／BugCheck 四個服務已經在做線上讀取，缺的是「使用者事後拿到的 `.dmp` 檔案」這條路。

**驗收標準：** 把 bug check 碼與四個參數、涉及的模組、時間讀成事實，並與 `WheaTimelineStore` 的時間軸併列；**沒有符號表時不得假裝歸因**（只列可讀欄位）。

### 3. 堆疊層網路丟包歸因——需要你先裁決

Windows 內建 `pktmon` 能回答「封包在哪一層被丟掉」，這是本專案目前只有介面層計數（錯誤／丟棄）而無法回答的問題。

**但要先想清楚一件事：** 本專案的主要讀取方式是自己讀（WMI／ioctl／MSR／PDH），但「呼叫外部行程」本身並不是禁忌——`Services/` 裡有 **20 個**服務會用 `Process.Start`／`ProcessStartInfo`（例如 `WinsatService`、`DnsService`、`WingetService`、`BiosMeService`）。真正值得照抄的是 `SleepDiagnosticsService` 的**那個選擇**：它**原樣呈現輸出、不解析**，因為 powercfg 的文字隨語言翻譯，字串比對會在別的語言上靜默失效——而「解析失敗」與「沒有東西阻止睡眠」長得一模一樣。

若要做，就照同一個模式：僅在使用者明確要求時執行、原始輸出原樣呈現、不解析。**要不要跨這條線是你的產品決定。**

---

## 四、學什麼：那份清單值得學的不是功能，是做法

1. **結構化輸出要當一等公民。** 清單裡被標為「SBOM-ready JSON」（CERT UEFI Parser）、「JSON／YAML／LUA」（NWinfo）的那幾項，賣點不是新功能而是**可被程式消費的輸出**。這正好對上本專案未決的 P0-1.4-5：目前 XML 只進不出、PDF 靠瀏覽器列印。
2. **不解析，勝過猜一個字串比對。** `SleepDiagnosticsService` 已經這樣做並寫下了理由；清單裡那一堆 powercfg 包裝工具正好是這條的反面教材。
3. **每個數字都要帶得出處。** 本專案的 `source`／`trust`／`availability` 三欄是清單中多數工具沒有的層次——這是優勢，不是待補的缺口。
4. **單一 portable 執行檔、零安裝、零上傳。** 清單把這點當新賣點在推（SPECS、NWinfo），本專案從第一天就是。

---

## 五、併入什麼：先講授權，再講方法

- 本專案授權是 **MIT**。清單中工具的授權我這次**沒有查證**（唯一的查詢沒有得到可用結果），所以我不會在任何工具名下寫「可以抄這段」。
- 我的全部建議都走**「依公開規格重寫成純解碼器」**，而不是搬移他人程式碼。這樣做：授權問題不存在、程式碼能進入本專案的測試與突變範圍、也不必引入任何執行期相依。
- **不要把清單裡的工具變成相依。** 本專案的差異化就是單一執行檔、免安裝、零上傳；引入外部工具鏈會直接把這個賣點拆掉。

---

## 六、界線：我沒做的事

- **沒有逐一查證**清單中任何工具的存在、功能或授權；本文件的「已有」全部以本專案程式碼為證據，「可補」全部以規格公開、唯讀可行為判準。
- 第三節的三件事**都還沒實作**，只是候選；第 3 件需要你先裁決能否呼叫外部 CLI。
- 對照表是**能力層**的判斷，不是對任何品牌的評價。
