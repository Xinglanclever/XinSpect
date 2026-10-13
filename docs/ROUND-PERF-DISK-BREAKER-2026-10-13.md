# 本輪：證據「更新」速度——儲存協定查詢斷路器（2026-10-13）

> 觸發：使用者指示「推進更新速度」。先量測，再動手；沒有量到的不寫。
> 接續 [ROUND-PERF-SIGCACHE-2026-10-12.md](ROUND-PERF-SIGCACHE-2026-10-12.md) 交棒清單的第一項。

## 1. 量測：錢花在哪裡

暫時探針（量完即刪）對**真實機器**逐來源計時。整條載入仍然幾乎不耗 CPU，時間全在等待。

`LoadSoftwareFacts`（21 個子收集器）逐項計時——**16,879 ms 裡有 12,057 ms 只是一個收集器**：

| 子收集器 | 冷 (ms) | 暖 (ms) |
|---|---|---|
| **`SmartFailingNowFactsService.Collect`** | **12,057** | **12,028**（完全沒改善） |
| `OptionalFeatureService.Collect` | 1,597 | 1,589 |
| `WindowsUpdateHistoryService.Collect` | 1,326 | 7 |
| `NicHealthFactsService.CollectMacVendors` | 600 | 596 |
| `ServiceInventoryService.Collect` | 523 | 470 |
| 其餘 16 項合計 | ~780 | ~640 |

再把 `smart.failing_now` 拆到單一 IOCTL（`disk-hang` 探針，逐顆實體磁碟）：

| 磁碟 | `TryGetBusType` | `TryReadNvmeHealth` | `TryReadNvmeIdentify` |
|---|---|---|---|
| 0、1（SATA） | 11 / 1 ms | 3 ms（不適用） | 3 ms |
| **2（NVMe）** | 0 ms | **3,006 ms** | **3,000 ms** |
| 3（NVMe） | 2 ms | 3 ms | 3 ms |
| **4（NVMe）** | 0 ms | **3,003 ms** | **2,999 ms** |
| 5–15（不存在） | 0 ms | — | — |

**根因**：PhysicalDrive 2 與 4 的 NVMe 協定查詢**根本不會回來**——3,006 / 3,003 / 3,000 / 2,999 ms
不是「慢」，是每一次都撞上看門狗（`DiskIo.DefaultTimeoutMs`）的 3 秒上限。
兩顆碟 × 兩條查詢 × 3 秒 = **12.0 秒**，與 `Collect` 的 12,057 ms 對得上。
同一顆碟的其他查詢（匯流排型別、ATA 屬性、門檻表）都是毫秒級，所以這是這兩條查詢專屬的現象。

**為什麼每輪都重付**：`StorageSmartService` 的靜態查詢是無狀態的；記得「問過不回應」的
`_noResponse` 是 UI 服務**實例**上的欄位，而走事實收集的這條路根本不經過它。
於是每按一次更新就再等 12 秒，**而且每筆逾時都在核心不可中斷等待裡留下一條殺不掉的執行緒**
（見 [DiskIo.cs](../Services/DiskIo.cs) 的 2026-09-04 實測記錄），每輪多洩兩條。

## 2. 修改（最小切片）

| 檔案 | 改動 |
|---|---|
| `Services/DiskIo.cs` | 新增 `GuardedProbe`／`GuardedProbeValue`：**說出這次是逾時還是快速失敗**。原 `Guarded`／`GuardedValue` 改成委派過去，行為與回傳值不變。 |
| `Services/StorageProbeBreaker.cs` | **新增**。靜態、執行緒安全；鍵＝**（磁碟, 查詢）**；冷卻 10 分鐘後重試；`XINSPECT_DISK_BREAKER=off` 完全不經斷路器（A/B 診斷用）。 |
| `Services/StorageSmartService.cs` | 七個受看門狗保護的呼叫點改走 `GuardedRead`／`GuardedReadValue`（先問斷路器，逾時才記一筆）。 |

三條刻意的紀律：

1. **只有逾時才記。** 裝置回一句「不支援」是合法的毫秒級結果，把它也當成卡住，
   會讓「讀不到」擴散到本來讀得到的地方。
2. **鍵含「哪一條查詢」。** 同一顆碟的匯流排型別讀得到（0 ms）而 NVMe 健康紀錄逾時——
   混用同一個鍵會把好的那條也一起放棄，事實會**真的少一項**。
3. **冷卻而不是永久放棄。** 永久放棄等於憑一次逾時就宣稱這顆碟這輩子不會回應；
   驅動重載、外接盒換埠、休眠喚醒都可能讓它回來。

## 3. 驗證（實測，非只建置）

同一支二進位、同一行程、同一支碟、同一輪交替。

| `SmartFailingNowFactsService.Collect` | 第 1 輪 | 第 2 輪 | 第 3 輪 |
|---|---|---|---|
| 斷路器**關**（舊行為） | 12,033 ms | 12,017 ms | 12,013 ms |
| 斷路器**開** | 12,011 ms（付一次） | **9 ms** | **7 ms** |

端到端、`LoadSoftwareFacts` **暖機第二輪**：**13,140 ms → 1,095 ms（省 12,045 ms，−92%）**。

**事實不變**（這是本輪最重要的一條）：

