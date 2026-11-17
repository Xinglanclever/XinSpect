# Round350：讀取語意與資源生命週期（2026-11-17）

接續已收口的[Round349](ROUND-349-CPER-AND-COORDINATION-2026-11-17.md)。六組候選已核前置及內容hash後套用，`obj/audit/r350/applied.json`列 **29份映射，27份實質變更**；`WheaEventEnvelopeParser.cs`與`CperMemoryEnvelopeDecoder.cs`兩份為hash未變的前置依賴，不重複算成新修改。套用前影像保留於同目錄`preimage/`，staged稿的未套用標籤屬製備歷史，以applied清單為準。

**現況：已套用，第四次定向348/348通過、0失敗／略過。** 第三次為347通過／1失敗，修正新增測試對SocketName的錯誤期待後重跑通過；前兩次編譯失敗的原因與原log亦保留，不換算成定向案例失敗。最新完整基線仍是r349實跑的 **8,602**；r349全套、66項收口守門及CLI smoke均不覆蓋本輪新改動。完整350＋351聯合回歸仍待r351安全暫存檔候選整合後執行，屆時按實際凍結／受測範圍記錄，不能把本次定向當作r350單輪全套通過。r351／r352目前仍只在obj staged，未套用。本輪未提交、上傳或發版，不增加候選完成數或硬體支援宣稱。

## 實作、正式入口與邊界

| 組別 | 已套用實作 | 尚未驗證／保留限制 |
|---|---|---|
| 01：磁碟breaker | `DiskSurfaceBackingDevice` lease揭露已核physical index；`DiskSurfaceScanService`三個native入口只觀察真正data read，整個scan latch合格已完成native錯誤，等讀取鏈結束才Report(false)。`StorageProbeBreaker.ShouldSkip`過期清除原子比較key＋原timestamp，避免刪掉新的Trip。 | 沒有native讀盤、取消時序或熱插拔實證；只表示存取路徑錯誤，不證壞媒體。cooldown阻止後續session，當次既有有界重試仍可繼續；metadata預檢不是排他磁碟預約。 |
| 02：參考資料啟動 | `CpuPinoutService`／`DimmReferenceService`／`M2AnalysisService`保留同instance的單次Task；MainViewModel建構不再啟動這三個來源，`StartupSequence.RunAsync`入口明確啟動，standalone公開constructor仍autoDetect。M2及UI組裝測試補來源注入。 | 仍有DriveInfo／SMBIOS／CPUID／Winget及本機狀態等其他VM constructor I/O，不宣稱完全隔離。沒有GUI／report readiness barrier；CPU WMI無新增硬取消，其他來源沿既有WmiGuard語意。 |
| 03：PCIe能力語意 | `PcieNegotiationGap`只以ASPM bits11:10及ClockPM bit18識別能力；排除width bit9、L0s／L1 latency位元。`PcieLinkService`／`PcieLinkDecoder`顯示分別的能力及啟用未知，移除因能力存在便認定降速屬設計／閒置會恢復的結論。 | 能力不等於當前啟用或降速原因。r349的BDF-only parent、TLS角色與segment／PnP限制保留；較寬的width／upstream因果語句不在此窄修正範圍，無真機鏈路fixture。 |
| 05：WHEA候選1／18 | `WheaMemoryRecordsReader`query及注入reader共用候選清單新增1／18；未知版本仍留下候選事件及明確拒解原因，不按Version先濾掉。保留r349有界CPER、flags拒解、事件／節區分帳及全域16列。 | 安裝manifest僅提供具名Length／RawData提取契約，沒有真實payload。EventRecordID不是跨機／log reset的全域身份；ToXml先配置與底層reader無硬取消的限制不變。 |
| 06：DRAM原生拒絕與GUI收尾 | `DramTrafficService.Run`在cancel／live policy之後，先於Motion、affinity、CPUID、bridge及負載回空值與所有權未核原因；移除native adapter，保留r349 fake MeasureCounters交易。初始／progress文案如實，完成／故障／取消均清舊值與原因，finally清IsBusy；不再把不量測入口列入quiet工作。 | 正式原生PMU通路在source上已封鎖，相關注入定向已通過，沒有native PMU量測證據；這是支援限制，不是獲得PMU所有權。GUI用未支援文字與em dash，沒有typed NotSupported／nullable HardwareFact輸出宣稱。 |
| 07：超頻初始化生命週期 | `OverclockService`在同engine slot中持有factory、boot、VRM factory／Detect及本地cleanup，generation撤銷後不採納或發布晚到成功。prior／未採納資源在awaited worker處置，cleanup await後立即複查generation；optional boot preference失敗保留診斷並繼續初始化。 | service內序列化不是跨程序SDK／硬體所有權。等待實際init完成不等於強制取消；真VRM非null偵測／清理時序、IPC及平台相容性未證。 |

### 磁碟錯誤分類與資源範圍

GUI HealthView及CLI使用`ScanProfilesAsync`；`Scan`／`ScanAsync`仍為公開入口，三條資料讀取路徑均接同一observation。僅精確`HRESULT_FROM_WIN32`且code為 **21／23／27／30／31／121／1117／1167／1460** 的IOException可latch。配置87、權限5、abort995、已取消token競態、generic managed IOException、metadata故障、幾何或短讀觀察不Trip；成功不呼叫Report(true)，不清除另一session故障。重試恢復不抹掉已latch故障。

awaited read完成後才離開observation及backing handle／對齊buffer的scope，未加WhenAny逾時或提前釋放。physical index沿既有單extent及descriptor核對取得，不由drive letter猜測；index可能在hotplug後重用，預檢也非原子身份鎖。compare-remove只針對觀察timestamp；不宣稱在人工混用時域或任意相同舊值下防所有ABA。

