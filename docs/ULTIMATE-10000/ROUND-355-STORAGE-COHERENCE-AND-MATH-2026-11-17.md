# Round355：storage發布、Coherence worker、SLC數學與throughput資料依賴（2026-11-17）

**最新聯合驗證（355–358）**：9,029/9,029通過、0失敗／略過，runner8分11秒；2,053來源／文件＋3DLL共2,056份hash零漂移。baseline與後續CLI／HTML／最終文件收口狀態見末節；下文原輪次失敗／定向及355–357full三失敗按歷史受測時點保留。

四組02／04-coherence／07-math／08已核hash套用 **9份targets**（`obj/audit/r355/applied.json`）。首次定向 **85通過／1失敗／0略過，共86項**，修正新Coherence fixture的排程假設後，第二次 **86/86通過、0失敗／略過**（`r355-target-2.trx`，runner1秒，exit0）。這是含既有WHEA／source回歸及本輪新增案例的實際窄filter，不是full；355已納入後續355–357聯合full（三項失敗，見下節），不能把作者新增案例計畫或86定向推為全套總數。最新完整合格基線為r354的 **8,834** 與2,033份hash零漂移；r351原full8,731／Drift2另保留。04 audio、06其他設計及09後續候選不在本輪9檔套用範圍。

## StorageReliability單domain發布隔離：02，3筆

正式shared collection的storage step原先經外層Measure watchdog後，遲到worker仍可直接替換storage facts。候選在dispatch前取得獨立storage generation／lease，Read、延遲provider metadata、欄位projection與read-only ownership copy先於local完成；current generation、Finished、token／monotonic deadline與assignment和timeout revoke共用同一lock。舊generation的late success／failure／revoke不再覆寫新domain；commit自身已過deadline會發布timeout ReadError，已完成的timely commit不被延遲timeout推翻，通知放在lock外。

既有collector可能catch Read exception或在Read後看到Available=false並正常return ReadError，wrapper按fact outcome回false讓Measure記failed；正常空provider的NotApplicable仍可timely成功。原有read diagnostics保留，escaping metadata／projection exception才使用type-only fallback，不能泛稱所有來源診斷均已去私密。direct API保持budget0；timeout只撤銷發布權，不停止WMI／native worker、不提前dispose，也不修其他domains或建立全collector原子快照。

只可依`staged/capacity02/manifest.json`的merged354前置SHA與單一replacement recipe整合EvidenceLabService，不能套用舊整檔覆蓋r353來源失敗／importer與r354 WHEA gate。EvidenceCollection同樣要求列出的r354前置；manifest中的歷史current-source hash不授權覆蓋。作者9 Fact加2 Theory rows的11項注入規劃已隨本輪窄filter編譯執行，總結果按86項TRX記錄，不另由方法數推分組通過數。fixture含真等待／釋放／Join的假source，非實際WMI查詢證據。

獨審`reviews/capacity05/review-capacity02-storage-publication.txt`核過七項前置／artifact／candidate hash與單hunk結果，窄範圍未發現blocking defect；這不是執行通過。EvidenceLabService預期CRLF結果SHA為`3FEEC50BD14D654F5F108868F912805CE8A734328F6DD91FB1C35B1A5BABB196`。

## Coherence真worker生命週期：04，2筆

只選`staged/capacity04/manifest-coherence.json`。原production coordinator在ready／finished token wait或部分Thread.Start失敗時，CountdownEvent using scope可早於worker退出而dispose；worker的Signal失敗進catch後再次Signal，可能讓第二個ObjectDisposedException逃出raw Thread。affinity acquisition及ready.Signal亦原在worker try之外。這是來源控制流程可達問題，沒有執行crash重現。

候選將成功started threads納入finally的Set gate及逐一Join，真正drain後才回傳／拋錯，caller events因此活到所有worker結束。pin acquisition／Dispose、gate、workload進同一worker try，finally ready fallback及finished各只signal一次；partial start／coordinator failure也先等已啟動workers。真正worker／coordinator faults先於最後caller取消判定，requested matching-token OCE才合作取消，foreign OCE不冒稱user cancellation；多重真錯聚合。

同步atomic／lock work可能拖延Join與取消，沒有硬截止／強制停止；externally interrupted／aborted coordinator等災難情況未保證。正常操作量與math未改，新bookkeeping成本的時間等價未benchmark。六個新managed gate fixture已納入本輪定向，只調正式start／drain helper，不做affinity或高負載；窄filter排除既有含真4096-operation affinity smoke的class。獨審`reviews/review-capacity04-coherence-capacity08.txt`核source／test hashes，未見新blocking生命週期問題，未認證真硬體行為。

## SLC確認窗及median數學：07，3筆

只選`staged/capacity07/math-manifest.json.txt`，以r353兩份SLC輸出與r351 OwnedTemporaryFile為前置；同目錄其他no-change帳不屬此差異。候選明確定義：cliff candidate嚴格低於第一個global maximum的35%；candidate加後五個sample須完整六點，排序中央兩點平均的median嚴格低於peak45%；post-cliff median仍含candidate起所有剩餘點。五點尾窗不能當六點確認，strict threshold、first tie peak及空paired sentinel維持。

