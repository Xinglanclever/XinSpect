# 接線守門那一輪 — 2026-10-10

> 目標：把「**做完沒接線**」這類缺陷，從「靠人記得」變成「靠測試守」。
> 這一輪不加新功能：只把三支已經寫好、測過、卻沒人呼叫的事實服務接上，並補上會咬人的守門。
> 全部數字可重跑；**三個紅燈如實記錄，不假裝全綠。**

---

## 1. 動工前的證據（這一段是這一輪存在的理由）

### 1.1 三支事實服務的生產引用數是 0

```bash
cd /c/Users/Administrator/XinSpect
for s in AudioEndpointFactsService BootTimingFactsService NetOffloadFactsService; do
  echo "$s 生產引用=$(grep -rlw "$s" --include=*.cs --include=*.xaml . \
      | grep -v '^./obj\|^./bin\|^./Tests' | grep -v "/$s.cs" | wc -l)"
done
# 動工前：三支都是 0
```

它們的單元測試全綠（`TempoTests/AudioEndpointFactsTests.cs`、`BootTimingTests.cs`、`NetOffloadFactsTests.cs`）——
**測得到服務，測不到「有沒有人接線」**。

### 1.2 更糟的是：目錄已經在宣稱它們存在

| 事實鍵 | 動工前在 `FactKeyCatalog` 裡？ | 有人生產嗎 |
|---|---|---|
| `audio.endpoints` | ✅ 在（第 40 行） | ❌ 沒有 |
| `boot.duration_ms` | ✅ 在（第 48 行） | ❌ 沒有 |
| `boot.last_time` | ✅ 在（第 49 行） | ❌ 沒有 |
| `net.offload` | ❌ 不在 | ❌ 沒有 |

覆蓋申報（v2.36 建立的治理機制）以目錄為分母，於是它會把**從未生產**的事實算成「已被規則考慮或明文豁免」——
畫面給出一個肯定的結論，那個結論是空的。這與 `FactKeyCatalogTests` 裡記載的已發佈缺陷是**同一種病**。

> 更正一則我先前的判斷：我原本寫「`boot.*` 不在目錄」——**錯的**，它們早就在。
> 這個錯誤已回填修正到 `docs/PROGRAM-ULTIMATE-2026-10-10.md`。

### 1.3 目錄的反向缺口（有生產、目錄沒登記）

掃描器（`CoverageService.ScanFactKeysFromSource`）原本只認四種寫法：
`new HardwareFact("…")`、`new("…", Category)`、`const …Key = "…"`、`FactKey = "…"`。
服務層常見的「不可得」helper 是把鍵當**參數**傳進去（`Unavailable("key", …)` 或 `Unavailable(at, "key", …)`），
這兩種形狀**一個都掃不到**。擴充樣式後實測多出 4 個鍵：

```
net.offload, amd.sev_es.enabled, amd.sev_snp.enabled, amd.mem_enc.enabled
```

---

## 2. 這一輪改了什麼（逐檔）

| 檔案 | 改動 | 為什麼 |
|---|---|---|
| `Services/EvidenceLabService.cs` | +3 個事實屬性、+3 個 `Load*` 方法、`AllFacts` 串接 3 組 | 事實要進得了共用的事實集合（UI 與 CLI 同源） |
| `Services/EvidenceCollection.cs` | `LoadUsermodeFacts` 新增 3 個 try/catch 載入 | 這是 UI 與 CLI 共用的**單一組合點**；接在這裡才不會兩邊漂移 |
| `Services/CoverageService.cs` | 掃描器補 2 個樣式（只認「至少含一個點」的字面值） | 修掉「有生產、目錄沒登記」的盲點；只認含點的字面值，免得把一般字串當成事實鍵 |
| `Services/FactKeyCatalog.cs` | 146 → 150，補上上述 4 鍵 | 目錄要與原始碼掃描逐鍵相等（既有守門測試要求） |
| `Tests/WiringGuardTests.cs` | **新增**（2 條測試） | 讓「沒接線」以後自己會紅燈 |
| `Tests/TestSuiteBaseline.cs`、`README.md`、`README.zh-CN.md`、`README.en.md` | 測試數 3628 → 3630 | 專案有「基線＝徽章」的守門，加測試就要同步 |
| `Nav/ChangelogCatalog.cs` | 2.43 紀錄補兩項（接線守門、目錄 146 → 150） | 版號與沿革的規矩由測試守著 |

