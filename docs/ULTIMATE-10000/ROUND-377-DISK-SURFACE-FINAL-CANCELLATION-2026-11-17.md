# Round377：同步表面掃描最後進度回呼取消（2026-11-17）

**來源已套用，第三次 rebuilt 聯合294/294，新class4/4；新full／freeze與正式文件守門待驗。** 本文件為obj中已對帳、由root核驗後套用的候選。

`DiskSurfaceScanService.ReadBlocks` 原在每次讀取前檢查 cancellation，但同步 `IProgress<int>.Report(100)` 可於最後一塊回呼中取消。迴圈隨即退出並回完整 SurfaceScanResult，將已取消操作誤報完成。修正只在同步迴圈後、結果建構前加 `ct.ThrowIfCancellationRequested()`，保留原 token；此檢查亦涵蓋 maxBlocks 限制下的最後一塊。正式同步 `Scan` 直接呼叫 ReadBlocks，因此不只修測試 helper；非取消的讀取、錯誤計數及進度語意保持既有行為。

候選與 preimage 綁定在 `obj/audit/next-storage-high/manifest-r377.json`，root 的 tests-only 原碼負控制、production applied receipt 分開保留。`obj/audit/r377/r377-original-2.trx` 實際 **1 通過／3 失敗／0 略過，共4，TestExit1**：單塊、17塊及 maxBlocks=1 三個最後回呼取消案例均因未拋 OCE 失敗，非取消對照通過。第一次 wrapper 因 Windows PowerShell5 對 native stderr 的 NativeCommandError 在寫 receipt 前停止；原 log／TRX 保留，第二次 pwsh 使用相同已建 DLL 的 no-build 執行取得真 exit1，見 `original-verified-root.json`，未改斷言。

`obj/audit/r379/r376-379-target.trx` 首次 rebuilt 聯合結果 **278 通過／2 失敗／0 略過，共280，runner3秒，TestExit1**；DiskSurfaceFinalProgressCancellationTests **4/4**、既有 DiskSurfaceIntegrityTests31/31。新案例直接走生產同步 ReadBlocks，注入 read delegate 和同步 progress，驗 token、讀取次數、100% 回呼與無取消對照，不做 native 磁碟讀取。兩個聯合失敗屬 R378；後續聯合結果待實際 receipt。

第二次 rebuilt `r376-379-target-2.trx` 實際 **293通過／1失敗／0略過，共294，TestExit1，runner3秒**；本class仍4/4，既有DiskSurfaceIntegrityTests31/31。唯一schema測試的假integrity fixture失敗由root改用Create，正式validator未放寬，第三次實跑待驗。

第三次新隔離輸出 rebuilt `r376-379-target-3.trx` 實際 **294/294通過、0失敗／略過，TestExit0，runner3秒**；本class4/4、DiskSurfaceIntegrity31/31。TRX時間2026-11-17T20:53:50.8796622+08:00–20:53:58.5299280+08:00，SHA-256 `5774d27736db5f97a1dcacf6830cc7d479a53916021510ccfcb74fc6f4554621`；`target3-observation.json`保存原TRX及來源hash對帳，前兩次失敗保留。

限制：這是合作取消的最後邊界，不是對進行中的同步 native I/O 作硬取消，也不宣稱最後檢查之後不再存在任何外部取消競態。已完成的讀取不能回復；既有資源與斷路器合約沿用。新 full／freeze、文件守門與實機磁碟驗收尚缺；baseline 仍為合格 full **9,257**，不由定向數累加。沒有 commit、push 或發版。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
