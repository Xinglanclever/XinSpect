# ULTIMATE-10000 實作佇列（2026-11-17）

## R372–382 聯合全套與後驗收口（2026-11-17，最新狀態）

**本次完整回歸已實跑 9,420/9,420 通過，0失敗／略過，runner 8分1秒；2,151 來源／文件＋3份同輪 DLL 共2,154份 SHA-256 零漂移。** 範圍為已套 R372–382，排除 R364 blocked 來源及仍在 obj 的 R383–387。原證據 `obj/audit/r382/joint-final02/r382-full.trx`、`full-command-exit.json`、`freeze-verification.json`、`actual-full-result.json` 綁定真 counters、時間、source／DLL freeze；此 freeze 不在後驗文件更新後重新驗覆寫。baseline 由最近合格 R371 的9,257依本次真 full更新為9,420，8份公開文件／常數的精確差異與套用收據保留於 baseline-plan、baseline-applied.json；百科 Part11 原 bytes SHA78876283… 未改。

前置第一次140/141的CN前綴摘要161錯字已按真實162修正，失敗與原seal保留；fresh final02前置141/141通過且來源零變更，才建立新freeze並跑full。更新baseline後，fresh後驗文件守門 **85/85**、0失敗／略過，runner13秒，兩官方generator checks0。CLI fresh建置0錯誤、13個既有CS0162警告、1分18.12秒；9個離線smoke退出0／1／2／2／0／0／0／0／0且內容／遮蔽／scope鍵集合斷言全部通過，包括canonical asset鍵與省略Present的Fleet讀入、Redfish私密欄遮蔽、雙控制器兩順序保留各值而scalar null及唯一320W scalar。這是合成離線入口驗證，不是BMC／WMI／XPF真producer或硬體矩陣驗收。

R380的目前控制器時序沒有新增實讀；WH-0001仍partial。XPF CLI生產接線是靜態trace＋注入service證據，沒有將本次普通CLI smoke冒稱XPF實際採集。memory.current／rate仍缺CLI即時採集，R386橋接修補另在obj候選；R383正面DIMM身份、R384按來源WMI、R385 AI來源描述已獨審修訂，尚未套用／編譯／C#實跑。新R387 ECAM bus255 wrap／讀取邊界修補正在obj獨審。R364 AuditLog仍blocked，另研究同object journal格式與恢復契約，未套來源。

本次公開來源706服務／1,715 C#／722頂層測試檔／7,712方法及1,216鍵／162域／72家族均不是10000完成率。下一步先驗本段收口文件，再串行套用R383–385負控／正控並推進386／387，保留歷史失敗與各階段證據。本機無commit、push、上傳或發版。以下各段是當時歷史狀態，本段為目前合格full／postfull狀態；收口文字回填後的獨立文件守門另記原TRX，不預填通過。

## R372–382 接續整合（2026-11-17，最新狀態）

R372–382 修補已套用；R364 AuditLog 仍 blocked、未套用。R376–379 最終定向實跑 294/294 通過；R380–382 首次定向為 294 通過／4 失敗，共 298，已保留原 TRX，定位為漏登既有 memory.current/rate 兩鍵、未覆蓋數及 README 對帳、Present 省略契約測試四處。修正後 `obj/audit/r382/r380-382-target-2.trx` 真實 298/298 通過，0 失敗／略過、runner 4 秒、命令退出 0；原始 counters、分帳與 SHA 見 `target2-verified-root.json`。R380–382 診斷負控制共 57 例、27 失敗，含 same-API 舊語意 mutant／instrumented 舊行為及舊 typed-retention 接線，並非全數 literal 原碼，候選已逐檔 hash 還原；各輪文件記錄具體口徑。

本批新增記憶體逐量來源／信任／六態、離線 CPER XPF processor section 解碼及通電時數協定派發；不宣稱新增控制器目前時序實讀或 WH-0001 全部完成。XPF 的 executable CLI 採集鏈已靜態追到共用 WHEA 服務，尚缺該入口實際 XPF 執行證據；memory.current/rate 目前只在桌面 EvidenceLab 路徑，CLI 接線缺口已列 R386 候選。CPU／DRAM 來源優先序及兩級安全閘門保持既有契約。

新 R372–382 聯合 full／同輪 freeze 待驗，**最近合格全套仍是 R371 的 9,257/9,257**，不可由本批定向或方法數預填。公開來源統計已同步：706 服務檔、1,715 C#、722 頂層測試檔、7,712 測試方法；1,216 catalog 鍵／162 域／72 動態家族，reviewed uncovered 481。兩把既有 memory 鍵因改為可掃描 producer 而顯現，不是新增兩能力；來源／鍵／方法數均不是 ULTIMATE-10000 完成率。百科 Part 11 歷史 bytes 保留。

新全套前置第一次 `obj/audit/r382/joint-final01/r382-prefull.trx` 實跑 140 通過／1 失敗，共141，0略過；source seal 比較零變更，未進入 full。唯一失敗是簡體 README 摘要表仍寫161個前綴，實際162；兩官方generator checks皆0未涵蓋這個摘要 literal，既有 ReadmeStatsTests 正確攔截。root只修該欄為162，不改斷言；原失敗、seal與收據完整保留，另建 fresh final02 前置／full，不重用輸出。

下一批 R383 DIMM 正面身份綁定、R384 分來源 WMI 對帳、R385 AI 記憶體來源描述與 R386 CLI 記憶體橋接僅在 obj 候選／提案，未納本輪正式來源或 full。獨審提出的弱身份誤配、placeholder 假矛盾與六態遺失仍須逐項完成候選修訂和實跑。本機尚無 commit、push 或發版。以下歷史段落依其當時階段解讀，以本段為目前整合狀態。

## 359–371聯合驗證收口（2026-11-17，最新狀態）

