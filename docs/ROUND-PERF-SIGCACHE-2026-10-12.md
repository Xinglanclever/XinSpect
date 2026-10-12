# 本輪：證據「更新」速度——Authenticode 指紋快取（2026-10-12）

> 觸發：使用者指示「推進更新速度」。先量測，再動手；沒有量到的不寫。

## 1. 量測（先看清楚再改）

以暫時探針（量完即刪，不進版控）對**真實機器**計時 `EvidenceCollection.LoadUsermodeFacts`
與各載入器。重點：整條更新路徑幾乎**不耗 CPU**（`user` 時間趨近 0），時間全花在**等待**。

| 量測 | 值 |
|---|---|
| `KernelModuleService.Collect` 冷（舊行為） | 1651 ms |
| 同上，暖（快取） | 16 ms |
| `ListenPortFactsService.Collect` 冷 | 1736 ms |
| 同上，暖 | 29 ms |

兩者都靠 `KernelModuleService.VerifyAuthenticode*`（wintrust `WinVerifyTrust`）**逐檔重複驗簽**：
監聽埠每個 (協定, 埠) 各驗一次，同一支 `svchost.exe` 往往同時持有多個埠；跨輪重載更是每次從頭驗一遍。
**原本沒有任何快取。**

## 2. 修改（最小切片）

`Services/KernelModuleService.cs`：`VerifyAuthenticodeWith` 外包一層 `VerifyCached`。

- 鍵＝**檔案指紋**：完整路徑＋大小＋最後寫入時間（100 ns ticks）＋動作 GUID。
- **以指紋失效，不是以時間失效**（沿用 QS-007 的同一條紀律）：檔案一被換掉就一定重驗。
- 有界：上限 2048 筆，超過整批清空；`ConcurrentDictionary` 執行緒安全。
- **不改變任何判定**：命中時回傳的 `(通過?, 說明)` 與不快取時逐字相同。
- 診斷閘：`XINSPECT_SIGCACHE=off` 時**完全不經快取**（不讀也不寫），用來把「快取效益」與
  「環境快慢」分開量——下面的前後對照就是這樣量出來的。

## 3. 驗證（實測，非只建置）

同一支二進位、同一行程、緊鄰交替、每輪先 `ClearVerdictCache()`：

| usermode 更新路徑 | 三次（ms） | 中位 |
|---|---|---|
| 快取**關**（舊行為） | 12802, 11094, 13236 | **12,802 ms** |
| 快取**開** | 7811, 7747, 7724 | **7,747 ms** |

**省下 5,055 ms／輪（−39%），穩定事實鍵集合逐鍵完全相同**（不計逐輪自然變動的 `net.listen.*`）。
四捨五入後「開」的離散度只有約 90 ms——這條路徑原本的抖動多半來自重複驗簽。

測試：`Tests/SignatureVerdictCacheTests.cs`（5 條，含指紋失效、判定不變、診斷閘、不存在檔）。
受影響集合（本組＋`KernelModuleTests`／`ListenPortFactsTests`／`SelfIntegrityFactsTests`／
`PeInspectTests`／`ChangelogTests` 兩條基線守門）＝**63 綠**。

### 平行的坑（已修）

第一版測試斷言**全域快取筆數**，單獨跑綠、與其他類別平行跑就紅（別的測試也在驗簽）。
改成以**各測試自己的唯一暫存檔**查 `IsVerdictCached(path)`——隔離在檔名上，與平行無關。

## 4. 誠實界線與未竟事項（交棒）

- 本輪**只**動簽章驗證；`StorageSmartService` 的 15 秒斷路器、`PlatformFacts`、
  `LoadSoftwareFacts` 其餘子服務尚未處理。
- 已知但**本輪未動**的下一批熱點：
  - `smart.failing_now` 對「協定查詢不回應」的磁碟會等 `DiskIo` 3 秒逾時（且逾時執行緒會留在
    核心不可中斷等待裡，**每輪多洩一條**，實測連提權後的 `taskkill /F` 都殺不掉）；
    `_noResponse` 目前是**實例**欄位，所以每一輪重載都重新付一次。提升為行程級備忘是最直接的下一步。
  - `LoadPlatformFacts`（SMBIOS／顯示器／WMI 群）仍未拆解量測。
- 前一次 CLI 端到端 51.7 秒的數字是**在有其他建置並行時**量的，不可當基準；請用上表（同條件 A/B）。
