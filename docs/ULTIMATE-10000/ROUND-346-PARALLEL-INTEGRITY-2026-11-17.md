# Round 346：七路審查與完整性修補

日期：2026-11-17（使用者目前時區 Asia/Singapore，UTC+08:00）。TRX 保留測試機原始時間與 offset。基底為已發佈 v3.30／`79a1073`。本輪僅本機推進，未改版號、未提交、未上傳或發版。

遵守 [工程憲法](../ENGINEERING-CONSTITUTION.md)。使用者授權帶既有七個代理全面推進；七路分別檢查 CLI、熱力圖、磁碟、SPD、企業讀取、安全閘門與感測時序，根代理整合、補測及驗證。代理報告是缺陷線索，結論須回查生產碼與測試。

## 實作與定義

| 路徑 | 缺陷與修補 | 證據及限制 |
|---|---|---|
| TS-0191／感測比較 | LHM CPU 取得視窗在 CPU 更新及複製數值時記錄；使用程序內單調鐘，拒絕缺失、反向或跨度超過 3 秒的視窗。快照複製為不可變記錄，後續硬體遍歷失敗時清空本輪比較快照。 | UTC 僅供時間標記；Core Temp 與 LHM 的 provider 更新時間、物理核心身份及感測器獨立性仍未知。候選差異不等於精度驗證。 |
| TS-0191／桌面熱力圖 | 移除按筆數比例折疊；拒絕重複來源／目的標籤與核心 ID。生產主通道及 Core Temp 備援均要求已載入的單插槽、單處理器群組、相符核心數與已知非混合 Intel 順序。 | 無法證實身份時清空，不按位置補值。取代 `HOTFIX-2026-11-01-network-heatmap.md` 關於比例折疊能支援混合架構的舊宣稱。socket/group/core 完整身份尚未建成。 |
| TS-0191／CLI | `--sensor-crosscheck [--out path]` 與 `sensor-crosscheck` 共用比較器。只建立 CPU 感測器，不呼叫全機收集。機器 JSON 越過巢狀本地化及 Console 同步 writer，保留原始資料。 | 退出碼 0＝存在候選，2＝無法比較，1＝引數／擷取／輸出失敗。輸出固定 `precisionVerified=false`、`sourceUpdatedAtUtc=null`。CPU-only LHM 初始化仍無硬截止時間。 |
| TS-0185／0187 | 所有塊尺及重讀共用 10 次額外重試；短讀續讀前查取消；內層進度同步轉送；最後進度觸發取消也不宣稱完成。 | 注入讀取驗證，沒有真碟負載。卷路徑尚未接 backing-device NVMe 身份及 StorageProbeBreaker；同步幾何／長度查詢仍有取消限制。兩候選維持進行中。 |
| DIMM／SPD | 初始切頁交易錯誤與 NoDevice 分開；DDR5 presence 的失敗保持未知，不能宣稱空匯流排。已取消／非法預算不開 worker；逾時原生 worker 未退出前不允許另一輪重啟。 | 保留已完成模組、WMI 與錯誤原因。逾時只取消等待及後續交易，不能保證中斷原生 I/O；gate 直到 reader 清理結束才釋放。沒有執行真實 SPD 讀取。 |
| AMD PCI 安全 | 公開適配器使用一般寫入授權；唯讀 SMN index 豁免封裝為 internal，只允許固定 0:0.0、兩個 index offset 及兩個已使用地址。 | 未驗證 AMD 實機相容性；跨程序 SMN index/data 交易仲裁仍待補。 |
| 最後穩定超頻設定 | 手動套用磁碟 profile 必須有目前第二級授權，不能繼承 recovery 豁免。拒絕留下失敗稽核。測試注入 OcSettings，建構不觸發持久化 setter。 | 當次操作 rollback 的原值恢復授權保留。沒有實際套用 profile 或硬體寫入。 |
| DRAM PMU | 先在釘選邏輯處理器檢查 Intel vendor、CPUID 0xA、PMU v2+、至少兩個 32–64 位元通用計數器與 LLC 事件支援，才開 bridge／配置負載。撤銷政策取消負載，每次 setup 查 live policy。保存 global control、兩 selector 與兩 PMC；缺基線、啟用中或非零殘留均拒絕，不搶占已知狀態。finally 嘗試全部恢復與讀回。 | 使用假 CPUID／MSR，沒有實機寫入。跨程序所有權仍缺；CPUID 可用不等於事件精度驗證。桌面、說明及 decoder 改列 LLC 等效量，使用 MiB／GiB/s；0.6–2 倍檢查只判量級，不稱 DRAM 實際流量或校準。 |
| WHEA／MM-0059、WH-0003 | 在查詢層先限候選 ID／固定 UTC 時間窗，明設 newest-first；不因舊時間戳提前停。64 筆候選上限以第 65 筆判截斷，另限 4,096 筆遍歷、3 秒列舉與每次 native read 250 ms。保留部分結果與 Complete／Truncated／TimedOut／InsufficientPrivilege／ReadError；FactsService 將不完整總數標非 Present、NumericValue=null。接入 `EvidenceCollection.LoadUsermodeFacts`，桌面與 CLI 共用。 | 注入測試不讀事件記錄。EventLogReader 建構沒有硬取消截止；全套既有唯讀生產收集會行經新入口。Section 仍為 null，候選事件不是已確認的 DIMM 錯誤；撤回歷史「記憶體節區已收口」判定。 |

