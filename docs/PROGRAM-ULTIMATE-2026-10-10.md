# XinSpect 極致化總綱 — 2026-10-10

> 這一版的工作名稱：**「機制化」**。目標不是再多加幾個功能，而是把「靠記性維持的正確」全部換成「靠守門維持的正確」，
> 再把每一個既有能力往**底層／專業／可量測**推進一層。未納版控、可隨時刪。
>
> 本文件的所有數字都可重跑；每一條缺陷都附「檔案:行」等級的出處。**凡是我沒能親眼驗證的，一律標成推測。**

---

## 0. 怎麼讀這份文件

### 0.1 證據等級（每一條目都必須落在其中一級）

| 等級 | 標記 | 意思 | 讀者該怎麼用 |
|---|---|---|---|
| 實測 | 【實測】 | 我在這台機器上跑過指令／測試，輸出貼得出 | 可以直接當事實 |
| 定位 | 【定位】 | 程式碼就在那裡，我讀過；但沒在真機上觸發過 | 修之前先重現一次 |
| 檢索 | 【檢索】 | 由靜態掃描（grep／腳本）得出的統計或清單，**啟發式，可能誤判** | 當線索，別當判決 |
| 推測 | 【推測】 | 依機制推導的未來風險，尚未發生 | 當設計約束 |

**這份文件刻意不寫「應該」「感覺」「大概」**——凡是不確定的，就寫出「怎麼驗」。

### 0.2 三種讀者

- **要動手的人**：§3（已確認缺陷）→ §5（機制補強）→ §6（深化路線）。
- **要方向的人**：§1（現況）→ §2（功能全圖）→ §8（路線圖）。
- **要打臉這份文件的人**：§0.1 的證據等級 + 每節附的指令；照著跑，數字對不上就是我錯。

### 0.3 一件必須先講清楚的事

本專案最大的資產不是功能數量，而是**一條自我約束**：讀不到就說讀不到（三態）、不以 0 假冒、不靜默降級、
判讀一律附依據與界線。這條約束的維護成本會隨功能數線性上升——**所以下面的所有建議，都必須服從它，而不是繞過它**。
任何「為了讓畫面好看」而填值的提案，請直接退回。

---

## 1. 現況基線（全部可重跑）

### 1.1 版本與交付狀態

| 項目 | 值 | 查法 |
|---|---|---|
| 分支／HEAD | `main`，`76b2198`，與 `origin/main` 同步 | `git status -sb`、`git log --oneline -1` |
| 版號 | `2.43`（csproj／AboutView／三份 README 徽章一致） | `grep -n "<Version>" XinSpect.csproj` |
| 最新 tag | **`v2.42`**（v2.43 尚未發佈） | `git tag --sort=-v:refname \| head` |
| 測試 | **3631 總／3628 通過／3 失敗**，離開碼 1——3 條失敗全部歸因於**環境**（見下方註記） | 見 §1.4 |
| 專案測試數 | 3630（＝ `Tests/TestSuiteBaseline.cs` 與徽章；差 1 條是本機未追蹤探針） | `Tests/TestSuiteBaseline.cs` |

> **環境註記（2026-10-10 深夜的一次系統重啟之後，實測）**：這台機器現在只有**虛擬顯示轉接器**
> （MuMu／GameViewer／Virtual Display Driver，無硬體 GPU）→ 兩條 DeepBench D3D11 實測失敗
> （`GpuUnsupportedException`，DXGI `enum stop=0x887A0002`）；且作用中的電源計畫是**節電**
> （GUID `a1841308-3541-4fab-bc81-f71556f20b4a`）→ CPU 綁定的效能預算測試（20 輪掃描 < 60 秒）
> 實測 74.4 秒；同一時間全套由 57 秒變 121 秒。**這三條與本輪程式改動無關**（改動不在那些路徑上），
> 但它們是當前紅燈的真實歸因，不得當成「已經全綠」。
| 工作樹 | 27 個修改檔（+681／−138）＋ 6 個未追蹤新原始檔 | `git diff --stat` |

【實測】2026-10-10 這輪修掉的紅燈：`csproj` 已升 2.43 但 `ChangelogCatalog` 最新一筆仍停在 2.42，
四條版號守門測試同時紅。補上 `2.43` 紀錄（12 項）＋ 兩份 README 的版本沿革首筆後全綠。
**這正是本文件主張的「機制化」的最好例證**：版號寫在六個地方，靠人是守不住的，靠測試才守得住。

### 1.2 程式碼規模（排除 obj/bin/publish）

| 目錄 | 檔數 | 行數 | 角色 |
|---|---|---|---|
| `Services/` | 386 | 88,169 | 事實採集、判讀、量測（含 `DeepBench/` 35 檔） |
| `Views/` | 157 | 20,899 | 頁面（69 個 XAML 頁／視窗） |
| `Controls/` | 43 | 5,644 | 自繪控制項（每秒重畫的高頻路徑） |
| `Models/` | 28 | 3,435 | 資料形狀 |
| `ViewModels/` | 14 | 4,309 | 呈現邏輯 |
| `Nav/` | 7 | 4,003 | 導覽、說明目錄（`HelpCatalog` 269 條）、版號沿革 |
| `XinSpect.Decoders/` | 49 | 5,427 | **純解碼器**（無 I/O、可讀性最高、SpecRef 覆蓋） |
| `Tests/` | 309 | 48,088 | 2882 個測試方法 → **3630** 個測試案例（接線守門那一輪 +2） |
| 合計 | ~993 | ~180,000 | |

【檢索】指令：
```bash
for d in Services XinSpect.Decoders Views Controls Models ViewModels Tests Nav; do
  echo -n "$d: "; find $d -name "*.cs" -o -name "*.xaml" | grep -v obj | wc -l | tr -d '\n'
  echo -n " 檔，"; find $d -name "*.cs" -o -name "*.xaml" | grep -v obj | xargs wc -l | tail -1; done
```

### 1.3 事實表面（能力的可數化骨架）

- **事實鍵目錄 150 個**（`Services/FactKeyCatalog.cs`；接線守門那一輪由 146 增至 150），前綴分布：`gpu` 11、`spi` 9、`uefi` 7、`virt` 6、
  `platform`/`pmu`/`psu`/`pci`/`nic`/`ent`/`cpu`/`asset`/`acpi` 各 4、`usb`/`ups`/`time`/`mem`/`esp`/`dbg`/`cmos`/`chipset`/`boot`/`backend`/`audit` 各 3……
- **說明目錄 269 條**（`Nav/HelpCatalog.cs`），分組：`oc/` 19、`settings/` 17、`bench/` 17、`health/` 16、`cpu/` 16、`memory/` 12、`mainboard/` 12、`overview/` 9、`gpuoc/` 8……
- **英文字串表 1716 條**（`Services/EnglishStrings.cs`）。
- **內建規則** `Rules/builtin.json`。
- **不變量型測試 229 條**（測試方法名含「守門／必須／不得／一律／對帳／一致／覆蓋／不允許」等）。

【檢索】指令：
```bash
grep -oE '^        "[a-z0-9]+\.' Services/FactKeyCatalog.cs | sort | uniq -c | sort -rn   # 鍵前綴分布
grep -cE '^\s+\[".*"\] = "' Services/EnglishStrings.cs                                  # 字串表條目
grep -h "public void " Tests/*.cs | grep -cE "守門|必須|不得|一律|對帳|一致|覆蓋|不允許"    # 不變量測試
```

### 1.4 測試指令與現況輸出【實測】

```bash
cd /c/Users/Administrator/XinSpect
set -o pipefail
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo -p:BaseOutputPath=obj/_verify_243/
#  → 已通過! - 失敗: 0，通過: 3629，總計: 3629，持續時間: 57 s     離開碼 0
#    （這是 01:32 當時的輸出。同日 01:49 系統重啟後環境改變，現況見 §1.1 的環境註記：
#      3631 總／3628 通過／3 失敗，三條都是環境：無硬體 D3D11 配接器 × 2、電源計畫為節電 × 1）
```
> `-p:BaseOutputPath=` 是為了繞開 apphost 鎖：**絕不 taskkill**，換一條新輸出路徑就好。

### 1.5 這一節的結論（要拿它做決策的話）

1. 專案已到「**功能多到需要治理**」的規模（180k 行、993 檔、269 條說明、150 個事實鍵）。
   下一個數量級的成本不在「再加功能」，而在**接線、對帳、發佈**這三件治理工作。
2. 測試數量（3630）已經很可觀，但**測試守的是「實作了什麼」，還沒守「接上了沒有、發佈得出去沒有」**——
   §3 的三支事實服務、§3 的 tag 與連結不一致，都是這一類。
3. 因此本文件的主軸定為：**先把機制補齊（§5），再談深化（§6）**。

---

## 2. 功能全圖：十二個能力域的深化分析

每個域固定五段：**它回答什麼問題 → 資料從哪來（接縫在哪） → 誠實界線 → 現況缺口（有出處） → 深化路線（含驗收標準）**。
「深化路線」一律寫成可驗收的形式：**做到什麼程度算完成**，而不是「持續優化」。

---

### 2.1 域一：即時感測與熱／功耗（使用者最常看的門面）

**回答什麼**：現在幾度、跑多快、吃多少電、風扇轉多少。

**資料來源與接縫**：LHM（0.9.6 編譯期參考）＋ LHM 0.9.4（WinRing0 來源，**內嵌資源**）＋ Core Temp 共享記憶體；
`HeatScale` 是色階單一來源；`CoreHeatCell` 是熱區圖的活轉接；`CoreTempMapService` 提供逐實體核心摘要。

**誠實界線**：無讀值＝中性底色＋「—」，**不用 0 代替**；沒有逐核感測器的平台整張圖不出現；
驅動不可用時 MSR／PCI／I/O／MMIO 一律標「讀不到」而非靜默降級。

**現況缺口**
- 【定位】`Services/WinRing0Bridge.cs` 的 `Load` 內 `error = "";` 連寫兩行（無害，讀起來像漏刪）。
- 【定位】內嵌 0.9.4 的路徑推導已修好（不再寫死使用者名稱），但**開發機退路仍會讀 NuGet 快取**——
  兩條路徑的優先序有測試（`Tests/EmbeddedLhm094Tests.cs`），但**「內嵌資源缺失時建置就失敗」**這件事
  目前靠 csproj 的 `<Error>`；若有人改壞 `EmbedLhm094` 目標，第一個發現的是建置而不是測試。
- 【檢索】感測輪詢與 UI 更新的頻率關係沒有單一來源可查；`Dispatcher.Invoke` 18 處、`.Result/.Wait()` 19 處
  散落在服務與 UI 之間，是「1 Hz 更新下會不會卡」的風險集中地。

