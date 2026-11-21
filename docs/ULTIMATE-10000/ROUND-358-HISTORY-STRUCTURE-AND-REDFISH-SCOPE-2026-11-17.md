# Round358：history結構／bytes預算、Redfish電力範圍及三項full失敗修正（2026-11-17）

本輪已按`obj/audit/r358/applied.json`核hash套用 **13份獨立targets**（含AGENTS代理政策），並保留三項355–357聯合full失敗；358定向248/248、prefull文件／coverage78/78、後續355–358聯合full9,029/9,029及2,056份hash零漂移均有原始執行證據。baseline／HTML／CLI／最終文件及closure各階段分列下節，不用全套綠燈代替後續收口。

## 保留355–357聯合full失敗

`obj/audit/r357/r357-full.trx`實際 **8,953通過／3失敗／0略過，共8,956**，runner **9分38秒**；TRX2026-11-17 12:56:13.1700342–13:05:55.4420501（UTC+08:00，9分42.272秒）。`full-command-exit.json`為TestExit1／FreezeExit0，`freeze-verification.json`為 **Checked2,052／Drift0**。原log／TRX／freeze／`final-failure-result.json`保留，零漂移不使失敗變通過；沒有把8,956寫入baseline。r354的8,834及2,033份Drift0／closure66是歷史合格結果；r351的8,731綠但Drift2／拒更事故永不改0。

三項精確修正依`staged/capacity05/manifest.json`：DecoderHardening的exact expected名冊按生產registry順序補`WheaProcessorGenericSectionDecoder`及`WheaPcieSectionDecoder`，不移除集合相等或覆蓋斷言；PCIe動態家族理由以中文明定EventRecordID＋descriptor成員身份、可選敏感序號child及16列上限，不加豁免／改家族數；將既有`WheaErrorFactsService`aggregator從PCIe同檔拆成獨立來源檔，使WiringGuard可見外檔production引用，不改聚合方法body或重讀EventLog。新增一份Services檔是檔案結構變更，不是新硬體功能。

## DeepBench history結構及64MiB實際bytes預算

R357拒絕壞JSON／object root／null record／超過100列，卻會接受`[{}]`，後續`DeepBenchHistoryRow.From`解參考缺少的Results；本輪檢查實際消費的reference結構。Results／Insights、result／metric／point、必要集合與字串成員不可null；optional Error仍可null，空集合／空字串及既有cancelled／platform-failure記錄保留。未驗證日期順序、session關係、樣本合理性、numeric enum含義或measurement物理支持，不把結構可載入稱為可信量測。

正式讀取共用單stream，每次至多64KiB、最多 **64MiB實際原始bytes**，再讀一個byte判斷超額；不依賴FileInfo.Length／Stream.Length／seek。FileStream bufferSize1避免managed read-ahead把額外probe擴成另一buffer。支援既有UTF-8／UTF-16／UTF-32 BOM；BOM計入raw budget。LoadRecent超限／結構錯時保empty projection與LastLoadError，Save在建立目錄／sidecar／AtomicWrite前拒絕並保原檔。新record先驗結構，序列化後UTF-8 bytes亦不得超限，不藉超限刪舊列。

64MiB是內容budget，**不是process-memory ceiling**：原始buffer、decoded string、JsonDocument及object graph可同時存在；Save仍先建立完整序列化字串。檔案同步讀取沒有deadline／取消，RMW並行last-writer-wins及AtomicWrite sidecar所有權未在此修。超過64MiB舊檔原可讀，現明確拒絕並保留bytes，不宣稱無條件向後相容。`staged/capacity09/DESIGN.txt`及`reviews/capacity05/review-history-structure-bounds.txt`保存消費端、預算與獨審界線；作者planned rows不列為執行通過。

## Redfish PowerControl逐筆來源範圍

既有reader對不同Chassis／PowerControl取第一個numeric，會因列序變動而把20或200當同一無身份scalar。DMTF官方`Power.v1_7_3.json`在`planning/capacity02/redfish/`保存原bytes與hash；PowerControl是array，MemberId僅在所在array唯一，PhysicalContext／RelatedItem不建立可相加的non-overlap契約。不得推定首筆或總和為整包總功耗。

