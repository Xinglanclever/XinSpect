# Round356：NUMA可用量、snapshot handle發布與CPER PCIe（2026-11-17）

**最新聯合驗證（355–358）**：9,029/9,029通過、0失敗／略過，runner8分11秒；2,053來源／文件＋3DLL共2,056份hash零漂移。baseline與後續CLI／HTML／最終文件收口狀態見末節；下文原輪次失敗／定向及355–357full三失敗按歷史受測時點保留。

已核hash套用 **19份獨立targets**（`obj/audit/r356/applied.json`、`apply-receipt.json`的statusFinal=APPLIED）：NUMA2、snapshot Save3、PCIe／catalog／required gates13及EvidenceLab integration1。01窄key gates已合進04-final輸出，不能另加四份當23targets或重覆套舊recipe。首次定向 **282通過／1失敗／0略過，總283**，runner9秒；唯一README coverage基線未同步，原證據保留。第二次定向加文件守門重建 **349/349通過、0失敗／略過**（`obj/audit/r356/r356-target-2.trx`），runner **24秒**，root session47345 exit0；TRX2026-11-17 09:13:53.0076469–09:14:21.5718473（UTC+08:00，28.564秒）。349為原283定向＋66文件門檻，不是full。355–357聯合full尚未跑；最新合格baseline仍r354 **8,834**、2,033份hash零漂移，不能由新案例／key／方法數推升。

## OS-0167每個列舉NUMA node的available bytes

`NumaTopologyService`保原uint node identity及多group masks，每個validated node只出`numa.node.{originalId}.available_bytes`。GetNumaAvailableMemoryNodeEx參數為USHORT，原node ID>65535回Unknown，不截斷、不發query；sample node ID不符亦丟值。成功0仍Present；exact ulong decimal Value與bytes unit不損精度，NumericValue僅在<=2^53時出，claimed Present缺值為ReadError。原sample時間／state／reason保留，native completion time不等OS counter refresh time。

available是free／zeroed pages，官方語義在單／多node對standby有差異，並非installed／total容量。最多64 memory calls及250ms monotonic dispatch budget只限制下一次派發，在途同步API仍可能超時而無硬取消；成功慢sample保留，其餘Unknown。逐node不構成同時快照，也不求和。注入snapshot若無memory probe不得意外進native，production正常入口才選native probe；本輪注入不代替真NUMA API／拓撲／效能驗證。

獨審`reviews/capacity05/review-numa-available-memory.txt`核前／後hash及官方API／SDK語義，未發現窄範圍blocking defect；這項靜態意見與定向執行分列，不提升成支援所有NUMA平台。

## Snapshot Save維持同一owned file object到發布／失敗清理

`HardwareSnapshotService.Save`保共用serializer、redaction、size／path前置驗證，353 Load不改；新`SnapshotSaveFile`在同目錄GUID basename以CreateNew、GENERIC_WRITE|DELETE、share0及WriteThrough取得owner，沒有DELETE_ON_CLOSE。write→Flush(true)完成後，以同SafeFileHandle FileRenameInfo發布；overwrite=false交kernel拒既有target，overwrite=true才允許替換。成功publish後dispose不刪published檔，失敗對仍開的owned handle要求disposition再close，沒有close/reopen source或path-delete fallback，不傷既有固定`.tmp` sidecar。

03 V2獨審修過實際cleanup問題：初版取`_stream.SafeFileHandle`可能暗中Flush，在原write／flush失敗後再throw，mask primary且跳過close。最終owner保留原CreateFileW `_handle`直接供publish／disposition，不透過stream getter；cleanup failure被catch，stream disposal仍嘗試，原例外保持。ABI依SDK conditional union及x86/x64 alignment：rename union位0，root8／4、length16／8、UTF16 name20／12，length為bytes；disposition為一byte BOOLEAN，native BOOL回4byte。

首次TRX中`HardwareSnapshotSaveOwnershipTests` **11/11通過**：含六個Windows小型GUID temp-directory native/file fixtures、兩個ABI theory rows及三個fake-owner錯誤rows。這是真實執行的本機檔案fixture，不是純fake無I/O；不代表全平台、遠端filesystem、長path、斷電namespace durability或真磁碟fault復原證據。share0不擋privileged kernel／祖先目錄retarget，parent未directory-handle pin；disposition可失敗並殘留自有隨機檔，abrupt termination亦可殘留，沒有硬deadline或取消保證。

獨審`reviews/capacity09/snapshot-save-v2-review.txt`及manifest tie-out保存初版blocking發現／修正、matching runtime primary來源與最終hash，不能只說「靜態無問題」而漏掉V2原因。