**深化路線**
1. **感測可追溯化**：每個感測值都帶「來源鏈＋取樣時間＋是否為推算」三欄（LHM／Core Temp／自算）。
   驗收：任一數字可回答「它從哪來、幾秒前量的、是不是估的」。
2. **熱圖的統計深化**：目前有最熱／最冷／平均／覆蓋率；再加**逐核溫差（max−min）隨時間的趨勢**與
   「偏熱核固定不變」偵測（散熱膏／壓合不均的機器指紋）。驗收：能只憑熱區圖判定「固定一角偏熱」。
3. **功耗面接線**：`PowerStateLatencyService`、`BoostRecoveryService` 等 DeepBench 能量測，但**沒有**
   持續記錄的輕量版；把 PL1／PL2 的**實際達成率**做成常駐取樣（不是一次性 bench）。
   驗收：跑 10 分鐘負載，能得到「宣告 4096 W、實測曲線」的落差表。
4. **風扇／電壓的閉環前先量測**：任何寫入（PWM、電壓）之前，先有「寫入前後的可比對量測」，否則無法宣稱效果。

---

### 2.2 域二：處理器微架構與底層暫存器（最「底層化」的一域）

**回答什麼**：這顆 CPU 真的具備什麼、跑到什麼狀態、限制在哪。

**資料來源與接縫**：`CpuId`（CPUID 葉）、`Msr`/`PmuProgrammingService`（MSR 讀**與少數寫**）、
`RdtService`（RMID/L3 佔用，含 `WriteMsrPair`）、`TopDownService`、`InvisibleStallService`（SMI／C-state 駐留）、
`UncorePmuService`、`TimerFoundationService`、`MicroarchProfile`、`FrequnecyTruthService`、`CoreLatencyHeatmap`。

**誠實界線**：編程驗證（PMU／RDT）只宣告「我設過且讀回符合預期」，**不宣稱硬體行為**；
能力位與生效位分開；`SignExtension`、保留位不當錯誤。

**現況缺口**
- 【定位】**`Services/UncorePmuService.cs` 只被測試引用**（`Tests/UncorePmuServiceTests.cs`），
  生產路徑零引用 → 這條能力做了沒接。
- 【定位】**`Services/AmdSmuService.cs` 同樣只被測試引用**（`Tests/AmdSmuServiceTests.cs`）——而它是**寫入路徑**的持有者（`WritePci`）。做好的 AMD SMU 讀取能力目前到不了畫面。
- 【定位】PMU／RDT 這類「會寫暫存器」的能力，缺少**寫入前的資源持有檢查**（例如 RMID 被別人佔用時
  的行為，見 `RdtService` 的 `MsrPqrAssoc` 收尾寫入）——目前沒有全域的「誰在寫 MSR」仲裁。
- 【檢索】`Services/TopDownService.cs`、`Services/RdtService.cs`、`Services/DramTrafficService.cs`、
  `Services/PmuProgrammingService.cs` 各自持有 `WriteMsr` 家族；`WinRing0Bridge` 是共同底層，
  但**沒有集中的寫入閘門（write gate）**與稽核（誰在什麼時候寫了哪個 MSR）。

**深化路線**
1. **接線 UncorePmu 與 AmdSmu**（見 §5.2 的守門測試會逼出所有這一類）。
   驗收：兩者的產出能以事實形式出現在對應頁面與 CLI。
2. **建立單一寫入閘門 `IWriteGate`**：所有 MSR／PCI／I/O 寫入必經此門，閘門負責
   （a）記錄前值→後值、（b）在 UI／CLI 標示「本次執行寫了 N 個暫存器」、（c）阻止未持有資源的寫入。
   驗收：`grep -rn "WriteMsrPair\|WritePciConfig\|WriteIoPortByte"` 的呼叫點**全部**經過閘門，
   且有測試斷言「未經閘門的寫入不存在」。
3. **微架構知識表外部化**：把 model→微架構／TMA 分類從程式碼（`Models/MicroarchProfile.cs`）抽成
   資料檔（可更新、可標註出處），程式碼只負責查表與「未收錄一律不猜」。
   驗收：新增一個世代不需要改 C#。
4. **底層能力的自證**：每一條 MSR 讀取都附「葉／位址／位元欄位／規格出處（SpecRef）」，
   並由 `SpecRef` 覆蓋測試強制（已有此機制，擴大覆蓋面即可）。

---

### 2.3 域三：記憶體子系統

**回答什麼**：插了什麼、跑多快、通道對不對、有沒有在偷降速、延遲從哪來。

**資料來源**：`SpdReader`／`SpdDecoder5`（SPD5118 hub、MR11 切頁、CRC-16）、`SmbiosService`、
`DimmLayout`／`DimmReferenceService`、`MemoryTruthService`、`MemoryTestService`、
`DeepBench/MemoryNumaTlbLargePageService`、`DramMappingInferenceService`、`RowhammerProbeService`、
`DramTrafficDecoder`／`DramTrafficService`（IMC 計數器）。

**誠實界線**：SPD 直讀失敗**不覆蓋** WMI 值（各自標來源）；理論上限的假設明說；`ECC` 三層不互相推論。

**現況缺口**
- 【檢索】直讀路徑（SMBus/SPD）與 WMI 路徑的**優先序與衝突處理**散在服務內，缺少統一的「來源裁決表」。
- 【定位】`RowhammerProbeService` 屬「有寫入風險」的實驗（若真做 hammer），目前是否僅讀取需逐一確認其界線文字。
- 【檢索】DDR5 頁切換（MR11）與 CRC 的錯誤路徑有測試（`SpdSurveyorTests`），但**真機是否曾在特定 DIMM 上失敗**
  沒有累積記錄（缺 corpus）。

**深化路線**
1. **來源裁決統一化**：所有記憶體事實走同一張「直讀 > WMI > 韌體表」裁決表，衝突時**兩邊都顯示**。
   驗收：任一記憶體欄位可回答「有幾個來源、誰贏、為什麼」。
2. **延遲歸因模型**：把 SPD 時序 + IMC 狀態 + NUMA 距離合成一個可解釋的延遲分解（哪一段貢獻多少）。
   驗收：與 AIDA64 類工具同機比較，誤差與差異原因都寫得出來（不追求一致，追求可解釋）。
3. **DDR5 訓練狀態**：讀 `MR` 相關訓練／DQ 眼圖狀態（若平台開放），標明「不支援的欄位」而不是留白。
4. **corpus 化**：把每次真機 SPD 直讀的「成功／失敗／CRC 失敗」匿名記錄，累積機型—模組相容性知識。

---

### 2.4 域四：韌體、開機與信任鏈

**回答什麼**：這台機器的韌體是否可信任、開機流程哪裡慢、Secure Boot 的撤銷清單裡有沒有我的檔案。

**資料來源**：`UefiFv`（FV/FFS 結構）、`UefiBootFactsService`（含 `EnableFirmwarePrivilege`）、
`UefiSignatureFactsService`（db/dbx/KEK/PK）、`EfiSigListDecoder`（新增 SHA-256 雜湊解出）、
**`EspScanService`（本輪新增：ESP 檔案層＋dbx 交叉）**、`SetupApiLog`（裝置安裝時間線）、
`BootBreakdownService`、`Tpm2`／`TrustRow`、`BiosWriteSurface`、`CmosTests`。

**誠實界線**：dbx 命中＝**攻擊面事實**，不是中毒判決；未命中不代表安全；ESP 列舉失敗／檔案讀不到如實標；
`SetFirmwareEnvironmentVariable` 類呼叫一律經權限檢查（`SeLastError=1300` 要查，否則假成功）。

**現況缺口**
- 【定位】**`Services/BootTimingFactsService.cs`（`boot.duration_ms`、`boot.last_time`）只被測試引用**，
  **鍵早在目錄裡**（`Services/FactKeyCatalog.cs:48-49`）→ 開機計時做了沒接，而目錄卻宣稱有它：覆蓋申報把從未生產的事實算成「已考慮」。**【2026-10-10 已接線，見 `docs/ROUND-WIRING-GUARD-2026-10-10.md`】**
- 【定位】`Services/UefiBootFactsService.EnableFirmwarePrivilege`／`UefiSignatureFactsService.ReadFirmwareVar`
  本輪由 `private` 改為 `internal`（給 `EspScanService` 用）——**這是「能力被借用」的訊號**：
  權限啟用與韌體變數讀取應該有自己的接縫型別，而不是靠 `internal` 打開。
- 【檢索】ESP 掃描上限 64 檔、超過如實截斷；但**沒有**「這台機器總共有幾個 .efi」的計數事實，
  讀者無法分辨「掃完只有 12 個」與「有 300 個、只列了 64」。

**深化路線**
1. **接線 BootTiming**（併入 §5.2 的守門）：`boot.duration_ms`、`boot.last_time` 進目錄並上頁。
2. **ESP 完整度事實**：新增 `esp.files.total`（未截斷前總數）與 `esp.scan.truncated`；
   驗收：任何一份報告都能回答「掃了幾個、總共幾個」。
3. **信任鏈閉環**：把「韌體宣告（db/dbx）→ 磁碟上的檔案（ESP）→ 執行期載入的驅動（Authenticode）」
   三層串成一條可追溯鏈，任何一層缺資料就標缺，不補。這是本專案最有價值的獨特能力方向之一。
4. **權限接縫抽出**：`IFirmwareVariableAccess`／`IPrivilegeEnabler` 兩個介面，`internal` 回歸 `private`。
5. **開機計時深化**：目前有總時長；再加「階段分解」（UEFI → OS loader → 驅動初始化 → 服務啟動），
   並標明來源（ETW／事件記錄／`setupapi`／`bootstat`）與各自誤差。

---

### 2.5 域五：儲存與可靠性

**回答什麼**：這顆碟會不會壞、資料寫下去真的落地了嗎、瓶頸在佇列還是在裝置。

**資料來源**：`StorageSmartService`（不再只報 SMART，含 failing-now／WCTEMP）、`NvmeLogDecoder`／`NvmePowerDecoder`、
`SataAnalysisService`、`M2AnalysisService`、`ChassisAndHpaFactsService`（HPA／機箱）、
`DiskSurfaceScanService`、`StorageQdJudge`（IOCP 佇列深度形狀）、`StorageReliabilityFactsService`（19 欄計數器）、
`DeepBench/StorageIocpEngineService`／`FlushDurabilityService`／`WriteIntegrityService`、`MediaVerification`。

**誠實界線**：SMART 值為 0 就是「讀到 0」，缺的欄位**數出並具名列出**；不安全關機不得多於通電次數（有對帳測試）。

