# Round 349：CPER 外層與硬體操作協調（2026-11-17）

接續 [Round 348](ROUND-348-HARDWARE-TRANSACTIONS-2026-11-17.md)及[實作佇列](IMPLEMENTATION-QUEUE-2026-11-17.md)。本輪六組完整候選共 **17 份來源／測試檔**已逐份核對原始及候選 SHA-256 後套用，清單在 `obj/audit/r349/applied.json`，套用前影像在同目錄 `preimage/`。暫存檔內的 staged-only 是製備時狀態，套用狀態以 applied 清單為準。

**本輪全套、最後文件守門與 CLI 已收口。** 完整回歸 **8,602/8,602 通過、0 失敗／略過**（`obj/audit/r349/r349-full.trx`），runner 9 分 29 秒；2,006 份來源／文件＋3 DLL 共 2,009 份 SHA-256 零漂移。基線已按實跑同步為 **8,602**，三語 README、百科、PR／CI／CONTRIBUTING 及生成 HTML 已同步；最終重建後文件守門 **66/66 通過、0 失敗／略過**，兩生成器 `--check` 回0。CLI 建置 **0 錯誤／13 個既有 CS0162 警告**，六項離線 smoke 均符合預期退出碼及內容斷言。前兩次定向失敗與原因完整保留；本輪未提交、上傳或發版，不新增候選完成數或硬體支援宣稱。後續 r350 已套用、驗證中，另讀該輪紀錄，本輪結果不覆蓋新改動。

## 實作與生產界線

以下保存r349受測時點；後續r350已套用的DRAM原生拒絕、初始化與query等修正，連同待驗限制另讀[Round350](ROUND-350-READING-AND-LIFECYCLE-2026-11-17.md)。

| 組別／範圍 | 已套用實作 | 仍未取得的證據及限制 |
|---|---|---|
| 01：DRAM PMU 雙故障 | `DramTrafficService.MeasureCounters` 保存 setup／workload／取消的原始例外，嘗試全部六項恢復寫入及五項讀回，再彙總清理故障；成功清理以原例外與堆疊重拋，取消加清理失敗保留兩者。 | 注入定向已通過。恢復 disable 失敗後繼續嘗試不代表已停止計數器；零 baseline 不是排他所有權。本輪仍有 GUI 原生 DRAM PMU 路徑，見下方 Round 350，不可誤寫為全部原生 PMU 已封鎖。 |
| 02：清單容量 WMI | `OperationsInventoryFactsService.CreatePhysicalDiskSizeSearcher` 改用明確的 `ManagementScope`／`ObjectQuery`，修正原本兩字串 overload 的 namespace／query 顛倒。兩 provider 路徑處置 searcher、結果、列舉器與物件；後段失敗回 ReadError，不發布部分比對成功。 | 新測試檢查正式 factory 的 Scope／Query 及未連線狀態；不等於執行 WMI。COM 故障清理、硬取消及 provider 原始 Size 語意未經本輪實機確認；相同容量仍不能唯一配對裝置。 |
| 03：PCIe 父橋及欄位語意 | `PcieLinkService` 同次掃描保存 BDF header、type-1 bus routing 及能力；新增 `PcieParentResolver`，使用一次預備索引解析直接父橋，並修正以下四項缺陷。 | 只在同次 BDF-only accessor 範圍內成立；segment 與 PnP 對應未知，未推 CPU／PCH 來源。設定空間讀取非原子快照，無代表性真 parent fixture 或平台驗證。 |
| 04：超頻 apply／recovery 協調 | `OverclockService` 共用 generation admission 與 engine semaphore；recovery 撤銷舊 generation、等待已派發操作、拒絕排隊及新寫入，再持有同一 slot 完整恢復。telemetry、boot-restore preference、一般 mutation、恢復及 engine／VRM 處置共用序列。 | 僅 service 內協調，不是跨程序 SDK／硬體所有權。XtuOcEngine 仍依既有 IPC timeout／MarkDead，沒有核心取消保證。原有 InitializeAsync 在 Dispose 後恢復並採納新 engine 的競態尚未修，本輪 disposed generation 不覆蓋初始化。 |
| 05：WHEA／MM-0059、WH-0003 | 新增 `WheaEventEnvelopeParser` 與 `CperMemoryEnvelopeDecoder`，正式 `ReadEvents → ToXml → envelope → WheaMemoryReadResult → EvidenceLabService.LoadWheaMemory → Facts` 接線；桌面與 CLI 共用 EvidenceCollection。保留 flags、拒解原因及候選事件／記憶體節區分帳。 | 只有安裝 manifest 與 SDK 合成 fixture，現存事件查詢為 0 筆；沒有真實 provider CPER payload、Binary CLR 形狀、DIMM 身份或實際錯誤事件相容性證據。EventLogReader 建構、ToXml 配置及底層 XML 工作沒有硬取消保證。 |
| 08：StorageWMI 生命週期 | `StorageReliabilityWmiReadGate` 按本機 storagewmi.mof 註冊身份跨 source 實例序列化；worker 持 lease 至正式查詢及清理真正完成，呼叫端 timeout 不釋放。正式 `WmiQuery.Read` 使用共用有界 cleanup，保存原讀取故障與清理故障。 | 只涵蓋本程序可靠性來源，不涵蓋其他 DiskInfo／StorageWMI 查詢、其他程序或硬體身份。卡住的 read／Dispose 仍占 lease；沒有宣稱取消 COM。共享同一可變 source／wrapper 的並發狀態 API 限制仍在。 |

