# Round370：被動功耗讀取不再隱式施壓（2026-11-17）

## 359–371聯合驗證收口（2026-11-17，最新狀態）

**聯合full已實跑9,257/9,257通過、0失敗／略過，runner8分7秒；2,079來源／文件＋3DLL共2,082份SHA-256零漂移。** 範圍359–371排除未套用的R364 AuditLog來源。`obj/audit/r371/r371-full.trx`、`full-command-exit.json`（TestExit0／FreezeExit0）、`freeze-verification.json`（Checked2082／Drift0）及`actual-full-result-root.json`保留原始counters、時間及hash；原freeze不重驗覆寫。TRX 2026-11-17T17:34:06.1163433+08:00–17:42:16.8309114+08:00。

baseline依此次合格實跑由9,029更新9,257（`baseline-update.json`）；三語README、百科來源／HTML及守門基線已同步。兩官方generator checks均0，更新後`r371-final-docs.trx`實際78/78通過、0失敗／略過（runner14秒）。CLI建置0錯誤／13個既有CS0162警告（1分15.70秒）；六個離線smoke退出0／1／2／2／0／0且內容斷言通過，三個Redfish電力scope補充退出0／0／0，20／200兩順序保留各筆而scalar null、單一320保scalar320，逐筆遮蔽及key集合斷言通過。它們是離線合成案例，不是BMC實機／硬體精準度驗收。

postfull第一次在電力scope前置檢查中停止：R358複製模板誤留Checked2056，與R371實際2082不符；已有78項文件／CLI建置／六項smoke結果保留。root另版只修此前置常數，再跑原三項scope，沒有重跑full或改freeze。原失敗與修正版hash見`postfull-original-failure-root.json`，真實收口見`postfull-root-result.json`。本段回填後的文件守門另存新TRX，不預填成功。

下面的「新full待驗／baseline9,029」屬先前階段紀錄，以本段為最新驗證狀態；歷史351 Drift2、357三紅、359–363定向失敗、361原碼負控制與371編譯／prefull失敗均保留。來源統計704服務／1,701 C#／711頂層測試檔／7,644方法不是10000完成率。R364仍blocked；下一批R372重啟路徑、R373查詢enum及R374 RAPL回繞候選已備，尚未套用／編譯／執行，AI序號與Ceiling worker後續推進。本機無commit、push或發版。

**來源已套用，聯合定向通過；新full／freeze待驗。**

五targets：RaplEnergyGate、新Probe、SensorService、CLI與safe tests。移除GUI功耗getter與CLI sys採樣中的隱式多核自旋；被動讀取只取已存在判決，未驗證維持null／Unknown。共用ValidatedPower過濾fallback與文字；沒有新增尚未接線的驗證按鈕／命令。

保留的明確API需第一級gate與取消，在讀取／worker／100ms觀察步及有限工作slice重核；溫度缺失、非有限、<=0或>=85°C拒跑／中止。85°C只是應用中止線，非CPU規格。DeepBenchWorkerLifetime持CTS到所有成功啟動worker真正Join，partial start／來源例外／late worker fault均保可見，結果在drain後發布。同步卡住reader不能硬取消，首個caller擁有共享run取消，失敗快取不隱式重試。

本輪fake threads只等stop，不跑負載slice；body-fault例在首個policy查詢即拋。GUI原XAML仍只顯`--`，原因屬性未綁tooltip；沒有宣稱新互動UI完成。P2 32-bit回繞算成64-bit模數與HistoryStore null→0仍未修。獨審`obj/audit/r370/reviews/high/FINAL-REVIEW.md`，sealed revision2及helper依賴見candidate manifest；root補using另帳，不改原seal。

## 實際整合與驗證

根代理按preimage及候選SHA-256依序套用365–371，34映射／32獨立targets；R367先於R371，CMOS與Rowhammer對LIMITATIONS的兩處不重疊修改由root保留雙方後另封merge，不覆原候選。R364有阻擋，未套來源。新C#只以`git add -N`登記，沒有提交、上傳或發版。

第一次`obj/audit/r371/r365-371-target.log`有10個編譯診斷，均由新`RaplLoadSafetyTests`漏`using System.IO`引起；沒有TRX，不能列成案例失敗。root只補using，斷言及方法數不變；`obj/audit/r370/compile-test-correction-root.json`保留前後hash。第二次`obj/audit/r371/r365-371-target-2.trx`實際**240/240通過、0失敗／略過**，runner3秒，TestExit0；TRX 2026-11-17T17:24:43.2477324+08:00–2026-11-17T17:24:50.7000867+08:00。這是七輪聯合精確filter，不能重複分算成每輪各240；主要為pure/fake邏輯，含實際WPF XAML狀態測試，沒有點擊壓力按鈕或啟動硬體worker。

AST實核704服務／1,701 C#／711頂層測試檔／7,200 Fact（含7 Property）／444 Theory／7,644方法，parse及來源hash漂移均0，見`obj/audit/r371/public-root-v2/stats-review.json`；方法、key及檔數不是功能完成率或full案例總數。最近合格full仍為355–358的9,029/9,029、2,056檔Drift0；新359–371聯合full／新freeze與公開文件後守門待驗，baseline保持9,029。

`obj/audit/r371/root-integration/plan.json`、各輪`applied.json`、root-integration分輪收據及preimage建立需求→來源→套用→測試鏈。原351 Drift2、357full三紅、359–363定向290/1及R361原碼2/3全數保留；本輪不能回填它們全綠。


## 新full前最後守門與凍結準備

`obj/audit/r371/r371-prefull.trx`原136通過／1失敗，共137：倉庫HTML已重生，但root以no-build使用先前DLL，內嵌百科仍舊。守門正確抓出bytes差異；沒有修改斷言。改以正常build重嵌後`r371-prefull-2.trx`實際**137/137通過、0失敗／略過**，TestExit0；TRX 2026-11-17T17:32:35.8521442+08:00–2026-11-17T17:33:15.5432081+08:00。兩官方generator checks均0。原失敗完整保留。

接續建立全新R371 source／assembly freeze，範圍359–371但排除未套R364來源；不重驗／覆寫R358歷史freeze。新full結果仍待實跑，baseline9,029保持。