**聯合full已實跑9,257/9,257通過、0失敗／略過，runner8分7秒；2,079來源／文件＋3DLL共2,082份SHA-256零漂移。** 範圍359–371排除未套用的R364 AuditLog來源。`obj/audit/r371/r371-full.trx`、`full-command-exit.json`（TestExit0／FreezeExit0）、`freeze-verification.json`（Checked2082／Drift0）及`actual-full-result-root.json`保留原始counters、時間及hash；原freeze不重驗覆寫。TRX 2026-11-17T17:34:06.1163433+08:00–17:42:16.8309114+08:00。

baseline依此次合格實跑由9,029更新9,257（`baseline-update.json`）；三語README、百科來源／HTML及守門基線已同步。兩官方generator checks均0，更新後`r371-final-docs.trx`實際78/78通過、0失敗／略過（runner14秒）。CLI建置0錯誤／13個既有CS0162警告（1分15.70秒）；六個離線smoke退出0／1／2／2／0／0且內容斷言通過，三個Redfish電力scope補充退出0／0／0，20／200兩順序保留各筆而scalar null、單一320保scalar320，逐筆遮蔽及key集合斷言通過。它們是離線合成案例，不是BMC實機／硬體精準度驗收。

postfull第一次在電力scope前置檢查中停止：R358複製模板誤留Checked2056，與R371實際2082不符；已有78項文件／CLI建置／六項smoke結果保留。root另版只修此前置常數，再跑原三項scope，沒有重跑full或改freeze。原失敗與修正版hash見`postfull-original-failure-root.json`，真實收口見`postfull-root-result.json`。本段回填後的文件守門另存新TRX，不預填成功。

下面的「新full待驗／baseline9,029」屬先前階段紀錄，以本段為最新驗證狀態；歷史351 Drift2、357三紅、359–363定向失敗、361原碼負控制與371編譯／prefull失敗均保留。來源統計704服務／1,701 C#／711頂層測試檔／7,644方法不是10000完成率。R364仍blocked；下一批R372重啟路徑、R373查詢enum及R374 RAPL回繞候選已備，尚未套用／編譯／執行，AI序號與Ceiling worker後續推進。本機無commit、push或發版。

## 365–371接續整合（2026-11-17）

已套七組34映射／32獨立targets；R364 AuditLog ACL／失敗恢復阻擋未套。聯合定向首次因R370測試缺System.IO而10個編譯診斷、無TRX；補using後240/240、0失敗／略過，runner3秒。R365舊碼三例0/3反證已保留；R361原碼2/3及還原後5/5已驗。詳各ROUND-365至371、ROUND-364阻擋帳與`obj/audit/r371/docs-root/actual-target-result.json`。

本機只做修補／驗證，無commit、push或發版。新359–371聯合full／freeze待驗，baseline仍9,029；704服務／1,701 C#／711頂層測試檔／7,644方法不是完成率。R358 closure在359–363來源下回填後最終文件守門亦78/78（`obj/audit/r363/r363-closure-docs-final.trx`），不重驗舊freeze。

下一批：先取得本批prefull文件／接線守門及全套零漂移；再處理AI磁碟序號標籤不被redactor辨識、BiosMe裸shutdown路徑、Ceiling partial thread construction、RAPL32-bit wrap及查詢非法enum。尚未修者不能從本批240綠燈推定完成。


## 359–363接續來源、實際定向與待驗全套（2026-11-17）

359–363聯合定向第一次`obj/audit/r363/r359-363-target.trx`為**290通過／1失敗／0略過，共291**，runner4秒；TRX2026-11-17 15:10:46.2487528–15:10:54.6283636（UTC+08:00，8.380秒），TestExit1。唯一失敗為Redfish新測試把`JsonException`具體診斷名稱寫成基類，實際parser回`JsonReaderException`。root只改該測試：獨立`JsonDocument.Parse("{")`以`ThrowsAny<JsonException>`取得實際具體型別名，再精確比對診斷；availability、privacy、時間及dispose斷言保留，沒有降低為任意例外字樣。`runtime-test-correction.json`保留原`cd28a566…`與新`a7466185…`hash，不改生產source或方法數。重跑`r359-363-target-2.trx`為**291/291通過、0失敗／略過**，runner4秒；TRX2026-11-17 15:16:39.1219474–15:16:47.3778164（UTC+08:00，8.256秒），TestExit0。這是五輪聯合filter，不能分帳成每輪各291項；原失敗TRX及log完整保留。

**R361原碼負控制與還原候選正控制均已實跑**：在非freeze範圍，root短暫還原已核舊AiService（SHA`eea6552a…`），同五個fakeHTTP測試`r361-original-red.trx`實際**2通過／3失敗／0略過，共5**，TestExit1；三紅精確對應圖片文字遮蔽、圖片文字budget與設定變更後retry，兩項無圖對照仍通過。這是缺陷反例而非產品綠燈；控制腳本按預期紅並完成還原而ScriptExit0。finally還原receipt確認候選SHA`b1f35f4…`、UnexpectedSource=false、RestoreError=null。原碼TRX 2026-11-17T15:23:13.6120640+08:00–2026-11-17T15:23:17.3399458+08:00（UTC+08:00，3.728秒）。CaptureHandler攔實際序列化HTTP而不連外；image data URL及原contentParts斷言保留。候選還原後獨立重建`obj/audit/r361/r361-candidate-green.trx`實際**5/5通過、0失敗／略過**，runner315ms，TestExit0；TRX 2026-11-17T16:09:49.2768408+08:00–2026-11-17T16:09:53.3716453+08:00。root核實TRX counters、command exit、正式source及test hash；`r361-candidate-green.verified-root.json`保留實際證據hash。此前聯合291/291早於source swap，另列不混用。其餘未執行的Redfish／USN／BootRepair mutants不稱kill。

359–363來源實核為**702服務／1,693 C#／705頂層測試檔／7,152 Fact（含7個Property）／429 Theory／7,581方法**；key1,212／domain161／family71／coverage477。這是純來源AST、穩定NuGet attribute繼承與來源hash前後核對，沒有以測試DLL反射冒稱新full；parseErrors、using aliases、all-source/test-source hash漂移均0，新增override/base僅測試helper、不新增繼承測試類。`public-sync-high-v2`明確採R360測試修正superseding hash；舊v1統計／manifest完整保留，不覆寫。四公開候選保留百科Part11、runtime9,029與歷史8,834，官方生成器記憶體內check0、ProcessorGeneric附錄H行數77；正式HTML已重生，兩官方generator checks均0；文件守門實際結果見本輪closure帳。