**現況缺口**
- 【定位】**`Services/FakeCapacityTestService.cs`（假容量驗證）只被 `Tests/MediaVerificationTests.cs` 引用**——
  它是「驗機殺手級」能力，但生產路徑沒有呼叫點。**必須人工確認**：是 UI 走別的路徑，還是功能真的沒接上。
- 【檢索】可靠性計數器 19 欄的**時間序列**沒有進歷史倉（`HistoryStore` 目前追蹤 13 項指標，不含這些）。
- 【檢索】`DiskSurfaceScanService` 的全碟掃描**無中斷續掃**設計（掃到一半關掉＝白掃）。

**深化路線**
1. **確認並接線 FakeCapacity**；接上時必須保留「同意閘門」（它是寫入操作）。
2. **可靠性計數器進歷史倉**：19 欄逐欄記錄，並用 CUSUM 找變化點（`TrendSentinelService` 已有演算法）。
   驗收：能在寫入量暴增時自動指出「哪一天開始不對」。
3. **掃描可續**：把表面掃描的進度與已知良好區間落地，重啟後續掃；驗收：中斷後重跑不重掃已完成的區塊。
4. **寫入完整性常態化**：`StorageWriteIntegrityService` 的「寫入後讀回比對」目前是一次性 bench，
   可做成「每次報告匯出時抽驗最後寫入的檔案」，成本低、發現真問題。

---

### 2.6 域六：網路與外接介面

**回答什麼**：網卡健康嗎、offload 設對了嗎、延遲是鏈路還是軟體堆疊、USB／Thunderbolt 掛在誰下面。

**資料來源**：`NicHealthFactsService`／`NicStatsEntry`、`NetAdapterService`、`Bufferbloat`、
`LoopbackSignalService`、`DnsView`／`HostsEditorView`、`WifiSignalService`、`UsbTopologyService`／`UsbEdge`、
`TbUsb4Service`、`PcieAnalysisService`（供給 vs 線路速率落差）。

**誠實界線**：供給不足時明說「跑不滿」；上游讀不到一律「未判定」，不當成「上游沒有限制」。

**現況缺口**
- 【定位】**`Services/NetOffloadFactsService.cs`（`net.offload`）只被測試引用**，且鍵**不在**目錄（掃描器看不到以參數傳入的鍵）→ 做了沒接、目錄也不認得。**【2026-10-10 已接線＋補進目錄，見 `docs/ROUND-WIRING-GUARD-2026-10-10.md`】**
- 【定位】**`Services/LoopbackSignalService.cs` 只被測試引用** → 音訊回授量測（dBFS）沒有入口。
- 【檢索】`Bufferbloat`、`DnsView` 的判讀與 `NetworkSpeedService` 的結果**沒有在同一頁交叉**，
  使用者要自己拼「延遲高是 bufferbloat 還是 DNS」。

**深化路線**
1. 接線 NetOffload 與 LoopbackSignal（同 §5.2 機制）。
2. **單一「網路診斷結論」層**：把 bufferbloat／DNS／offload／鏈路落差四條證據合成一個**有依據的結論卡**
   （每條結論都帶它的量測與誤差）。驗收：只讀一張卡就能回答「慢在哪一段」。
3. **USB／TB 拓撲加深**：目前只畫掛載關係；補「實際協商速率 vs 裝置宣告」與「是否共用控制器頻寬」。

---

### 2.7 域七：顯示、GPU 與繪圖管線

**回答什麼**：這張卡真的在做什麼、編碼器是不是硬體、幀時間抖動來自哪、畫面延遲鏈路哪一段最長。

**資料來源**：`AmdAdlFactsService`（ADL 唯讀）、`LevelZeroFactsService`（Intel Level Zero）、
`GpuCodecService`、`PresentFramePacingService`、`DpcLatencyService`、`HdrCapabilityService`、
`DeepBench/` 的 D3D11 系列（VRAM／Raster／FP32／PCIe／DispatchJitter）、`GpuStressService`、`FrameTimeService`。

**誠實界線**：Media Foundation 實際選用的編碼器**無法逐幀宣稱**（服務內已明文標示）；
非本機硬體路徑全數「未在本機驗證」。

**現況缺口**
- 【檢索】GPU 量測服務大量使用 D3D11 原生互操作（`Services/DeepBench/Interop/D3D11Native.cs`），
  這些引擎類別（`D3D11Fp32ComputeEngine` 等）**只被自己的檔案與測試引用**——
  表示接縫設計讓它們可測，但也提醒：**GPU 能力的真機驗證面很窄**。
- 【定位】`PresentFramePacingService` 與 `FrameTimeService`、`DpcLatencyService` 各自量測「一部分」，
  沒有統一的「延遲鏈路預算表」（哪一段佔幾 ms、哪一段還沒量）。

**深化路線**
1. **延遲鏈路預算表**：輸入→呈現的每一段（驅動 DPC → 佇列 → 合成 → 呈現 → 掃描輸出）都給出「已量／未量／量不到」。
   驗收：任何缺段都看得出來是「量不到」而不是「沒有」。
2. **硬體編碼器的事實化**：以 `MFTEnumEx` 列舉為事實（誰在），並用「編碼器選用」的間接證據（GPU 計數器）
   佐證；無法證明的一律標「無法逐幀確認」。
3. **GPU 能力的多廠商擴充**：目前 ADL（AMD）＋ Level Zero（Intel）；補 NVML／NVAPI 的讀取（唯讀），
   仍遵守「未在本機驗證」標註。

---

### 2.8 域八：作業系統、驅動與啟動面

**回答什麼**：這台機器上跑著什麼、誰在開機時被載入、有沒有可疑的驅動與服務。

**資料來源**：`EventLogSummaryService`、`DriverAuditDecoder`／`DriverInspectionFactsService`（PE 靜態檢視）、
`ServiceInventoryService`、`ScheduledTaskStartup`、`WindowsUpdateHistoryService`、`OptionalFeatureService`、
`CleanupService`、`WingetService`、`DiagService`。

**誠實界線**：驅動盤點**不載入、不呼叫 IOCTL**；CTL_CODE 只給「候選」；IOCTL 號與能力位兩邊一致（有測試）。

**現況缺口**
- 【檢索】`DriverAnalysisService` 有多個只在檔內使用的型別（`KnownBadDriver`、`RecommendPriority`…）——
  屬正常設計；但**BYOVD 清單的更新機制**若寫在程式碼裡，就會隨版本僵化（需確認來源是否為檔案／資料）。
- 【檢索】`Services/UefiBootFactsService.cs(114)` 有未使用變數 `attrib`（warning CS0219）；
  `Services/SecurityAuditFactsService.cs:101`、`Services/SmartFailingNowFactsService.cs` 有可空警告 CS8629/CS8604。
  **這些警告不是錯，但「已知警告清單」目前沒有守門**——久了就沒人看。

**深化路線**
1. **警告基線化**：把編譯警告數量寫進測試（像測試數基線一樣），只允許下降。
   驗收：新增警告就紅燈。
2. **驅動／服務的「變化監看」**：目前是快照；補「與上次相比多了什麼」的差異事實。
   驗收：報告能回答「這週新裝了哪些驅動」。
3. **啟動面統一**：把排程工作、服務、啟動項、驅動載入合成一張「開機時誰先跑」的表。

---

### 2.9 域九：安全稽核與信任

**回答什麼**：這台機器的攻擊面在哪、有沒有被動過、我能不能證明。

**資料來源**：`ByovdCompare`、`DefenderExclusion`、`SecurityAuditFactsService`（1102）、`PlatformTrustService`、
`VirtualizationJudge`、`MemoryEncryption`（TME/SME/SGX EPC）、`SpiFlashService`／`BiosWriteSurface`、
`AuditLogService`（+`AuditVerifier`）、`DataSovereignty`、`EcRisk`。

**誠實界線**：矛盾≠中毒（判決卡列出雙方數值）；VBS 讀不到**不得**當成關閉；
`AuditResultHash` 之類的鏈結要能驗（`AuditVerifier` 已有，但**只被測試引用**→ 見下）。

**現況缺口**
- 【定位】**`Services/AuditLogService.cs` 的 `AuditVerifier` 只被測試引用**——審計日誌的驗證器沒有入口。
  這很關鍵：**能產生審計日誌，卻不能在 App 內驗證日誌沒被改**，「可證明」的主張就打了折。
- 【定位】`Services/DeepAccessService.cs` 的 `X509TrustStore`、`ScmDriverService` 等接縫只被測試引用
  （合理：介面實作由生產者提供），但需確認**深層存取豁免開關**的實際收斂點只有一處。
- 【檢索】安全性質（SPI 寫入面、SMM 鎖定、BIOS 寫入保護）有大量「讀不到＝三態」，
  但**沒有集中一頁**回答「現在這台機器有哪些可被軟體寫入的韌體面」。

**深化路線**
1. **驗證器入口化**：UI／CLI 都能「驗證審計日誌」（顯示鏈結完整／斷點位置）。
   驗收：篡改一行後，驗證能指出第一個不對的位置。
2. **攻擊面一張表**：把所有「可被軟體寫入」的路徑（MSR／PCI／MMIO／SPI／韌體變數／驅動服務控制）
   集中列出，標「本次執行是否寫過」。這也正好是 §5.3 寫入閘門的輸出。
3. **風險評分科學化**：`docs/EC-RISK-ASSESSMENT.md` 已有骨架；把分數的每個輸入都變成事實鍵，
   可重算、可回溯（現在的評分若含人工判斷，必須標明）。

---

### 2.10 域十：深測與量測科學（DeepBench）

**回答什麼**：不是「跑分」，而是「用可重複的量測回答具體問題」。

**資料來源**：`Services/DeepBench/` 35 檔：`DeepBenchCatalog`（能力清單）、`SuitePlanner`、`MeasurementStatistics`、
`ConfidenceEngineService`、`GauntletMultiDomainService`、`TopologyCoreBandwidthService`、`SmtContentionService`、
`MemoryNumaTlbLargePageService`、`StorageIocpEngineService`、`PowerStateLatencyService`、`AudioBufferGlitchService`、
`CryptoMicrobenchService`、`CpuMicroarchBenchService`、`UxSyntheticWorkloadService`、`ReviewFixProbes`。

**誠實界線**（已做得很好的部分，應繼續）：驗證例外（`*ValidationException` 一族）＝「量測條件不成立就拒絕出數字」；
`DeepBenchHonestyTests` 專測誠實性；並行安全（`DeepBenchParallelSafety`）與資源類別（`DeepBenchResourceClass`）分級。

