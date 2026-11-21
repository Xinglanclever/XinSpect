# Round351：暫存檔所有權與企業基準（2026-11-17）

接續[Round350](ROUND-350-READING-AND-LIFECYCLE-2026-11-17.md)。四組候選已核前置及內容SHA-256後套用，`obj/audit/r351/applied.json`共 **19份映射**，涵蓋來源、測試及企業基準說明；套用前影像保留於同目錄`preimage/`。新檔已intent-to-add供diff審查，不代表提交。staged文件內的未套用狀態屬製備歷史，現況以applied清單為準。

**現況：350＋351全套8,731/8,731通過、0失敗／略過，但凍結Checked2020／Drift2，尚未完整收口。** 第三次定向475/475及先行文件門檻66/66證據保留，首次CS0619、第二次474／1排序斷言失敗及後補修正均見原帳。baseline-update因漂移拒絕，ProjectTests仍 **8,602**。原freeze-verification與full TRX不覆寫、不重建清單追認；後續352已套7／42定向通過、353已套17／84定向通過、354已套6／23定向通過，待350–354新聯合凍結full，不預估總數。未提交、上傳或發版。

## 四組實作及限制

| 組別／映射數 | 已套用實作與正式入口 | 保留限制 |
|---|---|---|
| 01：共用暫存檔及DeepBench，5份 | 新增`OwnedTemporaryFile`，GUID leaf以CreateNew、ReadWrite、FileShare.None及DeleteOnClose取得一個handle；`StorageGpuPipelineService`與`GauntletMultiDomainService`在工作範圍內持有它，不以檔名重開或finally刪他人檔案，移除Gauntlet共享目錄遞迴刪除。新增lease及清理注入回歸，GPU取消案例改觀察實際per-run路徑。 | 只修列出的GPU pipeline及Gauntlet storage路徑，不代表其他DeepBench固定檔名路徑已修。Gauntlet raw worker例外及忽略Join(2000)結果的生命週期缺口仍在；持有檔案至該worker結束不等於整個engine已drain。同步I/O沒有硬取消，DeleteOnClose僅為OS請求，不是已核實體消失。 |
| 02：最後合規輸入快照，5份 | `EvidenceCollection`在共享來源前開始generation；CLI/public最後完成package步驟，GUI deferred token跨過BaselineLearning及後續awaited深層來源，再於worker完成。`EvidenceLabService`一次發布凍結package、knowledge facts、輸入及compliance；舊generation不能覆寫新結果，同generation不重載。`KnowledgePackService`以同一次load供各消費端，Steps的Clear／Record／Copy共鎖。 | 凍結的是成功列舉的當下已提交evidence-domain projection，不是所有producer同輪完成或整份HardwareSnapshot原子交易。timeout worker仍可能稍後更新live facts；其他來源列表未在Begin全部清除，例如GUI History.Query失敗仍可能保留舊BaselineFacts及其原MeasuredAtUtc。GUI與CLI領域不完全相同，r350參考來源仍沒有readiness barrier。 |
| 06：DiskBench單一owned handle，2份 | `DiskBenchService`每次產生獨立GUID路徑；free-space guard先執行，CreateNew取得一次ReadWrite、None、NoBuffering／WriteThrough／DeleteOnClose handle，四階段沿同handle明確offset執行，同步workload退出後才處置。移除UI／外層按路徑刪除，碰撞或guard失敗不覆寫、也不刪既有檔案。新增七項fake-handle回歸設計。 | 保留原本512MiB負載及4096 alignment／4KiB transfer假設，不新增幾何保證；短讀後offset及random read長度計數等既有疑慮未修。free-space為快照，不是預約；root／reparse／實體volume未pin。fake handle不證明native CreateNew／NoBuffering／DeleteOnClose行為，未啟動實際benchmark。 |
| 09：企業條件核心及v2，7份 | 新增純`EnterpriseBaselineConditionEvaluator`；簽章package驗證來源／期限後共用核心，支援明確1／2且envelope與payload版本一致。v2要求逐字unit及唯一requirementId，允許多控制共用鍵；Exact逐字比較、NumericRange只用有限NumericValue且包含端點。CLI報實際驗證版本／semantics；DeviceCompliance僅新增內部v2純adapter。同步企業說明及回歸候選。 | GUI尚未接signed policy／trusted key選擇及匯入，純adapter不驗簽或期限，不能視為GUI端到端完成。沒有單位換算、語意布林／enum、複合條件、freshness、跨裝置等價或端點證明。簽章只認證政策來源，快照／report仍未簽署；非整機安全分數、不是稽核框架認證。 |

