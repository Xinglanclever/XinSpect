# Round360：Redfish實際bytes、Load准入及partial狀態（2026-11-17）

## 359–371聯合驗證收口（2026-11-17，最新狀態）

**聯合full已實跑9,257/9,257通過、0失敗／略過，runner8分7秒；2,079來源／文件＋3DLL共2,082份SHA-256零漂移。** 範圍359–371排除未套用的R364 AuditLog來源。`obj/audit/r371/r371-full.trx`、`full-command-exit.json`（TestExit0／FreezeExit0）、`freeze-verification.json`（Checked2082／Drift0）及`actual-full-result-root.json`保留原始counters、時間及hash；原freeze不重驗覆寫。TRX 2026-11-17T17:34:06.1163433+08:00–17:42:16.8309114+08:00。

baseline依此次合格實跑由9,029更新9,257（`baseline-update.json`）；三語README、百科來源／HTML及守門基線已同步。兩官方generator checks均0，更新後`r371-final-docs.trx`實際78/78通過、0失敗／略過（runner14秒）。CLI建置0錯誤／13個既有CS0162警告（1分15.70秒）；六個離線smoke退出0／1／2／2／0／0且內容斷言通過，三個Redfish電力scope補充退出0／0／0，20／200兩順序保留各筆而scalar null、單一320保scalar320，逐筆遮蔽及key集合斷言通過。它們是離線合成案例，不是BMC實機／硬體精準度驗收。

postfull第一次在電力scope前置檢查中停止：R358複製模板誤留Checked2056，與R371實際2082不符；已有78項文件／CLI建置／六項smoke結果保留。root另版只修此前置常數，再跑原三項scope，沒有重跑full或改freeze。原失敗與修正版hash見`postfull-original-failure-root.json`，真實收口見`postfull-root-result.json`。本段回填後的文件守門另存新TRX，不預填成功。

下面的「新full待驗／baseline9,029」屬先前階段紀錄，以本段為最新驗證狀態；歷史351 Drift2、357三紅、359–363定向失敗、361原碼負控制與371編譯／prefull失敗均保留。來源統計704服務／1,701 C#／711頂層測試檔／7,644方法不是10000完成率。R364仍blocked；下一批R372重啟路徑、R373查詢enum及R374 RAPL回繞候選已備，尚未套用／編譯／執行，AI序號與Ceiling worker後續推進。本機無commit、push或發版。

**來源已套用，聯合定向通過；新full及文件收口待驗。**

本輪套用位置：`Services/RedfishBundleService.cs`、`Services/RedfishFactsService.cs`、`Tests/RedfishBoundedReadTests.cs`。

已套3targets：RedfishBundleService、RedfishFactsService及新RedfishBoundedReadTests。root的apply-started記錄manifest SHA`02b97f3ab559a0a083472a9a22511131c1e56578a5b3c05a49cc16f90c96713c`，與revision3封存及applied各target相符；沒有另造不存在的apply-receipt。獨審原F1（超限警告被淹沒）與F2（Systems後段停止誤清既有完整電力scope）均在revision3修正；獨審仍只是靜態，不等於當時已有runtime成功。

生產路徑把FileInfo.Length＋重開ReadAllText改為同一read-only handle的分段returned-byte計帳，128次Load准入含root／rejected／missing／malformed／repeated，129次映射前拒；inline不算檔案Load，故不是所有輸出列上限。每檔接受8MiB、整次64MiB；為判超限各容額外1byte，實讀上限8MiB+1／64MiB+1，不是嚴格64MiB返回bytes。壞JSON、重讀與後續I/O例外以前已returned bytes均扣帳，不回退或重試。

FileStream BufferSize1避免managed read-ahead；不依Length／Position／Seek，short read繼續。同stream處置後才由bounded snapshot以UTF-8及BOM detection解碼，保UTF8／UTF16／UTF32相容，BOM計bytes。ReadIncomplete使部分summary Unknown，明確失敗空import為ReadError或明確UnauthorizedAccessException的InsufficientPrivilege；未知來源時間不覆蓋確定讀失敗，成功項仍保各自Reported／來源時間。warnings有空清單出口且budget code在64-warning及first-five投影可見，仍Sensitive。

