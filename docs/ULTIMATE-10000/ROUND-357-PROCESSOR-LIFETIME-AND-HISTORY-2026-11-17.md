# Round357：Processor Generic、worker所有權與history讀失敗保全（2026-11-17）

**最新聯合驗證（355–358）**：9,029/9,029通過、0失敗／略過，runner8分11秒；2,053來源／文件＋3DLL共2,056份hash零漂移。baseline與後續CLI／HTML／最終文件收口狀態見末節；下文原輪次失敗／定向及355–357full三失敗按歷史受測時點保留。

基本21＋privacy4已核hash套用 **25份獨立targets**（`obj/audit/r357/applied.json`）；原基本21精確另存`applied-base21.json`、基本apply-receipt不覆。首次 **236通過／2失敗／0略過，共238項**，production與fixture修正及privacy補後，第二次`obj/audit/r357/r357-target-2.trx` **273/273通過、0失敗／略過**，runner **5秒**，root session15522 exit0；TRX2026-11-17 12:32:36.5295759–12:32:46.2090693（UTC+08:00，9.679秒）。273含原238與privacy／CLI／HTML／snapshot額外35回歸，不是full。後續355–357聯合full已執行但三項失敗，精確結果見下節；356沒有獨立full／freeze。該次baseline保持r354 **8,834**；2,033份hash零漂移是r354歷史範圍，後續355–358結果及baseline更新狀態見末節。r351原full8,731／Drift2保留，不能改成0。

## WH-0002 Processor Generic bounded safe scalars

02 decoder／facts／synthetic tests依04-final transport／sharedcollector／catalog整合一起使用。官方SDK列192byte layout、ValidBits0、type8／ISA9／error10／operation11／flags12／level13、CPUVersion16、ProcessorId152等；只公開safe scalar欄位，brand、target／requester／responder／instruction address不保留到model／facts／ToString／JSON。unknown enums保raw numeric，不猜Windows logical CPU／socket或實體故障位置。

獨審找到初版CPUVersion全64bits在聲稱NativeModelId unsupported時仍帶出highDWORD的矛盾。最終拒bit13及unknown validity高bits；只有ProcessorType與CPUVersion均valid、type為x86/x64時出legacy low32 raw，IPF／ARM／unknown／type-invalid均不出CPUVersion，不對所有架構盲截low32。SDK中NativeModelId是未實作語義，不能稱欄位不存在。

同一accepted WHEA read新增ProcessorGeneric GUID typed slices與composite facts，無第二EventLog query；沿既有單WHEA publication gate，不新增provider／lease／timeout。range-valid short processor payload僅拒該decoder，不拒合法memory／PCIe；memory compatibility view保留。record／section severity與XML EventLevel分列，saved timestamp與collect time不同；compound event＋descriptor去重與16list cap，incomplete／plain injected lists不出Present完整總數，safe reason不抄arbitrary injected text。

registry新增兩static keys／一dynamic family，FamilyCap70→71、coverage475→477有explicit single-source理由，沒有新豁免／捏造第二來源。來源候選已隨基本21份套用，公開生成同步結果另待root真實證據。`reviews/capacity07/processor-generic-final-review.txt`核final14及SDK／官方Learn，記錄CPUVersion發現已修；19項synthetic作者規劃（13 theory rows＋6 Fact）已隨首次238範圍編譯執行，首次總結果236／2、第二次實際該class19/19通過，不由作者規劃推分組數，更不是實際RawData／OS provider／hardware相容或CPU診斷證據。

## 08 Throughput／Boost啟動及清理生命週期

原engine在取得per-instance runLock後、try/finally前做fallible unmanaged allocation及gate construction，例外可漏Release、漏Free；原worker先加入list再Start，若Start失敗cleanup對unstarted Thread.Join的ThreadStateException可蓋原錯並跳後續資源釋放。這是來源控制流程問題，未真誘發OOM／OS thread exhaustion；原正常成功／取消drain不被泛稱全壞。

候選internal helper只供這兩engine：取得lock後所有fallible setup進try，預先配置tracking array，只有Start成功才記owned thread；Stop並真Join所有started worker後才dispose gate／free buffer／release lock。Join interruption記住並重試到退出再拋原interruption；cleanup錯分別收集，單primary保物件／stack，有多錯時primary第一個aggregate，不把fault變success。若非interruption Join失敗而thread仍活，保留shared gate／buffer／lock，不能以timeout當exit或釋放worker仍可能用的資源；這是exceptional failclosed，不是恢復保證。