359–363尚無新聯合full／新freeze結果；ProjectTests保持最近合格355–358full的**9,029**，不從方法或定向數推算。R358的9,029/9,029及2,056檔Drift0是已完成的歷史驗證，不能背書接續新來源。R361原碼負控制已實紅，還原候選後獨立green5/5已驗；其餘未執行項不稱kill。此次文件／公開統計套用後守門已實跑78/78；新full／新freeze仍待root串行執行。

[ROUND-359-PROCESSOR-POWER-SCOPE](ROUND-359-PROCESSOR-POWER-SCOPE-2026-11-17.md)、[ROUND-360-REDFISH-BOUNDED-IMPORT](ROUND-360-REDFISH-BOUNDED-IMPORT-2026-11-17.md)、[ROUND-361-AI-ATTACHMENT-OUTBOUND](ROUND-361-AI-ATTACHMENT-OUTBOUND-2026-11-17.md)、[ROUND-362-USN-JOURNAL-LAYOUT](ROUND-362-USN-JOURNAL-LAYOUT-2026-11-17.md)、[ROUND-363-BOOT-REPAIR-SYSTEM-TOOLS](ROUND-363-BOOT-REPAIR-SYSTEM-TOOLS-2026-11-17.md)。各輪的生產定義、限制及來源文件分列，不把聯合291重複算入五次。

**R358 closure在359–363接續來源下文件守門已實跑**：第一次`obj/audit/r363/r363-closure-docs.trx`為77通過／1失敗，缺陷是五個已套新C#未登記Git。root只對這五個路徑執行`git add -N`，沒有提交；第二次`r363-closure-docs-2.trx`實際**78/78通過、0失敗／略過**，runner14秒，TestExit0；TRX 2026-11-17T17:16:17.7296902+08:00–2026-11-17T17:16:36.1459345+08:00。兩官方generator check均0，正式HTML已重生。這不是再次Round358 full，R358原full／freeze及原失敗TRX不覆寫；本段結果回填後另跑同組守門並以新receipt存證。359–363新full／新freeze仍待驗，baseline保持9,029。

從 [當前交接](HANDOFF-2026-11-16.md)、[Round 346](ROUND-346-PARALLEL-INTEGRITY-2026-11-17.md)與 [Round 347](ROUND-347-ENTERPRISE-BOUNDARIES-2026-11-17.md)接續。使用者已授權持續推進；此表是下一批具體工作，不是整份目錄的完成統計。對應 ID 開工前仍須核對正典定義、來源及既有消費端，避免重複造鍵。

**350–354歷史聯合驗證：聯合full及凍結已驗，baseline／HTML已同步，full8,834/8,834通過、0失敗／略過（runner9分1秒），2,030來源／文件＋3DLL共2,033份hash零漂移。** baseline 已按實跑由 **8,602更新8,834**（`obj/audit/r354/baseline-update.json`），不由作者案例計畫推算；baseline／HTML同步後重建66/66通過（runner16秒）；兩official generators check均回0（root串行session40321 exit0）、HTML`6aab08a367e8`；CLI建置 **0錯誤／13個既有CS0162警告**（`obj/audit/r354/r354-cli-build.log`，session16488 exit0）；六項離線smoke（`obj/audit/r354/r354-cli-smoke.json`）依序退出 **0／1／2／2／0／0**，help、非法sensor選項、缺目錄不可比、壞包SnapshotReadError、兩份合成可比快照及Redfish序號遮蔽／來源與privacy metadata全部內容斷言均通過，root確認session40321的smoke及兩generator checks串行exit0。r351全套8,731綠但2020檔Drift2及拒更事故保留，不能回填零漂移；r349的8,602及2,009檔零漂移保留為歷史合格基線。r350定向348/348、r351定向475/475及r352／353／354定向42／84／23原證據保留，方法計畫不推算full總數。closure回填後文件守門已以`obj/audit/r354/r354-closure-docs.trx` **66/66通過、0失敗／略過**（runner14秒，exit0）；TRX2026-11-17 07:47:38.5674937–07:47:57.4350997（UTC+08:00，18.868秒），r354正式收口證據已齊，未提交、上傳或發版。

**355–357聯合full歷史失敗（原證據保留）**：`obj/audit/r357/r357-full.trx`實際 **8,953通過／3失敗／0略過，共8,956**，runner **9分38秒**；TRX2026-11-17 12:56:13.1700342–13:05:55.4420501（UTC+08:00，9分42.272秒）。`full-command-exit.json`為TestExit1／FreezeExit0，`freeze-verification.json`為 **Checked2,052／Drift0**；凍結零漂移不能使三個失敗變通過。`final-failure-result.json`保留完整counters／times／失敗訊息；該次baseline維持r354 **8,834**，沒有更新為失敗總數8,956。三項為DecoderHardening精確registry名冊少ProcessorGeneric／PCIe兩解碼器、WiringGuard對同檔aggregator引用未被辨識的PCIe服務、KeyCatalogPolicy的PCIe家族成員定義契約。R358已按applied帳套用精確補名、獨立WheaErrorFactsService檔案及中文成員定義；後續355–358聯合full實際9,029/9,029通過，原三項失敗不覆寫；不得新增豁免、降低斷言或將修正預填成成功。

本節前段保留355–357歷史失敗。後續355–358聯合full已實際驗證，最新結果如下；不以新綠燈改寫舊TRX／freeze。