### PCIe 四項修正

1. **父橋不再按鄰近 BDF 猜測**：要求唯一 type-1 bridge、primary 等於橋自身 bus、secondary 等於 child bus、subordinate 範圍合法，且 parent port role 為 4／6、能力可核；範圍包含本身不足以證明直接相連。缺 header／routing、重複候選或缺父橋均保留未知，不再做第二輪全 BDF 探測。
2. **保留值及矛盾不作歸因**：寬度只接受 1／2／4／8／12／16／32，speed code 只接受 1–6；目前子鏈路超過自身或已解析父橋能力時，gap 為 Unknown。
3. **能力版本欄位修正**：PCIe capability DWORD 的 revision 取高半字（+0x02）的能力欄位，不再誤取低半字 CapID 的低 nibble；決定是否存在 Link Control 2。
4. **Target Link Speed 範圍修正**：保留原始 TargetSpeed／HasControl2，但 operational gap 僅接受本身 downstream port role 4／6 的 TLS；endpoint／legacy endpoint／switch upstream role 0／1／5 不套用 compliance-test 欄位，也不推定 parent TLS。

### 超頻協調與恢復

正常 admission 涵蓋單 knob、planner、Vcore fanout、ApplyAll、profile 共用路徑、Discard、手動 defaults、SpeedOptimizer 及 LLC 選單觸發寫入。舊 generation 的 continuation 不能發布成功或重啟 watchdog；已派發操作等待實際結束，沒有呼叫端逾時便提早釋放 slot。

恢復嘗試所有適用的 saved items；不可寫／缺 baseline 仍計入覆蓋不足，partial／empty／defaults 失敗保持 critical。Vcore Active 保留讀值，不把目標當 readback。觀察者例外經 Diag 記錄並隔離，不能阻斷 admission 收尾或 recovery TCS 完成；VRM 處置移入同一序列，等待 LLC 真正結束。25 項注入協調案例已於第三次定向通過；engine disposal 有注入斷言，VRM 自有 sealed bridge 的真實處置次序尚無新的執行證據。

### CPER 格式、flags 與計數

事件 XML 只接受精確 namespace／provider GUID／已核 ID-version 及唯一具名 scalar `Length`／`RawData`；DTD／外部解析停用，hex 必須無分隔且長度完全吻合。CPER 接受 128 bytes 至 1 MiB、1–64 descriptors，核 signature、revision、宣告長度、severity、descriptor revision、所有節區邊界與互不重疊；非記憶體 descriptor 也必須通過驗證。只提取 memory GUID 節區，不以 EventID 分類記憶體錯誤。