合計 9 檔、+103／−20（不含新測試檔）。

---

## 3. 守門測試為什麼長這樣（設計與它擋得住什麼）

### 3.1 它檢查什麼

> **檔案內宣告的、名稱以 `FactsService` 結尾的公開類別，必須被生產碼引用；
> 否則紅燈，並指名是哪一支。**

### 3.2 三個踩過的坑（都寫進測試的註解裡）

1. **不能用檔名比對類別名**：`ChassisAndHpaFactsService.cs` 裡面是
   `ChassisFactsService` 與 `HpaFactsService` 兩個服務（都已接線）。第一版用檔名 → 立刻誤報。
   現在改為解析檔案內宣告的類別名。
2. **守門可以被文字欺騙**：第一版把 `Nav/`（說明目錄、版本沿革）也算成生產碼，
   於是**只要在章程裡寫到某支服務的名字**，它就「看起來被引用了」——實測就是這樣騙過了守門。
   現在排除 `Nav/`，並在測試裡寫明為什麼。
3. **白名單必須自己也被守**：例外要寫理由、且有數量上限（`WhitelistCap = 5`，目前為空）。
   沒有上限的白名單三年後會變成第二份真相。

### 3.3 它的等級（不要誤读）

這是「**有沒有人引用它**」，**不是**完整的可達性分析。
它能擋住「完全沒人叫」（這一輪的缺陷類型），擋不住「叫了但叫不到使用者面前」的需求。
要做到後者需要「目錄／執行期註冊／申報」三方對帳（見 `docs/PROGRAM-ULTIMATE-2026-10-10.md` §5.10）。

### 3.4 它有牙嗎？（正對照實驗，實測）

臨時新增一支沒有任何引用的服務 `Services/TempPositiveControlFactsService.cs`：

```
[xUnit.net]  XinSpect.Tests.WiringGuardTests.每個事實服務都必須被生產入口引用 [FAIL]
  • TempPositiveControlFactsService
  失敗: 1，通過: 1        （離開碼 1）
```

刪除該檔後回到綠燈。**這條守門確實會在有人漏接線時紅燈，並指名對象。**

---

## 4. 驗證（全部可重跑）

```bash
cd /c/Users/Administrator/XinSpect
set -o pipefail

# 受影響的三組守門（離開碼保留）
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo -p:BaseOutputPath=obj/_verify_243/ \
  --filter "FullyQualifiedName~WiringGuardTests|FullyQualifiedName~FactKeyCatalogTests|FullyQualifiedName~ChangelogTests"
#  → 已通過! - 失敗: 0，通過: 24，總計: 24     離開碼 0

# 全套
dotnet test Tests/XinSpect.Tests.csproj -v q --nologo -p:BaseOutputPath=obj/_verify_243/
#  → 失敗: 3，通過: 3628，總計: 3631，持續時間: 1 m 51 s     離開碼 1
```

接線後，三支服務的生產引用從 0 變成 2（`EvidenceLabService` 與 `EvidenceCollection`）：

```
AudioEndpointFactsService    ./Services/EvidenceCollection.cs ./Services/EvidenceLabService.cs
BootTimingFactsService       ./Services/EvidenceCollection.cs ./Services/EvidenceLabService.cs
NetOffloadFactsService       ./Services/EvidenceCollection.cs ./Services/EvidenceLabService.cs
```

---

## 5. 三個紅燈：歸因（**不是**「已修」）

### 5.1 時間線（這是關鍵證據）

| 時間 | 事件 |
|---|---|
| 01:32 | 全套 **3629 全綠**、離開碼 0（57 秒） |
| 01:49 | 系統事件：`Wininit` Event ID 14（本機重啟／登入階段事件） |
| 之後 | 全套變 **3631 總／3 失敗**，耗時 1 m 51 s～2 m 1 s |

### 5.2 兩條 GPU 測試：**沒有硬體 D3D11 配接器**

```
XinSpect.GpuUnsupportedException : 沒有可用的 D3D11 硬體配接器；已排除 WARP 與軟體渲染。
診斷：Microsoft Basic Render Driver flags=0x00000002；…；enum stop=0x887A0002
  at XinSpect.D3D11Native.SelectHardwareAdapter()  Services/DeepBench/Interop/D3D11Native.cs:1590
```