**355–358聯合full及原凍結已驗證**：`obj/audit/r358/r358-full.trx`實際 **9,029/9,029通過、0失敗／略過**，runner **8分11秒**；TRX2026-11-17 14:27:45.5448893–14:36:00.7414577（UTC+08:00，8分15.197秒）。`full-command-exit.json`為Scope355–358／TestExit0／FreezeExit0；原`source-freeze.json`的 **2,053來源／文件＋3DLL，共2,056份SHA-256零漂移**（`freeze-verification.json`：Checked2056／Drift0）。原full與原freeze證據保留，核驗完成後才解除凍結，沒有重建freeze追認結果。baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`），不是由方法／作者案例數推估。

**baseline／HTML／CLI及文件後續已驗證**：baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`）；更新後文件／coverage重建`r358-final-docs.trx` **78/78通過、0失敗／略過**，runner **12秒**，TRX2026-11-17 14:50:00.1967118–14:50:16.5819422（UTC+08:00，16.385秒）。`postfull-root-result.json`記兩官方generators check均0，正式HTML來源SHA前綴`5e6c0abd95d0`。CLI建置 **0錯誤／13個既有CS0162警告**（`r358-cli-build.log`，1分18.70秒）；六項離線smoke依序退出 **0／1／2／2／0／0**（`r358-cli-smoke.json`），help、非法sensor選項、缺目錄不可比、壞包SnapshotReadError、兩份合成可比快照及Redfish序號遮蔽／來源／privacy metadata內容斷言均按root串行腳本完成。Redfish電力scope補充另三項退出 **0／0／0**（`docs-high/cli-power-scope-result.json`）：20／200雙Chassis正反序均保兩筆而scalar null、scopeKey集合相同；單一320保scalar320；敏感metadata遮蔽及逐筆內容斷言全true。這九項是離線合成與小型本機檔案fixture，不是BMC實機／硬體精準度驗收。

**R358 closure在359–363接續來源下文件守門已實跑**：第一次`obj/audit/r363/r363-closure-docs.trx`為77通過／1失敗，缺陷是五個已套新C#未登記Git。root只對這五個路徑執行`git add -N`，沒有提交；第二次`r363-closure-docs-2.trx`實際**78/78通過、0失敗／略過**，runner14秒，TestExit0；TRX 2026-11-17T17:16:17.7296902+08:00–2026-11-17T17:16:36.1459345+08:00。兩官方generator check均0，正式HTML已重生。這不是再次Round358 full，R358原full／freeze及原失敗TRX不覆寫；本段結果回填後另跑同組守門並以新receipt存證。359–363新full／新freeze仍待驗，baseline保持9,029。

355原85／1→86/86、356原282／1→349/349及prefull66、357原236／2→273/273、355–357聯合full原8,953／3／8,956與2,052檔Drift0均保留；356沒有獨立full／freeze。r351 full8,731綠但Drift2／拒更事故及r354 full8,834／2,033檔Drift0／closure66保留為歷史。方法、key、family與coverage不是案例數或10000完成率。未提交、上傳或發版，沒有GUI／硬體相容／精準度或native硬取消完成宣稱。 詳見[ROUND-358](ROUND-358-HISTORY-STRUCTURE-AND-REDFISH-SCOPE-2026-11-17.md)。

| 優先 | 範圍／對應候選 | 已確認缺口 | 下一項驗收證據 | 狀態 |
|---|---|---|---|---|
| P1 | WHEA／MM-0059、WH-0003 | Round 346 已補固定窗、方向、雜訊／候選預算與部分失敗狀態，並接共用桌面／CLI 收集；節區仍 null。 | 注入回歸及全套見 Round 346；下一步取得 CPER 外層裝載的可核來源、真機原始 fixture 與 DIMM 身份證據。EventLogReader 建構仍無硬取消上限。 | 部分實作，未收口 |
| P1 | Fleet／EN-0002／0015／0016／0021／0023 | Round 347 已補 metadata、型號來源、重複匿名識別組、有限統計與讀取預算；實體機器身份、取樣條件及來源語意仍未證實。 | 從合格快照取可核機器身份與量的範圍，跨版本／平台 fixture；不能把內容指紋當不同實體機器。 | 已修邏輯缺陷，功能未收口 |
| P1 | Redfish／EN-0015／0021、BT-0071 語意 | Round 347 已補序號遮蔽、採集／匯入時間、fan 單位與合法唯一鍵；包來源與時間仍為自述，缺少實機 BMC fixture。 | 具來源版本的離線包、完整覆蓋預算與 ID 身份，不因 hash 或時間欄存在便提升信任。 | 已修邏輯缺陷，待驗 |
| P1 | Windows 拓撲／OS-0167，複核 CP-0223／AC-0047／0048 邊界 | Round 347 已補 x64 有界 buffer parser、異質 cache、群組遮罩及 NUMA 預算；NUMA 單 group API 與記憶體節點仍有缺口。 | 正式列舉多群組 NUMA 節點及純記憶體節點；跨 Windows 版本 fixture；以 socket/group/core 身份供熱力圖等消費端。 | 部分實作，未收口 |
| P1 | 唯讀磁碟／TS-0185／0187 | profiles 未連 backing-device 身份及 breaker；幾何／長度查詢同步且未證取消。 | 卷到裝置映射與非 NVMe 推定邊界；注入 breaker／部分結果；另由限定範圍實機驗證取消及 handle／buffer 生命週期。 | 部分實作，待證 |
| P1 | 感測／TS-0191、DIMM／SPD | provider 更新時間、實體核心映射及來源獨立性未證；Round 347 補 ScanBus 停止後續交易及 CPU-Z gate，但 iMC ReleaseBus 恢復寫入仍不受包裝器控制。CPU-only 初始化無硬截止。 | iMC 模糊錯誤分類、Timeout 後狀態／恢復與跨程序 bus 所有權；代表性快照和真實取消紀錄。 | 部分實作，待證 |
| P2 | 知識包／EN-0008／0013／0022、KP-0019 | Round 347 已補大小／日期／條目預算；內容 hash 並非來源鏈，upstream 文件／commit／抽取器 manifest 仍缺。 | 相容來源 manifest；lookup 消費的表身與 manifest 雜湊一致；無簽章仍明列非防偽。 | 部分實作，未收口 |