post-apply純空白帳：`obj/audit/r356/snapshot-whitespace-fix.json`保存`SnapshotSaveFile.cs`初始SHA `9d426396a134d70caa5650cdbcccf8e6b756e42083c69fc2d1804b03e5260511`與刪除多餘terminal CRLF後SHA `a70b9c189cae9ecec67891f15734847c7b743ade0c8ea220cc9b7a3a601e580b`；沒有邏輯或案例數變更，initial applied不覆寫。此後官方domains重生行數及最新HTML`9306a74d918f`，prefull-docs已重建66/66通過、0失敗／略過（`obj/audit/r356/r356-pre-full-docs.trx`，runner15秒，exit0），TRX2026-11-17 09:20:45.1510291–09:21:03.9186691（UTC+08:00，18.768秒），349項原TRX不重命名成後續重建結果。

## WH-0006 bounded CPER PCIe saved-event projection與真接線

outer envelope仍核CPER header／descriptor revisions、全部bounds／overlap、severity、sections／size caps及simulated／truncated flags；memory Sections保持memory-only，另typed sections有own bytes。PCIe最小208bytes，逐ValidBits gate，unknown高bits拒絕；BDF非法只抑制identity，不捏造device。version照spec raw bytes、unknown port出numeric，Express／AER payload未發表，不把saved record視為live實體歸因或完整AER診斷。

同一accepted WheaMemoryReadResult餵memory及PCIe，不多讀一次EventLog；compound event+descriptor去重、listing cap16保完整retained count，incomplete read的summary非Present、null numeric／lower-bound，保saved rows的Reported信任。record／section severity與XML EventLevel分列。plain injected list無完整性contract，不能冒稱complete。

獨審修過三項：短但outer合法的PCIe不拒整個mixed envelope而保可用memory sections；record safe ToString不漏serial；typed sections／raw bytes JsonIgnore避免direct transport JSON漏serial。serial獨立Sensitive fact，default snapshot按既有規則redact；沒有原始serial常態輸出或全GUI隱私驗收宣稱。

EvidenceLab merged356 SHA `41546eff172aea5ead63b00675e860aeebb2061a636cbd2270694bfdd96230c4`以355 storage gate版`3feec50b…`為前置，保353 importer／失敗邊界、354 WHEA generation／timeout/revoke gate，切combined service；AllFacts抵firmware類及CLI snapshot/report，ECC subset刻意仍memory-only。catalog／coverage及01窄key gates使用04-final整合hash，相關scope registry／SDK來源知識同步，沒有新增一次EventLog讀取或繞gate直寫。

`reviews/capacity07/pcie-final-review.txt`及`pcie-final13-delta-review.txt`核凍結candidate、SDK layouts與最後13檔gate合併；上游網頁未成功讀取的部分沒有冒稱獨立UEFI重現。synthetic saved-event fixtures不是實際hardware／firmware／EventLog相容證明。

## 首次失敗、待重驗與聯合範圍

`obj/audit/r356/r356-target.trx` **282通過／1失敗／0略過，共283**，runner **9秒**；TRX2026-11-17 09:04:57.5788111–09:05:10.9392924（UTC+08:00，13.360秒）。唯一`FactCoverageReportTests.README的覆蓋基線要等於程式的已檢視上限`失敗：README仍舊473，程式已檢視上限為475，精確新字串`| 覆蓋基線 | 475 | 遞增需留理由 |`不存在。01後續已同步三語／百科；這是原次文件未同步失敗，原證據完整保留，不回填成首次283/283。snapshot分組11pass不能替代整輪。

第二次定向加文件守門重建 **349/349通過、0失敗／略過**（`obj/audit/r356/r356-target-2.trx`），runner **24秒**，root session47345 exit0；TRX2026-11-17 09:13:53.0076469–09:14:21.5718473（UTC+08:00，28.564秒）。349為原283定向＋66文件門檻，不是full。公開文件四份已核hash套用（`obj/audit/r356/public-sync/applied.json`：三語README及百科）；該時點gen-readme check回0、HTML sourceSHA為`a28b01d3e742`。後續SnapshotSaveFile移除多餘terminal CRLF後，官方domains重生H行數與HTML，最新sourceSHA前綴 **`9306a74d918f`**；原公開套用收據保留，prefull-docs已重建66/66通過、0失敗／略過（`obj/audit/r356/r356-pre-full-docs.trx`，runner15秒，exit0），確保當時DLL嵌更新HTML。本次實核規模698服務／1,678 C#／695測試檔，7,098 Fact／383 Theory／7,481方法；key1,210／domain161／family70／coverage475。方法、key及coverage數均不是full案例數。原282／1的TRX／log及applied初始SHA保留。355已9targets、首次85／1修Coherence fixture後86/86，兩輪原證據及test修前後像保留，355–357後續聯合full為8,953通過／3失敗，凍結2,052份Drift0；精確結果見下節，該次baseline8,834維持；後續355–358結果見末節。