- `smart.*` 5 筆，逐欄（鍵／值／單位／類別／名稱／三態／信任級／數值／不可得原因）**完全相同**；
- 整包 `LoadSoftwareFacts` 65 筆**逐字相同**。

理由很直接：逾時本來就回 `null`，跳過也回 `null`——斷路器省掉的是**重複的等待**，不是判定。

測試：`Tests/StorageProbeBreakerTests.cs`（7 條；第二刀另在 `VirtualizationJudgeTests.cs` 加 1 條守門，
受影響集合共 **125 條綠，exit 0**）——逾時才回報逾時、快速失敗／例外不算逾時、
實值型別版本語意相同、同一鍵跳過而**其他鍵與其他磁碟不連坐**、冷卻到期重新去問（注入時間，
不必真的等十分鐘）、診斷閘關閉時不記也不跳、以及一條**接線守門**：
`StorageSmartService` 裡不得再出現直接呼叫 `DiskIo.Guarded` 的查詢（新增查詢若繞過斷路器，
功能面完全看不出來，只有真機上多等 3 秒）。

## 3b. 第二刀：`Win32_OptionalFeature` 慢表被問三趟

斷路器落地後重跑同一組探針，機器的雜訊也降下來了，`LoadPlatformFacts` 的逐項數字變得可讀
（整包 **2,393 ms**，21 個子收集器加起來就是整包，沒有藏東西）：

| 子收集器 | 暖機 (ms) |
|---|---|
| **`VirtualizationFactsService.Collect`** | **1,262** |
| `RoleSurfaceFactsService.Collect` | 555 |
| `TimeSyncFactsService.Collect` | 201 |
| `AudioLatencyFactsService.Collect` | 136 |
| `CameraFactsService.Collect` | 129 |
| 其餘 16 項合計 | ~90 |

拆到 `VirtualizationFactsService` 裡面：**Hyper-V／Containers／WSL 三個選用功能各發一次
`Win32_OptionalFeature` 查詢**——而這是本機最慢的 WMI 類別之一，每次查詢都要走元件存放區。
同一張慢表走三趟。（`RoleSurfaceFactsService` 的 555 ms 是另一條 WMI 查詢，尚未處理。）

**修改**：`Services/VirtualizationFactsService.cs` 把 `OptionalFeatureState(單一)` 換成
`OptionalFeatureStates(多個)`——三個名稱用 `OR` 併成**一張**查詢，值仍逐筆取自
`InstallState`，查不到的功能一樣回 -1。

**驗證**（同一行程、緊鄰交替，探針就地複製舊行為與新行為）：

| | 第 1 輪 | 第 2 輪 | 第 3 輪 |
|---|---|---|---|
| 三次單查（舊） | 1,537 ms | 1,259 ms | 1,254 ms |
| 一張查詢（新） | **523 ms** | **525 ms** | **517 ms** |

**三個值逐字相同**：Hyper-V＝1、Containers＝1、WSL＝2（三次都是）。
`VirtualizationFactsService.Collect` 整包：**1,262 ms → 530 ms**。
產出事實也如常：`virt.platform＝是`、`virt.hns＝是`、`virt.vbs＝關閉`。

守門：`Tests/VirtualizationJudgeTests.cs` 新增一條——`Win32_OptionalFeature` 只准有一處查詢建構。
理由與斷路器那條相同：這件事在功能面**完全看不出來**，慢的是同一組事實，讀者只覺得頁面卡一下。

## 4. 誠實界線與未竟事項（交棒）

- **只省掉重複的那 12 秒**：行程內**第一次** `Collect` 仍要付 12 秒（那是真的在等逾時）。
  要連第一次也省，得動逾時策略或把兩條 NVMe 查詢並行——本輪沒做，因為那會動到判定邊界。
- 本輪動了兩條路徑：`smart.failing_now` 與虛擬化選用功能。前一輪已處理簽章驗證快取。
  其餘熱點（同一輪稍早、仍在機器有負載時量的數字，順序僅供排序）：`LoadSbom` **2,596 ms**、
  `LoadAdvancedSupplement` **2,123 ms**、`OptionalFeatureService.Collect` **1,589 ms**、
  `LoadServerRoleDeep` **853 ms**、`LoadEnvironmentRuntime` **839 ms**、
  `RoleSurfaceFactsService.Collect` **555 ms**。
  其中 `LoadSbom` 的 2.6 秒來自一發 `Win32_PnPSignedDriver` 全表查詢——
  那是事實本身需要的完整元件清單（計數與內容雜湊），**不能靠縮小集合來加速**，
  留待日後另想通路（例如改用 SetupAPI）並重新對帳事實。
- `LoadSoftwareFacts`（暖機後約 1.1 秒）與 `EvidenceCollection.LoadUsermodeFacts`（約 18 秒的來源總和）
  在 `StartupSequence.LoadDeepSpecsCore` 裡是**循序**的：兩者互不相依，可並行重疊。
  這是下一輪最直接的結構性加速。
- 環境註記：本機同時有另一個 agent 在跑測試與建置，數次建置因共用 `obj/` 的 WPF 暫存專案而失敗
  （`*_wpftmp.csproj` 找不到產生的 `.g.cs`）。重試即可，與本輪改動無關。
- 工作區裡 `Tests/TestResults/` 已累積 **791 MB** 的 .trx 與附件（未進版控），值得清一次。