分工原則：Round 348 使用十個代理，分配不重疊的服務／測試範圍，根代理負責共用模型、入口、文件和整合。共同修改需先指定所有權；建置序列執行並使用隔離輸出。全套測試期間凍結原始碼與生成文件，完成後再進下一批。

全計劃曾討論的「12 大階段、約 200–300 批，暫估 240 批」只是排程估計，尚未逐列估時；正典的十二批是規格展開歷史，不是已完成十二批產品實作。完成判準須同時具備生產通路、精確定義、來源、安全預設、桌面／CLI、故障驗證及對應硬體證據。

拓撲下一批的本機官方來源：Windows SDK `10.0.28000.0/um/winnt.h` 第 685 行的 x64 `GROUP_AFFINITY` 為 16 bytes；第 14400／14411 行的 NUMA／CACHE relationship 含 GroupCount 與變長 GroupMasks。不能沿用目前 NativeGroupAffinity 宣稱 24 bytes 的註解作解析依據。

## Round 346 複核補充（待修，不是支援宣稱）

下列為當時發現的歷史缺口；Round 347 已修部分邏輯，當前狀態以上表及該輪驗證紀錄為準，不能把本段當成所有缺陷仍未修。

- **Fleet 身份**：`FleetSnapshotReader.Project` 把 `asset.identify` 的識別完整性判語當成型號；該值來自 `AssetJudge.Verdict.Headline`，不是製造商與型號。應改讀有六態與來源的型號欄位；缺證據拒分群。`AnonymousMachineId` 目前未參與去重；同機多份不可滿足第二台門檻。原匿名 ID 也可能只是型號內容指紋，不能僅因字串不同便證明獨立機器。
- **Fleet 可比性／運算**：沒有共同數值鍵時 peer／summary 仍可 Present，CLI 因而 `comparable=true`。reader 與服務均需排除 Infinity；有限大數的 median／variance 也可能溢位。少樣本依原始絕對差跨單位排序、或將 z 與原始差混排沒有可比語意。目錄 `OrderBy` 會在 Take(200) 前緩存全部檔名，須另設枚舉與總 bytes 預算。
- **拓撲邊界**：`CpuTopologyService`、`CpuAffinity`、`CpuIdService` 的變長記錄須先驗完整 payload 及 masks；`ThreadMigrationService` 的 `off+size` 可能溢位且會接受不足 8 bytes 的尾部。`NumaTopologyService` 使用 ushort 迴圈，最高編號 65535 會回繞；最大 uint 則在加一時溢位。先修拒絕與有界 parser，再談多群組身份。
- **SPD 後續交易**：`SpdReader.ReadOne` 的 MR0／MR1 傳輸故障可被 DDR4 備援覆寫；中途 Timeout 後 finally 復位與下一插槽仍發命令。復位失敗也未附診斷。必須保存第一次故障並停止 bus，清理只在能確認閒置時執行；iMC 模糊硬體錯誤不能統一當 NoDevice。
- **SPD 外部備援**：`SpdScanRunner` gate 僅阻止另一個 runner；`StartupSequence` 仍可在原生 worker 未結束時啟動 CPU-Z 報告。需有結構化「原生工作仍執行」狀態供啟動序列使用，不解析中文 Notes 作控制。CPU-Z 實際匯流排行為仍須版本與硬體實測。

以上由既有代理唯讀追蹤後回查原始碼所得；本輪沒有執行相應實機探測，也沒有把缺陷數換算成 10000 計劃完成率。

## 驗證耗時的下一項改善

以相鄰兩輪完整 TRX 比較（`obj/audit/r347/r347-full.trx`／`obj/audit/r348/r348-full.trx`），同一案例語意的時間如下，單位為秒；storage 兩案本輪具名改為注入組裝，仍保留原接線／例外斷言。

| 案例 | Round 347 | Round 348 |
|---|---:|---:|
| AI 耗盡 10 次重試 | 110.018 | 0.017 |
| Storage 正常組裝及渲染列 | 79.250 | 0.021 |
| Storage 例外及繼續收集 | 78.728 | 0.002 |
| UI 有資料樣板及繫結 | 98.786 | 1.445 |
| 警告守門真正重建 | 73.007 | 87.493 |

警告重建本次較慢 14.486 秒，不列為加速成果。各測試時間不能直接相加推算全套牆鐘；這些數字不是應用啟動耗時或硬體讀取效能，也不能歸因於推理強度。

Round 348 已注入 `AiService` 的 delay 排程，仍驗 10 次重試、參數及取消；storage／UI 組裝只替換無關收集來源，保留真實組裝與 UI 斷言，具名 InjectedComposition。主 ViewModel 的 DIMM／M2 eager 初始化仍可能讀 WMI，不能聲稱完全無 OS 查詢。警告守門保持真正重建，補並讀 pipe 與五分鐘截止。實際耗時及完整結果見 Round 348；不得從測試加速推論應用冷啟動加速。

## Round 348 現況及下一批

本輪修改及逐項限制見 [ROUND-348](ROUND-348-HARDWARE-TRANSACTIONS-2026-11-17.md)，完整回歸已驗證，不新增候選完成數。下表保留 r348 收口時點的下一批起點；r349 已套用及剩餘證據以後節為準。