Boost PulseGate constructor materialize失敗時處置已建wait handles。355 volatile checksum及2048 arithmetic payload不變，setup helper／closure成本不聲稱整次allocation／schedule等價。payload worker自己的原始錯誤行為、catastrophic runtime failure及diagnostic allocation失敗不在窄修範圍，沒有硬native取消。

author曾取官方.NET10 Thread.Start／Join／AllocHGlobal API來源，contract不等此機重現。`staged/capacity08/manifest.json`列四份已套基本範圍，test7methods／11expanded cases作者規劃調正式helper，用fake allocation／free callback及managed gate-only threads，不跑arithmetic／affinity／power API；已納入首次238定向，第二次實際該class11/11通過，不能泛用既有broad engine filter。獨審`reviews/capacity06/review-capacity08-final.txt`結果依原報告列，不用靜態hash取代runtime證據。

## 09 DeepBench history未讀可信基底時拒覆寫

原Save經LoadRecent(100)，遇JSON／IO錯只得empty與LastLoadError，接著AtomicWrite可把不可讀／corrupt歷史換成單筆新record。候選共用ReadRecords：只有FileNotFound／DirectoryNotFound是known empty；Save在JSON／IO／access／security失敗先記LastLoadError並throw IOException含原inner，早於CreateDirectory／serialize／AtomicWrite。LoadRecent維持非拋出empty projection＋LastLoadError；不自動刪除／quarantine／修復原檔。

root必須array、null record拒絕、>100elements在record deserialize前拒，檢查包含平常display20以外的tail；有效append／session replace／newest100保留。這不是完整semantic schema validator，例如`[{}]`仍可能deserialize缺Results／Insights，HistoryRow.From既有dereference可失敗；input ReadAllText／JsonDocument及每record graph也無byte／memory／latency硬上限。 這是357受測時點限制；358已套結構驗證及64MiB實際bytes上限，後續355–358實跑結果見末節，不以357結果背書。

沒有跨instance read-modify-write lock／CAS，valid concurrent saves或missing/read-to-write replacement race仍lastwriterwins；AtomicWrite完整rename不等lossless merge，也未套用356 SnapshotSave owner。Save錯可阻止orchestrator finally交回已量測record，UI既有泛稱startup failure／cards行為不是本窄修保留結果承諾；generic IOException到VM.Errors，原cause仍在inner／LastLoadError，CLI CurrentRecord absent走既有exit1，這是來源路徑檢視未做UI驗收。

`reviews/capacity05/review-deepbench-history-preservation.txt`核兩份最初候選，當時窄範圍未見blocking finding，但首次實測仍找到下述production例外漏接，不把靜態意見當執行成功。10個planned cases用自有GUID tempdirs及synthetic records，write仍真filesystem，已納入首次238定向並有兩項失敗；兩項修正後第二次實際該class10/10通過。corrupt／null／object／nullrow、IO／denied、missing parent、valid100與tail等規劃不轉換成通過數。Redfish parked review不在本候選範圍。

## 首次236／2、history修正與privacy已套補修驗證

`obj/audit/r357/r357-target.trx`首次 **236通過／2失敗／0略過，共238**，runner **5秒**；`target-result.json`保存完整counters／failures。TRX2026-11-17 09:29:23.0332947–09:29:32.1933576（UTC+08:00，9.160秒）。原log／TRX保留，不改為成功。

一項是真production缺陷：`InvalidDataException`繼承SystemException，並非IOException；原shared IsReadFailure未列它，>100record的ReadRecords拒絕因而不能按契約讓LoadRecent非拋出顯示error，Save亦未包成IOException／記LastLoadError。雖仍未寫原檔，錯誤契約不符，不能只稱fixture錯。root已在共用filter加`or InvalidDataException`，加LoadRecent over-budget回空＋error斷言，Save保精確inner及原bytes／no-temp斷言。

另一項是fixture錯用`IsType<JsonException>`拒絕合法subtype `JsonReaderException`；改`IsAssignableFrom<JsonException>`核例外contract，仍保original cause／byte preservation，沒有放過無cause或覆寫。`history-exception-repair.json`保存兩檔initial applied與修後SHA：store `11dc6a89…`→`a87d49cceee532e2d951673035bbd021fc154f7fb14fdbe7e75cc4040b813fe9`；test `8471f352…`→`2227d38aeeb9db77e9460d11ff72c54a2e4cb00aeb1a1915fa20c00236c26d0b`。applied初始帳不覆寫，修後已經第二次273項定向通過，原修前／後帳均保留。

