# Round 348：硬體交易、身份與企業判定邊界

日期：2026-11-17（Asia/Singapore）。沿用 Round 346–347 的未提交工作樹。使用者授權持續推進與增加代理，本輪使用十路代理；所有 dotnet 建置及測試由根代理序列執行。未改版號、未提交、推送或發佈。

依 [工程憲法](../ENGINEERING-CONSTITUTION.md)、[公開方法學](../spec/METHODOLOGY.md)與[實作佇列](IMPLEMENTATION-QUEUE-2026-11-17.md)執行。以下是既有能力的可核修補及限制，不是 10,022 項定義的完成率。

## 實作及證據界線

| 候選／範圍 | 本輪修改 | 未證明的部分 |
|---|---|---|
| DIMM／SPD、iMC | `ImcSmbusController` 拒絕 Busy／Go／TSOD 活動、故障鎖存、逾時後不恢復重發舊命令；模糊錯誤為 TransactionFailed，不當空插槽。恢復只允許原始 Go-clear image 且讀回驗證。 | 正式 8086:2085／0x9C proxy 因 firmware／其他程序所有權交接未核而回 NotSupported，未發 PCI 交易；內部假介面驗證不能宣稱實機支援。PCH、SMBIOS/WMI 與受授權的 CPU-Z 備援仍保留。 |
| OS-0167／NUMA | 以 `GetLogicalProcessorInformationEx(RelationNumaNodeEx)` 按實際節點 ID 列舉完整群組；有界解析、非連續 ID、重複／重疊遮罩拒收。接受回傳 relation 1／6，不能把請求 enum 當唯一回傳形式。 | 未列舉作用中處理器不等於已證純記憶體節點；容量及跨節點量測未驗。沒有多群組真機 fixture；舊 OS 不用單群組備援猜完整拓撲。 |
| MM-0059／WH-0003、WHEA | `ErrorTypeHint` 不再從記憶體錯誤類型推論更正狀態、嚴重度、控制器位置或維修處置；修 Extended 欄位說明。 | 生產事件的 CPER 外層仍未接通，Section 仍為 null；Extended 位義及真實 DIMM 身份無 fixture，不硬拆。 |
| TS-0185／0187、磁碟 | async profiles 強制每輪 64 MiB、有限輸入列舉、有效幾何及取消邊界。backing 預檢核單一 extent、實體磁碟號、相符 ATA/SATA descriptor，拒絕 NVMe／RAID／Spaces／未知路徑。查 disk／volume alignment，配置原生對齊緩衝區，拒未對齊短讀及非零 sector offset；87 配置錯誤不當壞塊重試。以原卷重開 async handle，資源持有至讀取確實結束；GUI／CLI 顯示實際 IoMode 與預檢限制。 | metadata 為同步查詢，無硬取消保證；兩次身份快照不是熱變更的原子鎖。嚴格 descriptor 相符及 ReOpenFile 尚待限定硬體實測；DiskSurfaceScan breaker 只有 ShouldSkip，尚無 Trip／Report 閉環。 |
| PMU／Top-down | PMU 每輪、設定與負載前查當前政策；撤銷後只保留恢復。start／end 缺值不當 0；先驗固定計數器能力。執行異常及恢復失敗同時保留，不能丟失第二故障。Top-down 假交易完整保存 selectors／PMC／global，部分寫入失敗亦恢復及讀回，清理失敗不發布數值。 | 固定計數器原生 adapter 及 Top-down 正式編程因 CPU／SMT 所有權、釘選或完整位寬寫回未驗而在 bridge 前封鎖；原生 PMU bridge 引用洩漏亦由移除未驗證通路收口。這是支援限制，非新增實機能力。政策檢查與硬體執行間仍有時序窗口。 |
| AMD SMN | 白名單 index 寫入＋data 讀取使用跨實例鎖；native 使用同一 bridge，取消／例外後釋放，等待有界。 | 僅程序內序列化，不能據此宣稱韌體或其他程序所有權；既有未接線裁定保持。 |
| PC-0027／0028／0046／0049、PCIe | 移除 VEN/DEV 配第一筆鏈路，同型多卡保留各 BDF 讀數；PnP instance 與 backend 記錄分別保存並說明未配對。不按 CPU 名稱猜 lane 數，不按 bus 號判 CPU/PCH，也不以降寬判拆分。修正把 speed code 當 GT/s 後再轉世代；Gen1–5 顯示扣線路編碼的單向理論頻寬，Gen6 FLIT／FEC 未校核，不套 128/130。鏈路頁不把 Gen6 未知比例畫成零。 | BDF backend 未提供 segment／PnP instance 身份，不能安全合併實體數；上游連接關係仍需另核。理論頻寬未扣封包開銷，非量測吞吐。 |
| EN-0003／0004、企業基準 | `infer.*` 或 Trust=Speculative 即使 Present 亦為 Unverifiable，Exact／NumericRange 一致。簽署政策與 CLI 快照入口皆有回歸。 | 簽署政策不認證端點快照、硬體來源或遠端狀態。 |
| EN-0008／0013／0022、KP-0019、知識包 | 相容可空 manifest，記錄 upstream／revision／commit／extractor 與表 hash；manifest 納入 serial、逐表核對。直接 model 消費端也驗證，實際 JSON bytes 有界，雜湊前後查取消；敏感清單遮蔽自由文字 metadata。舊裝置清單缺比較條件與期望值，Present 亦不算符合，baseline 為 NotSupported、數值留空；空 MDM 的 Present＋0 有注入反例。 | hash 只綁定表身；manifest 是包作者自述，非來源認證或防偽。未造 PCI／SMART extractor 版本。未新增或冒稱已支援條件規則 schema。 |
| 儲存可靠性 | timeout wrapper 保留來源及狀態 getter 例外；同一 inner 的未完成讀取共用 gate，換 wrapper 不重啟；晚到結果不改寫逾時失敗，不提前釋放同步資源。 | 不同來源物件仍可能指向同一 WMI provider，跨實例／跨輪／跨程序 breaker 尚未完成。 |

