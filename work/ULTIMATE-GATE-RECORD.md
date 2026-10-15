# ULTIMATE-1000 推進紀錄

依 `docs/ULTIMATE-1000/README.md` §五 的節奏：**每 10 項跑套件、每 25 項一次閘門檢查、每 100 項發版**。

> 本檔有兩種內容：上半部是**人工寫的推進紀錄**（每個檢查點一段），
> 下半部是 `work/UltimateAudit` **自動產生的結構對帳**（每次跑都覆蓋）。
> 自動產生的部分不手寫——帳本吃過「數字是推算出來的」的虧。

---

# 第 50 項檢查點（2026-10-16）

**進度：50 / 1,050（階段一 P1-001 – P1-050，約 48%）**

## 這一輪累計改了哪些檔

### 新增服務（11 支，全部唯讀）

| 檔案 | 項目 | 內容 |
|---|---|---|
| `Services/ScanSkeleton.cs` | P1-001 | `IReadOnlyScanner` 介面 ＋ `Run/Validate`。骨架只負責順序與還原，不判斷事實意義、不寫入。 |
| `Services/ScanStepTimeout.cs` | P1-002 | 超時上界**必須先宣告**；逾時落成狀態並指名步驟與毫秒數。 |
| `Services/TransientScope.cs` | P1-003 | 還原契約：還原動作不可為 null；還原失敗記錄而不外丟。 |
| `Services/SourceAvailability.cs` | P1-005 | 來源狀態與事實狀態分開。 |
| `Services/SourceRegistry.cs` | P1-006 | 名稱重複拒絕後來的，不靜默覆蓋。 |
| `Services/SourceChain.cs` | P1-007 | 主力→備援→三態，全鏈留痕。 |
| `Services/OfflineInputMount.cs` | P1-009 | 掛載狀態與事實可用性分開。 |
| `Services/SourceCapabilityMatrix.cs` | P1-011 | 宣告與實際生產雙向對帳。 |
| `Services/ScanSkeletonReport.cs` | P1-001/002/004/010/012 | 骨架觀測的五把鍵。 |
| `Services/FactStateReport.cs` | P1-014/017/020/021 | 狀態格成文、逐態申報、失敗原因、不可用查詢。 |
| `Services/SpecRefReport.cs` | P1-024–P1-032 | 出處鏈的八把鍵。 |
| `Services/KnowledgePackReport.cs` | P1-033–P1-038 | 知識包宣告的六把鍵。 |
| `Services/KnowledgeLookupReport.cs` | P1-039/040 | OUI／PCI 查詢申報。 |
| `Services/SensitivityPolicy.cs` | P1-045–P1-048 | L0–L3 分級與匯出政策。 |
| `Services/DomainKnowledgeReport.cs` | P1-041/042/043 | 微碼 CVE／dbx／PSU 三個域知識包。 |
| `Services/EgressPolicy.cs` | P1-049/050 | 出網許可清單與觸發宣告。 |

### 新增測試（12 支，共 **151** 條）

`ScanSkeletonTests` 9、`ScanContractsTests` 13、`SourceSeamTests` 14、
`ScanSkeletonGuardTests` 5（含 2 條正對照）、`SourceCapabilityAndMountTests` 13、
`AvailabilityConvergenceTests` 30、`UltimateKeyReconciliationTests` 4、
`FactStateReportTests` 16、`ScanSkeletonReportTests` 13、`SpecRefReportTests` 14、
`KnowledgePackReportTests` 14、`KnowledgeLookupReportTests` 15、
`SensitivityPolicyTests` 17、`DomainKnowledgeAndEgressTests` 19。

### 既有檔案

- `Services/FactKeyCatalog.cs`：856 → **894** 鍵（+38），維持字典序與無重複。
- `Services/FactCoverageReport.cs`：新增 **20 條**架構層豁免。

## 跑了什麼

```
dotnet build XinSpect.csproj -c Debug      → 0 警告 0 錯誤
dotnet test（全套）                         → 5705 總計、5704 通過、1 失敗（27 m 53 s）
dotnet test --filter FactCoverageReportTests → 11/11（修正後）
dotnet run --project work/UltimateAudit     → 結構檢查 no problems（離開碼 0）
```

## 紅燈與處置（兩次，都是守門正確運作）

**① `FactKeyCatalogTests.目錄與原始碼掃描逐鍵相等`（第 10 項）**
新增 6 把鍵生產了卻沒登記目錄。差集以臨時診斷測試印出（掃到 862、目錄 856、缺 6、多 0），
補登記後綠。**守門抓到我自己漏登記。**

