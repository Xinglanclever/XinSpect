# 閘門紀錄：UEFI 變數通路的三個實質缺陷（第 262 輪）

> **前情**：第 261 輪（另一個代理做的，`GATE-LOG-261.md`）把「做了但看不到」的問題用事實總表
> 收斂、並把 C 群三把鍵收口；它在 commit message 裡留下一個觀察——「elevated 行程讀 `BootOrder`
> 得到 1314 ERROR_PRIVILEGE_NOT_HELD」。**本輪從那個觀察往下查，查出三個實質缺陷**
> （不是它的錯——三個都在它接手之前就在倉庫裡），修掉兩個、並把四份重複實作收斂成一份。
> **與第 261 輪同一個工作樹平行作業**：本輪只動它沒有在動的檔案。

---

## 一、三個缺陷，全部有實測證據

### ① 啟用權限的實作有四份，其中兩份是壞的

`SeSystemEnvironmentPrivilege` 是讀 UEFI 變數的前置條件。查證時專案裡有**四份**啟用它的實作：

| 實作 | 狀態 |
|---|---|
| `FirmwareService` | ✅ 正確（查了 `LastError`） |
| `UefiBootFactsService` | ✅ 正確（同上有查） |
| `UefiBootOrderFactsService` | ❌ **少查 `LastError`**——`AdjustTokenPrivileges` 在權杖未持有該權限時**仍回 true**（LastError＝1300），不查就是假成功 |
| `UefiSignatureFactsService` | ❌ **根本沒有啟用** |

**實測**（獨立 PowerShell P/Invoke，不走本專案的程式碼）：本機 elevated 行程讀 `BootOrder`
在啟用權限前回 `0 bytes / 1314`，**手動啟用之後同一個呼叫回 18 bytes**、`PK` 回 886 bytes。
——第 261 輪的 1314 觀察是對的，而且它擋住的比想像的多（見 ③）。

### ② 「先查大小再讀」的兩段式寫法是錯的：pdwAttributes 被當成大小用

`GetFirmwareEnvironmentVariableEx` 的第五個參數是 `pdwAttributes`（in/out），
**不是大小**；以 `dwSize＝0` 呼叫**不會**回報所需大小。舊版 `ReadFirmwareVar` 把它當
`ref size` 用，所以 `size` 永遠是 0 → **每一個變數都回 null**。
這正是 `WiringDecisions.Deferred` 裡 P2-083 那筆記的「把 pdwAttrib 的位置當成緩衝大小的進出用」——
**它不只是「屬性沒被保留」，而是整條通路都讀不到東西**。
正確做法：直接給足緩衝區，不夠時 Windows 回 122 再放大重試。

### ③ `db`／`dbx` 的 GUID 是錯的——兩把鍵從發佈以來就沒有真的讀到過

實測（同一支 API、同一個變數名，**只換 GUID**，權限已啟用）：

| GUID | db | dbx | PK | KEK |
|---|---|---|---|---|
| `{a7719a85-…}`（舊值） | 0 bytes / 203 | 0 bytes / 203 | 0 / 203 | 0 / 203 |
| **`{d719b2cb-3d3a-4596-a3bc-dad00e67656f}`** | **8188 bytes** | **3724 bytes** | 不存在 | 不存在 |
| `{8be4df61-…}`（Global） | 不存在 | 不存在 | **886 bytes** | **3955 bytes** |

結果是決定性的：**`db`／`dbx` 只在 `{d719b2cb-…}` 下存在**——那是 UEFI 規格的
`EFI_IMAGE_SECURITY_DATABASE_GUID`。
**出處的誠實說明**：本機對外網路取不到規格原文（DNS 被指到保留位址），
所以這一格的主張依據是**實測**而不是條號；規格原文到手後應補成條號。
**內部一致性的旁證**：dbx 的 3,724 bytes ＝ 28（清單標頭）＋ 77×48（SHA-256 簽章），
整除無餘——那串資料是真的 EFI_SIGNATURE_LIST，不是巧合。

**這個缺陷的直接後果**：`uefi.db`／`uefi.dbx`／`uefi.db.list.*`／`uefi.dbx.list.*`
（P2-080 的兩把別名鍵）從發佈以來**只能回 NotSupported**，而 `uefi.pk`／`uefi.dbx` 缺席
也讓 ESP 掃描的 dbx 交叉（`EspScanService`）永遠「無從對照」。

## 二、修了什麼

- **新增 `Services/FirmwareVariableAccess.cs`＝單一來源**：啟用權限（含 1300 檢查）、
  讀一個變數（緩衝區重試）、把失敗原因寫成**有依據的話**
  （權限不足／變數不存在／長度 0／緩衝不足／其他錯誤——**五種情況五句話，不猜**）。
  屬性只列**原始值**並明說「位元名未對準出處前不展開」（同 `UNSOURCED-CONSTANTS.md` ③ 的判斷）。