### 暫存檔身份、清理與I/O

CreateNew的精確碰撞是安全失敗，不退回Create或刪除既有路徑；GUID降低碰撞機會，不能代替所有權或安全隔離。同一handle綁定已取得物件，避免close/reopen及替代路徑清理競態；GetFullPath只是字面正規化，不會pin parent reparse或實體volume。DiskBench既有free-space margin與GPU的host-drive空間查詢仍是快照，Gauntlet未新增空間guard。OS、filter或filesystem可以延遲或失敗清理，SafeFileHandle.Dispose不提供已驗刪除完成結果，不應加按路徑刪除fallback。

GPU pipeline保留write／FlushToDisk／完整readback，再await GPU task；使用同一個buffered handle的RandomAccess，與舊close/reopen／File.ReadAllBytes存在API開銷差異，不背書歷史效能可直接相比。取消注入案例使用 **4,096-byte fixture**，仍真的寫、flush、讀及清理檔案，GPU engine為fake；不能稱沒有磁碟I/O。其他既有`StorageGpuPipelineServiceTests`保留各自原負載及小GPU smoke，不作全類無I/O宣稱。DiskBench新增fake-handle測試與實際native工作負載分開記錄。

### 合規凍結的實際邊界

package模型在有界驗證後深拷貝Tables及各Entries為read-only，再驗凍結副本。不可變HardwareFact保留原來源、時間及精確null／空／非空unit。AllFacts在derived projection之前先排除`zr.compliance.*`，凍結入口再濾一次；普通重複鍵仍可見於輸入，但lookup排除歧義鍵，不任選一筆。package載入／projection失敗發布當代ReadError，不沿用舊合規結果或私密例外訊息。Begin只清package／compliance／input，不能宣稱所有舊來源證據均已清空。

finalizer自身的Measure時間列在凍結輸入之後才記錄，所以輸入含先前完成step，不含它自己尚未完成的時間列。凍結後的live AllFacts／export仍可能因晚到來源變動；不宣稱固定整份報告或所有多來源capture為同一交易。GUI進階停用會跳過該deep path，GUI有history來源而CLI沒有；測試內部三參數組裝seam不自行完成package，不能單憑其回歸證明正式入口的完整順序。

### v1相容、v2單位及靜態審查修正

v1 canonical payload保留原欄位、順序、null operands、UTC序列化及key ordinal排序；驗證原始signed bytes，不重建後才驗。v1仍是未綁定unit語意，不自動升為v2。所有版本有意收緊證據資格：僅Present、鍵身份完全一致、來源非空、未遮蔽且信任為Reported／Derived／Measured可比較；Unknown／Speculative、infer.*及zr.compliance.*均不能符合或不符合，回Unverifiable。位元組／簽章相容不代表維持較寬鬆的舊判定。

v2要求unit非null，明確空字串才是無單位；ordinal比較不trim、不換大小寫或換算。審查曾發現CLI把快照null unit補成空字串，可能令未知單位誤符合；最終套用稿已保留null，新增signed CLI save/load案例區分null與明確空字串。此缺陷已靜態關閉，不冒稱尚未修，也不把靜態審查當成執行通過。證據六態與條件三判定分別保留，raw值、期望值及原始失敗原因不複製到詳細report；政策控制ID與鍵本身仍是可見metadata。

## 前置、驗證帳與後續順序

