# Round379：並行儲存探測成功不得清除另一筆故障（2026-11-17）

**正式來源與新測試已套用，第三次 rebuilt 聯合294/294、新class4/4；新full／freeze與正式文件守門待驗。** 本文件為obj中已對帳、由root核驗後套用的候選。

同一 `(disk, operation)` 可有先後已派發的兩次探測：B先回失敗並Trip，較早派發的A稍後回成功。原 `StorageProbeBreaker.Report(read:true)` 無條件TryRemove，A的成功抹掉B的新故障，冷卻閘門因此失效。本輪成功只回true，不清除任何故障記錄；失敗仍Trip。實際 `StorageSmartService` NVMe log通路在派發前走ShouldSkip，過期記錄由既有admission清除後才重試；改動不增加硬體I/O、retry或背景thread。

候選原沿storage審查的歷史ID封存在 `obj/audit/next-storage-high/r378/manifest-r378-potential.json`；正式輪次為R379，不能把該歷史檔名誤當R378資產修正。`obj/audit/r379/production-applied-root.json` 綁定正式StorageProbeBreaker與候選SHA；tests-only和production套用證據分開保留。

原碼負控制與R378共用 `r378-379-original.trx`，聯合 **7通過／9失敗／0略過，共16，TestExit1**。StorageProbeBreakerConcurrentReportTests獨立 **3通過／1失敗，共4**；故障後舊成功回報把ShouldSkip變false的案例紅，其餘三個隔離／未trip成功／到期admission對照綠。這是明確排程的順序事件重現，不宣稱進行真native並行探測；各class實帳及hash見 `original-negative-verified-root.json`。

第一次新隔離輸出 rebuilt `r376-379-target.trx` **278通過／2失敗／0略過，共280，runner3秒，TestExit1**；本class **4/4**、既有StorageProbeBreakerTests27/27，兩紅均為R378。後續聯合重建receipt待root，未預填綠。新測試直接呼叫正式ShouldSkip／Trip／Report並固定nowMs，驗不同disk／operation隔離及Cooldown邊界，無native存取。

第二次 rebuilt `r376-379-target-2.trx` 實際 **293通過／1失敗／0略過，共294，TestExit1，runner3秒**；本class仍4/4，StorageProbeBreakerTests27/27。唯一schema測試的假integrity fixture失敗由root改用Create，正式validator未放寬，第三次結果待驗。

第三次新隔離輸出 rebuilt `r376-379-target-3.trx` 實際 **294/294通過、0失敗／略過，TestExit0，runner3秒**；本class4/4、StorageProbeBreaker27/27。TRX時間2026-11-17T20:53:50.8796622+08:00–20:53:58.5299280+08:00，SHA-256 `5774d27736db5f97a1dcacf6830cc7d479a53916021510ccfcb74fc6f4554621`；`target3-observation.json`保存原TRX／exit與來源hash對帳，前兩次失敗不覆寫。

限制：本修正採保守故障保留，成功不證明另一筆故障已恢復；要等既有cooldown到期與ShouldSkip admission才放行。它沒有引入每次probe generation token，也不宣稱全面解決其他斷路器競態、native取消或裝置故障。新full／freeze與文件守門待驗，baseline仍為上一合格實跑 **9,257**；沒有commit、push或發版。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