## 驗證效率與範圍

- AI 測試注入實例級 delay 排程，正式預設仍為可取消 Task.Delay；首次＋10 次重試、2–20 秒退避與 Retry-After 語義不變。測試精確核序列，不真等待 110 秒。
- 兩個 storage 組裝案例與一個 UI 樣板案例只替換無關來源，仍走共用 storage 載入、AllFacts、UI 建構及繫結；具名 InjectedComposition。正式 107 步順序與既有 RealHardwareFactSnapshot 對帳保留。該歷史 fixture 本來含 NVMe 合成來源，不能擴稱 NVMe 實測。
- InjectedComposition 不等於完全沒有 OS 讀取：真 MainViewModel 仍建構 DimmReferenceService／M2AnalysisService，這兩個既有 eager 初始化會啟動 WMI；本輪沒有把它們算成全注入隔離。
- 警告守門仍真正重建主專案；改並行讀兩個 redirected pipe、單節點建置及五分鐘截止，超限失敗，不跳過守門。
- 公開方法學將舊冷啟動 2.7 秒標為歷史結果，不沿用為本版效能證據。

同一案例語意的全套 TRX 耗時對照如下；storage 兩案本輪改為具名注入組裝測試，不能把縮短的等待解讀成 WMI 或磁碟實機讀取變快。數值為秒，來源為 `obj/audit/r347/r347-full.trx` 與 `obj/audit/r348/r348-full.trx`。

| 案例 | Round 347 | Round 348 |
|---|---:|---:|
| AI 耗盡 10 次重試 | 110.018 | 0.017 |
| Storage 組裝：事實及渲染列接線 | 79.250 | 0.021 |
| Storage 組裝：來源例外及繼續收集 | 78.728 | 0.002 |
| UI 證據頁：有資料的樣板及繫結 | 98.786 | 1.445 |
| 警告守門：真正重建主專案 | 73.007 | 87.493 |

前三種注入路徑的既有斷言仍保留；警告守門本次反而較慢 14.486 秒，未將它列為加速成果。這是各測試的執行時間，並非可直接相加的全套牆鐘時間，也不構成應用啟動或硬體效能證據。

## 來源核對