04另沿真端到端匯出發現：Sensitive PCIe serial雖model／snapshot已有防護，generic CLI FactJson／HTML未按Sensitive去值，仍可能洩露。4份supplement已按`staged/capacity04-privacy/applied.json`套用（CliService、HtmlReportService、EvidenceLabView checkbox傳遞、WheaExportPrivacyTests），root `apply-privacy.py` exit0；`reviews/capacity07/privacy-supplement-review.txt` SHA256 `1AEB40AE05900EF6BACB283C4B136C2B393141378308771C11936A0032A15FDA`核凍結四份、無剩餘blocking finding，該報告本身仍是靜態審查，第二次執行結果由273項TRX另證。

正式改動使共用CLI FactJson對Sensitive Value用既有RedactedValue、NumericValue清null、非null UnavailableReason亦遮蔽；保availability與Sensitive及既有key／name／source／trust metadata。evidence JSON、stdin query batch、flash comparison、evidence-pack等共用callsite受此helper保護，沒有新CLI preserve opt-in；其他bespoke payload不因此全被認證。HTML Build預設includeSensitive=false，先複製遮value／reason後才做CSS／ValueText，防reason／classification繞路；explicit true保原raw presentation及HTML escaping，integrity計最終document。GUI只按既有預設未勾的IncludeSensitive checkbox傳明確opt-in，未做真對話框／accessibility操作。

四個新增export fixture規劃走actual accepted injected PCIe reader→shared service→CLI evidence／stdin、敏感numeric／reason、HTML預設／explicit preserve及integrity、snapshot default及public HTML escaping。注入阻止EventLog native query，不是實際provider讀取；第二次實際`WheaExportPrivacyTests` **4/4通過**，整輪273按TRX記錄，不從四case計畫推算總數。初次236／2與history修前後hash、privacy獨立收據及原基本21帳並存。

## 第二次定向、公開規模與post-apply純空白

`obj/audit/r357/r357-target-2.trx` **273/273通過、0失敗／略過**，runner **5秒**，root session15522 exit0；TRX2026-11-17 12:32:36.5295759–12:32:46.2090693（UTC+08:00，9.679秒）。`target-2-result.json`逐class實值為ProcessorGeneric19/19、WorkerLifetime11/11、HistoryPreservation10/10、ExportPrivacy4/4；其餘CLI／HTML／snapshot及原WHEA／source回歸亦按TRX保留，沒有將273當full。

第二定向後`git diff --check`只指出Processor Generic facts一個多餘EOF空行；root僅移除terminal CRLF，`processor-whitespace-fix.json`保存initial SHA `9d5f9163e4cf3bc33e896069fd3faabc0cf0263bd40ecb61c89148ed7aef9120`與修後 `9fdda9cf5c5db022e60927c1e19c576fded534be04de3ec50fe2bcea070d6672`。這是純空白、無邏輯或案例數改動；initial applied與原TRX不覆寫，百科H依既有generator規則取77，不手改為76。

來源規模實核 **700服務／1,685 C#／699測試檔／7,116 Fact／389 Theory／7,505方法**，key1,212／domain161／family71／coverage477；方法數、key與coverage都不是full案例總數。`obj/audit/r357/reviews/capacity05/review-public-sync-final.txt`獨立核四份公開文件候選及來源集合／AST／catalog／H行數，無blocking mismatch；歷史Part11與runtime baseline8,834原值保留。四公開文件及交接文件已由root套用並形成此次full的凍結輸入；前置`r357-pre-full-docs.trx`為78/78通過、0失敗／略過（runner18秒，含66基本文件與12coverage），`r357-handoff-docs.trx`為66/66通過、0失敗／略過（runner16秒）。這是357full前文件證據；full三項失敗及2,052份零漂移另列下節，公開來源規模不是案例通過總數。358來源變更後須重新實核及生成，不沿用此規模或前置docs背書新來源。

## 舊輪驗證與下一步

355已9targets，85／1因Coherence fixture排程假設修為等真正gate signal，之後86/86，原證據保留。356已19unique targets，282／1的README coverage473／475失敗保留，四公開文件同步後283定向＋66docs合計349/349（runner24秒）；EOF純空白post-apply及domains／HTML sourceSHA9306a74d918f另記。新`obj/audit/r356/r356-pre-full-docs.trx` **66/66通過、0失敗／略過**，runner15秒，root session24791 exit0；TRX2026-11-17 09:20:45.1510291–09:21:03.9186691（UTC+08:00，18.768秒）。這是356定向／文件證據，沒有啟動356獨立full或freeze。

## 355–357聯合full失敗及358續接

