# ULTIMATE-1000 推進紀錄

依 `docs/ULTIMATE-1000/README.md` §五 的節奏：**每 10 項跑套件、每 25 項一次閘門檢查、每 100 項發版**。

> 本檔有兩種內容：上半部是**人工寫的推進紀錄**（每個檢查點一段），
> 下半部是 `work/UltimateAudit` **自動產生的結構對帳**（每次跑都覆蓋）。
> 自動產生的部分不手寫——帳本吃過「數字是推算出來的」的虧。

---

# 第 10 項檢查點（2026-10-16）

**進度：10 / 1,050（階段一 P1-001 – P1-011）**

## 改了哪些檔

| 檔案 | 內容 |
|---|---|
| `Services/ScanSkeleton.cs` | **新增**——`IReadOnlyScanner` 介面 ＋ `ScanSkeleton.Run/Validate`。骨架只負責順序與還原，不判斷事實意義、不寫入。 |
| `Services/ScanStepTimeout.cs` | **新增**——P1-002 超時上界宣告。上界必須先宣告；逾時落成狀態並指名步驟與毫秒數。 |
| `Services/TransientScope.cs` | **新增**——P1-003 `finally` 還原契約。還原動作不可為 null；還原失敗記錄而不外丟。 |
| `Services/SourceAvailability.cs` | **新增**——P1-005 來源可用性接縫。來源狀態與事實狀態刻意分開。 |
| `Services/SourceRegistry.cs` | **新增**——P1-006 來源列舉與去重。名稱重複拒絕後來的，不靜默覆蓋。 |
| `Services/SourceChain.cs` | **新增**——P1-007 來源優先序與取代鏈。主力→備援→三態，全鏈留痕。 |
| `Services/OfflineInputMount.cs` | **新增**——P1-009 離線輸入源掛載接縫。掛載狀態與事實可用性分開。 |
| `Services/SourceCapabilityMatrix.cs` | **新增**——P1-011 來源能力矩陣。宣告與實際生產雙向對帳。 |
| `Services/FactKeyCatalog.cs` | 加入 6 把新鍵，856 → **862** 鍵，維持字典序、無重複。 |
| `Tests/ScanSkeletonTests.cs` | **新增** 9 條 |
| `Tests/ScanContractsTests.cs` | **新增** 13 條 |
| `Tests/SourceSeamTests.cs` | **新增** 14 條 |
| `Tests/ScanSkeletonGuardTests.cs` | **新增** 5 條（含 2 條正對照） |
| `Tests/SourceCapabilityAndMountTests.cs` | **新增** 13 條 |

## 跑了什麼

```
dotnet build XinSpect.csproj -c Debug                 → 0 警告 0 錯誤（25.7 s）
dotnet test（全套）                                    → 5566 總計；5565 通過、1 失敗（27 m 28 s）
dotnet test --filter 目錄／catalog 相關                 → 24/24 通過（修正後）
dotnet test --filter Coverage/KeyRange/WarningBaseline/Changelog/Wiring/ProbeWiring → 54/54 通過
dotnet run --project work/UltimateAudit               → 結構檢查：no problems（離開碼 0）
```

## 唯一一次紅燈與它的處置

`FactKeyCatalogTests.目錄與原始碼掃描逐鍵相等` 紅燈——**守門正確運作**。

新增的 6 把鍵（`off.mount.kind`、`scan.restore.ok`、`src.available`、`src.capability`、
`src.chain`、`src.registry.count`）生產了但沒登記目錄。差集以一支臨時診斷測試印出來
（掃到 862、目錄 856、缺 6、多 0），補進目錄後 24/24 綠，臨時診斷測試已刪除。

**這不是缺陷，是守門抓到我自己漏登記。**記在這裡是因為它證明目錄守門有牙。

## 未解的計數落差（刻意保留，有期限）

全套跑出 **5566**；我這一輪新增 54 條（9+13+14+5+13），基線 5512 + 54 = **5566**，相符。

但**測試基線與三份 README 徽章仍是 5512**——依節奏，這兩者每 100 項才動一次。
在那之前，「基線 5512」與「實跑 5566」會不一致。下一輪的人看到 `ChangelogTests`
的基線斷言時，要知道它指的是**上一個發版點的數字**，不是當前實跑值。

## 下一批

P1-012（骨架相容性報告）已在測試中建立「可列舉」的雛形，完整版要等既有服務逐一套用後才有內容。
P1-013 起進入六態與狀態格。

---
---

# 結構對帳（自動產生，每次 `work/UltimateAudit` 執行時覆蓋）

- 產生時間（UTC）：2026-10-15 20:29:44
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