- 本機 Windows SDK `10.0.28000.0/um/winnt.h` 的 NUMA_NODE_RELATIONSHIP（14400 起）、GROUP_AFFINITY；Microsoft Learn 的 LOGICAL_PROCESSOR_RELATIONSHIP、SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX。Ex selector 自 Server 2022 build 20348 引入；失敗碼映射是本程式政策，不宣稱涵蓋所有舊 OS 錯誤。
- 本機 SDK `10.0.26100.0/um/cper.h`：SectionSeverity 在 descriptor offset 48、memory ErrorType 在節區 offset 72，兩者獨立。
- Intel 330784-003（2015-06）§§2.1.13–15、頁34–38 與 338846-001US（2019-04）§§3.1.7–9、頁23–27：direct iMC SMBus；不能拿來背書現行 proxy 的所有權。C600 326514-002／C610 330788-003 的 PCH semaphore 亦不能套用 proxy。
- RAMSPDToolkit 固定 commit `198556be922df8aa870b0c37fd6e8b23cd02d0fd`；WorldSMBusMutex 僅合作慣例。2026-08-08 Linux v4 imc-skylake 是投稿，不當成已合入內核或官方安全契約；其自身仍載明缺仲裁及 stale DONE 限制。
- Microsoft Learn 的 CreateFileW「Physical Disks and Volumes」、ReOpenFile、DeviceIoControl、VOLUME_DISK_EXTENTS、DISK_EXTENT、STORAGE_DEVICE_DESCRIPTOR、CancelIoEx；本機 SDK winioctl.h 對應結構。

## 驗證紀錄

- 首次序列整合未進測試：新增 Top-down 測試漏 `using System.IO`，CS0246；原始失敗日誌 `obj/audit/r348/r348-target.log` 保留，已補引用。
- 第二次定向 437 通過、1 失敗、0 略過；`r348-target-2.trx`。原始失敗為企業基準測試把輸入順序當簽署 canonical 排序；改按唯一鍵核對 Trust 及 Satisfied，沒有放寬業務斷言。
- 第三次定向含文件統計：445 通過、8 失敗、0 略過；`r348-target-3.trx`。8 項均為新增檔案／方法及附錄未同步；不列為整合綠燈。其後獨立複核再修 PCIe speed code、PMU 原生所有權與雙故障、清單存在性判定。
- 第四次定向含統計：490 通過、2 失敗、0 略過；`r348-target-4.trx`。兩項是方法統計尚未同步；當次反射為 6,917 Fact、327 Theory、7,244 方法，670 測試檔及 1,644 C# 檔。已據實同步，案例基線仍待完整實跑。
- 最終完整回歸 **8,526/8,526 通過、0 失敗、0 略過**；`obj/audit/r348/r348-full.trx`。TRX 記錄 2026-11-17 03:43:17.809 至 03:53:01.268（UTC+08:00），區間 9 分 43.458 秒；runner 回報測試時間 9 分 39 秒，兩者計時範圍不同，分別保留。
- 全套結束時凍結核對 **1,999 份來源／文件＋3 份受測 DLL＝2,002 份，SHA-256 漂移 0**；證據 `obj/audit/r348/freeze-verification.json`，輸入清單為同目錄 `source-freeze.json`／`assembly-freeze.json`。其後僅回填本輪紀錄、交接及實作佇列三份文件；本次回填仍待後續文件守門，不冒充已重驗。
- 本次回填未取得新增的本輪 CLI smoke 或限定硬體實測證據；全套通過不能替代前述 DIMM、NUMA、CPER、PCIe parent、磁碟取消／對齊及 PMU 原生通路的真機證據。

## 下一項可取得的證據

本輪完整回歸已完成，下一步取 CPER 事件 payload、DIMM 原生讀取與多群組硬體 fixture。PCIe 的真正 parent bridge／segment 身份、WMI provider 共用斷路器、知識表可核 extractor manifest 仍屬具體佇列；不能用新測試數掩蓋這些缺口。Round 349 候選目前僅研究／暫存，尚未套用，不納入上述 8,526 通過結果。


### 收口補證

- 實跑後同步基線 8,526 與現在式文件；重建後文件／生成器／追蹤守門 66/66 通過，0 失敗／略過（`obj/audit/r348/r348-final-docs-complete.trx`）。較窄首次守門 47/47，兩份結果均保留。
- 兩個 Python 生成器 `--check` 回零；CLI 序列建置 0 錯誤、13 個既有警告（`r348-cli-build.log`）。六項離線 smoke 全符合預期退出碼：help、非法感測選項、缺少及損壞 Fleet、合成可比 Fleet、Redfish 序號遮蔽（`r348-cli-smoke.json`）。未跑 SPD、CPU-Z、PMU 寫入或真磁碟負載。
- 全套完成並核對零漂移後，僅同步基線／文件與收口證據；Round 349 候選須獨立編譯驗證，不能沿用本輪綠燈。
