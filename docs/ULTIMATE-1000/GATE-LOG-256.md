# 閘門紀錄：ULTIMATE-1000 推進至第 256 輪

> **這份檔案是「可貼進帳本」的紀錄**：改了哪些檔、跑了什麼、離開碼。
> 產出時機：第 256 輪（本輪次上限），階段二推進至 74/105 開工、71 列完成。

---

## 一、這一輪次的產出總覽

| 指標 | 起始（第 205 輪） | 現在（第 256 輪） |
|---|---|---|
| 計畫進度 | 148 / 1,050 | **159 / 1,050** |
| 階段二 `ok` | 44 列 | **52 列** |
| 階段二 `alias` | 17 列 | **18 列** |
| 階段二 `partial` | 1 列 | **2 列** |
| 階段二 `todo` | 44 列 | **35 列** |
| 事實鍵目錄 | 1,015 把 | **1,024 把** |
| 測試總數 | 5,848（README badge） | **6,369（實測）** |

**這一輪次完成的項目**：P2-034、P2-071、P2-072、P2-073、P2-076、P2-079（撤回）、
P2-080（撤回）、P2-083（partial）、P2-092、P2-093、P2-095、P2-061（partial）、P2-062。

---

## 二、逐項紀錄

### 已推送的 commit（遠端 `main`，HEAD `9711dd7`）

| commit | 內容 |
|---|---|
| `d386047` | 階段二 P2-034／P2-079／P2-080／P2-083 之前的一批 |
| `c036ddf` | **P2-092／P2-093：EC 的界線與存在性** |
| `5b65b78` | **P2-095：電池充放狀態；把「讀兩次」修掉** |
| `6309baf` | **P2-076：TPM 清除與重設的界線** |
| `9009530` | **撤回 `ev.pk.count`（既有的 `UefiSignatureFactsService` 早就做完了）** |
| `1e033b6` | **文件：未對準出處的常數表清單** |
| `e3cd9c1` | **守門：成組的規格常數表要有收錄政策（並查出一次假零）** |
| `857fa25` | **P2-061：韌體復原路徑（partial）** |
| `9711dd7` | **P2-062：信任根與 VBS 的對照（純交叉第三次）** |

### 未推送的 commit

**無**——`git log --oneline origin/main..HEAD` 的輸出只有 `9711dd7`，
而那是因為**上一次 `git push` 因為網路失敗**（`Failed to connect to github.com:443`）。
**它已經 commit，只是還沒到遠端。**

### 未提交的工作（工作樹）

```
 M Models/HardwareSnapshotModels.cs           ← HardwareFact 的第二條不變式
 M Services/AcpiTableService.cs               ← ac.tables.signatures 不再帶理由
 M Services/BatteryStateFactsService.cs       ← 同上
 M Services/EmbeddedControllerFactsService.cs ← 同上
 M Services/FirmwareRecoveryFactsService.cs   ← 同上
 M Services/TpmSecureBootCrossFactsService.cs ← 同上
 M Services/TrustRootVbsCrossFactsService.cs  ← 同上
 M Tests/HardwareFactInvariantTests.cs        ← 配合新不變式改的斷言
 M Tests/SpecTablePolicyTests.cs              ← raw string literal 縮排修正
```

---

## 三、跑了什麼、離開碼

### ① 完整測試套件（第 247 輪）

```
dotnet test Tests/XinSpect.Tests.csproj -c Debug --logger "console;verbosity=minimal"
```

| | |
|---|---|
| 離開碼 | **1** |
| 結果 | **失敗 25、通過 6,344、總計 6,369** |
| 耗時 | **29.5 分鐘** |
| 輸出 | `work/gate_full.txt` |

**而 25 條失敗裡，有 4 條是我造成的（其餘 21 條是既有的）：**

| 失敗 | 是不是我造成的 |
|---|---|
| `EvidenceLabIntegrationTests.擷取含ACPI表清單事實` | **是**（`Present` 帶了理由） |
| `SpecTablePolicyTests.常數表政策_正對照` | **是**（raw string 縮排） |
| `ServiceOrphanGateTests` | **是**（兩個孤兒服務） |
| `WiringGuardTests` | **是**（同上） |
| `FactCoverageReportTests`（未覆蓋 307 vs 基線 235） | **是**（新增鍵未納入對帳） |
| 其餘 21 條 | **否**（既有的紅燈） |

### ② 各批次的定向測試（每一輪）

**每一輪都跑**：該輪新服務的測試 ＋ `FactKeyCatalogTests` ＋ `TestArrayBoundsTests`
（後兩者是跨批次的守門）。**那些都綠。**

### ③ 這一輪最後的狀態（未跑完）

**我在這一輪把五個測試檔改壞又還原**（見下節），**所以現在的樹是紅的，而我沒有再跑一次完整套件來確認範圍。**

**這是一個未完成項，如實記在這裡。**

---

## 四、這一輪次最重要的一個發現

**`HardwareFact` 有兩條不變式，而我先前只知道一條。**