- 套用清單：`obj/audit/r351/applied.json`共19映射；01位於`obj/r351/staged/capacity01/manifest.json`，02／06／09及企業說明manifest位於`obj/audit/r351/staged/`。兩個staging根不是漏套或重複映射。
- `StartupSequence`以02 manifest的`dependencySHA256` **715C5CFC816C62C188B95471464B5B02FF19CDB364EEFA2155F41A0BD9A00BB0**嚴格匹配r350後來源，並非manifest中pre-r350的original。套用helper已按dependency優先核expected key，applied保留真前置；套用後hash為 **1DF70A4009F8430472B39980C759C35A7DEF3548F65DD77F4FB0E0EDC352B7D6**。保留r350參考來源啟動及既有SPD／CPU-Z worker gate。
- 獨立靜態審查：`review-capacity01-capacity06.txt`、`review-capacity02-by-capacity05.txt`及`reviews/capacity03/REVIEW.txt`，後者含CLI null-unit修正、來源及企業說明manifest tie-out。沒有新增blocking finding不等於編譯／測試或native生命週期通過。
- **首次編譯失敗**：`obj/audit/r351/r351-target.log`記錄`OwnedTemporaryFileTests`第70行always-throw lambda被選為已obsolete的`Assert.Throws(Func<Task>)`，觸發CS0619，沒有TRX，不記成定向案例失敗。根代理已明確cast為Action，保留同步unwind與處置檢查；這是套用後單檔修正，applied.json保留初次套用hash，不能冒稱現行測試與初始hash仍相同。
- **第二次定向474通過／1失敗／0略過**：`obj/audit/r351/r351-target-2.trx`共475項已執行。唯一失敗為`EnterpriseBaselineV2Tests.DeviceAdapter_UsesSameCoreButExcludesAmbiguousAndPreviousOutputs`第315行，新增測試誤以為signed canonical規則排序與adapter輸入排序相同。沒有公共輸出順序契約；根代理將雙方結果各按RequirementId ordinal排序後，仍用Equal比較完整record array，原Satisfied／NotSatisfied／Unverifiable counts斷言保留，獨立canonical排序測試不變。沒有改生產判定或以忽略欄位方式放寬檢查；原失敗TRX保留，該測試修正亦與applied初始hash分開記錄。
- **第三次定向475/475通過、0失敗／略過**：`obj/audit/r351/r351-target-3.trx`，runner **26秒**；TRX起訖2026-11-17 04:57:21.0192812至04:57:51.0332293（UTC+08:00，30.014秒），計時範圍分列。此為350＋351相關475項定向，沒有推算新全套總數；r350前兩次compile fail、第三次347／1及第四次348/348證據仍讀原紀錄。
- **凍結前文件門檻66/66通過、0失敗／略過**：`obj/audit/r351/r351-pre-full-docs.trx`，runner **15秒**；重建後兩生成器`--check`均退出0，HTML來源SHA-256前綴 **785384397b47**。根代理已核對並同步695服務、1,659 C#、680測試檔、7,006 Fact／357 Theory／7,363方法，其中方法數由實際反射門檻確認；這些是來源規模，不是測試案例總數。
- **350＋351完整回歸已執行但凍結不合格**：`obj/audit/r351/r351-full.trx` **8,731/8,731通過、0失敗／略過，runner9分22秒**；TRX2026-11-17 05:04:27.5405651–05:13:53.6720597（UTC+08:00，9分26.131秒），計時範圍分列。原`freeze-verification.json` **Checked2020／Drift2**，只列HANDOFF／QUEUE；不得宣稱0drift、不得刪除或覆零。baseline-update拒絕，ProjectTests仍8,602，不把測試綠燈提升成完整凍結收口。
- **凍結事故查因**：source-freeze.json在05:04:23.8880867（UTC+08:00）完成，HANDOFF／QUEUE最後寫入05:04:24.9669808／05:04:24.9691538，較manifest晚約1.079／1.081秒，雖在TRX開始前仍造成hash不符；ROUND351早於freeze寫完且hash一致。最後一次授權文件補寫與freeze建立交錯，不是測試零漂移。報告`obj/audit/r351/capacity10-freeze-drift-analysis-2026-11-17.md`與metadata JSON保存預期／目前hash及mtime；保留原full／freeze，不重新生成舊清單追認。
- **後續範圍**：根代理選擇不重跑相同351full，先整合352／353／354，再按350–354真實聯合範圍重新凍結。r352已套7 targets／42定向通過，r353已套17／84定向通過（含WPF實建／UiSmoke），r354已套6／23定向通過；皆另立輪次證據，不覆蓋本輪歷史失敗、規模或Drift2。
- 本輪未額外手動啟動SPD、CPU-Z、PMU或native磁碟benchmark；既有全套含temp fixture／小GPU I/O，不泛稱無I/O。未提交、上傳或發版；本次補記透過obj候選／manifest交根代理統一套用，所有writer停止後才建立新freeze。