### 單次Task與UI發布

三個參考服務各以instance lock快取一次Task，成功或fault都沿用，不因reinitialize重試；catalog仍立即可用，正式啟動不依賴頁面Loaded。既有頁面及ReportService消費相同VM instance和集合。

M2 Task完成僅表示collection完成且publisher同步返回，即**嘗試提交callback**；預設Shell.BeginOnUi可能尚未執行、未接收或丟棄callback。沒有dispatcher結果可供Task背書，空資料及不重試限制保持。注入測試分別設計immediate、queued、dropped及throwing publisher，不將合成即時發布視為WPF實際繫結或報表完成證據。

### 超頻初始化的審查修正

最終套用service hash為`9ba87846673c1f7f28f4a2c2e3729b596d13607eeb1382cb6ac1df79a9664057`，tests為`660bba4755f6fa8039116d7431dd81fcff8647a36cefdd6c7478c095cab97f1d`。`review-capacity04-init-boot-final.txt`對這份內容核對，舊final命名審查僅保留其當時範圍。

初稿在UI context同步Dispose可能卡dispatcher；修為持slot的awaited worker後，補上每次cleanup await後generation複查。另恢復service層optional boot preference的防禦契約：呼叫在worker內檢查generation、捕獲SetBootRestore例外並記錄未套用偏好，診斷失敗亦隔離，然後重查generation才繼續。正式XtuOcEngine本身已catch Send，沒有證明一般XTU IPC失敗會向外拋出，也沒有實機throw重現；throwing engine防禦契約的注入回歸已納入本輪通過定向，仍不背書真實IPC或VRM生命週期。

## 來源、驗證帳與後續順序

- 套用：`obj/audit/r350/applied.json`共29映射／27實質變檔；各manifest／NOTES／REVIEW與獨立review-capacity01／02／04／05／06保存靜態核對及修稿歷史。WHEA parser及CPER decoder兩個不變依賴不冒稱新功能。
- 磁碟分類：本機SDK10.0.26100.0 shared/winerror.h及既有RandomAccess生命週期。PCIe：SDK10.0.28000.0 km/ntddk.h的PCI_EXPRESS_LINK_CAPABILITIES_REGISTER。WHEA：r349 templates.json固定hash及SDK CPER欄位，不替代真實事件。
- DRAM：source追蹤證明原GUI通路可達未預約PMU；Intel／Microsoft來源摘錄見`capacity06-dram-native-review.txt`及附屬證據。政策、零baseline與LP affinity均不是PMU預約；本輪以封鎖未核能力收口，不啟動native量測求證。
- **首次編譯失敗**：`obj/audit/r350/r350-target.log`中`ReferenceDataStartupTests`缺少`System.IO`，共7個編譯錯誤，未進入測試、沒有TRX。新增`WheaEnvelopeSyntheticTests`的Assert.Single／Where亦觸發xUnit2031 warning，後續改用predicate overload，未弱化斷言。
- **第二次編譯失敗**：補using時首次patch誤置於檔尾，`r350-target-2.log`記錄CS1529；同樣沒有TRX，不記為定向案例失敗。using已移至namespace之前。
- **第三次定向347通過／1失敗／0略過**：`obj/audit/r350/r350-target-3.trx`記錄348個已執行案例。唯一失敗為`ReferenceDataStartupTests.獨立服務自動啟動仍沿用同一個once工作`將`SocketName`錯誤期待為`AM5`，實際顯示為`AM5 (LGA 1718)`；保留原TRX，不列為通過。修正為`DetectedSocket == "AM5"`與`SocketName == "AM5 (LGA 1718)"`兩條精確斷言，沒有放寬或移除檢查。
- **第四次定向348/348通過、0失敗／略過**：`obj/audit/r350/r350-target-4.trx`；TRX起訖2026-11-17 04:44:20.7240800至04:44:40.8492101（UTC+08:00，20.125秒），runner測試耗時16秒，兩者範圍分列。此結果只覆蓋本次348項定向，不代表完整350＋351聯合回歸、原生硬體或跨平台相容性已驗證。
- **完整回歸待聯合範圍**：安排至r351安全temp候選合併後，凍結最終來源／文件／受測DLL，再按實際聯合scope記結果；沒有r350獨立全套證據，不沿用r349的8,602綠燈。完整實跑前基線保持8,602。
- 本輪文件／HTML／生成器／追蹤、CLI建置／smoke及限定硬體證據均待相應結果。沒有提交、上傳或發版。
- 不能概括全套沒有磁碟／GPU I/O：既有StorageGpuPipelineServiceTests含temp fixture寫入及小GPU smoke。本輪未額外手動啟動SPD／CPU-Z／PMU或native磁碟benchmark；未來新增測試的I/O範圍須逐項如實記錄。

## Round351／352（目前僅obj staged）

Round351安全temp候選涵蓋DiskBench及DeepBench等既有暫存檔所有權：避免固定檔名覆寫／清理他人檔案，使用CreateNew、同一owned handle及DeleteOnClose等窄修；不能因候選存在便聲稱正式資料安全缺口已修。取消接線已改4,096-byte test fixture，小fixture仍可能有實際檔案I/O，不冒稱全隔離。其他r351候選包括最後合規證據snapshot與企業條件核心；各manifest前置須在本輪套用後重新核對，尤其StartupSequence的交疊變更。

Round352有WindowsMultiDomainEngine worker取消／例外／drain候選，依賴r351 owned temporary file；仍未套用或實跑。其設計是stop後等待真worker退出再釋放資源，不能宣稱同步I/O硬取消。這些後續候選均不受r349全套或本輪348項定向覆蓋，不新增完成數。