| 不變式 | 在哪 | 我什麼時候發現 |
|---|---|---|
| 非 `Present` 的事實不得帶數字 | `Models/HardwareSnapshotModels.cs` | 很早（**它擋過我五次**） |
| **`Present` 的事實不得帶 `UnavailableReason`** | **`Services/HardwareSnapshotService.cs:369`** | **第 250 輪（整合測試）** |

**而第二條只在匯出快照那條路徑上跑**，所以**單元測試全綠、只有整合測試紅燈**。

**而我在 P2-061～P2-095 那批服務裡，五處一起違反了它**——
把「這把鍵與那把的區隔」、「本服務不做判決」、「缺的是一張表不是實作」
寫進了 `Present` 的理由欄。

**處置（服務端，已完成且保留）**：
在 `HardwareFact` 加上第二條不變式——`Present` 時把理由欄的說明**併進 `Value`**、
理由欄留空。**與第一條不變式同樣的哲學：不 throw，把放錯位置的東西搬到對的位置。**

---

## 五、未完成項（如實列出）

### ① 測試端尚未配合（阻斷：現在是紅的）

**服務端改完之後，那五個測試檔的斷言仍讀 `UnavailableReason`／`Value` 的舊語意。**
我嘗試用腳本批次改，**改到第五支腳本時編譯失敗（`CS0102`：重複的常數宣告）**，
所以 `git checkout` 把那五個測試檔還原到 commit 狀態。

**正確的修法**（下一輪該做的）：
- 對 **`Present` 事實**的斷言，改成讀 `Value`（那些話被併進去了）
- 對**範圍宣告**（「這把鍵與那把的區隔」）的斷言，改成讀**服務檔原始碼**
- **一次改一個檔、每次跑該檔的測試** —— 不要再用腳本批次替換

### ② 兩個孤兒服務

`ServiceOrphanGateTests` 指名：**`BatteryStateFactsService`、`EfiVariableFactsService`**
（第三個 `UefiVersionCrossFactsService` 是既有的）。**兩個都是我的。**

**而那條守門說得很清楚**：
> 三選一：(a) 接進事實管線；(b) 接進互動入口；
> (c) 刻意的話，在登記表寫下『為什麼不接＋什麼時候才該接』。**沉默不是選項。**

### ③ 未覆蓋的事實鍵增至 307（基線 235）

**新的鍵沒有納入 `FactCoverageReport` 的對帳或 `Exemptions`。**

### ④ README badge 與實測不符

**badge 說 5,848，而實測是 6,369。** 而 `Tests/TestSuiteBaseline.cs` 的 `ProjectTests`
也還是 5,848。**兩者都該更新，而更新的依據要是「一次完整套件的實測值」。**

### ⑤ 階段二還有 35 列 `todo` ＋ 2 列 `partial`

**而整個計畫還有 891 項未做。** 以這一輪次的速度（約 1 列／輪），
**階段二還需要約 35 輪，整個計畫遠超過任何單一輪次上限。**

### ⑥ 未對準出處的五筆常數

**清單在 `docs/ULTIMATE-1000/UNSOURCED-CONSTANTS.md`。**
其中 **`TPM_PT_*` 那一張表一次解鎖兩列（P2-069、P2-075）**，
而 `Tpm2.cs` 已有那張 `[SpecRef]`，只是涵蓋範圍不夠。

---

## 六、這一輪次的教訓（可貼進帳本的部分）

### ① 「規格鍵名不存在」不等於「這件事沒人做」

**P2-052（FIT）與 P2-079／P2-080（`ev.pk.count`）是同一類錯**：
我 grep 了規格鍵名與領域詞，**但沒有 grep「這件事的動詞」**。
**鍵名前綴是規格的分類，而實作的分類可能不同**（`ev.*` 的實作在 `uefi.*` 底下）。

**所以 grep 要三條**：鍵名、領域詞、**「做這件事的動作」**。

### ② 一條什麼都不檢查的守門，跑起來和一條全部通過的守門一模一樣

**`SpecTablePolicyTests` 第一版跑出 4/4 綠，而掃描回報「0 組」**——
我的 regex 抓不到多變數宣告，所以**它什麼都沒檢查到**。

**防護**：加一條測試，**它不檢查被掃的程式碼，它檢查掃描器本身**。

### ③ 用大量低價值的編輯去滿足一個檢查，與「加標記讓紅燈消失」同型

**`SpecRefCoverageTests` 的第一版要求 55 個解碼器各加一段宣告**——
而其中多數的常數是**欄位偏移**，不是「一張規格表的一部分」。
**所以判準改成「同前綴 ≥ 3 個十六進位常數」。**

### ④ 在最後幾輪用腳本硬改測試，會把一個小問題變成一個大問題

**這一輪次的結尾就是這個**：發現不變式 → 改測試配合 → 五支腳本 → 編譯失敗 → 還原。
**而那個訊號是「改到第三支腳本時就該停下來手工做」。**

### ⑤ 「留著不說比讀兩次更糟」——而修掉比標記更好

**P2-071／P2-072 那處「讀兩次」是標記**，
**而 P2-092／P2-093 那處同樣的「讀兩次」我修掉了**（`ac.tables.signatures`）。
**標記的價值是「不隱藏」，修的價值是「不留下」。**
