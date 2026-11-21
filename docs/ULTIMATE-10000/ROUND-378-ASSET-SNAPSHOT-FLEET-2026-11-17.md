# Round378：資產型號經真快照保存到 Fleet（2026-11-17）

**canonical鍵及availability修正已套，第三次 rebuilt 聯合294/294、本class26/26；新full／freeze與正式文件守門待驗。** 本文件為obj中已對帳、由root核驗後套用的候選。

AssetFactsService 原產 `asset.field.系統製造商`／`asset.field.系統型號`，違反現有 snapshot ASCII key validator，Create 把兩筆換成 `snapshot.rejected.*`、清值並標 ReadError；Save／Load 可成功，真保存的型號卻永遠到不了 Fleet。既有 Fleet fixture 手寫 JSON 跳過這條 producer／serializer 鏈。本輪先只把兩個成員固定為 `asset.field.system_manufacturer`／`asset.field.system_model`，中文仍保留在 Name，來源、Reported trust、空單位、敏感度、時間與六態沿用。Fleet 接受 canonical 及各一個舊中文 alias；任何一個語意欄位同時出現兩鍵即 AmbiguousModelField，不依順序或可用性挑值。一般 Unicode validator 未放寬，其餘 asset 中文鍵仍在本輪範圍外；舊拒收值已清空，需重新採集。

`obj/audit/next-enterprise-high/manifest.json` 綁定初版五份候選；靜態獨立 review／hash observation 在 `obj/audit/r378/independent-review-high/`。初次靜態複核未發現阻擋缺陷，但遺漏 Availability property-level ignore 與 raw JSON reader 的交界，已另存 `RUNTIME-FOLLOW-UP.md`，不改寫原報告。schema 及 dynamic catalog 說明已隨初版更新；派生百科仍須由 generator 刷新。

原碼負控制與 R379 共用 `obj/audit/r379/r378-379-original.trx`：聯合 **7 通過／9 失敗／0 略過，共16，TestExit1**；本 class 獨立分帳 **4 通過／8 失敗，共12**，另 R379 為3／1。八紅包括真 Save/Load 的拒收、CLI不可比、四種 unavailable 被變成 ReadError及兩組 alias歧義未拒；ReadError自相等等對照通過不代表舊路徑正確。`original-negative-verified-root.json` 保留各 class counters、TRX與command-exit hashes。

第一次新隔離輸出 rebuilt `r376-379-target.trx` **278 通過／2 失敗／0 略過，共280，runner3秒，TestExit1**；本 class **10 通過／2 失敗，共12**。canonical保存及Load斷言已通過，Fleet projection兩份 ModelKey 仍 null，CLI仍退出2。`HardwareSnapshotFact.Availability` 對預設 enum零值 Present 使用 WhenWritingDefault，磁碟省略 availability；typed Load 用 initializer補 Present，Fleet ReadMetadata卻要求raw JSON明示 availability，導致另一個真實邊界失配。root 的後續正式修改、SHA與 rebuilt結果尚待 receipt；保留本次兩紅，不能由 typed Load成功推定 Fleet成功。

後續正式修正在 `obj/audit/r378/availability-fix-root/applied.json` 綁定四target：Fleet ReadMetadata只在欄位省略時補Present；明示availability必須為六態的正式字串名稱，null、空字串、未知名稱、數字及數字字串、bool、array／object均不得冒充Present。這也收緊了原先Enum.TryParse容許已定義數字的投影語法；JSON schema原本就是字串列舉。unit仍須明示字串、source非空、trust有效且不為Unknown／Speculative。snapshot schema移除fact.required中的availability並加default Present及缺欄／明示非法區別；serializer與integrity位元組計算不變，schema不升版。獨立availability契約審查見 `availability-contract-review-high/`。

新回歸擴充至26項；真Save/Load案例另驗model／sensor磁碟確實省略availability，九列非法token同時驗model拒用與sensor metadata不建立，五列顯式unavailable sensor即使手寫finite數字亦不比較；原五態model、placeholder、Unicode與alias控制均保留。第二次新隔離輸出 rebuilt `r376-379-target-2.trx` 實際 **293通過／1失敗／0略過，共294，runner3秒，TestExit1**，本class **26/26**，FleetBaseline35/35、FleetCli3/3。唯一SnapshotSchemaCompat新增required欄位序列化斷言，因沿用假integrity「h」的fixture被正式validator拒收；root改用Create取得合法快照，不放寬正式validator，第三次結果待驗。這一紅與第一次兩紅全部保留。

第三次新隔離輸出 rebuilt `r376-379-target-3.trx` 實際 **294/294通過、0失敗／略過，TestExit0，runner3秒**；本class26/26、FleetBaseline35、FleetCli3、AssetJudge20、HardwareSnapshot14、SnapshotSchemaCompat4、FactKeyCatalog13均全綠。TRX時間2026-11-17T20:53:50.8796622+08:00–20:53:58.5299280+08:00，SHA-256 `5774d27736db5f97a1dcacf6830cc7d479a53916021510ccfcb74fc6f4554621`。fixture correction receipt的正式SnapshotSchemaCompat SHA為 `9864902ad3ef601ba62e06240919490f6832821751334562433dd3172c842d78`；沒有更改正式validator。`target3-observation.json`綁定最終來源及原TRX／exit，初版manifest與278/280、293/294失敗均保留。

回歸走 AssetFactsService.Collect→Create／Save／Load→Fleet reader／baseline及 CLI，使用注入資產與合成serial／sensor；部分JSON故障fixture經改寫不重算hash，只供既有Fleet投影reader，不冒稱通過完整Load。沒有外部網路或硬體探測；hash匿名ID不證明獨立實體機器。Fleet原JSON projection的完整性、重複property及遮罩語意等既有hardening不在本輪修正內。新full／freeze及文件守門待驗，baseline仍 **9,257**；沒有commit、push或發版。

## R382聯合全套後驗補記

2026-11-17：本輪來源已納入 `obj/audit/r382/joint-final02/` 的合格聯合完整回歸 **9,420/9,420、0失敗／略過**；2,154份來源／DLL零漂移。baseline已依真full更新9,420，後驗文件85/85與9項離線CLI內容檢查通過。前文「新full待驗／9257」保留為該階段歷史；原負控、失敗與修正收據不覆寫。仍缺實機／producer的限制依本文件原定義保留；R364、R383–387排除本次full。收口補記之後的文件守門另存結果，尚未預填。