| 範圍 | 已修邏輯／界線 | 下一項證據 |
|---|---|---|
| iMC／SPD | Busy／Go／TSOD、鎖存失敗、禁止逾時重發及恢復核對；正式 proxy 在 PCI I/O 前拒絕未核所有權。 | direct 寄存器規格不能替代 proxy 仲裁契約；仍需受控 DIMM 實機及跨 channel／stale DONE 證據。 |
| NUMA | Ex 完整群組、有界 parser、relation 1／6 相容；無作用中 CPU 不自稱純記憶體。 | 跨版本／多群組 fixture、容量、真實 socket/group/core 對應。 |
| 唯讀磁碟 | backing-device／單 extent／協定 gate；disk-volume alignment、原生對齊 buffer、短讀邊界及真 IoMode。 | ReOpenFile 與 descriptor 實機相容性；同步 metadata 取消、breaker Trip／Report 仍缺。 |
| PMU／Top-down | live policy、缺值、雙故障及恢復讀回；未核 native 編程在 bridge 前拒絕。 | 核心釘選、CPU／SMT 及跨程序所有權、完整寫回與 S1–S5；不為取讀數硬開。 |
| PCIe | 同型多卡不配第一筆；speed code 單位修正；Gen6 頻寬未知，不猜 lane／來源／拆分。 | PcieLinkService 仍由其他 function／較小 device 猜上游；改用真 parent bridge 及 segment 身份，無證據保留未知。 |
| WMI／儲存 | 同 inner 跨 wrapper gate、例外狀態及資源生命週期。 | 同 namespace/provider 的不同實例仍可重發；依安裝 MOF 定義建立程序內 provider gate，lease 持有至 worker 清理完成。另修 OperationsInventory 的 namespace/query 參數顛倒。 |
| 知識包／企業 | 可空來源 manifest、逐表 hash、實際 JSON bytes 預算；推測不進合規。舊存在性清單缺期望值，改 NotSupported／null。 | manifest 自述非來源認證；具版本條件 schema／單位語意未設計，不用 Present 代替判定。 |
| WHEA | ErrorType 與 SectionSeverity 分離，Extended 未核位義不猜。 | 本機 provider 模板已取、既有事件 0 筆；CPER 外層仍需有界 parser、具名 RawData 模板及可核真實 fixture。 |

## Round 349 收口紀錄（歷史受測範圍）

[ROUND-349](ROUND-349-CPER-AND-COORDINATION-2026-11-17.md)記錄該輪17檔實作及生產限制，`obj/audit/r349/applied.json`保存套用帳。**全套8,602/8,602通過、0失敗／略過，2,009份hash零漂移**；runner9分29秒／TRX9分32.703秒分列。第三次定向322/322與重建0錯誤保留；首次263／8（文件規模／行數未同步）、第二次321／1（DLL嵌舊HTML）亦保留原TRX及原因。baseline已按full更新8,602，後補重建／文件守門 **66/66通過、0失敗／略過**（`obj/audit/r349/r349-final-docs.trx`），兩生成器check回0（HTML hash前綴`a43f43fa7a05`）；CLI建置 **0錯誤／13個既有CS0162警告**（`r349-cli-build.log`），六項離線smoke依序退出0／1／2／2／0／0（`r349-cli-smoke.json`），help、非法選項、缺目錄、壞包、合成可比資料及序號遮蔽內容斷言均符合預期，r349已收口。沒有真實Fleet／BMC硬體證據。

既有全套`StorageGpuPipelineServiceTests`會寫入temp fixture並含小GPU smoke，不能宣稱沒有任何磁碟／GPU I/O；該輪未額外手動啟動SPD、CPU-Z、PMU或native磁碟benchmark。下表是r349收口時點；r350對原生DRAM拒絕、初始化及WHEA query等後續source修正見下一節，不能把歷史缺口當成目前仍未套用，也不能用r349結果背書新改動。

| 範圍 | r349 已套用 | 下一項驗收與生產限制 |
|---|---|---|
| DRAM PMU（01） | 原始 setup／workload／取消與全部清理失敗同時保存，恢復六寫五讀均嘗試；注入定向已通過。 | 恢復失敗不保證counter已停止。r349時GUI另一條原生路徑仍可達，沒有跨程序／SMT所有權；後續r350已套用source窄拒絕，執行證據見下一節。 |
| 清單 WMI（02） | 以 typed scope/query factory 修正參數顛倒，兩 provider 資源範圍收尾，失敗不報部分比對成功；factory定向斷言已通過。 | 實際 WMI 及故障清理、硬截止未證，相同容量不能唯一配對磁碟。 |
| PCIe（03） | 直接父橋 secondary-bus 證據取代 BDF 猜測；合法速度／寬度與能力矛盾拒絕；capability 版本取高半字；operational TLS 僅 role 4／6；合成／注入定向已通過。 | 真正 parent fixture仍缺。BDF-only、segment／PnP 未知，非原子快照，不推 CPU／PCH。 |
| 超頻協調（04） | generation 撤銷及序列 slot 包住已派發 apply、恢復、telemetry、engine／VRM 處置；observer 故障不阻斷收尾；25項協調定向已通過。 | service-local 不是 SDK 排他所有權。初始化 await 後在 Dispose 之後採納 engine 的舊競態留給 r350，VRM 真 bridge disposal 未實證。 |
| WHEA／CPER（05） | 生產 ToXml→RawData→CPER→共用 facts 接線；核所有 descriptor，拒 simulated/truncated flags 並保留最多四種原因；候選事件／節區與全域16列分帳；parser／facts／reader定向已通過。 | 無真實 payload，既有事件0筆。query 仍17／19／23／46／47；ToXml 先配置及 reader 建構無硬取消，DIMM 身份未證。 |
| StorageWMI（08） | 按 MOF 註冊身份跨 source gate，lease 直到 read／cleanup 完成；正式七層資源走同一 cleanup helper，原讀取與清理故障並存；27項定向已通過。 | 真 COM 生命週期及取消未證。只涵蓋程序內可靠性路徑，其他 consumer／程序不受此 gate 保護。 |

## Round 350 已套用、定向通過

[ROUND-350](ROUND-350-READING-AND-LIFECYCLE-2026-11-17.md)正式記錄六組變更；`obj/audit/r350/applied.json`共 **29份映射／27份實質變更**。`WheaEventEnvelopeParser.cs`及`CperMemoryEnvelopeDecoder.cs`為hash不變的前置依賴，不重複計成修改。以下均為已套用source，執行證據另計：

