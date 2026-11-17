# Round352：儲存handle與worker生命週期（2026-11-17）

三組候選已核前置及內容SHA-256套用，`obj/audit/r352/applied.json`共 **7份映射**：DiskIoMatrix 2、StorageWriteIntegrity 3、Gauntlet 2。套用前像及清單保存於該目錄；staged標籤屬製備歷史，以applied為準。**本輪定向42/42通過、0失敗／略過**（`obj/audit/r352/r352-target.trx`），runner2秒；TRX起訖2026-11-17 05:18:22.8052389–05:18:29.4367398（UTC+08:00，6.632秒），計時範圍分列。這是本輪42項定向，不是350–354全套或native硬體驗證。

r351的全套`r351-full.trx`雖 **8,731/8,731通過**，原freeze-verification為 **2020檔／Drift2**（HANDOFF／QUEUE），不能背書本輪或改稱0drift。baseline更新已拒絕，保持 **8,602**。後續安排350–354真實聯合全套；舊TRX／freeze原檔保存不覆寫。

## 已套用範圍及尚缺證據

| 組別 | 實作 | 限制 |
|---|---|---|
| 01 DiskIoMatrix，2份 | 正式managed engine使用r351 OwnedTemporaryFile，單一CreateNew／ReadWrite／None／NoBuffering／WriteThrough／DeleteOnClose handle；取消在取得後立即檢查，準備及每點所有worker await完畢後才退出scope。服務移除按plan path刪除；回傳actual owned path，tail write不超出有效長度，短讀拒收。 | 未驗實際native I/O、取消或DeleteOnClose；固定4096幾何未核。SetLength及ReadWrite／WriteThrough準備與read路徑可能改變API／配置成本，不宣稱歷史效能等同。free-space／reparse／volume未pin，外部constructor相容僅核repo source用法。 |
| 06 StorageWriteIntegrity，3份 | engine使用同一owned handle進入正式WithOwnedHandleAsync，涵蓋所有pattern、write、flush、readback及最後結果；所有async I/O直接await，成功後再檢查late cancellation。服務不再推定plan path所有權或刪除；例外先於後續取消檢查保留。 | 同步FlushToDisk不可硬取消，buffered readback不是實體媒體持久性或斷電證明。owner fake測試不驗RandomAccess／分享／刪除native語意；自訂engine負責自身資源。未修FlushDurability，另在r353採能力拒絕。 |
| 07 Gauntlet，2份 | WindowsMultiDomainEngine的每個CPU／memory／storage body進RunWorker例外邊界；保留單／多fault與來源，只有已取消caller同token OCE可視為合作停止。partial Thread.Start／progress故障或取消均signal stop並Join每個真實已啟動thread，之後才處置signal或返回。 | 真同步I/O卡住可以令Join無限等待；停止請求不是硬取消。注入gate sentinel不是真handle／buffer instrumentation，沒有真disk或平台drain證據。catastrophic process fault、UI多故障分類及throughput方法不擴張。 |

DiskIoMatrix的Task.WhenAll等待所有已建立worker settle，不能在一個fault後先釋放其他worker仍在用的buffer／handle。最後測試finally先settle假worker、await outer task，再刪fixture，避免測試清理自己提早關閉。本輪小buffered lease fixture包含真實檔案I/O；不能泛稱沒有磁碟I/O，也不是實際NoBuffering／benchmark驗證。

StorageWriteIntegrity helper回歸含取消後late success拒收及取消後late IOException原例外身份保留，結果限本輪42項定向範圍。既有fake engine現在自管dictionary fixture，不把fake路徑清理當服務正式所有權。

Gauntlet storage worker自己持r351-owned file，真正同步I/O結束及using unwind後thread才退出；coordinator不在外部提前close／delete。fault優先於同時caller cancellation，多fault保留AggregateException，單一validation仍走Unstable。drain修正依實際Join結構與注入案例設計審查，不從候選數推測pass數。

## 來源及待驗帳

- `obj/audit/r352/applied.json`、`obj/r352/staged/capacity01/manifest.json`、`obj/r352/staged/capacity06/mapping.json`及`obj/audit/r352/staged/capacity07/manifest.json.txt`。
- 獨立審查：`obj/audit/r352/review-capacity01.txt`、`obj/r352/reviews/capacity03/REVIEW.txt`、`obj/audit/r352/review-capacity04-worker-lifecycle.txt`。這些為靜態範圍；沒有blocking finding不等於執行通過。
- 三組均依賴r351 `Services/OwnedTemporaryFile.cs` hash `6fc59d08f2657a6261e4344158b3a11ef65cf0b39f638e8b3bd6509d2c0b1621`；Gauntlet另以r351後service為前置。
- 定向42/42已驗；文件／來源規模／生成器／HTML／CLI及350–354聯合full仍待相應結果。ProjectTests baseline保持8,602，不預估新全套總數。
- r351 Drift2原因報告：`obj/audit/r351/capacity10-freeze-drift-analysis-2026-11-17.md`，原verification及full保留。
- 本輪未提交、上傳或發版，不新增候選完成數或硬體支援宣稱。後續r353已套用17 targets、定向84/84通過；r354已套用6 targets、定向23/23通過，分別另記，不沿用本輪42項結果背書。