原loop可接受五點、六點median取上中位而非平均；舊even median相加亦可能finite overflow。最終候選用sorted `lower + (upper - lower) / 2`，避免overflow並保留相等正subnormal。獨審曾找到初版half+half在double.Epsilon對會underflow為0，已修公式並加精確回歸，不能省略這次發現／修正。

候選拒絕null／不等長、非finite或負／不遞增time，以及非finite／不正throughput；paired empty仍保原sentinel。direct UI及DeepBench adapter現有catch將異常表為error／PlatformError，不產生假成功。16個新數值案例已納入本輪定向，結果按86項TRX記錄，並非實機精準度或cache大小證據。只改math、definition與flush comment，未改I/O所有權；FlushToDisk API returned也不證明device cache持久性／斷電安全。獨審`review-capacity07-by-capacity01.txt`已核最終三檔hash，所報subnormal問題closed，未見剩餘blocking finding。

## Throughput arithmetic result消費：08，1筆

`staged/capacity08/manifest.json`只替換WindowsThroughputDegradationEngine。原production worker每slice從constant1執行2048 arithmetic迭代，但結果不被讀取或存出，OperationCount獨立增加，existing checksum未受結果更新。這是來源資料流缺陷及JIT優化風險；未執行／反組譯此workload，不能說已證整loop被特定tier刪除或量測膨脹多少。

候選每worker使用自身checksum field，以Volatile.Read承接上slice值、同樣2048迭代後Volatile.Write，再增加operation count，刪未使用的exit sentinel。無新共享checksum鎖；worker objects未cache-line padding，仍可能false sharing。sink read／write及carried state會計入managed work，新舊數值不可假定時間等價；這不是校準ISA retirement count或禁止所有chain優化。原active／inflight、Stop／WaitIdle／Join及sample math保留，partial start、native allocation及更廣方法學未因此修好。

author曾唯讀取.NET v10.0.0 primary JIT來源，manifest保存來源hash及excerpts；獨審讀cached excerpts，未獨立重抓full upstream，不誇大來源重現層級。沒有新增implementation-mirroring test，既有all-core／power API tests本批未跑。`review-capacity08-by-capacity07.txt`核original／proposed hashes，窄差異未發現新blocking defect；本輪已隨正式專案編譯；定向未執行既有all-core／power API workloads，真throughput／JIT行為仍未驗證。

## 套用與失敗／修正驗證帳

`obj/audit/r355/applied.json`記錄初次9檔套用及其前／後像；EvidenceLabService以r354 merged354為前置、單一recipe加入storage gate，實際套用SHA `3feec50bd14d654f5f108868f912805ce8a734328f6dd91fb1c35b1a5babb196`。四組獨審與靜態限制保留，無blocking finding不替代執行結果。

首次`obj/audit/r355/r355-target.trx`為 **85通過／1失敗／0略過，總86**，runner1秒；TRX2026-11-17 07:50:04.8483385–07:50:10.4078511（UTC+08:00，5.560秒）。唯一失敗是`CoherenceWorkerLifecycleTests.CancellationDrainsWorkersBeforeSharedCountdownCanBeDisposed(duringReady: true)`：fixture錯誤假設發出Cancel時coordinator finally已同步Set gate，因此worker的`gate.Wait(0)`可撞排程race。04複核為測試排程假設，沒有建立production新缺陷。

root只改該fixture為`gate.Wait(Budget)`等真正finally信號，另加兩行解釋comment；仍保pending、drain、thread exit、finished及errors精確斷言，未改production或放寬成不等gate。初始applied的testSHA `551C3AD7A2CB7D9CDC638D627B5B9F0B1B7CE5BD160566362036774595FC4315`仍保原值；post-apply修正後testSHA為`A2650120B4138AD7C6879FF11E0EBA894FEA0F6FDD2FD74BDB49AC1414990946`，兩者不可混稱同一套用後像。

第二次`obj/audit/r355/r355-target-2.trx` **86/86通過、0失敗／略過**，runner **1秒**，root session58488 exit0；TRX2026-11-17 07:55:52.2122098–07:55:57.5010160（UTC+08:00，5.289秒）。原85／1與兩次log／TRX保留，定向包含既有WHEA／source與新fixture，不能宣稱全套或硬體測量完成。

r354已完成350–354 full8,834／2,033份hash零漂移、baseline同步8,834、CLI及兩生成器；closure回填後另以`obj/audit/r354/r354-closure-docs.trx` **66/66通過、0失敗／略過**（runner14秒，exit0）完成文件驗證。該歷史結果不背書本輪新增9檔。355已納入後續355–357聯合full（三項失敗，見下節）；356已19targets，第二次原283定向＋66文件門檻共349/349通過（runner24秒）。三語／百科與來源規模已同步、該時點gen-readme check0；後續純EOF空白修正及domains重生後最新HTML`9306a74d918f`另記ROUND356，prefull-docs已重建66/66通過（r356-pre-full-docs.trx，runner15秒，exit0）。355–357聯合full後續已執行但三項失敗，凍結2,052份Drift0；精確結果見下節，該次baseline8,834保持，不能把定向／文件綠燈當full通過；後續355–358結果見末節。