本輪保既有inventory constructor與nullable powerConsumedWatts，另保每筆PowerControl已解析的Chassis／Power本機resource位置、array index及原始成員metadata。payload自述URI不取代實際成功映射位置；inline來源明示JSON位置。只有一筆完整、scope已解析、finite且非負的觀測才投影到舊scalar；多筆、缺值、負值／非finite、partial enumeration、重複scope／截斷或矛盾legacy scalar均不挑首筆或相加，保Unknown／numeric null及理由。0仍有效；missing timestamp沿既有Unknown契約。

逐筆facts沿既有`rf.item.`家族，summary仍`rf.power.consumed`，不加static key或新family。scope hash僅供opaque key與碰撞區分，不能當實體裝置或跨來源identity；可讀raw scope在Sensitive Value，source不抄raw識別碼。CLI加逐筆list／complete／reason，預設遮蔽新raw metadata、保numeric及opaque scopeKey；snapshot沿既有Sensitive遮蔽，explicit preserve才保raw。仍是離線匯入、Reported與來源自述，不是Measured／BMC實機或freshness／authenticity驗證。

新retention cap256 controls、metadata256 chars，local link256及inline location1024；cap／壞成員／nextLink或declared count不完整均阻止唯一scalar。拒新增query／fragment／percent／colon routing並沿既有reparse防線；沒有宣稱handle-based TOCTOU保證。既有ReadAllText與FileInfo.Length race、整包bytes budget、PowerSubsystem遷移及跨資源加總仍在範圍外。`staged/capacity02/REVIEW.txt`詳列source-level限制；尚無實際BMC fixture。

## Redfish獨審追加privacy修復與邊界

`obj/audit/r358/reviews/high-redfish/REVIEW.txt`獨審追出初版scope修復仍有公開旁路：拒絕的Chassis.Power URI／路徑可含序號或token，reader把原值插入Warnings；CLI直接匯出且`rf.bundle.warnings`原為非Sensitive，會繞過新PowerControl metadata遮蔽。同次追查亦見CLI的inventory.Directory及讀／寫exception Message可能帶本機路徑。這是可核源碼資料流缺陷；不把當時source tracing稱為已執行C#重現。

正式整合採用high-redfish **final revision2**六份候選，manifest SHA256 `6a9ae6fae23afe45a9c162c790375358280a87e32d9f2228487a55c5e12c79fe`，不是原capacity02或被取代的provisional revision2。公開reader診斷只保role、拒絕原因、exception type及上限，不帶原URI／path；CLI directory預設redact，Redfish讀／寫錯誤只出type、不抄Message；`rf.bundle.warnings`改Sensitive=true，包含caller自造warning也受default snapshot保護。explicit Preserve仍可保授權原始warning證據，不能稱所有匯出無條件抹除。safe structural理由保留；本修補沒有證明整個應用所有bespoke輸出均無私密資料。

四個URI fixture涵蓋external、traversal、query／token及missing local Power，另有caller warning default／Preserve回歸。獨審當時只執行static_audit.py／hash與來源檢查，並未執行C#；後續target及full執行結果由本輪原TRX分別支持，不能用獨審的PASS source review替代runtime／硬體證據。既有FileInfo.Length／ReadAllText race、整包byte budget、BMC freshness／authenticity、PowerSubsystem及跨來源identity等限制不變。

## 代理派發政策及驗證狀態

`staged/root-policy/AGENTS.md.txt`依使用者要求新增：新派發代理使用high；首次之外暫時transport／rate-limit／service-unavailable最多100次重試，bounded backoff並保attempt evidence，接續既有成果。編譯錯／斷言失敗／永久authentication或invalid input先診斷，不能靜默重試求綠。這是開發代理dispatch政策，不改應用HTTP或硬體operation既有retry／budget。

代理派發政策與來源套用不構成執行成功；實際定向／full結果分列後節。既有全套含Windows Boost pulse、Throughput／power API、真temp I/O及最小GPU等路徑，不能概括無native／無CPU負載／無I/O。沒有額外手動SPD／CPU-Z／PMU／native磁碟benchmark；沒有GUI／硬體／native取消／全collector原子交易完成宣稱。


## 實際套用與來源統計