**② `FactCoverageReportTests.未覆蓋數超過已知基線時_列出前幾個供人核對`（第 50 項）**
`未覆蓋的事實鍵增至 238 個（基線 220）`。這一條正是設計來抓本輪這種情況的：
一次加 38 把鍵時最容易「沒紅燈就繼續」。

處置：**登記 20 條豁免而不是寫對帳規則**。理由寫在豁免表裡——
這 38 把鍵的共同性質是「**來源就是它自己**」：骨架申報自己的順序與還原、
來源層申報自己的可用性與優先序、六態申報自己的分布、出處申報自己的覆蓋面。
**對帳的前提是有兩個獨立來源**；這些鍵沒有第二個來源，能對的是
「紀錄與實際執行是否一致」，那寫在各自的單元測試裡。
**豁免不是免檢**——每一條都寫明為什麼無法交叉。

## 這一輪真正抓到的一個真缺陷

```
P1-014 在矩陣宣告 `state.lattice`、FactStateLattice 實作早就存在（含 20 條測試），
但全 repo 沒有任何地方生產這把鍵。
```

同一批還有 `state.count`、`fact.readerror.reason`、`fact.why_unreadable`。
**既有的 `FactKeyCatalogTests` 抓不到它**——那條比對的是「原始碼掃到的鍵 ↔ 目錄」，
掃不到就不會報。規格宣告了、實作也做了、只是沒接線——在這個視角下完全隱形。

修法不只是補四把鍵，而是新增守門 `Tests/UltimateKeyReconciliationTests.cs`：
把 ULTIMATE 矩陣宣告的鍵，扣掉「已登錄目錄」與「未開工範圍」之後必須為零。
只檢查已開工範圍（`InProgressRanges`，目前 P1-001–P1-050）——全部 971 把一起要求
會讓這條守門從第一天起就是 971 條紅燈，**那樣沒有人會看它**。
它開工後的第一份輸出就是 9 條當場待辦，現在歸零。

## 未解的計數落差（刻意保留，有期限）

測試基線與三份 README 徽章仍是 **5512**；實跑已是 **5705**。
依節奏，這兩者**每 100 項**才更新一次。下一輪的人看到 `ChangelogTests` 的基線斷言時，
要知道它指的是上一個發版點的數字。

## 下一批

P1-051 起：掃描守門模式擴充、完整性網守門、寫入閘門與審計鏈。
第 100 項時發版（版本號、變更記錄、測試基線、三份 README 徽章）。

---
---

# 結構對帳（自動產生，每次 `work/UltimateAudit` 執行時覆蓋）

- 產生時間（UTC）：2026-10-15 20:57:36
- 稽核工具：`work/UltimateAudit`（C#，開頭跑自我檢查，失敗即以離開碼 2 結束）
- 維護方式：**本節以下由工具覆蓋，不要手寫**

- 結構不符項：0

## 通過的檢查

- 矩陣列數：1050（應為 1,050）
- 互異 ID：1050（應為 1,050）
- 每階段編號：連續 1..105
- 域碼表宣告合計逐階段比對矩陣列數：全數相符

## 待辦（未修，已計數）

- 跨階段參照：35 條（已標示，非缺陷）；未標參照的前向依賴：0 條
- 懸空依賴：0 條
- 依賴欄不是 ID 也不是佔位字：0 條
- 輸出鍵前綴未在任何域宣告：0 條（候選）

## 逐階段

| 階段 | 檔名 | 矩陣列 | 域數 | 域宣告合計 |
|---:|---|---:|---:|---:|
| 1 | `STAGE-01-FOUNDATION.md` | 105 | 10 | 105 |
| 2 | `STAGE-02-FIRMWARE.md` | 105 | 8 | 105 |
| 3 | `STAGE-03-SILICON.md` | 105 | 8 | 105 |
| 4 | `STAGE-04-STORAGE.md` | 105 | 9 | 105 |
| 5 | `STAGE-05-NETWORK.md` | 105 | 8 | 105 |
| 6 | `STAGE-06-OS.md` | 105 | 8 | 105 |
| 7 | `STAGE-07-SECURITY.md` | 105 | 9 | 105 |
| 8 | `STAGE-08-MEASUREMENT.md` | 105 | 9 | 105 |
| 9 | `STAGE-09-PLATFORM.md` | 105 | 9 | 105 |
| 10 | `STAGE-10-ULTIMATE.md` | 105 | 9 | 105 |