主要生產檔：`SensorService.cs`、`SensorCrossCheckService.cs`、`CoreTempMapService.cs`、`DiskSurfaceScanService.cs`、`SpdReader.cs`、`SpdScanRunner.cs`、`AmdSmuService.cs`、`DramTrafficService.cs`、`Overclock/OverclockService.cs`、`CliService.cs` 與 `XinSpectCli/` 路由／本地化。

## 驗證紀錄

- 第一輪整合定向：`obj/audit/r346/r346-target.trx`，71 通過、0 失敗／略過。
- 第二輪整合定向：`obj/audit/r346/r346-integrated.trx`，119 通過、0 失敗／略過；早於後加的 writer、visitor／失敗快照回歸。
- 補充檢查：`obj/audit/r346/r346-preflight.trx`，61 通過、4 失敗、0 略過。4 紅均為 README／百科統計尚未同步：Fact／Theory 方法數、敘述總數、CLI 指令數與附錄 H 服務行數。依反射結果更新 6,804 Fact／296 Theory，CLI 51；重生附錄後另驗，保留原始失敗證據。
- CLI 初次獨立建置成功，0 錯誤、13 個既有警告；輸出 `XinSpectCli/obj/_verify_r346_cli/`。最終建置與完整測試結果完成後在本節補錄，不沿用 v3.30 的 8,235 當成本輪通過數。
- 第一次全套：`obj/audit/r346/r346-full.trx`，8,276 通過、3 失敗、0 略過、總計 8,279，15 分 30 秒。失敗為過時的 README 發版 bytes 守門與兩項未重生 HTML 守門，保留失敗報告；不稱全套通過。發版腳本本已支援 bytes，修的是測試對實際腳本正則的驗證。
- 安全／事件定向：`obj/audit/r346/r346-safety-event.trx`，71 通過、0 失敗／略過；早於最後的 LLC 等效量文案與 decoder 邊界修正。
- 最後預檢：`obj/audit/r346/r346-pre-full.trx`，112 通過、4 失敗、0 略過；4 項為新增測試後的方法／檔案統計與未同步附錄。按組件反射實數修為 6,819 Fact／300 Theory／7,119 方法、666 測試檔與 1,639 C# 檔；之後重生附錄與 HTML，完整結果另列。
- 第二次全套：`obj/audit/r346/r346-final-full.trx`，8,316 通過、2 失敗、0 略過、總計 8,318，15 分 28 秒。失敗為新列舉完整性狀態未登記六態對映及新測試檔未追蹤；結果保留，不稱全綠。已補登完整性與事實可用性的分工理由，新檔以 `git add -N` 納入本機 diff，未提交。
- 此輪測試後半至結束，1,645 個來源／XAML／專案檔的集合 SHA-256 均為 `A316FF1C84948105A4F7F1435124C2BD148C24E580FEF189C8F261DA300BFF88`。這只是兩次取樣間不變證據，不能回溯證明測試開始前也相同。
- 第二次全套期間 CLI 獨立建置成功，0 錯誤、13 個既有警告；`--help` 退出 0 且含 `sensor-crosscheck`，非法比較引數退出 1，沒有進入感測擷取。後續 WHEA 有效位修正完成後另列最終驗證。
- 後續 WHEA 有效位修正與七路續修合併至 Round 347，最終完整回歸 **8,406/8,406 通過、0 失敗／略過**；`obj/audit/r347/r347-full.trx`。此結果覆蓋本輪最終程式，先前紅燈報告保留。
- 沒有新做原始磁碟掃描、SPD 探測、PMU／PCI／超頻硬體寫入，也沒有發送資料到外部模型。全套既有測試可能包含已授權的系統／唯讀收集，其範圍不能冒充上述主動硬體驗證。

## 10000 計劃狀態

正典仍有 10,022 定義列、39 域；鍵對帳的 87 同名、80 前綴候選與 9,838 未命中只表示名稱關係，不能計完成率。本輪沒有增加同義事實鍵或候選完成數。TS-0185／0187／0191 均未滿足實機驗收條件。

後續已進入 [Round 347](ROUND-347-ENTERPRISE-BOUNDARIES-2026-11-17.md)，補 WHEA 有效位並修其他企業資料與拓撲缺陷；剩餘工作見 [實作佇列](IMPLEMENTATION-QUEUE-2026-11-17.md)。各功能仍需對應實機、身份及來源證據。

## 來源與實機邊界

- Intel SDM CPUID 0AH 的 version／counter count／width／event-vector length 與 EBX bits 3／4；沿用 `PmuDecoder` 的 `SpecRef`。僅作能力 gate，不是事件精度證據。
- 官方本機 `.NET 10.0.11` reference XML：`System.Diagnostics.EventLog.xml` 第 359 行為 ReverseDirection，408 行為 ReadEvent(TimeSpan)。
- `.NET runtime` v10.0.0 上游 `System.Diagnostics.EventLog/src/System/Diagnostics/Reader/EventLogException.cs`：native error code 1460 對應 HRESULT `0x800705B4`；故不能只 catch `TimeoutException`，另有注入回歸。
- `docs/PMU-SANDBOX-PLAN.md` 舊「未實作寫入」宣稱已加日期更正；其 S1–S5 仍沒有本輪實機完成證據。