上述是356時點的待驗流程；後續355–357聯合full已執行但三項失敗，原TRX／freeze與baseline維持證據如下。後續已按355–358範圍另建凍結並完成實跑，最新結果見末節，不覆寫失敗結果。

**355–357聯合full歷史失敗（原證據保留）**：`obj/audit/r357/r357-full.trx`實際 **8,953通過／3失敗／0略過，共8,956**，runner **9分38秒**；TRX2026-11-17 12:56:13.1700342–13:05:55.4420501（UTC+08:00，9分42.272秒）。`full-command-exit.json`為TestExit1／FreezeExit0，`freeze-verification.json`為 **Checked2,052／Drift0**；凍結零漂移不能使三個失敗變通過。`final-failure-result.json`保留完整counters／times／失敗訊息；該次baseline維持r354 **8,834**，沒有更新為失敗總數8,956。三項為DecoderHardening精確registry名冊少ProcessorGeneric／PCIe兩解碼器、WiringGuard對同檔aggregator引用未被辨識的PCIe服務、KeyCatalogPolicy的PCIe家族成員定義契約。R358已按applied帳套用精確補名、獨立WheaErrorFactsService檔案及中文成員定義；後續355–358聯合full實際9,029/9,029通過，原三項失敗不覆寫；不得新增豁免、降低斷言或將修正預填成成功。

本節前段保留355–357歷史失敗。後續355–358聯合full已實際驗證，最新結果如下；不以新綠燈改寫舊TRX／freeze。

## 355–358後續聯合驗證

**355–358聯合full及原凍結已驗證**：`obj/audit/r358/r358-full.trx`實際 **9,029/9,029通過、0失敗／略過**，runner **8分11秒**；TRX2026-11-17 14:27:45.5448893–14:36:00.7414577（UTC+08:00，8分15.197秒）。`full-command-exit.json`為Scope355–358／TestExit0／FreezeExit0；原`source-freeze.json`的 **2,053來源／文件＋3DLL，共2,056份SHA-256零漂移**（`freeze-verification.json`：Checked2056／Drift0）。原full與原freeze證據保留，核驗完成後才解除凍結，沒有重建freeze追認結果。baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`），不是由方法／作者案例數推估。

**baseline／HTML／CLI及文件後續已驗證**：baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`）；更新後文件／coverage重建`r358-final-docs.trx` **78/78通過、0失敗／略過**，runner **12秒**，TRX2026-11-17 14:50:00.1967118–14:50:16.5819422（UTC+08:00，16.385秒）。`postfull-root-result.json`記兩官方generators check均0，正式HTML來源SHA前綴`5e6c0abd95d0`。CLI建置 **0錯誤／13個既有CS0162警告**（`r358-cli-build.log`，1分18.70秒）；六項離線smoke依序退出 **0／1／2／2／0／0**（`r358-cli-smoke.json`），help、非法sensor選項、缺目錄不可比、壞包SnapshotReadError、兩份合成可比快照及Redfish序號遮蔽／來源／privacy metadata內容斷言均按root串行腳本完成。Redfish電力scope補充另三項退出 **0／0／0**（`docs-high/cli-power-scope-result.json`）：20／200雙Chassis正反序均保兩筆而scalar null、scopeKey集合相同；單一320保scalar320；敏感metadata遮蔽及逐筆內容斷言全true。這九項是離線合成與小型本機檔案fixture，不是BMC實機／硬體精準度驗收。

**R358 closure在359–363接續來源下文件守門已實跑**：第一次`obj/audit/r363/r363-closure-docs.trx`為77通過／1失敗，缺陷是五個已套新C#未登記Git。root只對這五個路徑執行`git add -N`，沒有提交；第二次`r363-closure-docs-2.trx`實際**78/78通過、0失敗／略過**，runner14秒，TestExit0；TRX 2026-11-17T17:16:17.7296902+08:00–2026-11-17T17:16:36.1459345+08:00。兩官方generator check均0，正式HTML已重生。這不是再次Round358 full，R358原full／freeze及原失敗TRX不覆寫；本段結果回填後另跑同組守門並以新receipt存證。359–363新full／新freeze仍待驗，baseline保持9,029。

355原85／1→86/86、356原282／1→349/349及prefull66、357原236／2→273/273、355–357聯合full原8,953／3／8,956與2,052檔Drift0均保留；356沒有獨立full／freeze。r351 full8,731綠但Drift2／拒更事故及r354 full8,834／2,033檔Drift0／closure66保留為歷史。方法、key、family與coverage不是案例數或10000完成率。未提交、上傳或發版，沒有GUI／硬體相容／精準度或native硬取消完成宣稱。