HeaderFlags（offset 104）保留於解析結果；**Simulated bit 2** 不能進入實體故障證據，**SectionsTruncated bit 6** 不能冒充完整節區，任一旗標存在即拒絕解碼並保留具體原因。事件列舉完整性與 payload 完整性分別保留。混合有效／拒解資料仍揭露最多四種相異原因，每種去控制字元並限 240 字元；有效但無 memory section 也與拒解分帳。

summary 的候選事件數沿用原始輸入，另列有效 memory section 數；所有節區先參與型別／位址／module 計數，再受全域 16 列顯示上限約束。RecordSeverity、SectionSeverity、memory ErrorType 不混用。生產動態鍵以 event record ID＋descriptor index 識別，舊 section 注入鍵保持；Envelope 為 JsonIgnore，不把 XML 或完整 RawData 放進公開事實。

生產查詢仍為 **17／19／23／46／47**，保留既有固定事件窗、64 候選及 4,096 掃描預算；parser 能接受 1／18 的安裝模板不代表查詢已擴大。安裝 provider version 0 的具名 RawData 契約不背書真實內容；17/version 1 沒有該模板，未知版本明確拒解。ToXml 先配置後才檢查 managed 字元上限，截止只能拒絕晚到結果，不能保證底層工作已取消。

### 正式 StorageWMI cleanup

正式 ReadCom 的七層資源是 searcher、disk collection、disk enumerator、disk、related collection、related enumerator、counter，均進同一 `cleanup.Use`，不只在測試替身外包一層。原始讀取故障以 InnerException 保留；清理失敗不取代原錯誤，只有清理失敗時停止後續新交易，已取得的外層資源仍逐層嘗試處置。最多七筆清理診斷、總摘要 4,096 字元。

新增 fake IDisposable 回歸走正式 WmiQuery.Read helper，並覆蓋讀取與清理同時失敗、lease 直到清理結束才釋放等情境。StorageReliabilityFactsTests 的27項案例已於第三次定向通過；這不等於 native COM 已驗證，實際釋放、卡住及取消行為仍需另取證。

## 來源與驗證帳