**現況缺口**
- 【檢索】35 檔中大量 `I*Engine` 介面只被自己的檔案與測試引用（設計使然），
  但**沒有「每個引擎在真機上跑過一次並留下結果」的記錄**（真機實測記錄散落在 changelog 文字裡）。
- 【檢索】統計核心 `DeepBenchMeasurementStatistics` 有測試，但**沒有外部金標**（例如與已知分布的合成資料比對）。

**深化路線**
1. **量測護照（measurement passport）**：每一輪 bench 產出一份可保存的紀錄：硬體指紋、條件、
   取樣數、統計量、離群處理、演算法版本。驗收：同一份護照可在別台機器重跑並比對。
2. **金標向量**：為統計核心與各引擎的「純計算部分」建立金標（合成資料 + 期望值），
   並納入突變測試（`StrykerOutput/` 已存在，分數 82%）。
3. **量測方法論外部化**：`docs/MEASUREMENT-METHODOLOGY.md` 應由程式產生一半（哪些量測、誤差怎麼估、限制），
   避免文件與程式漂移。

---

### 2.11 域十一：呈現層（每秒重畫的那一層）

**回答什麼**：把上面所有事實，變成「一眼看得懂、看得出來源、不騙人」的畫面。

**資料來源**：69 個 XAML 頁、43 個自繪控制項、`Themes/Theme.xaml`、`HeatScale`（單一色階來源）、
`LanguageService`（繁／簡／英三模式）、`SectionHead`／`HelpKey` 說明入口（269 條）。

**誠實界線**：缺讀值留白（熱區圖已落實）；`TComposite` 處理拼出來的字串；綁定字串不得被當行內文字寫死。

**現況缺口**
- 【檢索】`async void` 52 處（Views／Controls／Dialogs）——WPF 事件的常態，但每一處都是
  「例外會炸在 Dispatcher 上」的風險點；`.Result/.Wait()` 19 處是 UI 凍結的風險點。
- 【檢索】僅有少數自繪控制項有算繪測試（`CoreHeatmapRenderTests` 是這輪的產物）；
  其餘自繪控制項（`SpectrumBar`、`RadialGauge`、`HistoryGraph`、`Oscilloscope`…）**缺算繪層級的驗證**。
- 【檢索】語言三模式的覆蓋以「字串表 1716 條 vs 說明 269 條」維護，
  但**新增字串是否進入字串表**沒有守門（只有「已知未收錄」的人工清單）。

**深化路線**
1. **算繪快照守門**：把關鍵自繪控制項的算繪輸出存成基準圖，改動時比對像素（容忍度可設）。
   驗收：色階或版面意外改變時測試紅燈。
2. **字串覆蓋守門**：掃 XAML／C# 的中文字面值，凡未登記字串表者一律紅燈（可用白名單逐步收斂）。
3. **UI 執行緒衛生**：把 52 處 `async void` 收斂為 `AsyncRelay`／統一錯誤上報；
   19 處 `.Result` 逐一改為 `await` 或明確標註「為何必須同步」。
4. **無障礙與可讀性**：對比度（`HeatScale.InkFor` 已有）擴大到全部自繪控制項；鍵盤可達性檢查。

---

### 2.12 域十二：交付、運維與治理

**回答什麼**：能不能信任地發佈、使用者能不能拿到、壞了能不能查。

**資料來源**：單檔發佈（自包含）、內嵌驅動（`EmbedLhm094`）、`CliService`、`LocalApiHandler`、
`ExternalReportService`、`CrashLog`、`AuditLogService`、`SelfTelemetry`、`FeedbackService`、
`Installer/`、`cloudflare/ai-proxy`、`BlueSquadron/`。

**現況缺口**
- 【定位】**`Services/LocalApiHandler.cs` 只被測試引用** → 本機 API 有處理器、**沒有伺服**。
  「本機 API／查詢語言」在沿革裡是賣點，但實際入口不存在（除非另有伺服層，需人工確認）。
- 【實測】**三份 README 的下載連結指向尚未發佈的 v2.43**（`git tag` 最新 v2.42）→ 現在點下去 404。
- 【實測】`README.md:85` 宣稱「位元組數為本版（v2.43）實際發佈」，數字仍是 v2.42 那組。
- 【定位】`Services/CpuzReportService.cs:392` 與 `Services/AiService.cs:428` **寫死 `C:\Users\Administrator\...` 路徑**
  （與剛修掉的 WinRing0 是同一類缺陷）。
- 【檢索】`#pragma warning disable`、`catch { }`、`TODO` 沒有統一政策；`TODO/FIXME` 為 0 條（好事，值得守住）。

**深化路線**
1. **發佈程序自動化**：一支腳本依序做「升版號六處 → 建置 → 測試 → 發佈 → 補位元組數 → 驗證連結」，
   每步都失敗即停。驗收：發佈後 `curl -I` 三個下載連結全部 200。
