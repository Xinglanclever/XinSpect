# Round376：Ceiling 負載授權、worker 所有權與取消分類（2026-11-17）

**三份 v2 targets 已套用；第三次 rebuilt 聯合294/294，本class23/23；新full／freeze與正式文件守門待驗。** 本文件是obj下已對帳的文件候選，由root核驗後套用。

原 Ceiling 負載在 constructor 逐一啟動 worker，後續建立失敗會遺失先前已啟動的 thread；有限時 Join 的結果未證明 drain，linked cancellation source 可能在 worker 結束前釋放。public Start 亦須遵守第一級進階診斷授權與執行中撤權。本輪以 `CeilingLoadSession` 承接 linked stop、每個成功 Start 的 worker 及 session semaphore；真 `CeilingService.RunWindow` 直接使用 session，沿用 `DeepBenchWorkerLifetime` 的預配置、成功 Start 後登記、Stop、真 Join、最後 dispose／release 次序。第一級 admission、factory／Start 前後及有限算術 slice 重核授權；caller Stop 經 linked token 傳到 worker。被動 LoadStatic 不製造負載。

v1 靜態獨立審查找出兩個取消來源錯分：worker 先見 policy=false 被當 fault；owner Stop 後 callback 自行拋 OCE 又可能被吞成成功。v2 只把 gate 本身建立的 private LoadStoppedException 當合作停止；policy callback OCE 包成 InvalidOperationException，保留 InnerException，early／late 真故障在控制端及 drain 後仍可見。v1、v2 manifests 與兩份 review 均保留，既有 helper 未修改。

`obj/audit/r376/r376-v1-classification.trx` 實際 **2 通過／3 失敗／0 略過，共5，TestExit1**；這是「v1 staged session＋v2 測試」分類負控制，並非原始產品負控制。三紅分別為 worker-first 撤權回 AggregateException、late callback OCE 無例外，以及 admission callback OCE 未包裝；兩個順序對照通過。finally restore 的實際 SHA 與 v2 manifest 相符，見 `v1-restored-root.json`；`v1-negative-verified-root.json` 保留 counters、時間與 hash。

首次新隔離輸出目錄重建的 `obj/audit/r379/r376-379-target.trx` 實際 **278 通過／2 失敗／0 略過，共280，TestExit1，runner3秒**。CeilingLoadLifetimeTests **23/23**、DeepBenchWorkerLifetimeTests11/11、RaplLoadSafetyTests23/23、CeilingDecoderTests56/56；兩紅均為 R378，不能隱去。23 項使用受控 thread／factory／observer／public entry seams，涵蓋部分啟動、撤權順序、Stop、持有至 drain、重用及 early／late fault；測試在第一個 Check 退出或使用 idle worker，沒有執行實際算術 slice、MSR 或硬體負載。後續聯合重建結果待 root receipt，不預填。

第二次 rebuilt `r376-379-target-2.trx` 實際 **293通過／1失敗／0略過，共294，TestExit1，runner3秒**；本class仍23/23，R378擴充至26/26。唯一失敗是SnapshotSchemaCompatTests新增ToJson斷言使用舊fixture的假integrity「h」，正式validator正確拒收；root改用Create取得合法快照，第三次結果待驗。沒有為此放寬validator或刪斷言。

第三次新隔離輸出 rebuilt `r376-379-target-3.trx` 實際 **294/294通過、0失敗／略過，TestExit0，runner3秒**；本class23/23，owner11、RAPL safety23、Ceiling decoder56均全綠。TRX為2026-11-17T20:53:50.8796622+08:00–20:53:58.5299280+08:00；SHA-256為 `5774d27736db5f97a1dcacf6830cc7d479a53916021510ccfcb74fc6f4554621`。`target3-observation.json`從原TRX、command-exit與正式／候選來源重新分帳，非方法數推算；前兩次失敗未覆寫。

限制：ReadStatics 的既有同步前置讀取與窗內批次 native read 不能硬取消；owner 要等實際返回／Join，不宣稱 native 時限。Start-boundary 注入不是 OS Thread.Start 真失敗。未加熱中止、worker 上限或拓撲語意，沒有多群組／熱安全／量測精度驗收。非預期 Join error 若 worker 仍活，helper 保留 owner 並報錯，不宣稱已 drain。baseline 維持上次合格 full **9,257**，不按定向推算；沒有 commit、push 或發版。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
