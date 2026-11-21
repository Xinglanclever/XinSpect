# Round361：AI圖片附件送出文字的遮蔽與budget（2026-11-17）

## 359–371聯合驗證收口（2026-11-17，最新狀態）

**聯合full已實跑9,257/9,257通過、0失敗／略過，runner8分7秒；2,079來源／文件＋3DLL共2,082份SHA-256零漂移。** 範圍359–371排除未套用的R364 AuditLog來源。`obj/audit/r371/r371-full.trx`、`full-command-exit.json`（TestExit0／FreezeExit0）、`freeze-verification.json`（Checked2082／Drift0）及`actual-full-result-root.json`保留原始counters、時間及hash；原freeze不重驗覆寫。TRX 2026-11-17T17:34:06.1163433+08:00–17:42:16.8309114+08:00。

baseline依此次合格實跑由9,029更新9,257（`baseline-update.json`）；三語README、百科來源／HTML及守門基線已同步。兩官方generator checks均0，更新後`r371-final-docs.trx`實際78/78通過、0失敗／略過（runner14秒）。CLI建置0錯誤／13個既有CS0162警告（1分15.70秒）；六個離線smoke退出0／1／2／2／0／0且內容斷言通過，三個Redfish電力scope補充退出0／0／0，20／200兩順序保留各筆而scalar null、單一320保scalar320，逐筆遮蔽及key集合斷言通過。它們是離線合成案例，不是BMC實機／硬體精準度驗收。

postfull第一次在電力scope前置檢查中停止：R358複製模板誤留Checked2056，與R371實際2082不符；已有78項文件／CLI建置／六項smoke結果保留。root另版只修此前置常數，再跑原三項scope，沒有重跑full或改freeze。原失敗與修正版hash見`postfull-original-failure-root.json`，真實收口見`postfull-root-result.json`。本段回填後的文件守門另存新TRX，不預填成功。

下面的「新full待驗／baseline9,029」屬先前階段紀錄，以本段為最新驗證狀態；歷史351 Drift2、357三紅、359–363定向失敗、361原碼負控制與371編譯／prefull失敗均保留。來源統計704服務／1,701 C#／711頂層測試檔／7,644方法不是10000完成率。R364仍blocked；下一批R372重啟路徑、R373查詢enum及R374 RAPL回繞候選已備，尚未套用／編譯／執行，AI序號與Ceiling worker後續推進。本機無commit、push或發版。

**來源已套用，聯合定向通過；新full及文件收口待驗。**

本輪套用位置：`Services/AiService.cs`、`Tests/AiAttachmentOutboundTests.cs`。

已套2targets：AiService及新AiAttachmentOutboundTests。生產只改AiService.BuildMessages：image content的第一段原始text改用prepared.Texts[i]，接回原image parts；不改原contentParts list，RetryLastAsync重新套目前privacy／budget設定。UTF-8 BOM保留。

新增CaptureHandler攔實際序列化HTTP request、無外網；檢查plain string或text＋image_url形狀、image data URL原bytes，涵蓋有／無圖的redaction與text-file budget、同次preview／outbound及privacy由off轉on後retry。SettingsService(loadFromDisk:false)避免persist設定；不需OCR／圖像decoder或真clipboard硬體。

五個fakeHTTP案例已包含在291/291聯合定向；獨立舊source負控制實際2綠／3紅，還原候選後獨立green已實跑5/5（下列）。獨立error-response-body timeout／disposal、serial-label不符與pending-composer preview不在此修。

## 實際驗證與修正

359–363聯合定向第一次`obj/audit/r363/r359-363-target.trx`為**290通過／1失敗／0略過，共291**，runner4秒；TRX2026-11-17 15:10:46.2487528–15:10:54.6283636（UTC+08:00，8.380秒），TestExit1。唯一失敗為Redfish新測試把`JsonException`具體診斷名稱寫成基類，實際parser回`JsonReaderException`。root只改該測試：獨立`JsonDocument.Parse("{")`以`ThrowsAny<JsonException>`取得實際具體型別名，再精確比對診斷；availability、privacy、時間及dispose斷言保留，沒有降低為任意例外字樣。`runtime-test-correction.json`保留原`cd28a566…`與新`a7466185…`hash，不改生產source或方法數。重跑`r359-363-target-2.trx`為**291/291通過、0失敗／略過**，runner4秒；TRX2026-11-17 15:16:39.1219474–15:16:47.3778164（UTC+08:00，8.256秒），TestExit0。這是五輪聯合filter，不能分帳成每輪各291項；原失敗TRX及log完整保留。

**R361原碼負控制與還原候選正控制均已實跑**：在非freeze範圍，root短暫還原已核舊AiService（SHA`eea6552a…`），同五個fakeHTTP測試`r361-original-red.trx`實際**2通過／3失敗／0略過，共5**，TestExit1；三紅精確對應圖片文字遮蔽、圖片文字budget與設定變更後retry，兩項無圖對照仍通過。這是缺陷反例而非產品綠燈；控制腳本按預期紅並完成還原而ScriptExit0。finally還原receipt確認候選SHA`b1f35f4…`、UnexpectedSource=false、RestoreError=null。原碼TRX 2026-11-17T15:23:13.6120640+08:00–2026-11-17T15:23:17.3399458+08:00（UTC+08:00，3.728秒）。CaptureHandler攔實際序列化HTTP而不連外；image data URL及原contentParts斷言保留。候選還原後獨立重建`obj/audit/r361/r361-candidate-green.trx`實際**5/5通過、0失敗／略過**，runner315ms，TestExit0；TRX 2026-11-17T16:09:49.2768408+08:00–2026-11-17T16:09:53.3716453+08:00。root核實TRX counters、command exit、正式source及test hash；`r361-candidate-green.verified-root.json`保留實際證據hash。此前聯合291/291早於source swap，另列不混用。其餘未執行的Redfish／USN／BootRepair mutants不稱kill。

359–363來源實核為**702服務／1,693 C#／705頂層測試檔／7,152 Fact（含7個Property）／429 Theory／7,581方法**；key1,212／domain161／family71／coverage477。這是純來源AST、穩定NuGet attribute繼承與來源hash前後核對，沒有以測試DLL反射冒稱新full；parseErrors、using aliases、all-source/test-source hash漂移均0，新增override/base僅測試helper、不新增繼承測試類。`public-sync-high-v2`明確採R360測試修正superseding hash；舊v1統計／manifest完整保留，不覆寫。四公開候選保留百科Part11、runtime9,029與歷史8,834，官方生成器記憶體內check0、ProcessorGeneric附錄H行數77；正式HTML已重生，兩官方generator checks均0；文件守門實際結果見本輪closure帳。

359–363尚無新聯合full／新freeze結果；ProjectTests保持最近合格355–358full的**9,029**，不從方法或定向數推算。R358的9,029/9,029及2,056檔Drift0是已完成的歷史驗證，不能背書接續新來源。R361原碼負控制已實紅，還原候選後獨立green5/5已驗；其餘未執行項不稱kill。此次文件／公開統計套用後守門已實跑78/78；新full／新freeze仍待root串行執行。

## 證據與範圍

來源設計／限制：`obj/audit/r361/staged/ai-attachment-only/README.md`、`obj/audit/r361/staged/ai-attachment-only/manifest.json`。實際套用帳為各輪`applied.json`，361–363另有共同`apply-receipt.json`；本輪新測試修正另見R360 correction帳。方法／key／family／coverage不是10000完成率；沒有GUI、native repair、BMC或硬體精準度／硬取消驗收宣稱，未提交、上傳或發版。