r351原full8,731與freeze2020檔／Drift2事故、baseline當時拒更及全部失敗證據保留，不能回填成0。沒有GUI／native硬取消／全collector交易或真實throughput準確度宣稱，未提交、上傳或發版。

**355–357聯合full歷史失敗（原證據保留）**：`obj/audit/r357/r357-full.trx`實際 **8,953通過／3失敗／0略過，共8,956**，runner **9分38秒**；TRX2026-11-17 12:56:13.1700342–13:05:55.4420501（UTC+08:00，9分42.272秒）。`full-command-exit.json`為TestExit1／FreezeExit0，`freeze-verification.json`為 **Checked2,052／Drift0**；凍結零漂移不能使三個失敗變通過。`final-failure-result.json`保留完整counters／times／失敗訊息；該次baseline維持r354 **8,834**，沒有更新為失敗總數8,956。三項為DecoderHardening精確registry名冊少ProcessorGeneric／PCIe兩解碼器、WiringGuard對同檔aggregator引用未被辨識的PCIe服務、KeyCatalogPolicy的PCIe家族成員定義契約。R358已按applied帳套用精確補名、獨立WheaErrorFactsService檔案及中文成員定義；後續355–358聯合full實際9,029/9,029通過，原三項失敗不覆寫；不得新增豁免、降低斷言或將修正預填成成功。

本節前段保留355–357歷史失敗。後續355–358聯合full已實際驗證，最新結果如下；不以新綠燈改寫舊TRX／freeze。

## 355–358後續聯合驗證

**355–358聯合full及原凍結已驗證**：`obj/audit/r358/r358-full.trx`實際 **9,029/9,029通過、0失敗／略過**，runner **8分11秒**；TRX2026-11-17 14:27:45.5448893–14:36:00.7414577（UTC+08:00，8分15.197秒）。`full-command-exit.json`為Scope355–358／TestExit0／FreezeExit0；原`source-freeze.json`的 **2,053來源／文件＋3DLL，共2,056份SHA-256零漂移**（`freeze-verification.json`：Checked2056／Drift0）。原full與原freeze證據保留，核驗完成後才解除凍結，沒有重建freeze追認結果。baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`），不是由方法／作者案例數推估。

**baseline／HTML／CLI及文件後續已驗證**：baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`）；更新後文件／coverage重建`r358-final-docs.trx` **78/78通過、0失敗／略過**，runner **12秒**，TRX2026-11-17 14:50:00.1967118–14:50:16.5819422（UTC+08:00，16.385秒）。`postfull-root-result.json`記兩官方generators check均0，正式HTML來源SHA前綴`5e6c0abd95d0`。CLI建置 **0錯誤／13個既有CS0162警告**（`r358-cli-build.log`，1分18.70秒）；六項離線smoke依序退出 **0／1／2／2／0／0**（`r358-cli-smoke.json`），help、非法sensor選項、缺目錄不可比、壞包SnapshotReadError、兩份合成可比快照及Redfish序號遮蔽／來源／privacy metadata內容斷言均按root串行腳本完成。Redfish電力scope補充另三項退出 **0／0／0**（`docs-high/cli-power-scope-result.json`）：20／200雙Chassis正反序均保兩筆而scalar null、scopeKey集合相同；單一320保scalar320；敏感metadata遮蔽及逐筆內容斷言全true。這九項是離線合成與小型本機檔案fixture，不是BMC實機／硬體精準度驗收。

**R358 closure在359–363接續來源下文件守門已實跑**：第一次`obj/audit/r363/r363-closure-docs.trx`為77通過／1失敗，缺陷是五個已套新C#未登記Git。root只對這五個路徑執行`git add -N`，沒有提交；第二次`r363-closure-docs-2.trx`實際**78/78通過、0失敗／略過**，runner14秒，TestExit0；TRX 2026-11-17T17:16:17.7296902+08:00–2026-11-17T17:16:36.1459345+08:00。兩官方generator check均0，正式HTML已重生。這不是再次Round358 full，R358原full／freeze及原失敗TRX不覆寫；本段結果回填後另跑同組守門並以新receipt存證。359–363新full／新freeze仍待驗，baseline保持9,029。

355原85／1→86/86、356原282／1→349/349及prefull66、357原236／2→273/273、355–357聯合full原8,953／3／8,956與2,052檔Drift0均保留；356沒有獨立full／freeze。r351 full8,731綠但Drift2／拒更事故及r354 full8,834／2,033檔Drift0／closure66保留為歷史。方法、key、family與coverage不是案例數或10000完成率。未提交、上傳或發版，沒有GUI／硬體相容／精準度或native硬取消完成宣稱。