最終邊界依固定Managers→Chassis→Systems遍歷：before／during Chassis的attempt／aggregate全域停止保守清PowerControlsComplete；只有最後Systems或Systems member才停止時，保留此前已完整的電力scope，從不把既有false提升。summary仍Unknown，已證完整且唯一的20W scalar可保Present；局部錯誤沿R358角色規則。超限固定code置warnings前綴，64條上限及前5fact投影均可見，file拒絕後仍可讀後項。

64MiB是新增內容接受政策，不是RAM ceiling或Redfish規格上限，也非所有舊包相容保證；同步open/read可卡住，無取消／hard deadline。reparse-check/open race、整包原子匯入、BMC freshness／authenticity均未證。planned mutants不稱kill；public CLI有8MiB+1 fixture，不冒稱有預設64MiB全包CLI fixture。

## 實際驗證與修正

359–363聯合定向第一次`obj/audit/r363/r359-363-target.trx`為**290通過／1失敗／0略過，共291**，runner4秒；TRX2026-11-17 15:10:46.2487528–15:10:54.6283636（UTC+08:00，8.380秒），TestExit1。唯一失敗為Redfish新測試把`JsonException`具體診斷名稱寫成基類，實際parser回`JsonReaderException`。root只改該測試：獨立`JsonDocument.Parse("{")`以`ThrowsAny<JsonException>`取得實際具體型別名，再精確比對診斷；availability、privacy、時間及dispose斷言保留，沒有降低為任意例外字樣。`runtime-test-correction.json`保留原`cd28a566…`與新`a7466185…`hash，不改生產source或方法數。重跑`r359-363-target-2.trx`為**291/291通過、0失敗／略過**，runner4秒；TRX2026-11-17 15:16:39.1219474–15:16:47.3778164（UTC+08:00，8.256秒），TestExit0。這是五輪聯合filter，不能分帳成每輪各291項；原失敗TRX及log完整保留。

359–363來源實核為**702服務／1,693 C#／705頂層測試檔／7,152 Fact（含7個Property）／429 Theory／7,581方法**；key1,212／domain161／family71／coverage477。這是純來源AST、穩定NuGet attribute繼承與來源hash前後核對，沒有以測試DLL反射冒稱新full；parseErrors、using aliases、all-source/test-source hash漂移均0，新增override/base僅測試helper、不新增繼承測試類。`public-sync-high-v2`明確採R360測試修正superseding hash；舊v1統計／manifest完整保留，不覆寫。四公開候選保留百科Part11、runtime9,029與歷史8,834，官方生成器記憶體內check0、ProcessorGeneric附錄H行數77；正式HTML已重生，兩官方generator checks均0；文件守門實際結果見本輪closure帳。

359–363尚無新聯合full／新freeze結果；ProjectTests保持最近合格355–358full的**9,029**，不從方法或定向數推算。R358的9,029/9,029及2,056檔Drift0是已完成的歷史驗證，不能背書接續新來源。R361原碼負控制已實紅，還原候選後獨立green5/5已驗；其餘未執行項不稱kill。此次文件／公開統計套用後守門已實跑78/78；新full／新freeze仍待root串行執行。

## 證據與範圍

來源設計／限制：`obj/audit/r360/staged/high-redfish-budget/REVIEW-NOTES.md`、`obj/audit/r360/staged/high-redfish-budget/candidate-manifest.json`、`obj/audit/r360/reviews/high-budget/FINAL-REVIEW.md`。實際套用帳為各輪`applied.json`，361–363另有共同`apply-receipt.json`；本輪新測試修正另見R360 correction帳。方法／key／family／coverage不是10000完成率；沒有GUI、native repair、BMC或硬體精準度／硬取消驗收宣稱，未提交、上傳或發版。