2. **寫死路徑守門**：測試斷言生產碼不得出現 `C:\Users\` 之外還包含「使用者名稱」的字面值（含 `Administrator`）。
3. **崩潰與診斷閉環**：`CrashLog` ＋ `DiagService` 合流成一頁「這台機器發生了什麼」。
4. **本機 API 決策**：要嘛接上（含 loopback 綁定、token、僅本機），要嘛標為未實作並從沿革移除——
   留著半成品最傷信任。

---

## 3. 已確認缺陷清單（分級）

分級標準：**P0**＝會讓使用者看到錯的東西或拿不到東西；**P1**＝機制缺口，會持續產生同類缺陷；
**P2**＝技術債與風險，還沒咬人但會咬。

> 每一條都寫成「症狀／證據／根因／修法／回歸測試」。沒有回歸測試的修法，一律視為沒修完。

### 3.0 本輪已修（實測通過）

| # | 症狀 | 根因 | 修法 | 驗證 |
|---|---|---|---|---|
| F-1 | 四條版號守門測試紅燈：`csproj` 已 2.43、`ChangelogCatalog` 最新仍是 2.42 | 版號寫在六處（csproj／三份徽章／AboutView／沿革），靠人同步 | 補 `2.43` 紀錄＋兩份 README 沿革首筆 | 當時：`ChangelogTests` 11/11、全套 3629/3629、離開碼 0（後續環境變動見 §1.1） |
| F-2 | 三支事實服務「做完沒接線」，且目錄已宣稱它們存在；另四個鍵已生產卻沒登記 | 服務與接線是兩件事，而**沒有守門在盯接線** | 接進 `LoadUsermodeFacts`／`AllFacts`；目錄 146 → 150；新增 `Tests/WiringGuardTests.cs` | 受影響三組守門 24/24 綠、正對照紅燈、接線後生產引用 0 → 2（詳見 `docs/ROUND-WIRING-GUARD-2026-10-10.md`） |

### 3.1 P0：使用者拿不到或看到錯的

#### P0-1 三份 README 的下載連結指向尚未發佈的版本【實測】

- **症狀**：README 的 `XinSpect.exe`／`BlueSquadronBridge.exe` 連結是 `/releases/download/v2.43/...`；
  但 `git tag --sort=-v:refname | head` 最新是 **v2.42** → **現在點下去 404**。
- **根因**：升版號（六處同步）與「發 Release」是兩個動作，守門測試只檢查**文字一致性**，
  不檢查**目標是否存在**（沒有這個能力的測試）。
- **修法**：(a) 立刻補發 v2.43；(b) 加一條「發佈後檢查」腳本（`curl -I` 三個連結必須 200）；
  (c) 長期：把版本沿革與下載連結改成**由 Release 產生**，不手寫。
- **回歸測試**：`Tools/verify-release.ps1`（或加入 CI 的 post-release job），失敗即停。

#### P0-2 `README.md:85` 的位元組數與它自己的宣稱不符【實測】

- **症狀**：該行寫「位元組數為本版（**v2.43**）實際發佈的檔案大小」，表裡數字（30,483,243／6,502,948）
  是 v2.42 的（與 `76b2198` 同組）。
- **根因**：本專案的慣例是「發完版再補一筆 docs commit」（見 v2.38～v2.42 的 docs 提交），
  這一輪把版號往上跳時，**文字跟著跳、數字沒跳**。
- **修法**：發 v2.43 後量測真實位元組數並更新；並把「宣稱版本」與「數字來源版本」綁成同一個欄位。
- **回歸測試**：新增測試——README 該行的版號必須等於「最後一筆 docs 實測提交」的版號註記。

#### P0-3 `audio.endpoints` 在事實鍵目錄裡，但沒人生產它【定位】

> **狀態：已修（2026-10-10 同一輪）**——三支事實服務已接進 `LoadUsermodeFacts`／`AllFacts`，
> 並新增 `Tests/WiringGuardTests.cs` 守門（事實服務沒有生產碼引用即紅燈）。

- **症狀**：`Services/FactKeyCatalog.cs:40` 有 `audio.endpoints`；實際會產生它的
  `Services/AudioEndpointFactsService.cs` **在生產碼中零引用**（只有 `Tests/AudioEndpointFactsTests.cs`）。
- **為什麼是 P0**：目錄是「這版有哪些事實」的公開承諾，也是覆蓋率申報的依據。
  目錄說有、執行期沒有 → **覆蓋率申報是假的**，而覆蓋率申報正是 v2.36 才建立的治理機制。
- **根因**：目錄是**編譯期掃描原始碼字面值**產生的，只證明「字串存在」，不證明「會產生」。
- **修法**：(a) 接線該服務；(b) 把目錄的來源改成「執行期實際註冊」，或加一條**接線守門測試**（見 §5.2）。
- **回歸測試**：見 §5.2 的 `每個事實服務都必須被接線()`。

### 3.2 P1：機制缺口（會持續生出同類缺陷）

#### P1-1 七個服務「做了沒接」【定位】

| 服務 | 應該提供的能力 | 生產引用 | 測試引用 |
|---|---|---|---|
| `Services/BootTimingFactsService.cs` | 開機計時（`boot.duration_ms`／`boot.last_time`） | **0** | 1 |
| `Services/NetOffloadFactsService.cs` | 網卡卸載（`net.offload`） | **0** | 1 |
| `Services/AudioEndpointFactsService.cs` | 音訊端點（`audio.endpoints`） | **0** | 1 |
| `Services/UncorePmuService.cs` | uncore PMU 讀取 | **0** | 1 |
| `Services/AmdSmuService.cs` | AMD SMU 讀取（**含 `WritePci` 寫入路徑**） | **0** | 1 |
| `Services/LoopbackSignalService.cs` | 迴路訊號／dBFS 量測 | **0** | 1 |
| `Services/CorpusUploadService.cs` | corpus 貢獻建構／序列化 | **0** | 1 |

- **證據指令**：
  ```bash
  for s in AudioEndpointFactsService BootTimingFactsService NetOffloadFactsService \
           UncorePmuService AmdSmuService LoopbackSignalService CorpusUploadService; do
    echo "$s 生產引用=$(grep -rlw "$s" --include=*.cs --include=*.xaml . | grep -v '^./obj\|^./bin\|^./Tests' | grep -v "/$s.cs" | wc -l)"
  done
  ```
- **根因**：這是一個**結構性**現象，不是七次疏忽——本專案的服務一律「先寫服務＋接縫＋測試」，
  接線（UI／CLI／`AllFacts`）是**另一件事**，而**沒有任何守門在盯那件事**。
- **修法**：先加守門（§5.2），再逐條接線（§6 的 T1）。
- **回歸測試**：`Services/AudioLatencyFactsService.cs` 是對照組（被 `EvidenceLabService.cs:301` 接上），
  守門測試要能區分這兩種狀態。

#### P1-2 兩個「入口」級的服務只被測試引用【定位】

| 服務 | 應有的能力 | 影響 |
|---|---|---|
| `Services/LocalApiHandler.cs` | 本機 API／查詢語言（沿革裡的賣點） | 有處理器、**沒有伺服** → 功能實際不存在 |
| `Services/AuditLogService.cs` 的 `AuditVerifier` | 驗證審計日誌未被篡改 | 能產生日誌、**不能在 App 內證明它沒被改** |
| `Services/FakeCapacityTestService.cs` | 假容量驗證（「驗機殺手級」能力） | 只在 `Tests/MediaVerificationTests.cs` 出現，UI 入口需人工確認 |
| `Services/RuleLoader.cs` | 外部規則載入（`Rules/builtin.json`） | 只在 `Tests/RuleEngineTests.cs` 出現，規則引擎實際餵什麼需確認 |
| `Services/VerifyRules.cs` | 驗算規則表 | 只在測試出現（可能設計如此，需人工確認） |

- **修法原則**：這五條**不能盲目「接線」**——語意上可能是「測試專用工具」。
  正確做法是先**判定性質**（功能本體／測試工具），再決定「接上」或「標明測試專用並移出 Services/」。
  留著模糊狀態最貴。
- **回歸測試**：新增 `Services/` 的「入口分類」註記慣例（見 §5.2 的守門測試），分類必須顯式。

#### P1-3 生產碼寫死使用者名稱路徑【定位】

- **證據**：
  - `Services/CpuzReportService.cs:392`：`C:\Users\Administrator\Desktop\图吧工具箱\处理器工具\CPUZ\cpuz_x64.exe`
  - `Services/AiService.cs:428`：`C:\Users\Administrator\PaddleOCR-VL`
- **危害**：與剛修好的 WinRing0 是同一類——**只有這台開發機能用**，且失敗時若被當成「找不到工具」，
  使用者看到的會是功能缺失而不是錯誤。
- **修法**：改為「由環境推導＋使用者可設定路徑＋找不到時如實標示」，並比照 `NuGetCacheCandidates()` 的寫法。
- **回歸測試**：見 §5.5 的 `生產碼不得寫死使用者名稱路徑()`。

#### P1-4 `WinRing0Bridge.Load` 的重複賦值【定位】

- **證據**：`Services/WinRing0Bridge.cs` 的 `Load` 開頭 `error = "";` 連寫兩行。
- **性質**：無害，但**是「改到一半」的味道**；這類殘留通常伴隨真正的漏改。順手清掉。
- **回歸測試**：不需要（純清理）；但可用 IDE 分析器（見 §5.6）。

### 3.3 P2：技術債與風險

| # | 項 | 量 | 出處 | 風險 |
|---|---|---|---|---|
| P2-1 | 空／單行 `catch` | **369 處** | `Services` 49、`Bridge` 32、`Tests` 21、`Services/Overclock` 16、`Services/BlueSquadron` 15、`BlueSquadron` 14、`Services/Gpu` 5… | 有些是刻意的「附加功能失敗不中斷啟動」，但**沒有區分「可以吞」與「不該吞」** |
| P2-2 | 測試中的絕對日期 | **34 處／9 檔** | `BenchLogTests`、`CrashLogTests`、`DeepBenchMeasurementTests`、`DiagTests`、`DriverAuditTests`、`EtwTraceServiceTests`、`HistoryAndEventsTests`、`MachineAgeDecoderTests`、`TrendSentinelServiceTests` | 其中 3 檔同時用 `UtcNow`（時間炸彈高危）；`TrendSentinelServiceTests` 已炸過一次（8 條斷言同時紅） |
| P2-3 | `async void` | **52 處**（Views／Controls／Dialogs） | — | 例外會炸在 Dispatcher；WPF 事件常態但需收斂 |
| P2-4 | `.Result`／`.Wait()` | **19 處** | — | UI 凍結、死鎖風險 |
| P2-5 | 編譯警告 | 多條（CS0219／CS8604／CS8629／xUnit 分析器） | e.g. `Services/UefiBootFactsService.cs:114`、`Services/SecurityAuditFactsService.cs:101` | **無基線**：新增警告沒人會發現 |
| P2-6 | 自繪控制項算繪驗證 | 43 檔中僅少數有算繪測試 | `CoreHeatmapRenderTests`（本輪新增） | 版面／色階回歸無網 |

- **修法**（依序）：
  1. `catch { }` 分級：可吞的一律寫明理由（現行慣例已有註解，**把註解變成守門**：`catch` 後必須有說明標記）。
  2. 時間炸彈：全部改相對時間或注入 `IClock`（§5.4）。
  3. 警告基線化（§5.6）。
  4. 算繪快照（§5.7）。

---

## 4. 未來可能的缺陷（依機制分類，附偵測法）

這一節不是「猜想清單」，而是**每一類都給出「怎麼在它咬人之前發現」**。
凡標【推測】者，尚未發生；對應的偵測法都是可以今天就寫成測試或腳本的。

### 4.1 機制 A：時間（絕對時間 × 相對視窗）

- **為什麼會壞**：只要測試或邏輯把「某一天」寫死，而查詢窗用 `UtcNow` 相對計算，
  當天會過、隔天就紅。這已經發生一次：`Tests/TrendSentinelServiceTests.cs` 的 `t0` 原本寫死
  `2026-10-08`，配上 `UtcNow ± 1 天` 的窗，隔天 05:00 UTC 起窗的起點就越過樣本尾端，**8 條斷言同時紅**。
- **現在的量**：34 處絕對日期／9 檔；其中 3 檔同時出現 `UtcNow`：`BenchLogTests.cs`、
  `HistoryAndEventsTests.cs`、`TrendSentinelServiceTests.cs`（後者已修）。
- **偵測法**：
  ```bash
  for f in Tests/*.cs; do grep -qE "new DateTime\(20[0-9]{2}" $f && grep -q "UtcNow" $f && echo "高危: $f"; done
  ```
- **預防機制**：所有「現在時間」一律來自可注入的 `IClock`；測試只用**相對時間**；
  再加一條守門：`Tests/` 中不得出現「同檔同時有絕對日期與 UtcNow」（白名單逐步歸零）。

### 4.2 機制 B：語言與在地化（字串比對會靜默失效）

- **為什麼會壞**：解析器若用「標籤文字」辨識區段（例如 `Section start`），
  在中文／德文 Windows 上會**靜默**解不出東西（不是崩，是空）。
- **已做對的示範**：`Services/EspScanService`／`XinSpect.Decoders/SetupApiLog.cs` 只認語言中立的東西
  （標記符號、裝置 ID 語彙、時間戳格式），且有測試把標籤換成繁中形狀。
- **【推測】風險面**：其他解析器（事件記錄、更新歷史、驅動盤點、Winget 輸出）若有「比對輸出文字」的行為，
  同樣會在非英文環境失效。
- **偵測法**：掃描 `XinSpect.Decoders/` 與 `Services/` 中對「英文片語」的 `Contains`／`StartsWith` 比較，
  逐一標「是否語言中立」。**這是一條可以立刻寫成的報表腳本。**
- **預防機制**：解析器只准用四種語言中立的依據——**結構符號、數值欄位、GUID／OID、時間格式**；
  其餘一律列入白名單並註明理由。

### 4.3 機制 C：平台與硬體差異（能力矩陣缺格）

- **為什麼會壞**：Intel／AMD／NVIDIA、混合架構（P/E core）、小核數／大核數、伺服器／客戶端、
  有無驅動、有無 ESP、有無 TPM……每一格都可能「讀不到」或「讀錯」。
- **已做對的**：三態（`ReadError`／`NotApplicable`／`Unknown`）＋「未收錄一律不猜」＋
  「一般用戶端功能不得當成伺服器角色」等 229 條不變量測試。
- **【推測】風險面**：新世代硬體（未收錄 model）會退化成「不適用」，這是**正確但容易誤讀**的結果；
  使用者看到「不適用」會以為是軟體壞了。
- **預防機制**：(a) 每個「未收錄」都附**為什麼未收錄**（世代未知／無規格出處／平台不支援）；
  (b) 建立「能力矩陣」事實（哪個平台支援哪個鍵），可視覺化，讓缺格一眼可見。

### 4.4 機制 D：權限與假成功

- **為什麼會壞**：Windows 的權限 API 常「回 true 但沒生效」（`AdjustTokenPrivileges` 後必須查 `LastError=1300`），
  或「讀回 0」被當成真值。
- **已做對的**：`UefiBootFactsService.EnableFirmwarePrivilege` 明確檢查 `LastError`；
  「一般使用者權限不足」有專門文案。
- **偵測法**：把所有 `AdjustTokenPrivileges`、`GetFirmwareEnvironmentVariable*`、驅動開啟、
  `CreateFile(\\.\...)` 的呼叫點列出來，**每一處都必須有「失敗判定」**，沒有的一律列為缺陷。
- **預防機制**：權限相關操作集中到 `IPrivilegeGate`，回傳「拿到了／沒拿到／拿到了但沒生效」三態。

### 4.5 機制 E：資源生命週期與洩漏

- **為什麼會壞**：本專案大量使用原生資源——驅動 handle、`AssemblyLoadContext`、ETW session、
  WMI 連線、GDI/D3D 資源、記憶體映射（MMIO）。
- **已知的刻意取捨**：`WinRing0Bridge.Load094Assembly` 建 `AssemblyLoadContext(isCollectible: false)`
  並刻意不釋放 `MemoryStream`（組件映像留著比省 0.7 MB 重要）——**這是對的**，但需有註解（已有）。
- **【推測】風險面**：(a) 每次載入都建新 ALC（是否有重複載入路徑？）；
  (b) ETW session 沒關會殘留（`EtwTraceService` 已有 `IsValidEtl` 與工作階段狀態，需長期驗證）；
  (c) MMIO 映射未解除。
- **偵測法**：**長時運行測試**（例如 30 分鐘 × 1 Hz）並比對 handle／記憶體曲線；
  這是目前測試套件缺的一整類（所有測試都在秒級）。
- **預防機制**：新增 `LongRun` 分類測試（CI 可選跑），輸出資源曲線存檔。

### 4.6 機制 F：並發與每秒更新（WPF 執行緒）

- **為什麼會壞**：事實採集多在背景執行緒，UI 每秒重畫；`ObservableCollection` 跨執行緒增減、
  計時器重入（上一輪還沒跑完下一輪又開始）、共用狀態競態，都會在「使用者機器更慢」時才顯現。
- **現在的量**：`async void` 52、`.Result/.Wait()` 19、`Dispatcher.Invoke` 18。
- **偵測法**：加入「慢機器模擬」（在人為延遲下跑 UI 更新）與「重入偵測」（計時器 body 加計數斷言）。
- **預防機制**：統一 `DispatcherTimer`／`PeriodicTimer` 的封裝（單一節拍來源），
  並在每次 tick 記錄「上一輪耗時」，超過間隔就降頻並在診斷頁顯示——**這本身就是一個事實**。

### 4.7 機制 G：單檔發佈與環境差異

- **為什麼會壞**：自包含單檔 + 內嵌驅動 + 依賴 NuGet 生態，任何一項假設「開發機有的東西使用者也有」都會炸。
- **已發生並修好**：WinRing0 來源（改內嵌＋環境推導路徑）。
- **【推測】風險面**：中文字型／圖示資源是否內嵌？工作目錄不同時的相對路徑？防毒攔截驅動解壓？
  SmartScreen 未簽章警告？
- **偵測法**：**在乾淨環境（新 VM／另一台機器）跑一次全功能冒煙測試**，把結果寫成 `docs/RELEASE-SMOKE.md`。
- **預防機制**：發佈清單加入「乾淨機器冒煙」步驟，並記錄每次結果與差異。

### 4.8 機制 H：規格漂移

- **為什麼會壞**：UEFI、PCIe、DDR5、NVMe、Tpm2、CXL、IPMI……規格會改版；
  硬編在程式碼裡的知識表會過期，且**過期時不會有任何錯誤**。
- **已做對的**：`XinSpect.Decoders/` 的 `SpecRef` 覆蓋檢查（無缺引用、引用非空且可列舉）。
- **【推測】風險面**：SpecRef 只證明「有引用」，不證明「引用的是最新版」。
- **預防機制**：SpecRef 帶**版本號**（例：`UEFI 2.10 §32.4.1`）＋ 一份「規格現行版本」清單，
  定期比對並在診斷頁顯示「有 N 條引用落後」。

### 4.9 機制 I：守門漏洞（最貴的一類）

- **為什麼會壞**：**能被測試守住的東西會一直對；守不住的東西會慢慢錯。**
  本輪的四條紅燈、七個未接線服務、兩處寫死路徑、tag 與連結不一致——全部落在「沒有守門」的區域。
- **現在的守門覆蓋**：229 條不變量型測試（版號、三態、目錄對帳、SpecRef、文案與實際數量一致…），
  品質很高但**集中在「資料正確性」，不覆蓋「接線／發佈／覆蓋率」。**
- **預防機制**：§5 全部。

### 4.10 機制 J：測試自身的腐化

- **為什麼會壞**：測試數量（2882 方法／3630 案例）本身成了維護負擔；寫得不好的測試會
  (a) 綁實作細節，改重構就紅（逼人改測試而不是改對）、(b) 有時間炸彈、(c) 斷言太鬆、
  (d) 依賴執行順序。
- **現在的可觀測指標**：突變分數 **82%**（`StrykerOutput/`）、`TestSuiteBaseline` 守住數量、
  `.orphaned-tests/` 存放隔離的探針。
- **【推測】風險面**：突變分數是全專案平均，**個別高風險服務（寫入路徑、安全判讀）的分數可能偏低**。
- **預防機制**：對高風險子集（§5.3 的寫入閘門、安全判讀、ESP 交叉）**單獨量突變分數**並設門檻。

---

## 5. 機制補強：把「靠記性」換成「靠守門」

### 5.0 三條設計原則

1. **一條規則＝一條測試**。寫在文件裡的規矩（「記得接線」「記得同步版號」）等於沒有規矩。
2. **守門必須能在「有人犯錯」時紅燈**。若一條測試在缺陷存在時仍綠，它守的不是那件事——
   本輪的「目錄說有 `audio.endpoints`、實際沒人生產」就是這種情況。
3. **白名單必須自己也被守**。允許例外（例如「這支服務確定是測試專用」）時，
   白名單要有**理由註解**、**數量上限斷言**，否則三年後它會變成第二份真相。

### 5.1 守門矩陣（現況 vs 缺口）

| 維度 | 現在的守門 | 缺口 |
|---|---|---|
| 資料正確性 | 強（三態不變量、判讀邊界、對帳） | — |
| 版號一致性 | 有（`ChangelogTests` 4 條，含三份 README 與沿革） | 版號共六處，**新加第 7 處不會被發現** |
| 事實鍵目錄 | 有（目錄掃描、與執行期規則比對） | **「目錄有、沒人生產」與「有生產、目錄沒登記」** |
| 接線（服務→入口） | **無** | 11 個服務已證明會漏 |
| 規格引用 | 有（SpecRef 覆蓋） | 只證明「有引用」，不證明「是現行版本」 |
| 在地化 | 部分（字串表 1716 條、語言往返測試） | **新字串是否入表**、解析器的語言中立性 |
| 發佈 | 有（版本六處、README 連結文字） | **連結目標是否存在**、位元組數是否為本版 |
| 環境假設 | **無** | 寫死使用者名稱／磁碟機／工具路徑 |
| 時間 | **無** | 34 處絕對日期，已炸過一次 |
| 警告與靜態品質 | **無** | 警告數無基線；`async void` 52、`.Result` 19 |
| 視覺回歸 | 極少（本輪新增熱區圖算繪測試） | 其餘 40+ 自繪控制項 |
| 資源與長時 | **無** | 無長時運行測試（30 分鐘級） |
| 寫入安全 | 部分（同意閘門、寫入前後比對的個別測試） | **沒有集中閘門與「誰寫了什麼」的稽核** |

### 5.2 提案 1：接線守門（最高優先）

**守什麼**：`Services/` 下的「事實服務」必須被生產入口引用（`EvidenceLabService.AllFacts`／CLI／UI）。

**測試形狀**：
```csharp
[Fact] public void 每個事實服務都必須被接線()
{
    var wired = File.ReadAllText("Services/EvidenceLabService.cs") + File.ReadAllText("Services/EvidenceCollection.cs")
              + /* CLI 入口 */;
    var serviceFiles = Directory.GetFiles("Services", "*FactsService.cs");
    var orphans = serviceFiles.Where(f => !wired.Contains(Path.GetFileNameWithoutExtension(f))).ToList();
    Assert.True(orphans.Count <= Whitelist.Count,
        "以下事實服務沒有被任何生產入口引用：" + string.Join("、", orphans));
    Assert.All(Whitelist, w => Assert.Contains("// 測試專用：", File.ReadAllText(w)));  // 白名單必須有理由
}
```
**紅燈條件**：出現新的未接線服務，或白名單增加而沒有理由註解。
**成本**：半天。**收益**：直接擋掉 §3.2 那一整類（11 個已存在）。

### 5.3 提案 2：寫入閘門與寫入稽核

**守什麼**：所有對硬體／韌體的寫入必經單一閘門，可稽核、可回報、可拒絕。

**形狀**：
- 新增 `IWriteGate`：`WriteMsr(index, value)`／`WritePci(bus,dev,fn,reg,value)`／`WriteIoPort(port,value)`，
  內部記錄（前值→後值、時間、呼叫者、目的）並要求呼叫端先 `Acquire(reason)`。
- 現有呼叫點：`PmuProgrammingService`、`RdtService`、`DramTrafficService`、`TopDownService`、
  `AmdSmuService.WritePci`、`ImcSmbusDiscovery`、`IoPortAccess.OutByte`、`SmbusController.Out`、
  `WinRing0Bridge`（底層）。
- **守門測試**：`grep` 級斷言——`WriteMsrPair|WritePciConfig|WriteIoPortByte` 的呼叫點集合
  必須等於閘門內部的呼叫點集合（新出現的直接殘留呼叫會紅燈）。
**驗收**：UI／CLI 任一頁能回答「本次執行寫了哪些暫存器、前值後值各是什麼」。

### 5.4 提案 3：時間守門（`IClock`＋絕對日期禁令）

**守什麼**：測試不得因「今天幾號」而紅；邏輯不得依賴真實時鐘（可測性）。
**形狀**：
```csharp
[Fact] public void 測試不得同時使用絕對日期與當前時間()
{
    foreach (var f in Directory.GetFiles("Tests", "*.cs"))
        Assert.False(HasAbsoluteDate(f) && f.Contains("UtcNow"),
            $"{f}：同時出現絕對日期與 UtcNow——這是時間炸彈（查詢窗一移動就紅）");
}
```
**紅燈條件**：34 處降到 0（可分批：先修 3 檔高危，再全清）。
**配套**：`TrendSentinelService` 這類「相對視窗」服務，時間一律由外部注入。

### 5.5 提案 4：環境假設守門

**守什麼**：生產碼不得假設使用者名稱、磁碟機代號、桌面路徑。
**形狀**：測試掃 `Services/`／`Views/`／`Controls/`，命中
`C:\Users\`、`Administrator`、`Desktop\`、`D:\` 等字面值即紅燈（含理由白名單）。
**現存違規**：`Services/CpuzReportService.cs:392`、`Services/AiService.cs:428`。
**配套**：工具路徑改走「設定＋環境＋PATH 搜尋」，找不到時如實標示為事實（而不是靜默失敗）。

### 5.6 提案 5：警告基線與品質門檻

**守什麼**：編譯警告與分析器診斷數**只准下降**。
**形狀**：`Tests/TestSuiteBaseline.cs` 已有「測試數基線」的成功範例；照抄：
`CompilerWarnings` 常數 ＋ 由建置日誌解析的測試（或 `Directory.Build.props` 的
`WarningsAsErrors` 對新檔案生效）。
**現況違規**：CS0219（`Services/UefiBootFactsService.cs:114` 等）、CS8604、CS8629、
xUnit 分析器（`RawSnapshotUiTests`、`BlueSquadronSmokeTests`…）。
**配套**：`async void` 與 `.Result` 的數量也納入同一份基線（52／19），只准下降。

### 5.7 提案 6：算繪快照守門（視覺回歸）

**守什麼**：色階、版面、字級、留白規則不因重構而默默改變。
**形狀**：沿用本輪熱區圖的做法——在測試宿主用 `RenderTargetBitmap` 算繪，
與基準 PNG 比對（容忍度 0 或極小）。
**優先對象**：`HeatScale` 相關（熱區圖／逐核液柱）、`RadialGauge`、`HistoryGraph`、`DonutChart`、
`SpectrumBar`、`AnalogVoltMeter`（這幾個都是「一眼看錯就誤導」的自繪控制項）。
**注意**：基準圖要能明確更新（`--update-baseline`），並在 PR／提交訊息說明**為什麼**變。

### 5.8 提案 7：字串覆蓋守門

**守什麼**：新增中文介面字串必須進 `EnglishStrings`（否則英語模式夾中文）。
**形狀**：掃 XAML 的 `Text="…中文…"` 與 C# 的中文字面值，減去已知例外（安裝精靈、彩蛋頁——沿革已明列），
超出白名單即紅燈。
**附帶**：`LanguageService.TComposite` 的「分段查表」只處理程式拼接字串，
新拼接字串若有新的分隔符，會靜默夾中文——**建議把「拼接必須走 TComposite」也做成守門**。

### 5.9 提案 8：發佈後驗證（把 P0 變成不可能）

**守什麼**：README 宣稱的每一個下載連結都真的存在，位元組數是本版的。
**形狀**：`Tools/verify-release.ps1`（或 CI job）：
1. `git tag` 最新 == csproj 版本 == README 徽章 == AboutView == `ChangelogCatalog` 最新 == README 沿革首筆；
2. 逐一 `curl -I` 三份 README 的每一個 `/releases/download/...` → 必須 200；
3. 下載後 `Length` 與 README 表列相同。
**驗收**：任何一步失敗即非零退出；發佈流程必須跑到這支腳本綠燈才算完成。

### 5.10 提案 9：目錄／註冊／申報三方對帳

**守什麼**：消滅「目錄說有、實際沒有」與反向情況（有生產、目錄沒登記）。
**形狀**：三方對帳測試——
(a) `FactKeyCatalog.Keys`（靜態）、
(b) 執行期實際註冊／產生的事實鍵集合、
(c) 覆蓋率申報所依據的集合。
三者必須相等，否則紅燈並列出差集方向（「只在目錄」「只在執行期」）。
**現存差集（2026-10-10 接線守門那一輪之後的實況）**：

| 方向 | 鍵 | 狀態 |
|---|---|---|
| 只在目錄（沒人生產） | `audio.endpoints`、`boot.duration_ms`、`boot.last_time` | **已接線**（三支事實服務進 `LoadUsermodeFacts`／`AllFacts`），不再有差集 |
| 只在服務（目錄沒登記） | `net.offload`、`amd.sev_es.enabled`、`amd.sev_snp.enabled`、`amd.mem_enc.enabled` | **已補進目錄**（146 → 150） |
| 掃描器盲點 | 不可得 helper 以字面值傳鍵的兩種形狀 | **已修**（`CoverageService` 補兩個樣式；只認含至少一個點的字面值，免得把一般字串當成事實鍵） |

> 教訓：原先我把「`boot.*` 不在目錄」寫進本文件 —— **那是錯的**（它們早在目錄裡）。
> 真正缺的只有 `net.offload` 與三個 `amd.sev*`。這條錯誤證明了一件事：**沒有指令證據的斷言不該進文件**。

### 5.11 提案 10：能力矩陣事實化

**守什麼**：把「這台機器支援什麼」變成可查詢的事實，而不是散在各服務的 if-else。
**形狀**：新增事實鍵群 `cap.*`（`cap.msr`、`cap.pci`、`cap.mmio`、`cap.esp`、`cap.tpm`、
`cap.perfctr`…），值為三態＋原因；UI 一頁「本機能力矩陣」。
**收益**：使用者看到「不適用」時能立刻知道是環境不支援還是程式不會做；也讓 §4.3 的缺格可視化。

### 5.12 提案 11：長時與資源曲線

**守什麼**：1 Hz 更新數十分鐘後不出現記憶體／handle 成長、計時器堆疊、UI 卡頓累積。
**形狀**：`Categories=LongRun` 測試（預設略過，CI 或人工觸發），跑 N 分鐘，輸出曲線（CSV）並斷言
「成長率低於門檻」「tick 落後次數為 0」。**這也順便產生一份可公開的可靠性證據。**

### 5.13 這 11 條的實施順序（依「守掉多少缺陷 ÷ 成本」排）

| 序 | 提案 | 守掉的缺陷類型 | 成本 |
|---|---|---|---|
| 1 | 5.2 接線守門 | §3.2 全部（11 個已存在） | 半天 |
| 2 | 5.4 時間守門 | §4.1（已炸過一次） | 半天 |
| 3 | 5.5 環境守門 | §3.1 P1-3 | 2 小時 |
| 4 | 5.9 發佈驗證 | §3.1 P0-1／P0-2 | 半天 |
| 5 | 5.10 三方對帳 | §3.1 P0-3 | 一天 |
| 6 | 5.6 警告基線 | §4.10 部分 | 一天 |
| 7 | 5.3 寫入閘門 | §4.4／§4.8（硬體寫入面） | 2～3 天 |
| 8 | 5.8 字串覆蓋 | §4.2 | 一天 |
| 9 | 5.11 能力矩陣 | §4.3 | 2 天 |
| 10 | 5.7 算繪快照 | §4.6 部分 | 2 天 |
| 11 | 5.12 長時測試 | §4.5 | 2 天 |

---

## 6. 科學化：量測方法論（讓「數字」值得被相信）

本專案的目標不是「跑分」，而是**用可重複的量測回答具體問題**。要做到這一點，需要四件事：

### 6.1 量測的四要素（每一項量測都必須能回答）

1. **量什麼**：定義（例：停了多久算一次停頓？`InvisibleStallService` 的門檻寫在哪、憑什麼）。
2. **怎麼量**：方法與工具鏈（ETW／MSR／計時器解析度／取樣頻率）。
3. **誤差**：雜訊來源與量級（排程抖動、TSC 不穩、熱降頻、其他程式干擾）。
4. **可重複**：條件（電源計畫、背景負載、溫度起始值）與**「不滿足條件就不出數字」**的拒絕機制
   （本專案已用 `*ValidationException` 實現，應推廣到所有量測）。

### 6.2 誤差預算（建議格式）

每個量測結果附：取樣數 N、離群處理方式、穩態判定、變異係數（CV）、以及「本次量測的解析度下限」。
**驗收**：任一數字都能回答「這個差 3% 是訊號還是雜訊」。

### 6.3 金標與突變的組合

- **金標向量**：純函式（統計、解碼、幾何、判讀）以合成資料 + 已知期望值鎖定。
- **突變測試**：驗證金標與斷言的**殺傷力**（現況全專案 82%，建議對高風險子集單獨設門檻）。
- **兩者缺一不可**：金標保證「算對」，突變保證「測試會叫」。

### 6.4 真機實測護照（measurement passport）

建議 schema（可存成 JSON，與報告一起匯出）：
```json
{
  "passport": 1,
  "when": "2026-10-10T01:00:00Z",
  "machine": { "cpu": "...", "mb": "...", "bios": "...", "os": "..." },
  "conditions": { "powerPlan": "...", "ambientC": 24, "backgroundLoad": "idle" },
  "measurement": { "name": "storage.flush.durability", "samples": 64, "algorithm": "v3" },
  "result": { "mean": 0.0, "p99": 0.0, "cv": 0.0, "unit": "ms", "rejected": 3 },
  "limits": ["驅動未載入時不出數字", "..."],
  "honesty": { "unmeasured": ["..."] }
}
```
**收益**：不同機器／不同版本的結果可以**合法地**比較；也讓「本機實測 vs 經驗範圍」的界線
（專案現行原則）變成資料而不是文字。

### 6.5 因果與相關的界線

本專案的既有立場是對的：只描述數列與現象，不替硬體下因果結論。
建議把它寫成**可執行的規則**：判讀文案中若出現「因為／導致／證明」等字，
必須同時附「支持證據」與「無法排除的替代解釋」，否則測試紅燈（文案守門）。

### 6.6 統計工具的適用條件（避免誤用）

| 工具 | 適用 | 不適用（要拒絕出數字） |
|---|---|---|
| Theil–Sen 斜率 | 單調趨勢、有離群 | 樣本太少（< N）、時間軸不均 |
| CUSUM | 找變化點 | 常態漂移未去除、自我相關資料 |
| Pearson | 線性關係 | 非線性、離群、時間序列（需差分） |
| 百分位（p95/p99） | 尾端延遲 | 樣本不足以支撐該百分位（要標明） |

**驗收**：每個統計結果都附「本結果的適用條件檢查結果」；不滿足就回「不足以判斷」。

### 6.7 對外部數字的態度

引用外部基準（某評測、某廠商文件）時必須標**出處、版本／世代、量測條件**，
且**不得**與本機量測放在同一欄比較而不註明。

---

## 7. 分階段路線圖（每一階段都有「驗收」與「不做什麼」）

> 排序原則：**先讓現有東西可信（T0／T1），再把能力往下鑽（T2／T3）。**
> 「不做什麼」同樣重要——本文件的每一階段都刻意**不加新功能**，因為目前的瓶頸在接線與治理，不在功能數量。

### T0（今天～3 天）：清掉會咬人的，裝上三道守門

| 項 | 內容 | 驗收 |
|---|---|---|
| T0-1 | 發佈 **v2.43**：升版號六處已就緒 → 建置 → 測試 → 發 Release → 補位元組數 docs commit | 三份 README 的下載連結 `curl -I` 全部 200；`README.md:85` 數字為 v2.43 實測值 |
| T0-2 | 接線守門測試（§5.2） | 新出現未接線服務即紅燈；白名單每筆有理由註解 |
| T0-3 | 時間守門測試（§5.4） | 34 處絕對日期歸零（可先修 3 檔高危） |
| T0-4 | 環境假設守門（§5.5）＋修掉兩處寫死路徑 | 生產碼無 `C:\Users\`／`Administrator` 字面值 |
| T0-5 | 清 §3.3 的 P2 小項：`WinRing0Bridge` 重複行 | 該檔無重複賦值 |

**不做什麼**：不動色階、不動版面、不新增能力。

### T1（1～2 週）：把「做了沒接」清光，發佈流程自動化

| 項 | 內容 | 驗收 |
|---|---|---|
| T1-1 | 11 個未接線服務**逐一判定性質**（功能本體→接線；測試工具→移出 `Services/` 或標記） | `接線守門` 綠燈且白名單為空或全有理由 |
| T1-2 | `LocalApiHandler` 決策：接上（loopback＋token）或明確標為未實作並從沿革移除 | 文件與程式一致，無半成品 |
| T1-3 | `AuditVerifier` 入口化（UI／CLI 可驗證審計日誌） | 篡改一行後能指出第一個斷點 |
| T1-4 | 目錄／註冊／申報三方對帳測試（§5.10） | 三方差集為空 |
| T1-5 | 發佈驗證腳本（§5.9）＋寫進發佈清單 | 發佈流程跑腳本綠燈才算完成 |
| T1-6 | 警告基線（§5.6） | 警告數只准下降 |

**不做什麼**：不新增頁面；不擴張事實鍵目錄（先讓現有的都真的存在）。

### T2（1 個月）：寫入安全、能力矩陣、品質守門

| 項 | 內容 | 驗收 |
|---|---|---|
| T2-1 | 寫入閘門 `IWriteGate`（§5.3）＋呼叫點收斂 | 無殘留直接寫入；畫面可列「本次執行的寫入清單」 |
| T2-2 | 能力矩陣事實 `cap.*`（§5.11）＋一頁呈現 | 「不適用」一律附原因 |
| T2-3 | 算繪快照守門（§5.7）：優先熱區圖／徑向儀表／歷史圖／甜甜圈 | 改動未更新基準 → 紅燈 |
| T2-4 | 字串覆蓋守門（§5.8）＋ `TComposite` 使用守門 | 新中文未入表 → 紅燈 |
| T2-5 | 長時運行與資源曲線（§5.12） | 30 分鐘 1 Hz：handle／記憶體成長低於門檻、tick 落後 0 次 |
| T2-6 | 高風險子集的突變門檻（§4.10） | 寫入路徑／安全判讀／ESP 交叉的突變分數 ≥ 門檻 |

### T3（一季）：把「深度」做出來（真正專業化／底層化）

| 項 | 內容 | 驗收 |
|---|---|---|
| T3-1 | **三層信任鏈**：韌體宣告（db/dbx）→ ESP 檔案 → 執行期驅動簽章，一條可追溯鏈 | 任一層缺資料就標缺，不補；能回答「這個檔案被撤銷了嗎／這個驅動哪來的」 |
| T3-2 | **延遲鏈路預算表**（§2.7） | 每一段標「已量／未量／量不到」 |
| T3-3 | 量測護照（§6.4）落地為可匯出 JSON | 同一護照可在別台機器重跑比對 |
| T3-4 | 知識表外部化（微架構、世代對照、風險評分輸入） | 新增世代不需改 C#；資料有出處欄位 |
| T3-5 | 歷史倉擴充：可靠性計數器 19 欄＋趨勢判讀接上 CUSUM | 能指出變化點與發生日期 |
| T3-6 | corpus 化：SPD 直讀、驅動盤點、偵測結果的匿名累積與統計 | 有第一份「跨機型／跨模組」的相容性統計 |
| T3-7 | 掃描可續（表面掃描）與寫入完整性常態化（§2.5） | 中斷可續；每次報告抽驗最後寫入檔案 |

**貫穿 T0～T3 的一條線**：所有新增能力都必須（a）有三態、（b）有守門測試、（c）能在報告裡說出自己的界線。

---

## 8. 附錄

### 附錄 A：指令速查（本文件所有數字都由此而來）

```bash
cd /c/Users/Administrator/XinSpect