**355–357聯合full歷史失敗（原證據保留）**：`obj/audit/r357/r357-full.trx`實際 **8,953通過／3失敗／0略過，共8,956**，runner **9分38秒**；TRX2026-11-17 12:56:13.1700342–13:05:55.4420501（UTC+08:00，9分42.272秒）。`full-command-exit.json`為TestExit1／FreezeExit0，`freeze-verification.json`為 **Checked2,052／Drift0**；凍結零漂移不能使三個失敗變通過。`final-failure-result.json`保留完整counters／times／失敗訊息；該次baseline維持r354 **8,834**，沒有更新為失敗總數8,956。三項為DecoderHardening精確registry名冊少ProcessorGeneric／PCIe兩解碼器、WiringGuard對同檔aggregator引用未被辨識的PCIe服務、KeyCatalogPolicy的PCIe家族成員定義契約。R358已按applied帳套用精確補名、獨立WheaErrorFactsService檔案及中文成員定義；後續355–358聯合full實際9,029/9,029通過，原三項失敗不覆寫；不得新增豁免、降低斷言或將修正預填成成功。

本節前段保留355–357歷史失敗。後續355–358聯合full已實際驗證，最新結果如下；不以新綠燈改寫舊TRX／freeze。

既有全套仍含Windows Boost短80／120／120ms pulse、Throughput20ms warmup＋20mswindow、CallNtPowerInformation取樣、小型真temp I/O及最小GPU等路徑，不能概括full無CPU負載／無native或無I/O。未額外手動SPD／CPU-Z／PMU／native磁碟benchmark；全套workload不等代表性硬體精準度／相容性驗收。

## 355–358後續聯合驗證

**355–358聯合full及原凍結已驗證**：`obj/audit/r358/r358-full.trx`實際 **9,029/9,029通過、0失敗／略過**，runner **8分11秒**；TRX2026-11-17 14:27:45.5448893–14:36:00.7414577（UTC+08:00，8分15.197秒）。`full-command-exit.json`為Scope355–358／TestExit0／FreezeExit0；原`source-freeze.json`的 **2,053來源／文件＋3DLL，共2,056份SHA-256零漂移**（`freeze-verification.json`：Checked2056／Drift0）。原full與原freeze證據保留，核驗完成後才解除凍結，沒有重建freeze追認結果。baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`），不是由方法／作者案例數推估。

**baseline／HTML／CLI及文件後續已驗證**：baseline已按實跑由 **8,834更新9,029**（`obj/audit/r358/baseline-update.json`）；更新後文件／coverage重建`r358-final-docs.trx` **78/78通過、0失敗／略過**，runner **12秒**，TRX2026-11-17 14:50:00.1967118–14:50:16.5819422（UTC+08:00，16.385秒）。`postfull-root-result.json`記兩官方generators check均0，正式HTML來源SHA前綴`5e6c0abd95d0`。CLI建置 **0錯誤／13個既有CS0162警告**（`r358-cli-build.log`，1分18.70秒）；六項離線smoke依序退出 **0／1／2／2／0／0**（`r358-cli-smoke.json`），help、非法sensor選項、缺目錄不可比、壞包SnapshotReadError、兩份合成可比快照及Redfish序號遮蔽／來源／privacy metadata內容斷言均按root串行腳本完成。Redfish電力scope補充另三項退出 **0／0／0**（`docs-high/cli-power-scope-result.json`）：20／200雙Chassis正反序均保兩筆而scalar null、scopeKey集合相同；單一320保scalar320；敏感metadata遮蔽及逐筆內容斷言全true。這九項是離線合成與小型本機檔案fixture，不是BMC實機／硬體精準度驗收。

**R358 closure在359–363接續來源下文件守門已實跑**：第一次`obj/audit/r363/r363-closure-docs.trx`為77通過／1失敗，缺陷是五個已套新C#未登記Git。root只對這五個路徑執行`git add -N`，沒有提交；第二次`r363-closure-docs-2.trx`實際**78/78通過、0失敗／略過**，runner14秒，TestExit0；TRX 2026-11-17T17:16:17.7296902+08:00–2026-11-17T17:16:36.1459345+08:00。兩官方generator check均0，正式HTML已重生。這不是再次Round358 full，R358原full／freeze及原失敗TRX不覆寫；本段結果回填後另跑同組守門並以新receipt存證。359–363新full／新freeze仍待驗，baseline保持9,029。

355原85／1→86/86、356原282／1→349/349及prefull66、357原236／2→273/273、355–357聯合full原8,953／3／8,956與2,052檔Drift0均保留；356沒有獨立full／freeze。r351 full8,731綠但Drift2／拒更事故及r354 full8,834／2,033檔Drift0／closure66保留為歷史。方法、key、family與coverage不是案例數或10000完成率。未提交、上傳或發版，沒有GUI／硬體相容／精準度或native硬取消完成宣稱。