實測本機現在的顯示轉接器：

```
MuMu Virtual Display Adapter      (OK)
GameViewer Virtual Display Adapter (OK)
Virtual Display Driver            (OK)
```

**只有虛擬顯示轉接器，沒有硬體 GPU**。這兩條測試（DeepBench 的真實硬體實測）在這種環境下**必然**失敗；
本輪改動沒有碰 D3D11／DXGI 任何一行。修法不在程式裡，在環境（硬體 GPU 或接受它們在此環境略過）。

### 5.3 一條效能預算測試：**電源計畫是「節電」**

```
PerformanceBudgetTests.預算_全套掃描編排_20輪低於60秒 [FAIL]
  20 輪掃描編排 74.357 秒超過預算 60 秒

powercfg /getactivescheme
  → 電源配置 GUID: a1841308-3541-4fab-bc81-f71556f20b4a  (節電)
```

配套事實：同一時間**全套**由 57 秒變成 111～121 秒（約 2 倍），CPU 是 i9-7980XE（18C/36T，2.6 GHz 基準）。
「整份套件一起變慢」不可能由本輪 9 檔改動解釋——這是全機狀態。

**這條測試的路徑與本輪改動無關**（可查）：
它呼叫的是 `EvidenceLabService.ReloadDriverBackedFacts`（第 332–369 行），
整段只呼叫驅動後端收集器，**不會**走到這一輪新增的三個 usermode 載入；
本輪在該路徑上的唯一接觸是 `AllFacts` 多串三個（當下為空的）清單。

**要讓它回到綠燈**（需要使用者決定，指令一行）：

```powershell
powercfg /setactive SCHEME_MIN     # 切到「高效能」，之後重跑
```

我**沒有**自己改電源計畫：那是全機設定，會影響整台機器的行為，不該由我一廂情願地改。

---

## 6. 這一輪修正了我自己的三個錯誤（留給未來的自己）

1. **文件寫錯事實**：`docs/PROGRAM-ULTIMATE-2026-10-10.md` 原稱「`boot.*` 不在目錄」——錯，已在原處更正並註明教訓：**沒有指令證據的斷言不該進文件**。
2. **守門第一版用檔名比對類別名** → 對「一檔兩服務」誤報（`ChassisAndHpaFactsService.cs`）。
3. **守門第一版可被說明文字欺騙** → 章程裡寫到服務名就當成已接線；已排除 `Nav/`。

---

## 7. 還沒做（下一批，依價值排序）

1. **讓 3631 全綠**：需要在「有硬體 GPU ＋ 高效能電源計畫」的環境重跑（程式面已無事可做）。
2. **三方對帳**（目錄／執行期註冊／申報）：這一輪只做到「目錄 ≡ 原始碼掃描」，還沒做到「≡ 執行期真的註冊」。
3. **其餘未接線服務的性質判定**（需人工）：`AmdSmuService`（含 PCI 寫入路徑）、`UncorePmuService`、
   `LoopbackSignalService`、`CorpusUploadService`、`LocalApiHandler`、`RuleLoader`、`VerifyRules`、
   `FakeCapacityTestService`——逐一決定「接線」或「登記為測試專用」。
4. **環境假設守門**：生產碼還有兩處寫死 `C:\Users\Administrator\…`
   （`Services/CpuzReportService.cs:392`、`Services/AiService.cs:428`）。

---

## 8. 界線（這一輪**沒有**證明的事）

1. 我**沒有**開 App 目視確認這三支新事實的版面：它們進的是 `AllFacts`（→「韌體安全」頁的列、CLI、報告匯出）。
   新增的分類是「音訊」與「網路」，**那一頁的分組與排序會不會被新分類擠亂，需要人眼確認一次**。
2. 正對照（臨時孤兒服務）是**一次性人工實驗**，沒有留在套件裡——留著會讓套件永遠有一條刻意失敗的測試。
   它證明的是「守門在該情境下會紅燈」這一個事實，不是「它守得住所有漏接線」。
3. 那三條紅燈我只做到**歸因**（環境），沒有做到**修復**（需要硬體／全機設定）。
4. 本輪沒有動任何 UI、色階、量測邏輯；`FakeCapacityTestService` 之類「疑似沒接」的服務**尚未判定**，
   不要把它們當成已確認缺陷。
