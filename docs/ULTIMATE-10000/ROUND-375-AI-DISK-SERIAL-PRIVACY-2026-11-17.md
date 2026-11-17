# Round375：AI磁碟工具序號完整欄位遮蔽（2026-11-17）

**已套3個來源／測試targets，fresh隔離建置定向104/104；新聯合full／freeze待驗。**

`AiToolboxBuilder.get_disk_health`原輸出`・序號 {p.SerialNumber}`，但`AiRedactor`的一般序號規則要求冒號、等號或「是／為」。實際`AiService.RunAsync`雖以`AiAgentRedactToolResults && AiRedactIdentity`送入遮蔽器，這種文字仍不命中，可能進入第二輪role=`tool`的實際HTTP內容。只補冒號仍無法完整處理短序號、空白、斜線或其他字元，因此本輪為這個producer建立明確欄位格式。

`ViewModels/AiToolboxBuilder.cs`保留實際工具註冊與延後取值，抽出接收四個provider的internal `AddStorage`；MainViewModel wrapper只提供delegates。實體磁碟序號改為`・序號：`後接`JsonSerializer.Serialize(p.SerialNumber)`，讓內含引號、換行、分隔符的值仍為單一JSON token。`Services/AiRedactor.cs`將這種欄位的完整token規則放第一，早於UUID／MAC／IP／一般序號，採Compiled＋NonBacktracking，避免同一欄位重複遮蔽計數；Kinds依類別去重。關閉任一privacy開關時保留JSON字串表示，原始模型值不變。其他容量、溫度、SMART及磁碟區文字保留。

新增`Tests/AiDiskSerialOutboundTests.cs`使用真生產AddStorage註冊、合成StorageRow／PhysicalDiskInfo／VolumeInfo、AiToolbox.Invoke、AiService.SendAsync與HttpMessageHandler攔實際序列化request。端點回應先要求`get_disk_health`再回最終答案，驗第二輪tool角色、名稱／call ID、完整送出文字與可見查詢列一致。8個HTTP案例為一般序號四種開關組合，以及雙開的短值、空白／斜線、引號／反斜線／換行／分隔符、Unicode；另1個Fact驗序號內UUID／MAC先被完整欄位規則處理，以及machine／prose序號共用單一Kinds。沒有建構MainViewModel，因此不啟動其VolumeService探測或StorageSmartService建構時WMI列舉；設定用loadFromDisk:false，chat為唯一暫存目錄。這是服務HTTP序列化整合，不是TCP、外部模型或GUI驗收。

## 真實套用及驗證

三份正式來源與`obj/audit/r375/staged/disk-serial-json/manifest.json`候選SHA-256逐一相符；`applied.json`保留套用前後hash。staged manifest的STAGED-UNCOMPILED-UNRUN與applied的TARGET-PENDING是各自建立時狀態，原檔不回寫；後續實跑狀態由TRX、command-exit及本輪核驗receipt補充。

`r375-old-redactor.trx`使用**新JSON producer／測試seam＋舊AiRedactor**，實際3通過／6失敗／0略過，共9，runner223ms，TestExit1。三個任一開關關閉對照綠；五個雙開HTTP案例及mixed Fact紅。這是舊redactor相容性負控制，不是原始builder逐位元相同的舊碼執行。finally restore receipt確認候選redactor SHA相符，保留原失敗與還原帳。

首次`r375-target.trx`實際98通過／6失敗／0略過，共104，runner931ms，TestExit1；同六項失敗不能改寫成通過。負控制後Copy-Item還原保留候選redactor的10:02:06Z mtime，早於舊DLL10:10:50Z，原輸出目錄的incremental建置重用了舊生產DLL。正式source hash已是候選，但舊DLL未含新regex特徵字串。root未改斷言，改用全新`BaseOutputPath=obj/_verify_r375_green2/`重建；新DLL10:18:04Z含新regex，hash與舊DLL不同，兩輪Tests DLL hash相同。普通build箭頭本身不代表有重新編譯，此處另以來源hash、binary內容與實際結果交叉核對。

`r375-target-2.trx`實際**104/104通過、0失敗／略過，runner931ms，TestExit0**；TRX時間為2026-11-17T18:18:28.3204323+08:00–18:18:32.8792871+08:00。新class9項確實執行，其餘AiAttachmentOutbound5、AiHardening40、AiNetworkPath16、AiToolbox34；合計104，不分帳成每類各104。command-exit、TRX及正式source／candidate hashes由receipt重新讀取對帳；原98/104、3/9與各log保留。唯讀分析見`obj/audit/r375/read-only-review-high/`；本輪正式核驗receipt由root核對後套用，候選保存在`docs-staged-high/target-2-verified-candidate.json`。

## 限制與待驗

規則只保護精確`・序號：`後的JSON string／null欄位；其他工具若輸出不同標籤、裸字串、自由文字、圖片內文字，仍受既有規則與限制。empty、null、佔位值也會計為一次欄位替換，不代表已確認識別資訊；它們沒有新增HTTP案例。regex不是一般JSON驗證器：畸形`・序號：nullish`會只替換null前綴而留下ish，未閉合引號不命中；正常JsonSerializer producer不會產生這些形狀。不得宣稱所有工具、所有任意序號或所有AI內容均可遮蔽。

新來源尚無372–375聯合full／freeze；baseline保持上一合格359–371實跑**9,257**，不由定向數或新增方法推算。沒有native磁碟讀取、外部網路、實際裝置序號或硬體精準度驗證；沒有commit、push或發版。本候選只記錄已取得的來源與測試證據，正式文件套用及文件守門仍由root完成。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
