# Round354：WHEA發布隔離與GPU pipeline合作取消（2026-11-17）

兩組最終候選已核hash套用 **6個targets**（`obj/audit/r354/applied.json`）：WHEA發布隔離3筆、GPU pipeline取消3筆；EvidenceLabService使用保留r353來源失敗修正與importer的merged354版。**定向23/23通過、0失敗／略過**（`obj/audit/r354/r354-target.trx`），runner **535ms**；TRX起訖2026-11-17 05:26:58.5079318–05:27:02.8002306（UTC+08:00，4.292秒），計時範圍分列。本輪350–354聯合全套結果：**8,834/8,834通過、0失敗／略過**（`obj/audit/r354/r354-full.trx`），runner **9分1秒**；TRX2026-11-17 07:16:05.9169390–07:25:11.0004526（UTC+08:00，9分5.084秒），`full-command-exit.json`的TestExit／FreezeExit均0；凍結核對：**2,030來源／文件＋3DLL，共2,033份SHA-256零漂移**（`obj/audit/r354/freeze-verification.json`：Checked2033／Drift0，原freeze及verification保存），凍結核對完成後才解除凍結同步baseline／文件。這是實際聯合範圍，不能背書尚未整合的355或擴大為硬體／GUI驗收。

本輪後續帳：baseline／HTML同步後重建文件守門 **66/66通過、0失敗／略過**（`obj/audit/r354/r354-final-docs.trx`），runner16秒；TRX2026-11-17 07:31:25.5030210–07:31:45.8587826（UTC+08:00，20.356秒）；兩official generators check均回0（root串行session40321 exit0），HTML sourceSHA前綴`6aab08a367e8`。此66項早於closure三檔回填；closure回填後文件守門已以`obj/audit/r354/r354-closure-docs.trx` **66/66通過、0失敗／略過**（runner14秒，exit0）；TRX2026-11-17 07:47:38.5674937–07:47:57.4350997（UTC+08:00，18.868秒），r354正式收口證據已齊；CLI建置 **0錯誤／13個既有CS0162警告**（`obj/audit/r354/r354-cli-build.log`，session16488 exit0）；六項離線smoke（`obj/audit/r354/r354-cli-smoke.json`）依序退出 **0／1／2／2／0／0**，help、非法sensor選項、缺目錄不可比、壞包SnapshotReadError、兩份合成可比快照及Redfish序號遮蔽／來源與privacy metadata全部內容斷言均通過，root確認session40321的smoke及兩generator checks串行exit0；ProjectTests baseline 已按實跑由 **8,602更新8,834**（`obj/audit/r354/baseline-update.json`），不由作者案例計畫推算。r351 full8,731/8,731與原freeze2020檔／Drift2及baseline-update拒絕的歷史完整保留，不改稱0drift，也不由新增測試推算新總數。

## WHEA單domain發布隔離

02候選在正式shared collection改由MeasureWheaMemory入口。Begin在worker dispatch前持per-instance WHEA lock取得generation、monotonic start、budget及token；Read／Facts／read-only copy在local進行，成功及故障最後走相同commit critical section。commit及timeout revoke共用同一lock、generation、Finished一次決定，deadline在lock內核，不能先check token後於別處無保護發布。

timeout先贏便發布summary ReadError、null numeric並去舊section，late success／throw不能再覆寫；commit在deadline前先贏，延後timeout callback不能推翻。超代generation不發布或通知，notification在lock外。worker先看到deadline過期亦回false，wrapper固定非私密exception使Measure記failed，避免先前候選「timeout fact但Succeeded=true」矛盾；此審查發現已在最終候選修正，不冒稱仍未修。

這是publication revocation，未Abort／Interrupt thread、提前dispose source handle、註冊假native cancellation或聲稱deadline時worker已退出。真底層生命週期仍由worker完成及finally收尾，其他100+domain、provider-wide重讀、全collector原子交易／reset未修。讀取pending期間仍可能顯示先前WHEA stamps；公開direct LoadWheaMemory API保原簽名及budget0，仍無新增硬截止。cancel在commit觀察後抑制發布，valid commit之後取消不回溯；production各來源沒有因此全部接caller token。

九項注入候選含late success／late throw、expired commit、delayed timeout、superseded result、safe failure、cancel、failed step及outside-lock reentry；real-wait fixture有release／finally／Join，不遺棄假worker。相關注入已納入本次23項通過定向，沒有EventLog／hardware／GUI實機證據。

## GPU pipeline合作取消與drain

04候選保留r351單一owned handle，增加planning／free-space／acquisition／資料產生／write／flush／read／progress-to-engine各階段合作token checkpoint。data loop每64KiB檢查，read loop每次同步read前檢查；已派發的同步I/O不拋棄、不為回應取消先close handle。

engine仍直接await實際ProcessAsync，不用WaitAsync／WhenAny提前釋放。合法late success在await及payload／timing Validate後轉Cancelled且空metrics；真engine fault維持PlatformError／NotSupported／Unstable分類，requested matching-token OCE才是caller取消，foreign／未請求OCE為PlatformError。發現mismatch後保留完整已觀察integrity failure及Unstable，不讓稍後取消蓋掉；尚未發現mismatch前可以取消停止比較，不將未比較資料說成已驗正常。

production default D3D11 engine仍以CancellationToken.None派native Task.Run，COM在finally處置；未新增硬GPU取消、OS同步I/O硬中斷或真GPU drain證據。既有VM／orchestrator等待真service完成期間維持busy，不因本候選另建硬體所有權。

13項新增及一項既有ownership取消案例候選均4,096-byte fake-engine fixture，本次執行含真小型檔案I/O。pending engine fixture檢查取消時lease仍開，settle後await service才刪fixture；原既有其他主類測試仍可能128MiB及最小真GPU smoke，不可泛稱全類無I/O。GPU範圍使用該窄filter，總結果按本次23項定向TRX記錄，不將個別候選方法數當作完整全套。

## EvidenceLab重疊與待驗帳

- 02 WHEA base hash `4e971c2e5e577ea4e3782e5fb22c9140ec4f8abb7f2bca924afd40762079d278`已保留r353 Baseline／Fleet修正；必須另保留09 importer插入，不可拿base覆蓋merged353。
- `obj/audit/r354/integration/capacity03/manifest.json`的merged354 hash **13c01f6640b99b815c226a3af80e74452b38937b5e41b18fdd682ed1b2cfea37**，apply真前置為merged353 **94e871aa…**。獨立byte review確認只加198 bytes importer property，移除後逐byte還原WHEA base，沒有遺失r353來源失敗修正。
- review：`obj/audit/r354/reviews/capacity05/review-capacity02-whea-publication.txt`、`review-capacity03-merged353-354.txt`及`review-capacity04-by-capacity01.txt`。無blocking靜態發現不是執行成功。
- 套用6 targets及23項定向已核；350–354 full、freeze、來源規模／文件／生成器／HTML及CLI各按本輪首段與後續帳記錄真實結果。baseline／HTML同步後的66文件門檻已過；closure回填後文件守門已以`obj/audit/r354/r354-closure-docs.trx` **66/66通過、0失敗／略過**（runner14秒，exit0）；TRX2026-11-17 07:47:38.5674937–07:47:57.4350997（UTC+08:00，18.868秒），r354正式收口證據已齊；實際UI對話框／visual／accessibility與native硬體／取消仍未驗；限定UI／native硬體未驗項目仍不能由offline結果背書。
- r351原freeze Drift2與full證據不覆寫，350–354新聯合凍結另立證據；本輪未提交、上傳或發版，不新增候選完成數或硬體支援宣稱。