- **01 磁碟 breaker**：特定完成 native read/device 錯誤在讀取結束後回饋實體 index 的 cooldown；不把配置、取消或短讀當故障，不清除並行 session 故障。沒有當次硬取消或熱插拔持久身份保證。
- **02 參考服務啟動**：CPU pinout／DIMM／M.2改由正式startup明確一次啟動，各instance快取單次Task；其餘constructor I/O仍存在，M2 Task只代表嘗試提交publisher callback，沒有UI／report readiness保證。
- **03 PCIe能力語意**：ASPM只取bits11:10、ClockPM只取bit18，排除width及latency欄位；能力不等於啟用，不據此斷定降速原因或閒置會恢復。真parent、segment／PnP及非原子快照限制保留。
- **05 WHEA query**：新增1／18候選及保留未知版本原因；manifest 是提取契約，不是真實 payload 證據。
- **06 DRAM原生PMU**：正式source已在bridge／大buffer／負載前拒絕未核所有權通路，保留fake MeasureCounters，GUI完成／故障／取消均清舊值及IsBusy；相關注入定向已通過，沒有native PMU量測證據。政策開關、零baseline、LP affinity都不是PMU所有權。
- **07 超頻初始化**：Dispose／generation／engine-slot協調factory、boot、VRM及awaited worker清理；cleanup await後重查generation，optional boot preference例外保留診斷並繼續init。正式Xtu已catch Send，throwing-engine防禦契約的注入定向通過仍不是實機throw重現；不保證強制取消SDK初始化。

**編譯／驗證帳**：第一次`r350-target.log`因`ReferenceDataStartupTests`缺`System.IO`有7個編譯錯誤；第二次`r350-target-2.log`因補入的using位於檔尾而觸發CS1529。兩次都沒有TRX，不能記成定向案例失敗。using現已移至namespace前，新增WHEA測試的Assert.Single改用predicate overload修正warning。第三次`r350-target-3.trx`為 **347通過／1失敗／0略過**，新增測試誤將SocketName期待為AM5；修為`DetectedSocket == "AM5"`與`SocketName == "AM5 (LGA 1718)"`兩條精確斷言後，第四次`r350-target-4.trx` **348/348通過、0失敗／略過**。保留全部失敗證據，不把定向結果當作聯合全套或硬體驗證完成。

r350＋351全套後續已執行8,731通過，但freeze Drift2，baseline更新拒絕；原資料保留。根代理選擇先整合352–354，再重新凍結350–354聯合全套；沒有r350獨立全套凍結證據，ProjectTests基線保持8,602，不重跑相同351full追認零漂移。

## Round351歷史定向與全套（凍結事故未收口）

[ROUND-351](ROUND-351-TEMP-OWNERSHIP-AND-BASELINE-2026-11-17.md)及`obj/audit/r351/applied.json`記錄19份映射；下表保留該輪受測時點，後續352／353／354窄修正另記。新檔intent-to-add不代表提交，staged-only屬製備歷史，以applied為準。

| 組別 | 已套用範圍 | 未完成界線及下一項證據 |
|---|---|---|
| 01：共用owned temp，5份 | OwnedTemporaryFile及GPU／Gauntlet storage持同一CreateNew handle，移除固定名重開／共享目錄遞迴刪除；取消案例核實際per-run檔案。 | 其他DeepBench固定檔名路徑未全修，Gauntlet例外／Join生命週期仍未修；reparse／volume／free-space未pin，DeleteOnClose僅請求。4,096-byte取消fixture有實際寫／flush／讀，fake GPU不是全類無I/O。 |
| 02：最後合規輸入，5份 | generation、凍結package及輸入、一次load、Steps鎖與GUI deferred最後完成。Startup以dependencySHA256嚴格匹配r350後715c…，applied保存真前置。 | 當下已提交evidence-domain projection不代表同輪原子producer交易；舊BaselineFacts及晚到timeout worker仍可能存在，GUI／CLI領域不同，不等於全HardwareSnapshot凍結。 |
| 06：DiskBench，2份 | GUID路徑、guard先於CreateNew、單一ReadWrite／NoBuffering／WriteThrough／DeleteOnClose handle及同步workload後處置，移除按路徑刪除。 | fake handle不證native行為；4096幾何假設、短讀計數及取消呈現仍有限制，未執行實際512MiB benchmark。 |
| 09：企業條件v2，7份 | 共用純核心、v1／v2簽章與真實schema報告、精確unit／requirementId、CLI null unit修正、純adapter及企業說明。 | v1位元組相容但證據資格有意收緊；v2不換算單位，GUI signed policy／trusted key匯入未接，純adapter不驗簽或期限，沒有端點證明／freshness。 |

首次`r351-target.log`在OwnedTemporaryFileTests第70行always-throw lambda誤選obsolete Assert.Throws(Func<Task>)而CS0619，沒有TRX，不是定向案例失敗；根已cast Action保同步unwind檢查。第二次`r351-target-2.trx` **474通過／1失敗／0略過**，唯一EnterpriseBaselineV2Tests.DeviceAdapter案例誤以為signed canonical及adapter有相同輸出排序。沒有該公共順序契約；根將雙方按RequirementId ordinal排序後仍完整record array Equal，原counts及獨立canonical測試保留，未改生產判定。第三次`r351-target-3.trx` **475/475通過、0失敗／略過，runner26秒**，filter含350＋351相關。原log／TRX保留，套用後測試修正與applied初始hash分列。

r351先行文件門檻66/66、runner15秒及兩generator check0／HTML來源785384397b47已驗；該時點規模695服務／1,659 C#／680測試檔及7,006 Fact／357 Theory／7,363方法已核。方法數不是全套案例總數，後續新規模待實核，不推算。

**r351全套與凍結事故分帳**：`obj/audit/r351/r351-full.trx` **8,731/8,731通過、0失敗／略過，runner9分22秒**；TRX 2026-11-17 05:04:27.5405651–05:13:53.6720597（UTC+08:00，9分26.131秒）。但原`freeze-verification.json`為 **Checked2020／Drift2**，只列HANDOFF及QUEUE；source-freeze.json在05:04:23.8880867完成，兩份文件最後寫入05:04:24.9669808／05:04:24.9691538，較manifest晚約1.079／1.081秒，雖早於TRX開始仍使hash不符。ROUND351在05:03:16已寫完且hash相符。這是最後一次授權文件更新與freeze建立時序重疊，不是零漂移；原verification／TRX保留，不覆零、不重建清單追認。baseline-update已拒絕，ProjectTests保持 **8,602**。事故報告見`obj/audit/r351/capacity10-freeze-drift-analysis-2026-11-17.md`，逐檔預期／目前hash及mtime見`capacity10-freeze-drift-metadata.json`。根代理不重跑相同351full，先整合352／353／354後按350–354真實聯合範圍重新凍結驗證；不預估新總數。