# 現況
git status -sb; git log --oneline -1; git tag --sort=-v:refname | head
grep -n "<Version>" XinSpect.csproj

# 測試（保留離開碼；換新 BaseOutputPath 繞 apphost 鎖）
set -o pipefail
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo -p:BaseOutputPath=obj/_verify_243/

# 規模
for d in Services XinSpect.Decoders Views Controls Models ViewModels Tests Nav; do
  echo -n "$d: "; find $d -name "*.cs" -o -name "*.xaml" | grep -v obj | wc -l | tr -d '\n'
  echo -n " 檔，"; find $d -name "*.cs" -o -name "*.xaml" | grep -v obj | xargs wc -l | tail -1; done

# 未接線服務（核心一招）
for s in AudioEndpointFactsService BootTimingFactsService NetOffloadFactsService \
         UncorePmuService AmdSmuService LoopbackSignalService CorpusUploadService \
         LocalApiHandler FakeCapacityTestService RuleLoader VerifyRules; do
  echo "$s 生產引用=$(grep -rlw "$s" --include=*.cs --include=*.xaml . \
      | grep -v '^./obj\|^./bin\|^./Tests' | grep -v "/$s.cs" | wc -l)"
done

# 時間炸彈
for f in Tests/*.cs; do grep -qE "new DateTime\(20[0-9]{2}" $f && grep -q "UtcNow" $f && echo "高危: $f"; done

# 環境假設
grep -rn "C:.[Uu]sers\|Administrator" --include=*.cs --include=*.xaml . | grep -v "^./obj\|^./Tests"

# 靜默例外與執行緒衛生
grep -rn "catch { }\|catch {$" --include=*.cs . | grep -v "^./obj" | wc -l
grep -rn "async void" --include=*.cs Views Controls Dialogs | wc -l
grep -rnE "\.Result\b|\.Wait\(\)" --include=*.cs Views Controls Services | wc -l

# 不變量型測試（守門盤點）
grep -h "public void " Tests/*.cs | grep -cE "守門|必須|不得|一律|對帳|一致|覆蓋|不允許"
```

### 附錄 B：事實鍵前綴分布（146 鍵，前 24 名）

| 前綴 | 數 | 備註 |
|---|---|---|
| gpu | 11 | 顯示與編碼／記憶體／鏈路 |
| spi | 9 | 快閃區域、讀保護、熵、雜湊、比對 |
| uefi | 7 | FV／FFS、變數、簽章庫 |
| virt | 6 | 虛擬化判定（元件／服務／虛擬層分離） |
| platform / pmu / psu / pci / nic / ent / cpu / asset / acpi | 4 | 各域主幹 |
| usb / ups / time / mem / esp / dbg / cmos / chipset / boot / backend / audit | 3 | |

### 附錄 C：風險面（可被軟體寫入的路徑）清單【檢索】

| 路徑 | 出處 | 現況 |
|---|---|---|
| MSR 寫 | `Services/PmuProgrammingService.cs:64,85,126`、`Services/RdtService.cs:251,258,264,313`、`Services/DramTrafficService.cs:189`、`Services/TopDownService.cs:437` | 各自持有，**無集中閘門** |
| PCI 設定空間寫 | `Services/AmdSmuService.cs:64,111,168`（`WritePciConfig`）、`Services/ImcSmbusDiscovery.cs:23` | 同上 |
| I/O 埠寫 | `Services/IoPortAccess.cs:42`、`Services/SmbusController.cs:21` | 同上 |
| MMIO 寫 | `Services/MmioAccess.cs`（後端選擇器；未載入時 `NotLoadedMmioReader`） | 唯讀為主，需逐點確認 |
| 韌體變數寫 | `SetFirmwareEnvironmentVariable*`（若存在，需逐一核對權限與同意閘門） | 需盤點 |
| 驅動服務控制 | `Services/DeepAccessService.cs`（`ScmDriverService`／`IDriverServiceControl`） | 豁免開關需收斂單點 |
| 檔案／磁碟寫 | 假容量驗證、表面掃描、報告匯出、審計日誌 | 有同意閘門者需列出 |

**建議**：這張表由程式產生（附錄 A 的 grep 之一鍵），並在「深層存取」頁直接顯示——
**使用者有權知道這個程式能寫什麼。**

### 附錄 D：與既有文件的關係（避免重工）

| 既有文件 | 定位 | 本文件與它的關係 |
|---|---|---|
| `HANDOFF-2026-10-10.md` | 本輪交接（現況、修了什麼、界線） | 本文件的 §1／§3.0 是它的展開 |
| `MASTER-INDEX-2026-10-02.md` | 索引 | 本文件應被收進索引（作為「機制化」主題） |
| `PROGRAM-EVEREST-V7-2026-10-02.md` | 完整體路線圖 | 本文件**不重複其功能規劃**，只補「治理與守門」這條線 |
| `docs/ITERATIONS.md` | 迭代記錄 | 本文件的 T0～T3 完成後，逐項回填到這裡 |
| `docs/CAPABILITY-ADDITIONS-2026-10-09.md`、`docs/CAPABILITY-DOMAINS-2026-10-09.md` | 能力增補與分域 | 本文件 §2 的十二域是其對照面；若有衝突以「實際程式」為準 |
| `docs/INTEGRATE-VS-DISPATCH-2026-10-09.md` | 整合 vs 分派 | 本文件 §5.2 的接線守門正是該主題的可執行版 |
| `docs/NEXT-STEPS-2026-10-08.md` | 下一步 | 本文件 §7 對其排序做了「先治理後深化」的調整 |
| `docs/MEASUREMENT-METHODOLOGY.md` | 量測方法論 | 本文件 §6 是它的擴充（誤差預算、護照、適用條件） |

> 上述既有文件在本輪**未逐字細讀**（依檔名與日期定位），合併時應先對照以避免重複。

### 附錄 E：本文件的自我限制（請照著打）

1. 我**沒有**在真機上逐一觸發 §3 的缺陷（除測試外），P1 清單來自靜態檢索，**可能誤判**
   （例如某服務可能是刻意的測試專用工具，見 §3.2 的處理原則）。
2. §4 全部是【推測】等級的機制風險，**尚未發生**；但每一條都給了可立刻寫成的偵測法。
3. §1 的規模數字是靜態行數，**不是**複雜度指標；行數多不等於難維護。
4. 我**沒有**開真的 App 視窗做端到端走查；本輪的視覺驗證只有測試宿主算繪的那張熱區圖。
5. 任何與實際程式衝突之處，**以程式為準**——本文件是導航，不是法規。

---

### 一句話總結

**XinSpect 的功能密度已經很高，缺的是「讓密度不腐化的機制」：**
先把接線、時間、環境、發佈四道守門裝上（T0），把做了沒接的清光（T1），
再讓寫入可控、能力可查、品質只准上升（T2），最後才是三層信任鏈、延遲預算、量測護照這些真正的深度（T3）。