- 套用與前像：`obj/audit/r349/applied.json`／`preimage/`；逐組 staged manifest、review-capacity01／02／04／05／08 及 capacity03 NOTES 保存審查過程。初稿審查發現不當作最終仍未修：04 observer／VRM 次序、05 flags／原因／舊斷言、08 雙故障與正式清理均已反映於最新套用稿，執行驗證另計。
- WMI：本機 System.Management API XML 的 typed constructor／Dispose；provider gate 身份取本機官方 storagewmi.mof 註冊資料，不是 runtime provider PID。
- PCIe：SDK 10.0.28000.0 km/wdm.h 的 PCI_COMMON_HEADER／type-1 bus routing；TLS 角色語意參照 Intel 328898-003 §3.2.47 與 I210 333016 §9.4.6.13，引用及限制保留於 staged NOTES。
- CPER：SDK 10.0.26100.0 um/cper.h 的 header 128／descriptor 72 bytes、欄位 offset／revision／flags，及本機 WHEA provider manifest（`templates.json`／`provider.xml`）；皆非真實 payload 相容性證據。
- 首次序列建置成功，定向 263 通過／8 失敗／0 略過（`obj/audit/r349/r349-target.trx`）；8 項均為新增檔案、方法及服務行數的文件統計未同步，不列為整合綠燈。反射實得 6,950 Fact、341 Theory、7,291 方法；673 測試檔、1,650 C#、693 服務檔，已同步並重生 HTML。
- 第二次不重建定向 321 通過／1 失敗／0 略過（`r349-target-2.trx`）；唯一失敗為 `DocsHtmlTests.內嵌進程式的百科與倉庫檔案逐位元組相同`：DLL 仍嵌入更新前 HTML。此失敗保留，不列為通過；後續真正重建更新資源。
- 第三次重建 **0 錯誤**，定向 **322/322 通過、0 失敗／略過**（`obj/audit/r349/r349-target-3.trx`）。範圍含六組變更的注入／合成回歸及文件、HTML、生成器與來源追蹤守門；TRX 起訖 2026-11-17 04:15:00.215–04:15:21.362（UTC+08:00）。這是本次定向結果，沒有推算完整案例總數或實機支援。
- 完整回歸 **8,602/8,602 通過、0 失敗／略過**（`obj/audit/r349/r349-full.trx`）。TRX 2026-11-17 04:22:17.0678362 至 04:31:49.7709080（UTC+08:00），區間 **9 分 32.703 秒**；runner 回報 **9 分 29 秒**，兩者計時範圍分列。
- 全套完成後核對 **2,006 份來源／文件＋3 份受測 DLL＝2,009 份，SHA-256 漂移0**（`obj/audit/r349/freeze-verification.json`；輸入為 source-freeze.json／assembly-freeze.json）。凍結已解除，之後更新 baseline **8,602**、三語／百科／PR／CI／CONTRIBUTING 及生成 HTML；這些後補修改不冒稱包含於先前freeze。
- 最終重建後文件／HTML／生成器／追蹤守門 **66/66 通過、0 失敗／略過**（`obj/audit/r349/r349-final-docs.trx`）；兩個生成器 `--check` 回0，本次生成HTML SHA-256前綴`a43f43fa7a05`。這是baseline／現在式文件同步後的另次驗證，不與先前freeze範圍混算。
- **CLI 收口完成**：`obj/audit/r349/r349-cli-build.log` 記錄 **0 錯誤／13 個既有 CS0162 警告**，建置耗時 1 分 29.38 秒。`r349-cli-smoke.json` 六項退出碼依序為 help=0、invalid-sensor-option=1、fleet-missing-directory=2、fleet-corrupt=2、fleet-compatible=0、redfish-serial-redaction=0，均符合預期。`cli-smoke.ps1` 同時核 help 含 sensor-crosscheck、缺目錄／壞包 comparable=false（壞包含 SnapshotReadError）、合成可比兩快照 comparable=true 且無診斷，以及序號不外洩、sensitive=true 與來源 GeneratedAtUtc。這些是離線／合成 fixture，不是真實 Fleet 或 BMC 硬體證據，也不沿用 r348 的 CLI 結果。
- **本次全套並非沒有磁碟／GPU I/O**：既有 `StorageGpuPipelineServiceTests` 會寫入及清理暫存檔 fixture，且含最小 GPU 往返 smoke。本輪未額外手動啟動 SPD、CPU-Z、PMU 或 native 磁碟 benchmark；全套通過不等於這些限定硬體驗證完成，也不保證每台機器的 GPU 後端均支援。
- Round 348 已收口：全套 8,526/8,526、文件／生成器／追蹤守門 66/66、CLI 建置 0 errors／13 個既有 warnings、六項離線 smoke；詳細證據讀該輪收口補證，不套用到本輪。

## 後續輪次與驗證範圍

[Round 350](ROUND-350-READING-AND-LIFECYCLE-2026-11-17.md)現已套用 **29 份映射／27 份實質變更**，涵蓋磁碟 breaker、參考服務啟動、PCIe 能力語意、WHEA query、DRAM 原生拒絕及超頻初始化生命週期。該輪前兩次編譯失敗、無TRX；第三次定向347通過／1失敗，修正新增測試對SocketName的錯誤期待為DetectedSocket及完整顯示名稱兩條精確斷言後，第四次 **348/348通過、0失敗／略過**（`obj/audit/r350/r350-target-4.trx`），原失敗紀錄保留。完整350＋351聯合驗證仍待，基線保持8,602。上文的r349實作及剩餘限制是本輪受測時點，後續source修正以r350文件為準；r349全套與CLI均不背書新改動。

Round351／352 目前僅在 obj staged，未套用。完整回歸安排在 r351 安全暫存檔候選整合後，按實際凍結及受測的聯合範圍記錄。Round351 取消接線的 **4,096-byte test fixture** 仍可能有檔案 I/O，不列入本輪已驗範圍，也不據此抹除上述既有全套暫存檔／GPU smoke I/O。本輪未提交、上傳或發版。