來源規模實核 **701服務／1,688 C#／701測試檔／7,130 Fact／400 Theory／7,530方法**，key1,212／domain161／family71／coverage477。`obj/audit/r358/public-sync-high/stats-review.json`為逐方法AST統計，parseErrors、test及全部C#來源hash drift均0；未編譯、未載受測DLL或執行dotnet。方法數與key不是full案例總數。`source-hashes.json`保全部來源集合及生成器hash；公開三語／百科候選使用官方domains generator記憶體輸出且候選check回0，這不是正式重建或HTML／docs通過。AppendixH依terminal newline既有算法，Processor Generic仍77，新增WheaErrorFactsService獨立列。


## 聯合full前實際驗證（歷史前置）

358定向已實跑 **248/248通過、0失敗／略過**（`obj/audit/r358/r358-target.trx`，runner14秒）；文件與coverage重建 **78/78通過、0失敗／略過**（`r358-prefull-docs.trx`，runner14秒）。正式domains generator check回0，生成HTML來源SHA前綴`783997831034`；baseline仍8,834。這是下一次355–358聯合full前證據，不是全套或硬體驗收。


## 355–358聯合full與後續收口

**355–358聯合full及原凍結已驗證**：`obj/audit/r358/r358-full.trx`實際 **9,029/9,029通過、0失敗／略過**，runner **8分11秒**；TRX2026-11-17 14:27:45.5448893–14:36:00.7414577（UTC+08:00，8分15.197秒）。`full-command-exit.json`為Scope355–358／TestExit0／FreezeExit0；原`source-freeze.json`的 **2,053來源／文件＋3DLL，共2,056份SHA-256零漂移**（`freeze-verification.json`：Checked2056／Drift0）。原full與原freeze證據保留，核驗完成後才解除凍結，沒有重建freeze追認結果。baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`），不是由方法／作者案例數推估。

**baseline／HTML／CLI及文件後續已驗證**：baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`）；更新後文件／coverage重建`r358-final-docs.trx` **78/78通過、0失敗／略過**，runner **12秒**，TRX2026-11-17 14:50:00.1967118–14:50:16.5819422（UTC+08:00，16.385秒）。`postfull-root-result.json`記兩官方generators check均0，正式HTML來源SHA前綴`5e6c0abd95d0`。CLI建置 **0錯誤／13個既有CS0162警告**（`r358-cli-build.log`，1分18.70秒）；六項離線smoke依序退出 **0／1／2／2／0／0**（`r358-cli-smoke.json`），help、非法sensor選項、缺目錄不可比、壞包SnapshotReadError、兩份合成可比快照及Redfish序號遮蔽／來源／privacy metadata內容斷言均按root串行腳本完成。Redfish電力scope補充另三項退出 **0／0／0**（`docs-high/cli-power-scope-result.json`）：20／200雙Chassis正反序均保兩筆而scalar null、scopeKey集合相同；單一320保scalar320；敏感metadata遮蔽及逐筆內容斷言全true。這九項是離線合成與小型本機檔案fixture，不是BMC實機／硬體精準度驗收。

**R358 closure在359–363接續來源下文件守門已實跑**：第一次`obj/audit/r363/r363-closure-docs.trx`為77通過／1失敗，缺陷是五個已套新C#未登記Git。root只對這五個路徑執行`git add -N`，沒有提交；第二次`r363-closure-docs-2.trx`實際**78/78通過、0失敗／略過**，runner14秒，TestExit0；TRX 2026-11-17T17:16:17.7296902+08:00–2026-11-17T17:16:36.1459345+08:00。兩官方generator check均0，正式HTML已重生。這不是再次Round358 full，R358原full／freeze及原失敗TRX不覆寫；本段結果回填後另跑同組守門並以新receipt存證。359–363新full／新freeze仍待驗，baseline保持9,029。

355原85／1→86/86、356原282／1→349/349及prefull66、357原236／2→273/273、355–357聯合full原8,953／3／8,956與2,052檔Drift0均保留；356沒有獨立full／freeze。r351 full8,731綠但Drift2／拒更事故及r354 full8,834／2,033檔Drift0／closure66保留為歷史。方法、key、family與coverage不是案例數或10000完成率。未提交、上傳或發版，沒有GUI／硬體相容／精準度或native硬取消完成宣稱。