## Round352已套用、42項定向通過

`obj/audit/r352/applied.json`共7映射（DiskIoMatrix2／StorageWriteIntegrity3／Gauntlet2）。`r352-target.trx` **42/42通過、0失敗／略過**，runner2秒；TRX05:18:22.8052389–05:18:29.4367398（UTC+08:00），6.632秒範圍另列。這是正式ownership／worker glue及注入回歸，沒有native disk／GPU／硬取消或全套證據；fixture可能有真小型檔案I/O。各組及限制見[ROUND-352](ROUND-352-STORAGE-LIFETIME-2026-11-17.md)。

## Round353／354已套用及定向通過

r353已套17獨立targets（原18映射含EvidenceLabService重疊，以merged353去重），定向84/84、0失敗／略過（runner15秒，含WPF實建／UiSmoke）；r354已套6 targets，以merged354保留來源失敗修正及importer，定向23/23、0失敗／略過（runner535ms）。350–354後續聯合**8,834/8,834通過、0失敗／略過**（`obj/audit/r354/r354-full.trx`），runner **9分1秒**；TRX2026-11-17 07:16:05.9169390–07:25:11.0004526（UTC+08:00，9分5.084秒），`full-command-exit.json`的TestExit／FreezeExit均0；**2,030來源／文件＋3DLL，共2,033份SHA-256零漂移**（`obj/audit/r354/freeze-verification.json`：Checked2033／Drift0，原freeze及verification保存），凍結核對完成後才解除凍結同步baseline／文件；baseline／HTML同步後重建文件守門 **66/66通過、0失敗／略過**（`obj/audit/r354/r354-final-docs.trx`），runner16秒；TRX2026-11-17 07:31:25.5030210–07:31:45.8587826（UTC+08:00，20.356秒）；兩official generators check均回0（root串行session40321 exit0），HTML sourceSHA前綴`6aab08a367e8`。此66項早於closure三檔回填；closure回填後文件守門已以`obj/audit/r354/r354-closure-docs.trx` **66/66通過、0失敗／略過**（runner14秒，exit0）；TRX2026-11-17 07:47:38.5674937–07:47:57.4350997（UTC+08:00，18.868秒），r354正式收口證據已齊；CLI建置 **0錯誤／13個既有CS0162警告**（`obj/audit/r354/r354-cli-build.log`，session16488 exit0）；六項離線smoke（`obj/audit/r354/r354-cli-smoke.json`）依序退出 **0／1／2／2／0／0**，help、非法sensor選項、缺目錄不可比、壞包SnapshotReadError、兩份合成可比快照及Redfish序號遮蔽／來源與privacy metadata全部內容斷言均通過，root確認session40321的smoke及兩generator checks串行exit0。

詳見[ROUND-353](ROUND-353-BOUNDARIES-AND-IMPORT-2026-11-17.md)及[ROUND-354](ROUND-354-PUBLICATION-AND-CANCELLATION-2026-11-17.md)。baseline 已按實跑由 **8,602更新8,834**（`obj/audit/r354/baseline-update.json`），不由作者案例計畫推算；baseline／HTML同步後的66文件門檻已過；closure回填後文件守門已以`obj/audit/r354/r354-closure-docs.trx` **66/66通過、0失敗／略過**（runner14秒，exit0）；TRX2026-11-17 07:47:38.5674937–07:47:57.4350997（UTC+08:00，18.868秒），r354正式收口證據已齊；實際UI對話框／visual／accessibility與native硬體／取消仍未驗。舊r351 full8731／Drift2及全部失敗證據保留；不宣稱所有DeepBench能力、全collector原子交易、GUI完整企業流程或native硬取消已完成，未提交、上傳或發版。

## 355–357歷史準備與後續聯合驗證

355兩次85／1→86/86、356282／1→349/349及prefull66、357236／2→273/273與各post-apply修正原帳均保留。來源規模實核 **700服務／1,685 C#／699測試檔／7,116 Fact／389 Theory／7,505方法**，key1,212／domain161／family71／coverage477；方法數、key與coverage都不是full案例總數。`obj/audit/r357/reviews/capacity05/review-public-sync-final.txt`獨立核四份公開文件候選及來源集合／AST／catalog／H行數，無blocking mismatch；歷史Part11與runtime baseline8,834原值保留。這是357full前的規模與準備歷史；當時四公開／交接候選套用後，prefull文件78/78及handoff66/66實際通過，後續355–357full為8,953通過／3失敗／0略過，共8,956，2,052檔Drift0，baseline未改。358再整合history／Redfish／精確守門修正後，355–358full實際9,029/9,029及2,056檔Drift0；現在式baseline／CLI／HTML／文件後續收口以上方最新結果為準，不沿用舊待驗計畫冒充現況。原失敗、準備與凍結證據均保留。

## 新full前最後守門與凍結準備

`obj/audit/r371/r371-prefull.trx`原136通過／1失敗，共137：倉庫HTML已重生，但root以no-build使用先前DLL，內嵌百科仍舊。守門正確抓出bytes差異；沒有修改斷言。改以正常build重嵌後`r371-prefull-2.trx`實際**137/137通過、0失敗／略過**，TestExit0；TRX 2026-11-17T17:32:35.8521442+08:00–2026-11-17T17:33:15.5432081+08:00。兩官方generator checks均0。原失敗完整保留。

接續建立全新R371 source／assembly freeze，範圍359–371但排除未套R364來源；不重驗／覆寫R358歷史freeze。新full結果仍待實跑，baseline9,029保持。