- **四份實作收斂成一份**：`FirmwareService`／`UefiBootFactsService`／`UefiBootOrderFactsService`
  改為薄殼委派（各自的註解保留歷史），多餘的 P/Invoke 與 `TOKEN_PRIVILEGES` 版面宣告移除。
  **新增守門**：`啟用SeSystemEnvironmentPrivilege的呼叫只能有一處`——掃的是呼叫樣式不是關鍵字
  （註解裡提到這件事的地方很多，**守門不得被文字欺騙**）。
- `UefiSignatureFactsService` 改走富結果：**同一輪只啟用一次權限**；`uefi.pk`／簽章清單的事實
  帶上屬性原始值；`uefi.boot_entries.count` 把「一個都沒有」與「全都讀不到」分開
  （前者是查證後的答案，後者標 `NotSupported` 並寫出錯誤碼）。
- **GUID golden**：`簽章資料庫的GUID是實測出來的那一個`——改回舊值會讓 db／dbx
  再一次永遠讀不到，而那不會有任何錯誤訊息。
- **更正三處因為這次查證而變成假的舊文字**：`EfiVariableFactsService` 的類別註解
  （原本把「兩段式大小查詢」列為優點——**那正是缺陷本身**）、
  `WiringDecisions.Deferred` 的 P2-083 判定（**輸入已經存在**，剩下的是接線決定）、
  與 `EfiVariableFactsTests` 的註解（「未進 Setup Mode」→ 實際是權限與 GUID）。

## 三、修好之後，這台機器的真值（第一次）

```
uefi.db.count        11 條簽章（7 條清單）、8188 bytes、屬性 0x00000027
uefi.dbx.count       77 條簽章（1 條清單，SHA-256 型）、3724 bytes
uefi.kek.count        3 條簽章（3 條清單）、3955 bytes
uefi.pk              886 bytes
uefi.boot_entries.count  9 個（Windows Boot Manager ×2、NVMe/HDD 各碟、Aquantia 網卡、iSCSI/PXE）
```

**連鎖影響**：`WiringDecisions` 的 P2-083 判定從「輸入還不存在」改為「輸入已存在、待接線」——
接線動作本身（把 `EfiVariableFactsService` 接在既有的 `uefi.*` 讀取上、不新增第二次讀取）
**這一輪沒有做**，因為 `EvidenceLabService` 正被第 261 輪的代理編輯中（同一個工作樹）。
`STAGE-02-REMAINING-KEYS.md` 裡「dbx 本身在本機也讀不到」這句話**已經不成立**，該檔已更正。

## 四、這一輪沒有做、與要交代的事

- **全套件沒有在這一輪重跑**：工作樹上還有第 261 輪未提交的變更
  （三把新鍵讓未覆蓋數到 320，而上限還在 317——**那一條守門此刻是紅的，要等它收口**）。
  本輪的證據是目標測試（93 條中 92 綠，唯一紅的就是上面那條它自己的待辦）
  加上第 259 輪的 6,414 全綠。

- **提交方式與一個要交代的副作用**：commit 用**逐一 `git add` 明確路徑**（不用 `git add -u`），
  所以第 261 輪的程式碼（`FactAtlas*`、`FirmwareVariableSurface*`、`EvidenceLabService`、
  `FactKeyCatalog`、矩陣列）**一檔都沒進我的 commit**。
  **但 `git add` 是按檔案取整個現況**——`STAGE-02-REMAINING-KEYS.md` 那一檔同時含
  它未提交的改寫（25→22 的計數、C 群收口的敘述）與我的更正，
  所以**它那份文件敘述跟著我的 commit 一起進去了**。內容本身是真的、也自相一致
  （它講的是它自己那一輪做完的事），**但歸屬要在這裡寫清楚**：
  那是它的文字，不是我加的。**同一個工作樹平行開工的代價就是這種事**——
  文件比程式碼更容易被「順手帶走」。
- **兩處第 260 輪留下的小帳沒有替它改**（它的檔案正在它手上）：
  `FirmwareDocReconcileFactsService` 類別註解寫「20 把鍵是別名」而常數是 24；
  `STAGE-02-REMAINING-KEYS.md` 的分組合計 24 與標題 25 差一把（P2-053 被放在括號裡沒有計入）。
  **都已記在這裡，等它收口時一起改。**
- **屬性位元名（EFI_VARIABLE_*）依舊不展開**——沒有出處就不展開，這一輪也沒有動那張表。

## 五、教訓

1. **「同一個判斷寫四次」的下場是「兩個地方各自漂移」**——這次是權限啟用。
   下次寫第二份之前，先找第一份。
2. **錯誤碼是唯一能讓理由欄說真話的東西。** 把 `byte[]?` 當成讀取結果的全部，
   等於把「為什麼失敗」的判斷權交給了猜測。
3. **對帳測試驗得了「鍵在目錄裡」，驗不了「鍵曾經產出過值」。**
   `uefi.db`／`uefi.dbx` 在目錄裡、在矩陣上是 ok、測試全綠——
   而**它們從發佈以來一次都沒有真的讀到過**。要抓這種缺陷，只有真機實測一條路。
